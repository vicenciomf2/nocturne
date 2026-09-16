using FluentAssertions;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Services.Legacy;

/// <summary>
/// <c>notes</c> is free text an uploader chooses, so it is the field on an entry most likely to
/// carry markup. <see cref="Treatment.Notes"/> is already marked
/// <see cref="SanitizableAttribute"/>; the same field on <see cref="Entry"/> was not, and
/// <c>DocumentProcessingService.ProcessEntry</c> sanitizes exactly what the attribute names — so
/// entry notes reached storage unsanitized on the one ingest path that sanitizes at all.
/// </summary>
[Trait("Category", "Unit")]
public class EntryNotesSanitizationTests
{
    [Fact]
    public void EntryNotes_IsSanitizable()
    {
        var fields = new Entry { Notes = "anything" }.GetSanitizableFields();

        fields.Should().ContainKey(nameof(Entry.Notes));
    }

    /// <summary>
    /// Pins the pairing rather than the single field: the two models carry the same free-text note
    /// through the same processor, and only one of them being sanitized is the defect.
    /// </summary>
    [Fact]
    public void EntryAndTreatmentNotes_AreSanitizedAlike()
    {
        var entryFields = new Entry { Notes = "anything" }.GetSanitizableFields();
        var treatmentFields = new Treatment { Notes = "anything" }.GetSanitizableFields();

        entryFields.ContainsKey(nameof(Entry.Notes))
            .Should().Be(treatmentFields.ContainsKey(nameof(Treatment.Notes)));
    }

    [Fact]
    public void EntryNotes_KeepsTheFieldsThatWereAlreadySanitized()
    {
        // Guards against the fix being made by widening the attribute's reach rather than by
        // marking the field: Type and Device must still be named.
        var fields = new Entry { Type = "sgv", Device = "xdrip", Notes = "n" }.GetSanitizableFields();

        fields.Should().ContainKeys(nameof(Entry.Type), nameof(Entry.Device));
    }
}
