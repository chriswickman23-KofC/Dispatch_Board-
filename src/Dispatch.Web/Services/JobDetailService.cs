using Dispatch.Shared;
using Dispatch.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Services;

public sealed record ProofVm(string? PhotoUrl, string? SignatureUrl, string? RecipientName, DateTimeOffset CapturedAt);

public sealed record DetailStopVm(
    StopKind Kind, string Address, StopStatus Status,
    DateTimeOffset? ArrivedAt, DateTimeOffset? CompletedAt, string? FailureReason,
    IReadOnlyList<ProofVm> Proofs);

public sealed record EventVm(string Description, string ActorName, string Action, DateTimeOffset At);

public sealed record JobDetailVm(
    int Id, string Ref, JobStatus Status, JobType Type, bool Reviewed, string DisplayName,
    string? RecipientName, string? RecipientPhone, string? Notes,
    DateOnly? ScheduledDate, DateTimeOffset? WindowStart, DateTimeOffset? WindowEnd,
    string? DriverName, IReadOnlyList<DetailStopVm> Stops, IReadOnlyList<EventVm> Events)
{
    public bool NeedsReview => Status == JobStatus.Failed && !Reviewed;
    public DetailStopVm? FailedStop => Stops.Where(s => s.Status == StopStatus.Failed).OrderBy(s => s.CompletedAt).FirstOrDefault();
}

public sealed class JobDetailService(IDbContextFactory<ApplicationDbContext> dbFactory, IProofStorage storage)
{
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromHours(1);

    public async Task<JobDetailVm?> LoadAsync(int jobId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var job = await db.Jobs.AsNoTracking()
            .Include(j => j.Client)
            .Include(j => j.Driver)
            .Include(j => j.Stops).ThenInclude(s => s.Proofs)
            .Include(j => j.Events)
            .AsSplitQuery()
            .FirstOrDefaultAsync(j => j.Id == jobId);
        if (job is null) return null;

        var stops = new List<DetailStopVm>();
        foreach (var s in job.Stops.OrderBy(s => s.Kind == StopKind.Pickup ? 0 : 1))
        {
            var proofs = new List<ProofVm>();
            foreach (var p in s.Proofs.OrderBy(p => p.CapturedAt))
                proofs.Add(new ProofVm(await LinkAsync(p.PhotoPath), await LinkAsync(p.SignaturePath), p.RecipientName, p.CapturedAt));

            stops.Add(new DetailStopVm(s.Kind, s.Address, s.Status, s.ArrivedAt, s.CompletedAt, s.FailureReason, proofs));
        }

        var history = job.Events
            .OrderBy(e => e.CreatedAt)
            .Select(e => new EventVm(e.Description, e.ActorName, e.Action, e.CreatedAt))
            .ToList();

        return new JobDetailVm(
            job.Id, job.Ref, job.Status, job.Type, job.ReviewedAt is not null,
            job.Client?.Name ?? job.RecipientName ?? "No name",
            job.RecipientName, job.RecipientPhone, job.Notes,
            job.ScheduledDate, job.WindowStart, job.WindowEnd,
            job.Driver?.Name, stops, history);

        async Task<string?> LinkAsync(string? path) =>
            path is null ? null : (await storage.GetReadUrlAsync(path, LinkLifetime))?.ToString();
    }
}
