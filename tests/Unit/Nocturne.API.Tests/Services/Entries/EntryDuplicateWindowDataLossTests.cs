using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Nocturne.API.Services.Entries;
using Nocturne.API.Services.Platform;
using Nocturne.Core.Contracts.Audit;
using Nocturne.Core.Contracts.Entries;
using Nocturne.Core.Contracts.Infrastructure;
using Nocturne.Infrastructure.Data;
using Nocturne.Infrastructure.Data.Entities.V4;
using Nocturne.Infrastructure.Data.Repositories.V4;
using Nocturne.Tests.Shared.Infrastructure;
using Xunit;

namespace Nocturne.API.Tests.Services.Entries;

/// <summary>
/// Characterises what the v1 duplicate window costs, so the trade-off is visible where the rule
/// lives rather than inferred from a support report.
///
/// <para>
/// <c>POST /api/v1/entries</c> probes with <c>windowMinutes: 5</c>
/// (<c>V1/EntriesController.PartitionStoredEntriesAsync</c>), and the rule counts a stored reading
/// as the same reading when the device matches and the value is within 0.01 mg/dL anywhere in
/// ±5 minutes. CGM cadence is also five minutes, so the reading immediately before or after a
/// submitted one sits exactly on the window edge, which
/// <c>EntryReadServiceBatchProbeParityTests.WindowEndsAreInclusive</c> pins as inclusive.
/// </para>
///
/// <para>
/// A glucose series therefore loses every reading whose neighbour five minutes away carries the
/// same integer value — which is what a flat overnight stretch is made of. The write is skipped
/// and the stored neighbour is echoed back in its place, so the uploader is told the reading
/// landed. Nightscout itself dedups on an exact timestamp, not on a value window, so this is
/// stricter than the compatibility target.
/// </para>
///
/// <para>
/// These tests assert today's behaviour. They are not a claim that it is correct: changing it is a
/// product decision about PHI, and the window was inherited from the per-entry SQL probe during a
/// performance refactor rather than chosen on its merits.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class EntryDuplicateWindowDataLossTests : IDisposable
{
    private static readonly Guid TestTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly DateTime Start = new(2025, 1, 15, 2, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestDatabase _db;
    private readonly NocturneDbContext _context;
    private readonly EntryReadService _sut;

    public EntryDuplicateWindowDataLossTests()
    {
        _db = TestDbContextFactory.CreateSqliteWithTenant(TestTenantId, "test");
        _context = _db.CreateContext();

        var factory = new TestTenantDbContextFactory(_context);
        var audit = new Mock<IAuditContext>().Object;
        var demoMode = new Mock<IDemoModeService>();
        demoMode.Setup(d => d.IsEnabled).Returns(false);

        _sut = new EntryReadService(
            new SensorGlucoseRepository(
                factory, new Mock<IDeduplicationService>().Object, audit,
                NullLogger<SensorGlucoseRepository>.Instance),
            new MeterGlucoseRepository(factory, audit, NullLogger<MeterGlucoseRepository>.Instance),
            new CalibrationRepository(factory, audit, NullLogger<CalibrationRepository>.Instance),
            TestDoubles.CanonicalGlucosePassThrough.Create(),
            demoMode.Object,
            NullLogger<EntryReadService>.Instance);
    }

    public void Dispose()
    {
        _context.Dispose();
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The single reading a flat sensor produces next. Nothing about it is a repeat submission:
    /// its timestamp has never been stored.
    /// </summary>
    [Fact]
    public async Task AFreshReadingFollowingAnIdenticalValue_IsCountedAsAlreadyStored()
    {
        SeedSgv(Start, 120, "Dexcom G6");

        var fresh = Probe("Dexcom G6", "sgv", 120, Start.AddMinutes(5));
        var result = await _sut.CheckDuplicatesAsync([fresh], windowMinutes: 5);

        result.Single().Should().NotBeNull(
            "the stored reading five minutes earlier carries the same value and the window is "
            + "inclusive, so a never-before-seen timestamp is classified as already stored");
    }

    /// <summary>
    /// The same reading with the value moved by a single mg/dL is written, which is what makes the
    /// loss intermittent and hard to spot: it tracks how flat the glucose was, not anything about
    /// the uploader.
    /// </summary>
    [Fact]
    public async Task TheSameReadingOneMgdlApart_IsWritten()
    {
        SeedSgv(Start, 120, "Dexcom G6");

        var fresh = Probe("Dexcom G6", "sgv", 121, Start.AddMinutes(5));
        var result = await _sut.CheckDuplicatesAsync([fresh], windowMinutes: 5);

        result.Single().Should().BeNull();
    }

    /// <summary>
    /// The loss is per upload, at the seam between what is stored and what is arriving. A batch is
    /// classified against stored rows only — it does not see its own earlier entries — so of a flat
    /// run only the reading adjacent to the stored history is suppressed. An uploader posting every
    /// five minutes submits exactly that one reading per cycle, which is how a flat stretch is lost
    /// a reading at a time rather than all at once.
    /// </summary>
    [Fact]
    public async Task AcrossAFlatSeam_TheArrivingReadingIsLostAndTheRestAreWritten()
    {
        for (var step = 0; step < 12; step++)
            SeedSgv(Start.AddMinutes(5 * step), 105, "Dexcom G6");

        var arriving = Enumerable.Range(12, 3)
            .Select(step => Probe("Dexcom G6", "sgv", 105, Start.AddMinutes(5 * step)))
            .ToArray();

        var results = await _sut.CheckDuplicatesAsync(arriving, windowMinutes: 5);

        results[0].Should().NotBeNull(
            "it is five minutes after the newest stored reading and carries the same value");
        results.Skip(1).Should().OnlyContain(entry => entry == null,
            "the batch is classified against stored rows only, so readings further out have no "
            + "stored neighbour yet — they are lost on the next upload instead");
    }

    private void SeedSgv(DateTime at, double mgdl, string device)
    {
        _context.SensorGlucose.Add(new SensorGlucoseEntity
        {
            Id = Guid.CreateVersion7(),
            TenantId = TestTenantId,
            Timestamp = at,
            Mgdl = mgdl,
            Device = device,
        });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
    }

    private static EntryDuplicateProbe Probe(string? device, string type, double? sgv, DateTime at) =>
        new(device, type, sgv, new DateTimeOffset(at, TimeSpan.Zero).ToUnixTimeMilliseconds());
}
