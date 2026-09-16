// scripts/export-glucose.cs
//
// Exports a tenant's sensor glucose as CSV, with every reading labelled by what it actually is
// and a coverage summary for the window.
//
// The point of the script is that the labelling is not reimplemented here. It calls
// GlucoseReadingClassifier from Nocturne.Core.Models — the same code the API applies when it
// computes statistics — so an analysis built on this export and a figure shown in the app cannot
// disagree about which numbers were measurements. Anything that re-derives "is this glucose?"
// downstream is how the two drift apart.
//
// Usage:
//   dotnet run scripts/export-glucose.cs --token <token> --url https://slug.example.org
//   dotnet run scripts/export-glucose.cs --token <token> --from 2026-01-01 --to 2026-03-01
//   dotnet run scripts/export-glucose.cs --token <token> --out readings.csv
//   dotnet run scripts/export-glucose.cs --token <token> --include-markers
//
// Options:
//   --token   Bearer token or api-secret for the tenant. Required.
//             With a Nocturne noc_ token this is sent as Authorization: Bearer.
//   --url     Tenant base URL (default: http://localhost:1610, the dev AppHost port).
//   --from    Inclusive ISO date/time. Default: 90 days before --to.
//   --to      Exclusive ISO date/time. Default: now.
//   --out     CSV path. Default: stdout.
//   --device  Only readings from this device.
//   --include-markers
//             Emit warm-up, device-code and sentinel rows too. They are labelled either way;
//             by default only measurements are written, because that is what the consensus
//             metrics are defined over.
//
// Exit codes: 0 export written, 1 usage or transport error.

#:project ../src/Core/Nocturne.Core.Models/Nocturne.Core.Models.csproj

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nocturne.Core.Models.V4;

const int PageSize = 1000;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var token = Option("--token");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("--token is required. See the header of this file for usage.");
    return 1;
}

var baseUrl = Option("--url") ?? "http://localhost:1610";
var to = ParseBound(Option("--to")) ?? DateTime.UtcNow;
var from = ParseBound(Option("--from")) ?? to.AddDays(-90);
var device = Option("--device");
var outPath = Option("--out");
var includeMarkers = args.Contains("--include-markers");

if (from >= to)
{
    Console.Error.WriteLine($"--from ({from:O}) must be before --to ({to:O}).");
    return 1;
}

using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
// A noc_ token authenticates as a bearer; anything else is a legacy pre-hashed secret, which the
// api-secret header is what accepts.
if (token.StartsWith("noc_", StringComparison.Ordinal))
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
else
    http.DefaultRequestHeaders.Add("api-secret", token);

var readings = new List<Reading>();
var offset = 0;

while (true)
{
    var url = $"/api/v4/glucose/sensor?from={Uri.EscapeDataString(from.ToString("O"))}"
              + $"&to={Uri.EscapeDataString(to.ToString("O"))}"
              + $"&limit={PageSize}&offset={offset}&sort=timestamp_asc"
              + (device is null ? "" : $"&device={Uri.EscapeDataString(device)}");

    HttpResponseMessage response;
    try
    {
        response = await http.GetAsync(url);
    }
    catch (HttpRequestException ex)
    {
        Console.Error.WriteLine($"Could not reach {baseUrl}: {ex.Message}");
        return 1;
    }

    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine(
            $"GET {url} returned {(int)response.StatusCode} {response.StatusCode}. "
            + (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? "Check --token, and that --url names the tenant's own subdomain."
                : await response.Content.ReadAsStringAsync()));
        return 1;
    }

    var page = JsonSerializer.Deserialize<Page>(
        await response.Content.ReadAsStringAsync(),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    var items = page?.Data ?? [];
    readings.AddRange(items);
    Console.Error.Write($"\r  fetched {readings.Count} readings");

    if (items.Count < PageSize) break;
    offset += PageSize;
}

Console.Error.WriteLine();

if (readings.Count == 0)
{
    Console.Error.WriteLine($"No readings between {from:u} and {to:u}.");
    return 0;
}

// The device's own reporting limits decide which numbers are markers, and they differ: a Libre
// names glucose up to 500 where a Dexcom stops at 400. Without a known device the classifier
// applies no ceiling rather than guessing one.
var cgm = LookupCgm(readings);

var labelled = readings
    .OrderBy(reading => reading.Timestamp)
    .Select(reading => (Reading: reading, Kind: GlucoseReadingClassifier.Classify(reading.Mgdl, cgm)))
    .ToList();

var measurements = labelled.Where(row => row.Kind == GlucoseReadingKind.Value).ToList();

var writer = outPath is null
    ? Console.Out
    : new StreamWriter(outPath, append: false, Encoding.UTF8);

