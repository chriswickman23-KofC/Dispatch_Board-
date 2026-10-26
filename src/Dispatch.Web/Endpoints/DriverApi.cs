using System.Security.Claims;
using Dispatch.Shared;
using Dispatch.Web.Data;
using Dispatch.Web.Services;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dispatch.Web.Endpoints;

public static class DriverApi
{
    public const string Policy = "DriverApi";

    private const long MaxPhotoBytes = 8 * 1024 * 1024;
    private const long MaxSignatureBytes = 1024 * 1024;
    private const int MaxPointsPerRequest = 500;
    private static readonly string[] PhotoTypes = ["image/jpeg", "image/png"];

    public static void MapDriverApi(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/driver/auth");
        auth.MapPost("/login", LoginAsync);
        auth.MapPost("/refresh", RefreshAsync);

        var api = app.MapGroup("/api/driver").RequireAuthorization(Policy);
        api.MapGet("/me", GetMeAsync);
        api.MapGet("/route", GetRouteAsync);
        api.MapPost("/clock-in", (ClaimsPrincipal u, IDbContextFactory<ApplicationDbContext> f, DispatchService d) => SetShiftAsync(u, f, d, true));
        api.MapPost("/clock-out", (ClaimsPrincipal u, IDbContextFactory<ApplicationDbContext> f, DispatchService d) => SetShiftAsync(u, f, d, false));
        api.MapPost("/locations", PostLocationsAsync);
        api.MapPost("/stops/{stopId:int}/status", UpdateStopAsync);
        // Bearer-authenticated, not cookie-based, so antiforgery tokens don't apply
        api.MapPost("/stops/{stopId:int}/complete", CompleteDeliveryAsync).DisableAntiforgery();
    }

    // ---------- Auth ----------

    private static async Task<IResult> LoginAsync(
        DriverLoginRequest req, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users)
    {
        var user = await users.FindByEmailAsync(req.Email.Trim());
        if (user is null || !await users.IsInRoleAsync(user, Roles.Driver))
            return Results.Problem("Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);

        signIn.AuthenticationScheme = IdentityConstants.BearerScheme;
        var result = await signIn.PasswordSignInAsync(user, req.Password, isPersistent: false, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            var message = result.IsLockedOut ? "Too many failed attempts, or this account is deactivated."
                        : result.IsNotAllowed ? "This account hasn't been activated yet."
                        : "Invalid email or password.";
            return Results.Problem(message, statusCode: StatusCodes.Status401Unauthorized);
        }
        return Results.Empty;   // the bearer handler has already written the token response
    }

    private static async Task<IResult> RefreshAsync(
        TokenRefreshRequest req,
        IOptionsMonitor<BearerTokenOptions> bearerOptions,
        SignInManager<ApplicationUser> signIn,
        UserManager<ApplicationUser> users)
    {
        var protector = bearerOptions.Get(IdentityConstants.BearerScheme).RefreshTokenProtector;
        var ticket = protector.Unprotect(req.RefreshToken);

        // The security stamp check means changing a driver's password, removing their role, or deactivating
        // them stops their phone at its next refresh (within an hour)
        if (ticket?.Properties.ExpiresUtc is not { } expires
            || DateTimeOffset.UtcNow >= expires
            || await signIn.ValidateSecurityStampAsync(ticket.Principal) is not { } user
            || !await users.IsInRoleAsync(user, Roles.Driver))
        {
            return Results.Unauthorized();
        }

        var principal = await signIn.CreateUserPrincipalAsync(user);
        return Results.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
    }

    // ---------- Driver data ----------

    private static async Task<IResult> GetMeAsync(ClaimsPrincipal user, IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var driver = await CurrentDriverAsync(user, db);
        return driver is null ? NotLinked() : Results.Ok(new DriverProfileDto(driver.Id, driver.Name, driver.Status));
    }

    private static async Task<IResult> GetRouteAsync(
        ClaimsPrincipal user, IDbContextFactory<ApplicationDbContext> dbFactory, DispatchClock clock)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var driver = await CurrentDriverAsync(user, db);
        if (driver is null) return NotLinked();

        var today = clock.Today;
        var routeId = await db.Routes
            .Where(r => r.DriverId == driver.Id && r.RouteDate == today)
            .Select(r => (int?)r.Id)
            .FirstOrDefaultAsync();
        if (routeId is null) return Results.Ok(Array.Empty<RouteStopDto>());

        var stops = await db.Stops.AsNoTracking()
            .Where(s => s.RouteId == routeId)
            .OrderBy(s => s.Sequence)
            .Select(s => new RouteStopDto(
                s.Id, s.Kind, s.Address, s.Lat, s.Lng, s.Sequence ?? 0, s.Status,
                s.JobId, s.Job.Ref, s.Job.Type, s.Job.Status,
                s.Job.Client != null ? s.Job.Client.Name : (s.Job.RecipientName ?? ""),
                s.Job.RecipientName, s.Job.RecipientPhone, s.Job.Notes,
                s.Job.WindowStart, s.Job.WindowEnd,
                s.Kind == StopKind.Pickup || s.Job.Stops.Any(p => p.Kind == StopKind.Pickup && p.Status == StopStatus.Completed)))
            .ToListAsync();

        return Results.Ok(stops);
    }

    private static async Task<IResult> SetShiftAsync(
        ClaimsPrincipal user, IDbContextFactory<ApplicationDbContext> dbFactory, DispatchService dispatch, bool onShift)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var driver = await CurrentDriverAsync(user, db);
        if (driver is null) return NotLinked();

