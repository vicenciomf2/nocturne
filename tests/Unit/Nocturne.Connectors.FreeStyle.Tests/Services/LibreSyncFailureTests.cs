using System.Net;
using FluentAssertions;
using Nocturne.Connectors.Core.Models;
using Xunit;

namespace Nocturne.Connectors.FreeStyle.Tests.Services;

/// <summary>
/// A sync that fetched nothing because it could not authenticate, could not list the followed
/// patients, or could not identify one must report failure. The poller writes
/// <c>isHealthy: true</c> and clears the stored error off a successful <see cref="SyncResult"/>,
/// and a publish of zero records is recorded as a success, so a run that returns an empty list
/// with <see cref="SyncResult.Success"/> still set leaves the tenant looking at a healthy
/// connector that is ingesting nothing.
/// </summary>
public class LibreSyncFailureTests
{
    private static readonly SyncRequest GlucoseRequest = new()
    {
        DataTypes = [SyncDataType.Glucose],
    };

    [Fact]
    public async Task Sync_WhenLoginIsRejected_ReportsFailure()
    {
        var (service, _) = LibreTestHarness.Build(_ =>
            (HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}"""));

        var result = await service.SyncDataAsync(
            GlucoseRequest, LibreTestHarness.Config(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Sync_WhenNoPatientIsShared_ReportsFailure()
    {
        var (service, _) = LibreTestHarness.Build(request =>
            request.RequestUri!.AbsolutePath.Contains("/auth/login")
                ? (HttpStatusCode.OK, LibreTestHarness.LoginOk())
                : (HttpStatusCode.OK, LibreTestHarness.ConnectionsEmpty()));

        var result = await service.SyncDataAsync(
            GlucoseRequest, LibreTestHarness.Config(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Sync_WhenConnectionsCannotBeListed_ReportsFailure()
    {
        var (service, _) = LibreTestHarness.Build(request =>
            request.RequestUri!.AbsolutePath.Contains("/auth/login")
                ? (HttpStatusCode.OK, LibreTestHarness.LoginOk())
                : (HttpStatusCode.InternalServerError, """{"message":"upstream"}"""));

        var result = await service.SyncDataAsync(
            GlucoseRequest, LibreTestHarness.Config(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Errors.Should().NotBeEmpty();
    }

    /// <summary>
    /// A caregiver following several people who mistypes a patient id must not silently receive
    /// somebody else's glucose. The configured id is a filter, so failing to match it is a
    /// configuration error, not an invitation to fall back to the first connection.
    /// </summary>
    [Fact]
    public async Task Sync_WhenConfiguredPatientIsNotAmongTheConnections_ReportsFailure()
    {
        var (service, handler) = LibreTestHarness.Build(request =>
            request.RequestUri!.AbsolutePath.Contains("/auth/login")
                ? (HttpStatusCode.OK, LibreTestHarness.LoginOk())
                : (HttpStatusCode.OK, LibreTestHarness.ConnectionsWith("someone-else")));

        var result = await service.SyncDataAsync(
            GlucoseRequest,
            LibreTestHarness.Config(patientId: "the-patient-i-meant"),
            CancellationToken.None);

        result.Success.Should().BeFalse();
        handler.Requests.Should().NotContain(path => path.Contains("someone-else"));
    }
}
