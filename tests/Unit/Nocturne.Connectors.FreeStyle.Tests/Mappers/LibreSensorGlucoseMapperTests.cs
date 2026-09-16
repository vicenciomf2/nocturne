using System.Globalization;
using FluentAssertions;
using Nocturne.Connectors.FreeStyle.Mappers;
using Nocturne.Connectors.FreeStyle.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;
using Xunit;

namespace Nocturne.Connectors.FreeStyle.Tests.Mappers;

/// <summary>
/// What the connector writes for one LibreLinkUp measurement, and the keys the write is repeated
/// and corrected under.
/// </summary>
public class LibreSensorGlucoseMapperTests
{
    private const string PatientId = "11111111-2222-3333-4444-555555555555";

    /// <summary>
    /// A recent instant, because the mapper discards readings outside the window the graph endpoint
    /// can return. Seconds are trimmed so the vendor's formats round-trip exactly.
    /// </summary>
    private static readonly DateTime Recent = new DateTime(
        DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day,
        DateTime.UtcNow.Hour, 30, 0, DateTimeKind.Utc).AddHours(-2);

    private static readonly string UsFormat =
        Recent.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);

    private static readonly string IsoFormat =
        Recent.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private readonly LibreSensorGlucoseMapper _sut = new();

    /// <summary>
    /// Without a sync identifier the repository's upsert path never runs and the row is
    /// insert-only, so a value the vendor later corrects can never replace the one already stored.
    /// The key is built from the parsed instant rather than the formatted string the vendor sent,
    /// so a change in that formatting cannot split one reading into two.
    /// </summary>
    [Fact]
    public void SyncIdentifier_IsStableAcrossTimestampFormats()
    {
        var us = _sut.ConvertMeasurement(Measurement(UsFormat), PatientId);
        var iso = _sut.ConvertMeasurement(Measurement(IsoFormat), PatientId);

        us!.SyncIdentifier.Should().NotBeNullOrEmpty();
        us.SyncIdentifier.Should().Be(iso!.SyncIdentifier);
    }

    /// <summary>
    /// A caregiver account can follow more than one person, and a tenant reconfigured from one to
    /// another must not have the two collapse onto the same key.
    /// </summary>
    [Fact]
    public void SyncIdentifier_DistinguishesPatients()
    {
        var one = _sut.ConvertMeasurement(Measurement(UsFormat), PatientId);
        var other = _sut.ConvertMeasurement(Measurement(UsFormat), "another-patient");

        one!.SyncIdentifier.Should().NotBe(other!.SyncIdentifier);
    }

    /// <summary>
    /// LegacyId is what every already-stored Libre row is keyed by. Changing its shape would make
    /// the stored history unreachable and re-insert all of it on the next sync, so it is pinned
    /// exactly as it is.
    /// </summary>
    [Fact]
    public void LegacyId_KeepsItsStoredShape()
    {
        var reading = _sut.ConvertMeasurement(Measurement(UsFormat), PatientId);

        reading!.LegacyId.Should().Be($"libre_{UsFormat}");
    }

    [Fact]
    public void DataSource_IsSetSoTheUpsertKeyIsComplete()
    {
        // The (data_source, sync_identifier) index is the upsert key; a null source skips it.
        var reading = _sut.ConvertMeasurement(Measurement(UsFormat), PatientId);

        reading!.DataSource.Should().Be(DataSources.LibreConnector);
    }

    [Theory]
    [InlineData(1, GlucoseDirection.SingleDown)]
    [InlineData(3, GlucoseDirection.Flat)]
    [InlineData(5, GlucoseDirection.SingleUp)]
    [InlineData(0, GlucoseDirection.NotComputable)]
    [InlineData(9, GlucoseDirection.NotComputable)]
    public void TrendArrow_MapsToDirection(int arrow, GlucoseDirection expected)
    {
        var reading = _sut.ConvertMeasurement(
            Measurement(UsFormat, trendArrow: arrow), PatientId);

        reading!.Direction.Should().Be(expected);
    }

    /// <summary>
    /// The shape a month/day misread leaves: a reading from the twelve-hour graph window landing
    /// months away. Storing it would put glucose in a month the sensor was not worn.
    /// </summary>
    [Fact]
    public void AReadingOutsideTheGraphWindow_YieldsNoReading()
    {
        var farPast = Recent.AddMonths(-8)
            .ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture);

        _sut.ConvertMeasurement(Measurement(farPast), PatientId).Should().BeNull();
    }

    [Fact]
    public void AnUnparseableTimestamp_YieldsNoReading()
    {
        _sut.ConvertMeasurement(Measurement("not a timestamp"), PatientId).Should().BeNull();
    }

    private static LibreGlucoseMeasurement Measurement(string timestamp, int trendArrow = 3) =>
        new() { FactoryTimestamp = timestamp, ValueInMgPerDl = 120, TrendArrow = trendArrow };
}
