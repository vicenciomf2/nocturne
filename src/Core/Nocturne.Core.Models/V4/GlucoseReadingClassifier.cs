namespace Nocturne.Core.Models.V4;

/// <summary>
/// Decides what a stored glucose number is, from the number and the reporting limits of the device
/// that produced it.
/// </summary>
/// <remarks>
/// <para>
/// Pure and allocation-free by design, so it can run on a write path inside a retry scope and give
/// the same answer every time — the same shape as <see cref="CanonicalGlucoseStream"/> and the
/// sensor integrity detector.
/// </para>
/// <para>
/// It classifies rather than clamps. A discarded reading is unrecoverable and its threshold cannot
/// be revisited; a reading kept with the wrong label is a later one-line change. That asymmetry is
/// the whole argument for this being a label and not a filter.
/// </para>
/// </remarks>
public static class GlucoseReadingClassifier
{
    /// <summary>
    /// Nightscout's warm-up marker. Every uploader in the ecosystem writes it, and it is not a
    /// glucose value.
    /// </summary>
    public const double WarmUpSentinel = 9;

    /// <summary>
    /// The lowest number a CGM in this ecosystem will name. Below it, Dexcom encodes device status
    /// codes rather than glucose.
    /// </summary>
    public const double LowestNamedValue = 39;

    /// <summary>
    /// Classifies a reading. <paramref name="cgm"/> supplies the device's own reporting limits;
    /// pass null when the device is unknown.
    /// </summary>
    /// <remarks>
    /// An unknown ceiling means no ceiling is applied. Assuming one would turn a real severe
    /// hyperglycemia on a device that reports past it — a FreeStyle Libre names up to 500 where a
    /// Dexcom stops at 400 — into a sentinel, which is exactly the misreading this type exists to
    /// prevent.
    /// </remarks>
    public static GlucoseReadingKind Classify(double mgdl, CgmProperties? cgm = null)
    {
        if (double.IsNaN(mgdl) || double.IsInfinity(mgdl)) return GlucoseReadingKind.ErrorCode;

        if (mgdl == WarmUpSentinel) return GlucoseReadingKind.WarmUp;

        if (mgdl < LowestNamedValue) return GlucoseReadingKind.ErrorCode;

        var floor = cgm?.ReportingMinMgdl ?? LowestNamedValue + 1;
        if (mgdl < floor) return GlucoseReadingKind.LowSentinel;

        if (cgm?.ReportingMaxMgdl is { } ceiling && mgdl > ceiling)
            return GlucoseReadingKind.HighSentinel;

        return GlucoseReadingKind.Value;
    }

    /// <summary>
    /// Whether a reading is a measurement, and so belongs in the consensus metrics.
    /// </summary>
    public static bool IsMeasurement(double mgdl, CgmProperties? cgm = null) =>
        Classify(mgdl, cgm) == GlucoseReadingKind.Value;
}
