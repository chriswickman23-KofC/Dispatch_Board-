using System.Net.Mail;
using System.Text;
using System.Text.Encodings.Web;
using Dispatch.Shared;
using Dispatch.Web.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services;

public enum PersonState { Active, Invited, Deactivated, NoLogin }

public sealed record StaffVm(string UserId, string Name, string Email, string Role, int? ZoneId, PersonState State);

public sealed record DriverPersonVm(
    int DriverId, string Name, string? Phone, int? ZoneId, DriverStatus Status,
    string? UserId, string? Email, PersonState State, int ActiveJobs);

public sealed record PeopleSnapshot(IReadOnlyList<StaffVm> Staff, IReadOnlyList<DriverPersonVm> Drivers, IReadOnlyList<ZoneVm> Zones)
{
    public static PeopleSnapshot Empty { get; } = new([], [], []);
}

public sealed record PeopleActor(string UserId, bool IsAdmin);

/// <summary>
/// Invites, edits, and deactivates people. UserManager isn't designed to live for a whole Blazor
/// Server connection, so each operation creates a fresh scope.
/// </summary>
public sealed class PeopleService(
    IServiceScopeFactory scopes,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IAppEmailSender email,
    DispatchEvents events,
    ILogger<PeopleService> log)
{
    // ---------- Reading ----------

    public async Task<PeopleSnapshot> LoadAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var zones = await db.Zones.AsNoTracking().OrderBy(z => z.Name)
            .Select(z => new ZoneVm(z.Id, z.Name)).ToListAsync();

        var staffRows = await (
            from u in db.Users
            join ur in db.UserRoles on u.Id equals ur.UserId
            join r in db.Roles on ur.RoleId equals r.Id
            where r.Name == Roles.Admin || r.Name == Roles.Dispatcher
            select new { u.Id, u.FullName, u.Email, Role = r.Name!, u.ZoneId, u.EmailConfirmed, u.LockoutEnd })
            .ToListAsync();

        var staff = staffRows
            .GroupBy(x => x.Id)
            .Select(g =>
            {
                var x = g.OrderBy(r => r.Role == Roles.Admin ? 0 : 1).First();   // admin wins if someone has both
                return new StaffVm(x.Id, string.IsNullOrWhiteSpace(x.FullName) ? x.Email ?? "" : x.FullName,
                    x.Email ?? "", x.Role, x.ZoneId, StateOf(true, x.EmailConfirmed, x.LockoutEnd));
            })
            .OrderBy(s => s.Name)
            .ToList();

        var driverRows = await db.Drivers.AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new
            {
                d.Id, d.Name, d.Phone, d.ZoneId, d.Status, d.Active, d.UserId,
                Email = d.User != null ? d.User.Email : null,
                Confirmed = d.User != null && d.User.EmailConfirmed,
                Lockout = d.User != null ? d.User.LockoutEnd : null,
                ActiveJobs = db.Jobs.Count(j => j.DriverId == d.Id && (j.Status == JobStatus.Assigned || j.Status == JobStatus.InProgress)),
            })
            .ToListAsync();

        var drivers = driverRows.Select(d => new DriverPersonVm(
            d.Id, d.Name, d.Phone, d.ZoneId, d.Status, d.UserId, d.Email,
            StateOf(d.UserId is not null, d.Confirmed, d.Lockout, d.Active), d.ActiveJobs)).ToList();

        return new PeopleSnapshot(staff, drivers, zones);
    }

    // ---------- Inviting ----------

    public async Task<Result<string>> InviteStaffAsync(string name, string emailAddress, string role, int? zoneId,
        PeopleActor actor, string baseUri)
    {
        if (role is not (Roles.Dispatcher or Roles.Admin)) return Result<string>.Fail("Choose a role.");
        if (role == Roles.Admin && !actor.IsAdmin) return Result<string>.Fail("Only admins can invite admins.");
        if (string.IsNullOrWhiteSpace(name)) return Result<string>.Fail("Enter a name.");
        if (NormalizeEmail(emailAddress) is not { } address) return Result<string>.Fail("Enter a valid email address.");

        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByEmailAsync(address) is not null) return Result<string>.Fail("Someone already uses that email address.");

        var user = new ApplicationUser { UserName = address, Email = address, FullName = name.Trim(), ZoneId = zoneId };
        var created = await users.CreateAsync(user);   // no password until they accept the invite
        if (!created.Succeeded) return Result<string>.Fail(Describe(created));
        await users.AddToRoleAsync(user, role);

        var sent = await SendSetPasswordEmailAsync(users, user, isInvite: true, isDriver: false, baseUri);
        return Result<string>.Ok(sent ? $"Invite sent to {address}." : "Account created, but the email couldn't be sent. Use Resend invite.");
    }

    /// <summary>Invites a new driver, or gives an existing driver record (existingDriverId) an app login.</summary>
    public async Task<Result<string>> InviteDriverAsync(int? existingDriverId, string name, string emailAddress,
        string? phone, int? zoneId, string baseUri)
    {
        if (string.IsNullOrWhiteSpace(name)) return Result<string>.Fail("Enter a name.");
        if (NormalizeEmail(emailAddress) is not { } address) return Result<string>.Fail("Enter a valid email address.");

        await using var db = await dbFactory.CreateDbContextAsync();
        Driver? driver = null;
        if (existingDriverId is int id)
        {
            driver = await db.Drivers.FindAsync(id);
            if (driver is null) return Result<string>.Fail("Driver not found.");
            if (driver.UserId is not null) return Result<string>.Fail("This driver already has an app login.");
        }

        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByEmailAsync(address) is not null) return Result<string>.Fail("Someone already uses that email address.");

        var user = new ApplicationUser { UserName = address, Email = address, FullName = name.Trim() };
        var created = await users.CreateAsync(user);
        if (!created.Succeeded) return Result<string>.Fail(Describe(created));
        await users.AddToRoleAsync(user, Roles.Driver);

        try
        {
            if (driver is null)
            {
                driver = new Driver { Status = DriverStatus.Offline };
                db.Drivers.Add(driver);
            }
            driver.Name = name.Trim();
            driver.Phone = Clean(phone);
            driver.ZoneId = zoneId;
            driver.UserId = user.Id;
            driver.Active = true;
            await db.SaveChangesAsync();
        }
        catch
        {
            await users.DeleteAsync(user);   // don't leave a login without a driver record
            throw;
        }

        events.Publish(new DispatchChange(ChangeKind.Drivers, DriverId: driver.Id));
        var sent = await SendSetPasswordEmailAsync(users, user, isInvite: true, isDriver: true, baseUri);
        return Result<string>.Ok(sent ? $"Invite sent to {address}." : "Driver created, but the email couldn't be sent. Use Resend invite.");
    }

    /// <summary>Resends an unaccepted invite, or sends a password reset to someone already active.</summary>
    public async Task<Result<string>> SendPasswordEmailAsync(string userId, PeopleActor actor, string baseUri)
    {
        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null) return Result<string>.Fail("Account not found.");
        if (!actor.IsAdmin && await users.IsInRoleAsync(user, Roles.Admin))
            return Result<string>.Fail("Only admins can manage admin accounts.");

        var isInvite = !user.EmailConfirmed;
        var isDriver = await users.IsInRoleAsync(user, Roles.Driver);
        var sent = await SendSetPasswordEmailAsync(users, user, isInvite, isDriver, baseUri);
        return sent
            ? Result<string>.Ok(isInvite ? $"Invite resent to {user.Email}." : $"Password reset sent to {user.Email}.")
            : Result<string>.Fail("The email couldn't be sent. Please try again.");
    }

    // ---------- Editing ----------

    public async Task<Result<string>> UpdateStaffAsync(string userId, string name, int? zoneId, string role, PeopleActor actor)
    {
        if (string.IsNullOrWhiteSpace(name)) return Result<string>.Fail("Enter a name.");
        if (role is not (Roles.Dispatcher or Roles.Admin)) return Result<string>.Fail("Choose a role.");

        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null) return Result<string>.Fail("Account not found.");

        var isAdmin = await users.IsInRoleAsync(user, Roles.Admin);
        if ((isAdmin || role == Roles.Admin) && !actor.IsAdmin) return Result<string>.Fail("Only admins can manage admin accounts.");
        if (isAdmin && role != Roles.Admin && await ActiveAdminCountAsync(users) <= 1)
            return Result<string>.Fail("You can't remove the last active admin.");

        user.FullName = name.Trim();
        user.ZoneId = zoneId;
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded) return Result<string>.Fail(Describe(updated));

        var currentRole = isAdmin ? Roles.Admin : Roles.Dispatcher;
        if (currentRole != role)
        {
            await users.RemoveFromRoleAsync(user, currentRole);
            await users.AddToRoleAsync(user, role);
            await users.UpdateSecurityStampAsync(user);   // new permissions take effect at their next session check
        }
        return Result<string>.Ok($"{user.FullName} updated.");
    }

    public async Task<Result<string>> UpdateDriverAsync(int driverId, string name, string? phone, int? zoneId)
    {
        if (string.IsNullOrWhiteSpace(name)) return Result<string>.Fail("Enter a name.");

        await using var db = await dbFactory.CreateDbContextAsync();
        var driver = await db.Drivers.Include(d => d.User).FirstOrDefaultAsync(d => d.Id == driverId);
        if (driver is null) return Result<string>.Fail("Driver not found.");

        driver.Name = name.Trim();
        driver.Phone = Clean(phone);
        driver.ZoneId = zoneId;
        if (driver.User is not null) driver.User.FullName = driver.Name;
        await db.SaveChangesAsync();

        events.Publish(new DispatchChange(ChangeKind.Drivers, DriverId: driverId));
        return Result<string>.Ok($"{driver.Name} updated.");
    }

    // ---------- Deactivating ----------

    public async Task<Result<string>> SetStaffActiveAsync(string userId, bool active, PeopleActor actor)
    {
        if (userId == actor.UserId) return Result<string>.Fail("You can't deactivate your own account.");

        using var scope = scopes.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId);
        if (user is null) return Result<string>.Fail("Account not found.");

        var isAdmin = await users.IsInRoleAsync(user, Roles.Admin);
        if (isAdmin && !actor.IsAdmin) return Result<string>.Fail("Only admins can manage admin accounts.");
        if (!active && isAdmin && await ActiveAdminCountAsync(users) <= 1)
            return Result<string>.Fail("You can't deactivate the last active admin.");

        await SetLockedAsync(users, user, locked: !active);
        return Result<string>.Ok(active ? $"{user.FullName} reactivated." : $"{user.FullName} deactivated and signed out.");
    }

    public async Task<Result<string>> SetDriverActiveAsync(int driverId, bool active)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var driver = await db.Drivers.FirstOrDefaultAsync(d => d.Id == driverId);
        if (driver is null) return Result<string>.Fail("Driver not found.");

        if (!active)
        {
            var activeJobs = await db.Jobs.CountAsync(j =>
                j.DriverId == driverId && (j.Status == JobStatus.Assigned || j.Status == JobStatus.InProgress));
            if (activeJobs > 0)
                return Result<string>.Fail($"{driver.Name} has {activeJobs} active job(s). Reassign them on the board first.");
        }

        driver.Active = active;
        if (!active) driver.Status = DriverStatus.Offline;
        await db.SaveChangesAsync();

        if (driver.UserId is not null)
        {
            using var scope = scopes.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (await users.FindByIdAsync(driver.UserId) is { } user)
                await SetLockedAsync(users, user, locked: !active);
        }

        events.Publish(new DispatchChange(ChangeKind.Drivers, DriverId: driverId));
        return Result<string>.Ok(active ? $"{driver.Name} reactivated." : $"{driver.Name} deactivated and signed out of the app.");
    }

    // ---------- Helpers ----------

    private async Task<bool> SendSetPasswordEmailAsync(UserManager<ApplicationUser> users, ApplicationUser user,
        bool isInvite, bool isDriver, string baseUri)
    {
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var code = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));
        var link = $"{baseUri.TrimEnd('/')}/Account/AcceptInvite?userId={Uri.EscapeDataString(user.Id)}&code={code}";

        var name = string.IsNullOrWhiteSpace(user.FullName) ? "there" : user.FullName;
        var subject = isInvite ? "You're invited to Dispatch" : "Reset your Dispatch password";
        var intro = isInvite
            ? $"Hi {name}, you've been invited to the dispatch system. Set a password to get started."
            : $"Hi {name}, a password reset was requested for your account.";
        var next = isDriver
            ? "After setting your password, open the Courier Driver app on your company phone and sign in with this email address."
            : "After setting your password, you can sign in to the dispatch board.";

        var enc = HtmlEncoder.Default;
        var html = $"""
            <p>{enc.Encode(intro)}</p>
            <p><a href="{enc.Encode(link)}" style="display:inline-block;background:#2563eb;color:#fff;padding:10px 18px;border-radius:6px;text-decoration:none">Set your password</a></p>
            <p>{enc.Encode(next)}</p>
            <p style="color:#64748b">This link expires in 3 days. If you weren't expecting this email, you can ignore it.</p>
            """;
        var text = $"{intro}\n\nSet your password: {link}\n\n{next}\n\nThis link expires in 3 days.";

        try
        {
            await email.SendAsync(user.Email!, subject, html, text);
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to send password email to {Email}", user.Email);
            return false;
        }
    }

    private static async Task SetLockedAsync(UserManager<ApplicationUser> users, ApplicationUser user, bool locked)
    {
        await users.SetLockoutEnabledAsync(user, true);
        await users.SetLockoutEndDateAsync(user, locked ? DateTimeOffset.MaxValue : null);
        await users.UpdateSecurityStampAsync(user);   // invalidates existing sessions and refresh tokens
    }

    private static async Task<int> ActiveAdminCountAsync(UserManager<ApplicationUser> users) =>
        (await users.GetUsersInRoleAsync(Roles.Admin))
            .Count(a => a.LockoutEnd is null || a.LockoutEnd <= DateTimeOffset.UtcNow);

    private static PersonState StateOf(bool hasUser, bool confirmed, DateTimeOffset? lockoutEnd, bool driverActive = true) =>
        !driverActive || lockoutEnd > DateTimeOffset.UtcNow ? PersonState.Deactivated
        : !hasUser ? PersonState.NoLogin
        : !confirmed ? PersonState.Invited
        : PersonState.Active;

    private static string? NormalizeEmail(string? s) =>
        MailAddress.TryCreate(s?.Trim(), out var address) ? address.Address.ToLowerInvariant() : null;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(e => e.Description));
}
