using FluentAssertions;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Core.Models.Tests.V4;

/// <summary>
/// A CGM reports more than measurements through the glucose field, and the consensus metrics are
/// defined over measurements only. Averaging a warm-up marker in as 9 mg/dL is an hour of severe
/// hypoglycemia that never happened, moving the one metric that most needs to stay conservative.
/// </summary>
public class GlucoseReadingClassifierTests
{
    private static readonly CgmProperties Dexcom = Cgm(min: 40, max: 400);
    private static readonly CgmProperties Libre = Cgm(min: 40, max: 500);
    private static readonly CgmProperties Unknown = Cgm(min: null, max: null);

    [Theory]
    [InlineData(120)]
    [InlineData(40)]
    [InlineData(400)]
    public void OrdinaryReadings_AreMeasurements(double mgdl)
    {
        GlucoseReadingClassifier.Classify(mgdl, Dexcom).Should().Be(GlucoseReadingKind.Value);
    }

    [Fact]
    public void TheWarmUpMarker_IsNotAMeasurement()
    {
        GlucoseReadingClassifier.Classify(9, Dexcom).Should().Be(GlucoseReadingKind.WarmUp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(38)]
    public void ValuesBelowTheLowestNamedNumber_AreDeviceCodes(double mgdl)
    {
        GlucoseReadingClassifier.Classify(mgdl, Dexcom).Should().Be(GlucoseReadingKind.ErrorCode);
    }

    [Fact]
    public void TheLowMarker_IsASentinel()
    {
        GlucoseReadingClassifier.Classify(39, Dexcom).Should().Be(GlucoseReadingKind.LowSentinel);
    }

    [Fact]
    public void AboveTheDeviceCeiling_IsASentinel()
    {
        GlucoseReadingClassifier.Classify(401, Dexcom).Should().Be(GlucoseReadingKind.HighSentinel);
    }

    /// <summary>
    /// The case a single global ceiling gets wrong. A FreeStyle Libre names glucose up to 500, so
    /// 450 from one is a real and clinically urgent reading — discarding it as a marker because a
    /// Dexcom would have stopped at 400 is the failure this parameterisation exists to prevent.
    /// </summary>
    [Fact]
    public void AValueOnlySomeDevicesCanName_IsAMeasurementOnThoseDevices()
    {
        GlucoseReadingClassifier.Classify(450, Libre).Should().Be(GlucoseReadingKind.Value);
        GlucoseReadingClassifier.Classify(450, Dexcom).Should().Be(GlucoseReadingKind.HighSentinel);
    }

    /// <summary>
    /// With no device known, no ceiling is invented. An unknown limit must not become a guessed
    /// one, because the reading it would discard cannot be recovered.
    /// </summary>
    [Theory]
    [InlineData(401)]
    [InlineData(450)]
    [InlineData(600)]
    public void WithNoKnownCeiling_HighValuesStayMeasurements(double mgdl)
    {
        GlucoseReadingClassifier.Classify(mgdl, Unknown).Should().Be(GlucoseReadingKind.Value);
        GlucoseReadingClassifier.Classify(mgdl).Should().Be(GlucoseReadingKind.Value);
    }

    /// <summary>
    /// The markers below the lowest named number are the ecosystem's convention rather than any one
    /// device's, so they hold with no device known.
    /// </summary>
    [Fact]
    public void WithNoKnownDevice_TheEcosystemMarkersStillHold()
    {
        GlucoseReadingClassifier.Classify(9).Should().Be(GlucoseReadingKind.WarmUp);
        GlucoseReadingClassifier.Classify(20).Should().Be(GlucoseReadingKind.ErrorCode);
        GlucoseReadingClassifier.Classify(39).Should().Be(GlucoseReadingKind.LowSentinel);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteValues_AreNeverMeasurements(double mgdl)
    {
        // Postgres orders NaN above every number, so a SQL "> 0" filter passes it downstream.
        GlucoseReadingClassifier.Classify(mgdl, Dexcom).Should().Be(GlucoseReadingKind.ErrorCode);
        GlucoseReadingClassifier.IsMeasurement(mgdl, Dexcom).Should().BeFalse();
    }

    [Fact]
    public void IsMeasurement_AgreesWithClassify()
    {
        foreach (var mgdl in new double[] { 9, 20, 39, 40, 120, 400, 401 })
        {
            GlucoseReadingClassifier.IsMeasurement(mgdl, Dexcom)
                .Should().Be(GlucoseReadingClassifier.Classify(mgdl, Dexcom) == GlucoseReadingKind.Value);
        }
    }

    [Fact]
    public void TheCatalog_GivesAbbottAndDexcomTheirOwnCeilings()
    {
        DeviceCatalog.GetById("libre-2")!.Cgm!.ReportingMaxMgdl.Should().Be(500);
        DeviceCatalog.GetById("dexcom-g7")!.Cgm!.ReportingMaxMgdl.Should().Be(400);
    }

    /// <summary>
    /// A device whose limits nobody established must say so rather than inherit a neighbour's.
    /// </summary>
    [Fact]
    public void TheCatalog_LeavesUnestablishedLimitsUnknown()
    {
        DeviceCatalog.GetById("medtronic-guardian-4")!.Cgm!.ReportingMaxMgdl.Should().BeNull();
    }

    private static CgmProperties Cgm(int? min, int? max) => new()
    {
        SensorDurationDays = 10,
        WarmupMinutes = 60,
        UpdateIntervalMinutes = 5,
        HasSeparateTransmitter = false,
        ReportingMinMgdl = min,
        ReportingMaxMgdl = max,
    };
}