try
{
    writer.WriteLine("timestamp_utc,mills,mgdl,reading_kind,device,data_source");

    foreach (var (reading, kind) in labelled)
    {
        if (!includeMarkers && kind != GlucoseReadingKind.Value) continue;

        writer.WriteLine(string.Join(',',
            reading.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            new DateTimeOffset(reading.Timestamp.ToUniversalTime()).ToUnixTimeMilliseconds(),
            reading.Mgdl.ToString(CultureInfo.InvariantCulture),
            kind,
            Csv(reading.Device),
            Csv(reading.DataSource)));
    }
}
finally
{
    if (writer is StreamWriter stream) stream.Dispose();
}

ReportCoverage(labelled, measurements, from, to, cgm, outPath);
return 0;

// Coverage is elapsed time covered rather than readings counted, which is what the consensus
// "% time CGM active" means. Without it the export is a pile of rows with no way to tell a
// complete fortnight from a third of one.
void ReportCoverage(
    List<(Reading Reading, GlucoseReadingKind Kind)> all,
    List<(Reading Reading, GlucoseReadingKind Kind)> kept,
    DateTime windowStart, DateTime windowEnd,
    CgmProperties? properties, string? destination)
{
    var cadence = properties?.UpdateIntervalMinutes is { } known and > 0
        ? known
        : MedianIntervalMinutes(kept.Select(row => row.Reading).ToList());

    var windowMinutes = (windowEnd - windowStart).TotalMinutes;
    var coverage = windowMinutes > 0
        ? Math.Min(kept.Count * cadence / windowMinutes * 100, 100)
        : 0;

    Console.Error.WriteLine();
    Console.Error.WriteLine($"  window          {windowStart:u} .. {windowEnd:u}");
    Console.Error.WriteLine($"  readings        {all.Count}");
    Console.Error.WriteLine($"  measurements    {kept.Count}");

    foreach (var group in all.Where(row => row.Kind != GlucoseReadingKind.Value)
                 .GroupBy(row => row.Kind).OrderBy(group => group.Key.ToString()))
        Console.Error.WriteLine($"    {group.Key,-14}{group.Count()}");

    Console.Error.WriteLine(
        $"  cadence         {cadence:0.#} min"
        + (properties?.UpdateIntervalMinutes is > 0 ? " (device)" : " (median interval)"));
    Console.Error.WriteLine($"  coverage        {coverage:0.#}%");

    if (coverage < 70)
    {
        Console.Error.WriteLine(
            "  NOTE            below the 70% the 2019 consensus asks for before CGM metrics are");
        Console.Error.WriteLine(
            "                  reported. Time-below-range and variability degrade first.");
    }

    if (destination is not null) Console.Error.WriteLine($"  written to      {destination}");
}

static double MedianIntervalMinutes(List<Reading> ordered)
{
    var gaps = new List<double>();
    for (var index = 1; index < ordered.Count; index++)
    {
        var minutes = (ordered[index].Timestamp - ordered[index - 1].Timestamp).TotalMinutes;
        if (minutes > 0) gaps.Add(minutes);
    }

    if (gaps.Count == 0) return 5;

    gaps.Sort();
    return gaps[gaps.Count / 2];
}

// The catalogue entry for the device the readings came from, when they agree on one. Readings from
// several devices have no single set of limits, so none is applied.
static CgmProperties? LookupCgm(List<Reading> readings)
{
    var names = readings
        .Select(reading => reading.Device)
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    if (names.Count != 1) return null;

    return DeviceCatalog.GetAll()
        .FirstOrDefault(entry =>
            entry.Cgm is not null
            && (names[0]!.Contains(entry.Id, StringComparison.OrdinalIgnoreCase)
                || names[0]!.Contains(entry.Name, StringComparison.OrdinalIgnoreCase)))
        ?.Cgm;
}

static DateTime? ParseBound(string? value) =>
    value is null
        ? null
        : DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : throw new ArgumentException($"Not a date: {value}");

static string Csv(string? value) =>
    value is null ? "" :
    value.Contains(',') || value.Contains('"')
        ? $"\"{value.Replace("\"", "\"\"")}\""
        : value;

internal sealed record Page
{
    [JsonPropertyName("data")] public List<Reading> Data { get; init; } = [];
}

internal sealed record Reading
{
    [JsonPropertyName("timestamp")] public DateTime Timestamp { get; init; }
    [JsonPropertyName("mgdl")] public double Mgdl { get; init; }
    [JsonPropertyName("device")] public string? Device { get; init; }
    [JsonPropertyName("dataSource")] public string? DataSource { get; init; }
}
