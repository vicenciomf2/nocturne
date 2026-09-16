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
        var us = _sut.ConvertMeasurement(Measurement("3/14/2026 3:30:00 PM"), PatientId);
        var iso = _sut.ConvertMeasurement(Measurement("2026-03-14T15:30:00"), PatientId);

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
        var one = _sut.ConvertMeasurement(Measurement("3/14/2026 3:30:00 PM"), PatientId);
        var other = _sut.ConvertMeasurement(Measurement("3/14/2026 3:30:00 PM"), "another-patient");

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
        var reading = _sut.ConvertMeasurement(Measurement("3/14/2026 3:30:00 PM"), PatientId);

        reading!.LegacyId.Should().Be("libre_3/14/2026 3:30:00 PM");
    }

    [Fact]
    public void DataSource_IsSetSoTheUpsertKeyIsComplete()
    {
        // The (data_source, sync_identifier) index is the upsert key; a null source skips it.
        var reading = _sut.ConvertMeasurement(Measurement("3/14/2026 3:30:00 PM"), PatientId);

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
            Measurement("3/14/2026 3:30:00 PM", trendArrow: arrow), PatientId);

        reading!.Direction.Should().Be(expected);
    }

    [Fact]
    public void AnUnparseableTimestamp_YieldsNoReading()
    {
        _sut.ConvertMeasurement(Measurement("not a timestamp"), PatientId).Should().BeNull();
    }

    private static LibreGlucoseMeasurement Measurement(string timestamp, int trendArrow = 3) =>
        new() { FactoryTimestamp = timestamp, ValueInMgPerDl = 120, TrendArrow = trendArrow };
}
