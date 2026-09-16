using System.Text.Json.Serialization;

namespace Nocturne.Core.Models.V4;

/// <summary>
/// What a stored glucose number actually is. A CGM reports more than measurements through the same
/// field: a device at or past its reporting limits sends a marker rather than a number, and some
/// send codes for states that are not glucose at all.
/// </summary>
/// <remarks>
/// <para>
/// The distinction matters because the consensus metrics are defined over measurements. A warm-up
/// marker averaged in as 9 mg/dL is an hour of severe hypoglycemia that never happened, and it
/// moves time-below-range — the metric clinicians most need to be conservative about — in the
/// dangerous direction.
/// </para>
/// <para>
/// The numbering follows Nightscout's own convention, which every uploader in the ecosystem already
/// writes: 9 marks warm-up, values below 39 are device error codes, 39 renders as LOW and anything
/// past the device's ceiling renders as HIGH.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<GlucoseReadingKind>))]
public enum GlucoseReadingKind
{
    /// <summary>A glucose measurement: the only kind the consensus metrics are defined over.</summary>
    Value,

    /// <summary>The device reported glucose below the lowest number it will name.</summary>
    LowSentinel,

    /// <summary>The device reported glucose above the highest number it will name.</summary>
    HighSentinel,

    /// <summary>A device status code carried in the glucose field; not a measurement.</summary>
    ErrorCode,

    /// <summary>The sensor is warming up and is not yet reporting glucose.</summary>
    WarmUp,
}
