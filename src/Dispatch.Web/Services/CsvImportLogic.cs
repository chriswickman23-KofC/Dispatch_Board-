using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Dispatch.Shared;

namespace Dispatch.Web.Services;

public sealed record CsvField(string Key, string Label, bool Required, string[] Guesses);

public enum RowStatus { Ok, Check, Error }

public sealed class ImportRow
{
    public int Line { get; init; }
    public bool Include { get; set; } = true;
    public string? RecipientName { get; init; }
    public string? RecipientPhone { get; init; }
    public string? ExternalId { get; init; }
    public string? Notes { get; init; }
    public JobType Type { get; init; }
    public DateOnly? ScheduledDate { get; set; }   // settable so rows can be moved to an open day
    public TimeOnly? Start { get; init; }
    public TimeOnly? End { get; init; }
    public string PickupText { get; init; } = "";
    public string DropoffText { get; init; } = "";
    public GeoPoint? Pickup { get; set; }
    public GeoPoint? Dropoff { get; set; }
    public bool LowConfidence { get; set; }
    public string? ClosedReason { get; set; }
    public List<string> Errors { get; } = [];

    public IEnumerable<string> Issues
    {
        get
        {
            foreach (var e in Errors) yield return e;
            if (DropoffText.Length > 0 && Dropoff is null) yield return "Dropoff not found";
            if (Pickup is null) yield return "Pickup not found";
        }
    }

    public RowStatus Status =>
        Issues.Any() ? RowStatus.Error
        : LowConfidence || ClosedReason is not null ? RowStatus.Check
        : RowStatus.Ok;
}

public static partial class CsvImportLogic
{
    public const int MaxRows = 500;

    public static readonly CsvField[] Fields =
    [
        new("dropoff_address", "Dropoff address", true,
            ["dropoff address", "delivery address", "address", "street", "address 1", "address1", "shipping address", "ship to address", "destination"]),
        new("dropoff_city", "Dropoff city", false, ["city", "ship city", "shipping city"]),
        new("dropoff_state", "Dropoff state", false, ["state", "ship state", "province"]),
        new("dropoff_zip", "Dropoff ZIP", false, ["zip", "zip code", "zipcode", "postal code", "postcode"]),
        new("pickup_address", "Pickup address", false, ["pickup address", "pickup", "origin", "from address"]),
        new("recipient_name", "Recipient name", false,
            ["recipient name", "recipient", "customer name", "customer", "name", "ship to name", "contact"]),
        new("recipient_phone", "Recipient phone", false, ["recipient phone", "phone", "phone number", "mobile", "cell", "customer phone"]),
        new("external_id", "Order # (prevents duplicates)", false, ["order number", "order id", "order", "order #", "reference", "ref", "id"]),
        new("date", "Delivery date", false, ["delivery date", "date", "scheduled date", "ship date"]),
        new("window_start", "Window start (or \"10am-12pm\")", false, ["window start", "start time", "delivery window", "window", "time", "from"]),
        new("window_end", "Window end", false, ["window end", "end time", "to", "until"]),
        new("notes", "Notes", false, ["delivery instructions", "instructions", "notes", "note", "comments"]),
    ];

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"^(\d{1,2})(?::(\d{2}))?\s*(am|pm|a|p)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TimePattern();

    public static string Normalize(string s) => NonAlphanumeric().Replace(s.ToLowerInvariant(), " ").Trim();

    public static (List<string> Headers, List<Dictionary<string, string>> Rows) Read(Stream stream)
    {
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            DetectDelimiter = true,
            BadDataFound = null,
            MissingFieldFound = null,
            TrimOptions = TrimOptions.Trim,
        };
        using var csv = new CsvReader(reader, config);

        if (!csv.Read()) return ([], []);
        csv.ReadHeader();
        var headers = (csv.HeaderRecord ?? []).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().ToList();

