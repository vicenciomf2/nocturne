using FluentAssertions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// Completeness is the fraction of the readings a sensor was expected to produce that actually
/// arrived, and the consensus 70% gate is read off it. The expectation therefore has to come from
/// the cadence the sensor actually reports at: a FreeStyle Libre series pulled from the vendor's
/// 15-minute history produces 96 readings on a flawless day, and scoring it against a
/// five-minute assumption reports a third of that as the coverage.
/// </summary>
public class DataSufficiencyCadenceTests
{
    private readonly StatisticsService _sut = new();

    [Theory]
    [InlineData(5.0)]
    [InlineData(15.0)]
    [InlineData(1.0)]
    public void AssessDataSufficiency_WithCompleteWear_ReportsFullCoverageAtAnyCadence(
        double cadenceMinutes)
    {
        var readings = Series(days: 14, cadenceMinutes: cadenceMinutes);

        var assessment = _sut.AssessDataSufficiency(readings, days: 14);

        assessment.CompletenessPercentage.Should().BeApproximately(100, 2);
        assessment.IsSufficient.Should().BeTrue();
    }

    /// <summary>
    /// Uniformly dropping every other reading is indistinguishable, from the series alone, from a
    /// sensor that reports half as often — and the consensus measure is elapsed time covered, not
    /// readings counted, so the honest answer is that the period is still covered. This is pinned
    /// so that a future change back to a reading-count denominator has to argue with it: the
    /// property that must hold is the one below, that a stretch with no readings is uncovered.
    /// </summary>
    [Fact]
    public void AssessDataSufficiency_WithUniformlyThinnedReadings_StillReportsThePeriodCovered()
    {
        var readings = Series(days: 14, cadenceMinutes: 5)
            .Where((_, index) => index % 2 == 0)
            .ToList();

        var assessment = _sut.AssessDataSufficiency(readings, days: 14);

        assessment.CompletenessPercentage.Should().BeApproximately(100, 2);
    }

    /// <summary>
    /// A contiguous stretch with no readings is the shape that must reduce coverage, because it is
    /// the one the 70% gate exists to catch.
    /// </summary>
    [Theory]
    [InlineData(4, 70)]
    [InlineData(7, 50)]
    public void AssessDataSufficiency_WithContiguousDaysMissing_ReportsProportionalCoverage(
        int missingDays, double expectedCoverage)
    {
        var readings = Series(days: 14, cadenceMinutes: 5)
            .Where(reading => reading.Timestamp.Day > missingDays)
            .ToList();

        var assessment = _sut.AssessDataSufficiency(readings, days: 14);

        assessment.CompletenessPercentage.Should().BeApproximately(expectedCoverage, 6);
    }

    /// <summary>
    /// A wholly absent day is the shape a sensor change or a dead uploader leaves, and it must not
    /// be absorbed by the median interval.
    /// </summary>
    [Fact]
    public void AssessDataSufficiency_WithAWholeDayMissing_DropsBelowFullCoverage()
    {
        var readings = Series(days: 14, cadenceMinutes: 5)
            .Where(reading => reading.Timestamp.Day != 7)
            .ToList();

        var assessment = _sut.AssessDataSufficiency(readings, days: 14);

        assessment.CompletenessPercentage.Should().BeLessThan(95);
    }

    [Fact]
    public void AssessDataSufficiency_WithAnExplicitExpectation_HonoursIt()
    {
        var readings = Series(days: 1, cadenceMinutes: 15);

        var assessment = _sut.AssessDataSufficiency(
            readings, days: 1, expectedReadingsPerDay: 96);

        assessment.ExpectedReadings.Should().Be(96);
        assessment.CompletenessPercentage.Should().BeApproximately(100, 2);
    }

    private static List<SensorGlucose> Series(int days, double cadenceMinutes)
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var step = TimeSpan.FromMinutes(cadenceMinutes);
        var count = (int)(days * 24 * 60 / cadenceMinutes);

        return [.. Enumerable.Range(0, count).Select(index => new SensorGlucose
        {
            Id = Guid.CreateVersion7(),
            Timestamp = start + step * index,
            Mgdl = 120,
        })];
    }
}
