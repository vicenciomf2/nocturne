using FluentAssertions;
using Nocturne.API.Services.Analytics;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.API.Tests.Services.Analytics;

/// <summary>
/// <c>DataQuality.NoiseLevel</c> was returned as a literal zero while every reading carries the
/// signal noise the uploader reported, so a series of heavily noisy readings was reported as having
/// none. Zero is also the value that means "no noise", which is why the placeholder read as a
/// finding rather than as a gap.
/// </summary>
[Trait("Category", "Unit")]
public class DataQualityNoiseLevelTests
{
    private readonly StatisticsService _sut = new();

    [Theory]
    [InlineData(1, 0.25)]
    [InlineData(2, 0.5)]
    [InlineData(4, 1.0)]
    public void NoiseLevel_NormalisesTheReportedScale(int noise, double expected)
    {
        var quality = Assess(Series(noise));

        quality.NoiseLevel.Should().BeApproximately(expected, 0.01);
    }

    [Fact]
    public void NoiseLevel_WithACleanSeries_IsZero()
    {
        var quality = Assess(Series(0));

        quality.NoiseLevel.Should().Be(0);
    }

    /// <summary>
    /// A reading that reports no noise value is unknown, not clean. Counting it as zero would pull
    /// the average of a noisy series down in proportion to how many uploaders omit the field.
    /// </summary>
    [Fact]
    public void NoiseLevel_IgnoresReadingsThatReportNoNoise()
    {
        var readings = Series(4);
        foreach (var reading in readings.Take(readings.Count / 2))
            reading.Noise = null;

        var quality = Assess(readings);

        quality.NoiseLevel.Should().BeApproximately(1.0, 0.01);
    }

    [Fact]
    public void NoiseLevel_WithNoReadingReportingNoise_IsZero()
    {
        var quality = Assess(Series(null));

        quality.NoiseLevel.Should().Be(0);
    }

    private DataQuality Assess(List<SensorGlucose> readings) =>
        _sut.AnalyzeGlucoseData(readings, [], []).DataQuality;

    private static List<SensorGlucose> Series(int? noise)
    {
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        return [.. Enumerable.Range(0, 48).Select(index => new SensorGlucose
        {
            Id = Guid.CreateVersion7(),
            Timestamp = start.AddMinutes(5 * index),
            Mgdl = 120,
            Noise = noise,
        })];
    }
}
