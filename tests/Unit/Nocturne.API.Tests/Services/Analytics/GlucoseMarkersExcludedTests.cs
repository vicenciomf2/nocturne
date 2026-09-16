using FluentAssertions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// The consensus metrics are defined over glucose measurements. A CGM also reports markers through
/// the same field — 9 for warm-up, codes below 39 for device states, 39 for "LOW" — and counting
/// those as glucose puts severe hypoglycemia into the record that the person never experienced.
/// Time below range is the metric clinicians act on most urgently, so this is the direction that
/// matters.
/// </summary>
[Trait("Category", "Unit")]
public class GlucoseMarkersExcludedTests
{
    private readonly StatisticsService _sut = new();

    [Theory]
    [InlineData(9)]     // warm-up marker
    [InlineData(1)]     // device status code
    [InlineData(38)]    // device status code
    public void Markers_DoNotCountAsTimeBelowRange(double marker)
    {
        var readings = Series(inRange: 100, marker: marker, markerCount: 48);

        var analytics = _sut.AnalyzeGlucoseData(readings, [], []);

        analytics.TimeInRange.Percentages.VeryLow.Should().Be(0);
        analytics.TimeInRange.Percentages.Low.Should().Be(0);
    }

    [Fact]
    public void Markers_DoNotDragTheMeanDown()
    {
        var withMarkers = Series(inRange: 100, marker: 9, markerCount: 48);
        var withoutMarkers = Series(inRange: 100, marker: null, markerCount: 0);

        var withMean = _sut.AnalyzeGlucoseData(withMarkers, [], []).BasicStats.Mean;
        var withoutMean = _sut.AnalyzeGlucoseData(withoutMarkers, [], []).BasicStats.Mean;

        withMean.Should().BeApproximately(withoutMean, 0.01);
    }

    /// <summary>
    /// The LOW marker is a real hypoglycemic episode the device declined to put a number on, but it
    /// is not the number 39 — averaging it in understates the mean and overstates how precisely the
    /// low is known. It is excluded here for the same reason as the other markers; representing it
    /// as a censored observation is a separate piece of work.
    /// </summary>
    [Fact]
    public void TheLowMarker_IsNotCountedAsTheNumber39()
    {
        var readings = Series(inRange: 100, marker: 39, markerCount: 48);

        var analytics = _sut.AnalyzeGlucoseData(readings, [], []);

        analytics.TimeInRange.Percentages.VeryLow.Should().Be(0);
    }

    /// <summary>
    /// Guards against the exclusion being made by widening it: real hypoglycemia must still be
    /// counted, or the change would hide exactly what it is meant to protect.
    /// </summary>
    [Fact]
    public void RealHypoglycemia_IsStillCounted()
    {
        // 60 mg/dL is the consensus level-1 low band (54-70), just above the very-low threshold.
        var readings = Series(inRange: 100, marker: 60, markerCount: 48);

        var analytics = _sut.AnalyzeGlucoseData(readings, [], []);

        analytics.TimeInRange.Percentages.Low.Should().BeGreaterThan(0);
    }

    [Fact]
    public void RealReadingsAtTheDeviceFloor_AreStillCounted()
    {
        var readings = Series(inRange: 100, marker: 40, markerCount: 48);

        var analytics = _sut.AnalyzeGlucoseData(readings, [], []);

        analytics.TimeInRange.Percentages.VeryLow.Should().BeGreaterThan(0);
    }

    private static List<SensorGlucose> Series(double inRange, double? marker, int markerCount)
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var readings = new List<SensorGlucose>();

        for (var index = 0; index < 48; index++)
            readings.Add(Reading(start.AddMinutes(5 * index), inRange));

        for (var index = 0; index < markerCount && marker.HasValue; index++)
            readings.Add(Reading(start.AddMinutes(5 * (48 + index)), marker.Value));

        return readings;
    }

    private static SensorGlucose Reading(DateTime at, double mgdl) => new()
    {
        Id = Guid.CreateVersion7(),
        Timestamp = at,
        Mgdl = mgdl,
    };
}
