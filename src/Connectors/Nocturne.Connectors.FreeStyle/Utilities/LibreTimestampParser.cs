using System.Globalization;

namespace Nocturne.Connectors.FreeStyle.Utilities;

/// <summary>
///     Timestamp parsing utilities for LibreLinkUp API formats.
/// </summary>
public static class LibreTimestampParser
{
    /// <summary>
    ///     How far ahead of the poller's clock a reading may sit. Clocks drift between the vendor
    ///     and the host; a reading further ahead than this did not come from the graph window.
    /// </summary>
    private static readonly TimeSpan MaxAhead = TimeSpan.FromHours(6);

    /// <summary>
    ///     How far behind a reading may sit. The connector reads a rolling window about twelve
    ///     hours wide, so the bound is generous by two orders of magnitude and still catches the
    ///     month/day ambiguity below, which displaces a reading by whole months.
    /// </summary>
    private static readonly TimeSpan MaxBehind = TimeSpan.FromDays(30);

    /// <summary>
    ///     The formats LibreLinkUp is known to send, month-first before day-first.
    /// </summary>
    /// <remarks>
    ///     The two are mutually ambiguous whenever the day is 12 or less — "3/4/2026" is the 4th of
    ///     March read one way and the 3rd of April read the other — and nothing in the payload says
    ///     which was meant. Order alone decides it, so a day-first account is misread for eleven
    ///     twelfths of the calendar. Nothing here can resolve that from a single value;
    ///     <see cref="IsPlausible"/> is what stops a misread reading being stored, because the
    ///     misreading lands months from the window the connector actually fetched.
    /// </remarks>
    private static readonly string[] Formats =
    [
        "M/d/yyyy h:mm:ss tt",
        "M/d/yyyy h:mm tt",
        "M/d/yyyy H:mm:ss",
        "M/d/yyyy H:mm",
        "d/M/yyyy H:mm:ss",
        "d/M/yyyy H:mm",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
    ];

    /// <summary>
    ///     Parses a LibreLinkUp <c>FactoryTimestamp</c>, the vendor's UTC field.
    /// </summary>
    /// <param name="value">The timestamp string from the LibreLinkUp API</param>
    /// <returns>The instant, in UTC</returns>
    /// <exception cref="FormatException">The value matches none of the known formats</exception>
    public static DateTime Parse(string value)
    {
        // Invariant only: a culture-sensitive fallback made the same payload parse to different
        // instants on two deployments of the same image, which is the one failure that cannot be
        // reproduced from the data that caused it.
        if (DateTime.TryParseExact(
                value,
                Formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
            return parsed;

        throw new FormatException($"Invalid LibreLinkUp timestamp: {value}");
    }

    /// <summary>
    ///     Whether a parsed timestamp could have come from the window the connector reads. The
    ///     graph endpoint returns roughly the last twelve hours, so a reading months away is the
    ///     month/day ambiguity resolved the wrong way rather than data.
    /// </summary>
    public static bool IsPlausible(DateTime timestamp, DateTime now) =>
        timestamp <= now + MaxAhead && timestamp >= now - MaxBehind;
}
