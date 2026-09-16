using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Nocturne.API.Controllers.V1;
using Nocturne.Core.Contracts.Alerts;
using Nocturne.Core.Contracts.Glucose;
using Nocturne.Core.Contracts.Legacy;
using Nocturne.Core.Contracts.V4;
using Nocturne.Core.Models;
using Xunit;

namespace Nocturne.API.Tests.Controllers.V1;

/// <summary>
/// <c>PUT /api/v1/entries/{id}</c> handed the submitted entry straight to the entry service, while
/// <c>POST</c> on the same resource runs it through <c>IDocumentProcessingService</c> first. That
/// processor is what sanitizes the entry's free text and normalizes its timestamp, so an update
/// could write markup and an unnormalized timestamp that a create of the same content could not.
/// </summary>
[Trait("Category", "Unit")]
public class EntryUpdatePreprocessingTests
{
    private const string EntryId = "0123456789abcdef0123456789abcdef";

    private readonly Mock<IEntryService> _entries = new();
    private readonly Mock<IDocumentProcessingService> _processing = new();
    private readonly EntriesController _controller;

    public EntryUpdatePreprocessingTests()
    {
        _controller = new EntriesController(
            _entries.Object,
            _processing.Object,
            new Mock<IProcessingStatusService>().Object,
            new Mock<ICanonicalAlertEvaluator>().Object,
            new Mock<ILogger<EntriesController>>().Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        _entries
            .Setup(service => service.UpdateEntryAsync(
                It.IsAny<string>(), It.IsAny<Entry>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Entry entry, CancellationToken _) => entry);
    }

    [Fact]
    public async Task UpdateEntry_RunsTheEntryThroughDocumentProcessing()
    {
        _processing
            .Setup(processor => processor.ProcessEntry(It.IsAny<Entry>()))
            .Returns((Entry entry) => entry);

        await _controller.UpdateEntry(EntryId, new Entry { Sgv = 120, Notes = "note" });

        _processing.Verify(processor => processor.ProcessEntry(It.IsAny<Entry>()), Times.Once);
    }

    /// <summary>
    /// The processed entry, not the submitted one, is what must reach the store — otherwise the
    /// processor runs and its result is discarded.
    /// </summary>
    [Fact]
    public async Task UpdateEntry_StoresWhatTheProcessorReturned()
    {
        _processing
            .Setup(processor => processor.ProcessEntry(It.IsAny<Entry>()))
            .Returns((Entry entry) =>
            {
                entry.Notes = "sanitized";
                return entry;
            });

        await _controller.UpdateEntry(
            EntryId, new Entry { Sgv = 120, Notes = "<script>alert(1)</script>" });

        _entries.Verify(
            service => service.UpdateEntryAsync(
                EntryId,
                It.Is<Entry>(entry => entry.Notes == "sanitized"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The id still comes from the route, not from the body: processing must not reopen the
    /// mismatch the endpoint closes.
    /// </summary>
    [Fact]
    public async Task UpdateEntry_KeepsTheRouteId()
    {
        _processing
            .Setup(processor => processor.ProcessEntry(It.IsAny<Entry>()))
            .Returns((Entry entry) => entry);

        await _controller.UpdateEntry(
            EntryId, new Entry { Id = "ffffffffffffffffffffffff", Sgv = 120 });

        _entries.Verify(
            service => service.UpdateEntryAsync(
                EntryId, It.Is<Entry>(entry => entry.Id == EntryId), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
