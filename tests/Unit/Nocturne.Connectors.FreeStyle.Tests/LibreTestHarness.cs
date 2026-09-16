using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Services;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.FreeStyle.Tests;

/// <summary>
/// Builds a <see cref="LibreConnectorService"/> over a scripted transport, so a test can state the
/// LibreLinkUp responses it wants and assert on the <see cref="SyncResult"/> that comes back.
/// Delays are stubbed out: the production strategies sleep for minutes.
/// </summary>
internal static class LibreTestHarness
{
    internal const string PatientId = "11111111-2222-3333-4444-555555555555";

    /// <summary>
    /// A login response carrying a token whose <c>id</c> claim the service hashes into the
    /// Account-Id header. The payload is an unsigned JWT: the service reads it with
    /// <c>JwtSecurityTokenHandler</c>, which does not validate signatures here.
    /// </summary>
    internal static string LoginOk()
    {
        var header = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Base64Url("{\"id\":\"" + PatientId + "\",\"exp\":4102444800}");
        return "{\"data\":{\"authTicket\":{\"token\":\"" + header + "." + payload
               + ".\",\"expires\":4102444800}}}";
    }

    internal static string ConnectionsWith(params string[] patientIds)
    {
        var entries = patientIds.Select(id =>
            "{\"patientId\":\"" + id + "\",\"firstName\":\"Ada\",\"lastName\":\"Lovelace\"}");
        return "{\"data\":[" + string.Join(",", entries) + "]}";
    }

    internal static string ConnectionsEmpty() => """{"data":[]}""";

    internal static (LibreConnectorService Service, ScriptedHandler Handler) Build(
        Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond)
    {
        var handler = new ScriptedHandler(respond);
        var resolver = new ConnectorServerResolver<LibreLinkUpConnectorConfiguration>(
            new Dictionary<string, string> { ["EU"] = LibreLinkUpConstants.Endpoints.Eu },
            config => ((LibreLinkUpConnectorConfiguration)config).Region,
            LibreLinkUpConstants.Endpoints.Eu);

        var tokenProvider = new LibreLinkAuthTokenProvider(
            new HttpClient(handler, disposeHandler: false),
            new InMemoryTokenCache(),
            resolver,
            new FixedTenantAccessor(),
            NullLogger<LibreLinkAuthTokenProvider>.Instance,
            new NoDelay());

        var service = new LibreConnectorService(
            new HttpClient(handler, disposeHandler: false),
            resolver,
            NullLogger<LibreConnectorService>.Instance,
            new NoDelay(),
            new NoDelay(),
            tokenProvider);

        return (service, handler);
    }

    internal static LibreLinkUpConnectorConfiguration Config(string? patientId = null) => new()
    {
        Username = "ada@example.test",
        Password = "secret",
        Region = "EU",
        PatientId = patientId ?? string.Empty,
        Enabled = true,
    };

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal sealed class ScriptedHandler(
        Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> respond) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            var (status, body) = respond(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class NoDelay : IRetryDelayStrategy, IRateLimitingStrategy
    {
        public Task ApplyRetryDelayAsync(int attemptNumber) => Task.CompletedTask;
        public Task ApplyDelayAsync(int requestIndex) => Task.CompletedTask;
    }

    private sealed class InMemoryTokenCache : IConnectorTokenCache
    {
        private readonly Dictionary<string, ConnectorSession> _sessions = [];
        private readonly Dictionary<string, SemaphoreSlim> _locks = [];

        public Task<ConnectorSession?> GetAsync(string connectorName, Guid tenantId) =>
            Task.FromResult(_sessions.GetValueOrDefault(Key(connectorName, tenantId)));

        public Task SetAsync(string connectorName, Guid tenantId, ConnectorSession session)
        {
            _sessions[Key(connectorName, tenantId)] = session;
            return Task.CompletedTask;
        }

        public Task<SemaphoreSlim> GetLockAsync(string connectorName, Guid tenantId)
        {
            var key = Key(connectorName, tenantId);
            if (!_locks.TryGetValue(key, out var gate))
                _locks[key] = gate = new SemaphoreSlim(1, 1);
            return Task.FromResult(gate);
        }

        public void Invalidate(string connectorName, Guid tenantId) =>
            _sessions.Remove(Key(connectorName, tenantId));

        private static string Key(string connectorName, Guid tenantId) =>
            $"{connectorName.ToLowerInvariant()}:{tenantId}";
    }

    private sealed class FixedTenantAccessor : ITenantAccessor
    {
        public Guid TenantId { get; } = Guid.Parse("99999999-8888-7777-6666-555555555555");
        public bool IsResolved => true;
        public TenantContext? Context => null;
        public void SetTenant(TenantContext context) { }
    }
}
