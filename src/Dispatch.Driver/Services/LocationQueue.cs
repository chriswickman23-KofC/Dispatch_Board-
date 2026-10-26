using System.Text.Json;
using Dispatch.Shared;
using Microsoft.Extensions.Logging;

namespace Dispatch.Driver.Services;

/// <summary>
/// Every point is written to a file on the phone first, then sent in batches every 30 seconds.
/// Without signal, points wait in the file (up to 2,000) and go out when the phone reconnects.
/// </summary>
public sealed class LocationQueue(DriverApiClient api, ILogger<LocationQueue> log)
{
    private const int MaxQueued = 2000;
    private const int BatchSize = 500;
    private static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _file = Path.Combine(FileSystem.AppDataDirectory, "pending-locations.json");
    private List<LocationDto>? _pending;
    private DateTimeOffset _lastAccepted = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;

    public async Task AddAsync(double lat, double lng, double? accuracy, DateTimeOffset recordedAt)
    {
        await _gate.WaitAsync();
        try
        {
            // Phones can report every second; one point every ~25 seconds is plenty for dispatch
            if (recordedAt - _lastAccepted < MinSpacing) return;
            _lastAccepted = recordedAt;

            var list = await LoadAsync();
            list.Add(new LocationDto(lat, lng, (float?)accuracy, recordedAt.ToUniversalTime()));
            if (list.Count > MaxQueued) list.RemoveRange(0, list.Count - MaxQueued);
            await SaveAsync(list);
        }
        finally
        {
            _gate.Release();
        }

        if (DateTimeOffset.UtcNow - _lastFlush >= FlushEvery) await FlushAsync();
    }

    public async Task FlushAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _lastFlush = DateTimeOffset.UtcNow;
            var list = await LoadAsync();
            while (list.Count > 0)
            {
                var batch = list.Take(BatchSize).ToList();
                await api.SendLocationsAsync(batch);   // throws when offline; remaining points stay queued
                list.RemoveRange(0, batch.Count);
                await SaveAsync(list);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or ApiException or TaskCanceledException)
        {
            log.LogInformation("Location upload deferred: {Message}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<LocationDto>> LoadAsync()
    {
        if (_pending is not null) return _pending;
        _pending = File.Exists(_file)
            ? JsonSerializer.Deserialize<List<LocationDto>>(await File.ReadAllTextAsync(_file), Json.Options) ?? []
            : [];
        return _pending;
    }

    private Task SaveAsync(List<LocationDto> list) =>
        File.WriteAllTextAsync(_file, JsonSerializer.Serialize(list, Json.Options));
}