        var status = await dispatch.SetShiftAsync(driver.Id, onShift);
        return Results.Ok(new DriverProfileDto(driver.Id, driver.Name, status));
    }

    private static async Task<IResult> PostLocationsAsync(
        List<LocationDto> points,
        ClaimsPrincipal user,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        LocationService locations,
        CancellationToken ct)
    {
        if (points.Count > MaxPointsPerRequest)
            return Results.Problem($"Send at most {MaxPointsPerRequest} points per request.", statusCode: StatusCodes.Status400BadRequest);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var driver = await CurrentDriverAsync(user, db);
        if (driver is null) return NotLinked();

        // Accept queued points up to a day old (phones that lost signal), reject nonsense
        var now = DateTimeOffset.UtcNow;
        var valid = points
            .Where(p => p.Lat is >= -90 and <= 90 && p.Lng is >= -180 and <= 180
                        && p.RecordedAt > now.AddDays(-1) && p.RecordedAt < now.AddMinutes(5))
            .Select(p => new LocationPoint(p.Lat, p.Lng, p.Accuracy, p.RecordedAt))
            .ToList();

        await locations.RecordAsync(driver.Id, valid, ct);
        return Results.Ok(new LocationsAccepted(valid.Count));
    }

    private static async Task<IResult> UpdateStopAsync(
        int stopId,
        StopStatusRequest req,
        ClaimsPrincipal user,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        DispatchService dispatch)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var driver = await CurrentDriverAsync(user, db);
        if (driver is null) return NotLinked();

        if (req.Status == StopStatus.Pending)
            return Results.Problem("That status can't be set from the app.", statusCode: StatusCodes.Status400BadRequest);
        if (req.Status == StopStatus.Failed && string.IsNullOrWhiteSpace(req.Reason))
            return Results.Problem("Choose a reason.", statusCode: StatusCodes.Status400BadRequest);

        if (req.Status == StopStatus.Completed)
        {
            var kind = await db.Stops.Where(s => s.Id == stopId).Select(s => (StopKind?)s.Kind).FirstOrDefaultAsync();
            if (kind == StopKind.Dropoff)
                return Results.Problem("Deliveries need a photo. Use the complete endpoint.", statusCode: StatusCodes.Status400BadRequest);
        }

        var reason = req.Status == StopStatus.Failed ? Truncate(req.Reason, 200) : null;
        var result = await dispatch.UpdateStopStatusAsync(stopId, req.Status, reason, ActorFor(user, driver));
        return result.Success ? Results.NoContent() : Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> CompleteDeliveryAsync(
        int stopId,
        IFormFile photo,
        IFormFile? signature,
        [FromForm] string? recipientName,
        [FromForm] double? lat,
        [FromForm] double? lng,
        ClaimsPrincipal user,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        IProofStorage storage,
        DispatchService dispatch,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var driver = await CurrentDriverAsync(user, db);
        if (driver is null) return NotLinked();

        var stop = await db.Stops.AsNoTracking()
            .Include(s => s.Job).ThenInclude(j => j.Stops)
            .FirstOrDefaultAsync(s => s.Id == stopId, ct);

        if (stop is null || stop.Job.DriverId != driver.Id) return Results.NotFound();
        if (stop.Kind != StopKind.Dropoff || stop.Status is StopStatus.Completed or StopStatus.Failed)
            return Results.Problem("This stop can't be completed.", statusCode: StatusCodes.Status409Conflict);
        if (stop.Job.Stops.Any(s => s.Kind == StopKind.Pickup && s.Status != StopStatus.Completed))
            return Results.Problem("Complete the pickup for this job first.", statusCode: StatusCodes.Status409Conflict);

        if (photo.Length == 0 || photo.Length > MaxPhotoBytes || !PhotoTypes.Contains(photo.ContentType))
            return Results.Problem("The photo must be a JPEG or PNG under 8 MB.", statusCode: StatusCodes.Status400BadRequest);
        if (signature is not null && (signature.Length > MaxSignatureBytes || signature.ContentType != "image/png"))
            return Results.Problem("The signature must be a PNG under 1 MB.", statusCode: StatusCodes.Status400BadRequest);

        string photoPath;
        await using (var photoStream = photo.OpenReadStream())
            photoPath = await storage.SaveAsync(driver.Id, stopId, "photo", photoStream, photo.ContentType, ct);

        string? signaturePath = null;
        if (signature is not null)
        {
            await using var sigStream = signature.OpenReadStream();
            signaturePath = await storage.SaveAsync(driver.Id, stopId, "signature", sigStream, "image/png", ct);
        }

        db.Proofs.Add(new Proof
        {
            StopId = stopId,
            PhotoPath = photoPath,
            SignaturePath = signaturePath,
            RecipientName = Truncate(recipientName, 100),
            Lat = lat,
            Lng = lng,
        });
        await db.SaveChangesAsync(ct);

        var result = await dispatch.UpdateStopStatusAsync(stopId, StopStatus.Completed, null, ActorFor(user, driver));
        return result.Success ? Results.NoContent() : Results.Problem(result.Error, statusCode: StatusCodes.Status409Conflict);
    }

    // ---------- Helpers ----------

    private static async Task<Driver?> CurrentDriverAsync(ClaimsPrincipal user, ApplicationDbContext db)
    {
        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return userId is null ? null : await db.Drivers.AsNoTracking()
            .FirstOrDefaultAsync(d => d.UserId == userId && d.Active);
    }

    private static Actor ActorFor(ClaimsPrincipal user, Driver driver) =>
        new(user.FindFirstValue(ClaimTypes.NameIdentifier)!, driver.Name, driver.Id);

    private static IResult NotLinked() =>
        Results.Problem("This login isn't linked to an active driver. Ask dispatch to set you up.", statusCode: StatusCodes.Status403Forbidden);

    private static string? Truncate(string? s, int max)
    {
        s = s?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return s.Length > max ? s[..max] : s;
    }
}