        var rows = new List<Dictionary<string, string>>();
        while (csv.Read() && rows.Count <= MaxRows)   // read one extra so we can report "too many"
        {
            var row = headers.ToDictionary(h => h, h => csv.GetField(h) ?? "");
            if (row.Values.All(string.IsNullOrWhiteSpace)) continue;
            rows.Add(row);
        }
        return (headers, rows);
    }

    /// <summary>The client's saved layout first, then header-name guesses.</summary>
    public static Dictionary<string, string?> Guess(IReadOnlyList<string> headers, IReadOnlyDictionary<string, string>? saved)
    {
        var mapping = new Dictionary<string, string?>();
        var used = new HashSet<string>();

        if (saved is not null)
            foreach (var f in Fields)
                if (saved.TryGetValue(f.Key, out var h) && headers.Contains(h) && used.Add(h))
                    mapping[f.Key] = h;

        foreach (var f in Fields)
        {
            if (mapping.ContainsKey(f.Key)) continue;
            foreach (var guess in f.Guesses.Select(Normalize))
            {
                var hit = headers.FirstOrDefault(h => Normalize(h) == guess && !used.Contains(h));
                if (hit is null) continue;
                mapping[f.Key] = hit;
                used.Add(hit);
                break;
            }
        }
        return mapping;
    }

    /// <summary>US month/day order. Invalid = present but unreadable.</summary>
    public static (DateOnly? Value, bool Invalid) ParseDate(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return (null, false);
        var candidate = s.Length >= 10 && s[4] == '-' ? s[..10] : s.Split(' ')[0];
        string[] formats = ["yyyy-MM-dd", "M/d/yyyy", "M/d/yy", "yyyy/M/d"];
        return DateOnly.TryParseExact(candidate, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? (d, false) : (null, true);
    }

    public static (TimeOnly? Value, bool Invalid) ParseTime(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return (null, false);
        var m = TimePattern().Match(s);
        if (!m.Success) return (null, true);

        var hour = int.Parse(m.Groups[1].Value);
        var minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        var ampm = m.Groups[3].Value.ToLowerInvariant();

        if (ampm.Length > 0)
        {
            if (hour is < 1 or > 12) return (null, true);
            if (ampm.StartsWith('p') && hour != 12) hour += 12;
            if (ampm.StartsWith('a') && hour == 12) hour = 0;
        }
        return hour > 23 || minute > 59 ? (null, true) : (new TimeOnly(hour, minute), false);
    }

    public static ImportRow BuildRow(IReadOnlyDictionary<string, string> raw, int index,
        IReadOnlyDictionary<string, string?> mapping, JobType defaultType, DateOnly defaultDate)
    {
        string V(string key) => mapping.GetValueOrDefault(key) is { } header ? raw.GetValueOrDefault(header, "").Trim() : "";

        var dropoffText = string.Join(", ",
            new[] { V("dropoff_address"), V("dropoff_city"), V("dropoff_state"), V("dropoff_zip") }.Where(p => p.Length > 0));

        var startRaw = V("window_start");
        var endRaw = V("window_end");
        if (startRaw.Length > 0 && endRaw.Length == 0 && (startRaw.Contains('-') || startRaw.Contains('–')))
        {
            var parts = startRaw.Split(['-', '–'], 2, StringSplitOptions.TrimEntries);
            (startRaw, endRaw) = (parts[0], parts[1]);
        }

        var (date, badDate) = ParseDate(V("date"));
        var (start, badStart) = ParseTime(startRaw);
        var (end, badEnd) = ParseTime(endRaw);

        var errors = new List<string>();
        if (dropoffText.Length == 0) errors.Add("No dropoff address");
        if (badDate) errors.Add($"Unreadable date \"{V("date")}\"");
        if (badStart) errors.Add($"Unreadable time \"{startRaw}\"");
        if (badEnd) errors.Add($"Unreadable time \"{endRaw}\"");
        if ((start is not null && end is null && !badEnd) || (end is not null && start is null && !badStart))
            errors.Add("Window needs a start and an end");
        if (start is { } s && end is { } e && e <= s) errors.Add("Window ends before it starts");

        var scheduled = date is not null || start is not null || defaultType == JobType.Scheduled;
        var row = new ImportRow
        {
            Line = index + 2,   // line 1 is the header
            RecipientName = NullIfEmpty(V("recipient_name")),
            RecipientPhone = NullIfEmpty(V("recipient_phone")),
            ExternalId = NullIfEmpty(V("external_id")),
            Notes = NullIfEmpty(V("notes")),
            Type = scheduled ? JobType.Scheduled : JobType.OnDemand,
            ScheduledDate = scheduled ? date ?? defaultDate : null,
            Start = start,
            End = end,
            PickupText = V("pickup_address"),
            DropoffText = dropoffText,
        };
        row.Errors.AddRange(errors);
        return row;
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}
