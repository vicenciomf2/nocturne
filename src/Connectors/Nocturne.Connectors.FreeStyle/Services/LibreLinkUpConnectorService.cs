using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Models;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.Core.Utilities;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Mappers;
using Nocturne.Connectors.FreeStyle.Models;
using Nocturne.Core.Constants;
using Nocturne.Core.Models;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.FreeStyle.Services;

/// <summary>
///     Connector service for LibreLinkUp data source.
///     Writes SensorGlucose records directly instead of legacy Entry objects.
/// </summary>
public class LibreConnectorService(
    HttpClient httpClient,
    IConnectorServerResolver<LibreLinkUpConnectorConfiguration> serverResolver,
    ILogger<LibreConnectorService> logger,
    IRetryDelayStrategy retryDelayStrategy,
    IRateLimitingStrategy rateLimitingStrategy,
    LibreLinkAuthTokenProvider tokenProvider,
    IConnectorPublisher? publisher = null
)
    : BaseConnectorService<LibreLinkUpConnectorConfiguration>(
        httpClient,
        serverResolver,
        logger,
        publisher
    )
{
    private readonly LibreSensorGlucoseMapper _sensorGlucoseMapper = new(logger);

    private readonly IRateLimitingStrategy _rateLimitingStrategy =
        rateLimitingStrategy ?? throw new ArgumentNullException(nameof(rateLimitingStrategy));

    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    private readonly LibreLinkAuthTokenProvider _tokenProvider =
        tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));

    private string _accountIdHash = string.Empty;
    private string? _bearerToken;
    private LibreUserConnection? _selectedConnection;

    private Dictionary<string, string>? RequestHeaders
    {
        get
        {
            Dictionary<string, string>? headers = null;

            if (!string.IsNullOrWhiteSpace(_accountIdHash))
            {
                headers = new Dictionary<string, string> { { "Account-Id", _accountIdHash } };
            }

            if (!string.IsNullOrEmpty(_bearerToken))
            {
                headers ??= new Dictionary<string, string>();
                headers["Authorization"] = $"Bearer {_bearerToken}";
            }

            return headers;
        }
    }

    public override string ServiceName => "LibreLinkUp";
    protected override string ConnectorSource => DataSources.LibreConnector;

    private async Task<bool> AuthenticateWithConfigAsync(
        LibreLinkUpConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetValidTokenAsync(config, cancellationToken);
        if (token == null)
        {
            _accountIdHash = string.Empty;
            TrackFailedRequest("Failed to get valid token");
            return false;
        }

        _accountIdHash = string.Empty;
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadToken(token) as JwtSecurityToken;
            if (jwt is null) _logger.LogWarning("LibreLinkUp token is not a valid JWT");

            if (jwt is not null)
            {
                var claim = jwt.Claims.FirstOrDefault(c => c.Type == "id");
                if (claim?.Value is { Length: > 0 } value) _accountIdHash = HashUtils.Sha256Hex(value);
                if (_accountIdHash.Length == 0) _logger.LogWarning("LibreLinkUp token missing id claim");
            }
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("LibreLinkUp token is not a valid JWT");
        }

        _bearerToken = token;

        // A token proves the credentials, not that there is a patient to read. Reporting success
        // without a selected connection is what let a sync fetch nothing and still look healthy.
        if (!await LoadConnectionsAsync(config, cancellationToken))
        {
            TrackFailedRequest("No usable LibreLinkUp connection");
            return false;
        }

        TrackSuccessfulRequest();
        return true;
    }

    /// <summary>
    ///     Authenticates before the requested-range sync runs, so a rejected credential or an
    ///     unusable connection ends the run as a failure instead of an empty, successful-looking
    ///     fetch.
    /// </summary>
    protected override Task<bool> EnsureAuthenticatedAsync(
        LibreLinkUpConnectorConfiguration config, CancellationToken cancellationToken) =>
        AuthenticateWithConfigAsync(config, cancellationToken);

    /// <summary>
    ///     Fetches SensorGlucose records from the LibreLinkUp API. Throws
    ///     <see cref="InvalidOperationException"/> rather than returning an empty list when the
    ///     connector cannot reach a usable state, so the caller records the run as failed instead
    ///     of as a sync that found nothing.
    /// </summary>
    private async Task<IEnumerable<SensorGlucose>> FetchSensorGlucoseAsync(
        LibreLinkUpConnectorConfiguration config,
        DateTime? since,
        CancellationToken cancellationToken)
    {
        if (_tokenProvider.IsTokenExpired || _selectedConnection == null)
        {
            _logger.LogInformation("Token expired or missing connection, attempting to re-authenticate");
            if (!await AuthenticateWithConfigAsync(config, cancellationToken))
                throw new InvalidOperationException("Failed to authenticate with LibreLinkUp");
        }

        if (string.IsNullOrWhiteSpace(_selectedConnection?.PatientId))
        {
            TrackFailedRequest("Invalid patient id");
            throw new InvalidOperationException("No LibreLinkUp patient id to read");
        }

        // Captured before the retry loop: a re-authentication inside it can replace the selected
        // connection, and the readings of one attempt must not be keyed to another's patient.
        var patientId = _selectedConnection.PatientId;

        var url = _serverResolver.BuildUrl(config,
            string.Format(LibreLinkUpConstants.ApiPaths.GraphData, patientId));

        // No-op as called: ProductionRateLimitingStrategy only delays for a request index above
        // zero, and this is the sync's single request. Kept because the pacing that matters for
        // this vendor is the tenant's sync interval, not spacing within one sync — Cloudflare
        // rate-limits by source IP across the whole deployment.
        await _rateLimitingStrategy.ApplyDelayAsync(0);

        var result = await ExecuteWithRetryAsync(
            async () => await FetchSensorGlucoseCoreAsync(url, patientId, since, cancellationToken),
            _retryDelayStrategy,
            async () =>
            {
                _tokenProvider.InvalidateToken();
                _selectedConnection = null;
                return await AuthenticateWithConfigAsync(config, cancellationToken);
            },
            maxRetries: config.MaxRetryAttempts,
            operationName: "FetchSensorGlucoseData"
        );

        return result ?? [];
    }

    /// <summary>
    ///     Performs sync, publishing SensorGlucose records directly to the V4 data store.
    /// </summary>
    protected override async Task<SyncResult> PerformSyncInternalAsync(
        SyncRequest request,
        LibreLinkUpConnectorConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new SyncResult { StartTime = DateTimeOffset.UtcNow, Success = true };

        var activeTypes = ResolveActiveTypes(request, config);
        if (!activeTypes.Contains(SyncDataType.Glucose))
        {
            result.EndTime = DateTimeOffset.UtcNow;
            return result;
        }

        try
        {
            var sensorGlucose = await FetchSensorGlucoseAsync(config, request.From, cancellationToken);

            await PublishRecordTypeAsync(result, SyncDataType.Glucose, activeTypes,
                sensorGlucose.ToList(), PublishSensorGlucoseDataAsync, config, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during LibreLinkUp sync");
            result.Success = false;
            result.Errors.Add($"Sync error: {ex.Message}");
        }

        result.EndTime = DateTimeOffset.UtcNow;
        return result;
    }

    /// <summary>
    ///     Selects the followed patient to read. Returns false when the connector must not proceed:
    ///     the list could not be fetched, nobody is sharing, or a configured patient id matches
    ///     none of the connections.
    /// </summary>
    private async Task<bool> LoadConnectionsAsync(
        LibreLinkUpConnectorConfiguration config, CancellationToken cancellationToken)
    {
        _selectedConnection = null;

        try
        {
            var response = await GetWithHeadersAsync(
                _serverResolver.BuildUrl(config, LibreLinkUpConstants.ApiPaths.Connections),
                RequestHeaders,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to load LibreLinkUp connections: {StatusCode}", response.StatusCode);
                return false;
            }

            var connectionsResponse = await DeserializeResponseAsync<LibreConnectionsResponse>(response);

            if (connectionsResponse?.Data == null || connectionsResponse.Data.Length == 0)
            {
                _logger.LogError(
                    "No LibreLinkUp connections found — nobody is sharing with this account");
                return false;
            }

            // A configured patient id is a filter, not a hint. Falling back to another connection
            // would write a different person's glucose into this tenant.
            if (!string.IsNullOrEmpty(config.PatientId))
            {
                _selectedConnection = connectionsResponse.Data.FirstOrDefault(c =>
                    string.Equals(c.PatientId, config.PatientId, StringComparison.OrdinalIgnoreCase));

                if (_selectedConnection == null)
                {
                    _logger.LogError(
                        "Configured LibreLinkUp patient id matches none of the {Count} shared connections",
                        connectionsResponse.Data.Length);
                    return false;
                }

                return true;
            }

            _selectedConnection = connectionsResponse.Data[0];
            _logger.LogInformation(
                "Selected LibreLinkUp connection: {PatientName} ({PatientId})",
                _selectedConnection.FirstName + " " + _selectedConnection.LastName,
                _selectedConnection.PatientId
            );
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading LibreLinkUp connections");
            return false;
        }
    }

    private async Task<List<SensorGlucose>?> FetchSensorGlucoseCoreAsync(
        string url, string patientId, DateTime? since, CancellationToken cancellationToken)
    {
        var response = await GetWithHeadersAsync(url, RequestHeaders, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.StatusCode}: {errorContent}",
                null,
                response.StatusCode
            );
        }

        var graphResponse = await DeserializeResponseAsync<LibreGraphResponse>(response);

        if (graphResponse?.Data?.GraphData == null || graphResponse.Data.GraphData.Length == 0)
        {
            _logger.LogDebug("No glucose data returned from LibreLinkUp");
            return [];
        }

        var measurements = graphResponse.Data.GraphData.ToList();
        var latestMeasurement = graphResponse.Data.Connection.GlucoseMeasurement;

        var latestTimestamp = latestMeasurement.FactoryTimestamp;
        if (!measurements.Any(m => m.FactoryTimestamp == latestTimestamp))
        {
            measurements.Add(latestMeasurement);
        }
        else
        {
            var existing = measurements.First(m => m.FactoryTimestamp == latestTimestamp);
            if (existing.TrendArrow == 0 && latestMeasurement.TrendArrow != 0)
                existing.TrendArrow = latestMeasurement.TrendArrow;
        }

        var glucoseRecords = measurements
            .Where(m => m.ValueInMgPerDl > 0)
            .Select(m => _sensorGlucoseMapper.ConvertMeasurement(m, patientId))
            .Where(sg => sg != null)
            .Cast<SensorGlucose>()
            .Where(sg => !since.HasValue || DateTimeOffset.FromUnixTimeMilliseconds(sg.Mills).UtcDateTime > since.Value)
            .OrderBy(sg => sg.Mills)
            .ToList();

        _logger.LogInformation(
            "[{ConnectorSource}] Successfully fetched {Count} SensorGlucose records from LibreLinkUp",
            ConnectorSource,
            glucoseRecords.Count
        );

        return glucoseRecords;
    }

    private class LibreConnectionsResponse
    {
        public required LibreUserConnection[] Data { get; set; }
    }

    private class LibreUserConnection
    {
        public required string PatientId { get; set; }
        public required string FirstName { get; set; }
        public required string LastName { get; set; }
    }

    private class LibreGraphResponse
    {
        public required LibreConnectionData Data { get; set; }
    }

    private class LibreConnectionData
    {
        public required LibreConnection Connection { get; set; }
        public required LibreGlucoseMeasurement[] GraphData { get; set; }
    }

    private class LibreConnection
    {
        public required LibreGlucoseMeasurement GlucoseMeasurement { get; set; }
    }
}
