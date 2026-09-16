using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nocturne.Connectors.Core.Interfaces;
using Nocturne.Connectors.Core.Services;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Core.Contracts.Multitenancy;

namespace Nocturne.Connectors.FreeStyle.Services;

/// <summary>
///     Token provider for LibreLinkUp authentication.
///     Returns a Bearer token for API requests.
/// </summary>
public class LibreLinkAuthTokenProvider(
    HttpClient httpClient,
    IConnectorTokenCache tokenCache,
    IConnectorServerResolver<LibreLinkUpConnectorConfiguration> serverResolver,
    ITenantAccessor tenantAccessor,
    ILogger<LibreLinkAuthTokenProvider> logger,
    IRetryDelayStrategy retryDelayStrategy
) : AuthTokenProviderBase<LibreLinkUpConnectorConfiguration>(httpClient, tokenCache, serverResolver, tenantAccessor, logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IRetryDelayStrategy _retryDelayStrategy =
        retryDelayStrategy
        ?? throw new ArgumentNullException(nameof(retryDelayStrategy));

    /// <summary>
    ///     LibreLinkUp tokens typically last 24 hours.
    /// </summary>
    protected override int TokenLifetimeBufferMinutes => 60;

    /// <summary>
    ///     The registered connector name, not the vendor family this assembly is named for. The
    ///     token cache keys on this value and <c>ConnectorConfigurationService.InvalidateCaches</c>
    ///     passes the registered name, so the two have to agree for a credential change to evict
    ///     the cached session.
    /// </summary>
    protected override string ConnectorName => "LibreLinkUp";

    protected override async Task<(string? Token, DateTime ExpiresAt, IReadOnlyDictionary<string, string>? Metadata)> AcquireTokenAsync(
        LibreLinkUpConnectorConfiguration config, CancellationToken cancellationToken)
    {
        var maxRetries = LoginAttempts(config);

        var token = await ExecuteWithRetryAsync(
            async attempt =>
            {
                _logger.LogInformation(
                    "Authenticating with LibreLinkUp for user: {Username} (attempt {Attempt}/{MaxRetries})",
                    config.Username,
                    attempt + 1,
                    maxRetries);

                var loginPayload = new
                {
                    email = config.Username,
                    password = config.Password
                };

                var json = JsonSerializer.Serialize(loginPayload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(
                    _serverResolver.BuildUrl(config, LibreLinkUpConstants.ApiPaths.Login),
                    content,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var shouldRetry = await HandleErrorResponseAsync(
                        response, "LibreLinkUp authentication", cancellationToken);
                    return (null, shouldRetry);
                }

                var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

                if (IsLockedOutResponse(responseContent))
                {
                    _logger.LogWarning(
                        "LibreLinkUp locked (429), backing off for 5 minutes"
                    );
                    await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                    return (null, true);
                }

                var loginResponse = JsonSerializer.Deserialize<LibreLoginResponse>(
                    responseContent,
                    JsonOptions
                );

                if (loginResponse?.Data?.AuthTicket?.Token != null) return (loginResponse.Data.AuthTicket.Token, false);

                // LibreLinkUp answers three quite different situations with a 200 and no auth
                // ticket. Reported as one "invalid response" they are indistinguishable from a
                // wrong password, and the two that a user can fix themselves stay unfixed.
                if (loginResponse?.Data is { Redirect: true, Region.Length: > 0 } redirected)
                {
                    _logger.LogError(
                        "LibreLinkUp account is served by the {Region} region, not {Configured}. "
                        + "Set the connector's Region to {Region} and sync again.",
                        redirected.Region!.ToUpperInvariant(), config.Region,
                        redirected.Region.ToUpperInvariant());
                    return (null, false);
                }

                if (loginResponse?.Data?.Step?.Type is { Length: > 0 } step)
                {
                    _logger.LogError(
                        "LibreLinkUp is waiting on the account to complete '{Step}'. Open the "
                        + "LibreLinkUp app, finish the prompt it shows, then sync again — it "
                        + "cannot be completed from here.",
                        step);
                    return (null, false);
                }

                _logger.LogError("LibreLinkUp authentication failed: Invalid response structure");
                return (null, false);
            },
            _retryDelayStrategy,
            maxRetries,
            "LibreLinkUp authentication",
            cancellationToken
        );

        if (string.IsNullOrEmpty(token)) return (null, DateTime.MinValue, null);

        var expiresAt = DateTime.UtcNow.AddHours(24);

        _logger.LogInformation(
            "LibreLinkUp authentication successful, token expires at {ExpiresAt}",
            expiresAt);

        return (token, expiresAt, null);
    }

    private static bool IsLockedOutResponse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        try
        {
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return false;

            if (data.TryGetProperty("code", out var code) && code.GetInt32() != 60)
                return false;

            if (!data.TryGetProperty("message", out var message))
                return false;

            var messageText = message.GetString();
            return !string.IsNullOrWhiteSpace(messageText) &&
                   string.Equals(messageText, "locked", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    #region Response Models

    private class LibreLoginResponse
    {
        public LibreLoginData? Data { get; set; }
    }

    private class LibreLoginData
    {
        public LibreAuthTicket? AuthTicket { get; set; }

        /// <summary>
        ///     Set when the account lives on another regional endpoint. The response is a 200 with
        ///     no auth ticket, which is why it has to be told apart explicitly.
        /// </summary>
        public bool Redirect { get; set; }

        /// <summary>
        ///     The region to re-issue against when <see cref="Redirect"/> is set, lowercased
        ///     (e.g. "de").
        /// </summary>
        public string? Region { get; set; }

        /// <summary>
        ///     Set when the account owes a consent or verification step before it can be used.
        /// </summary>
        public LibreLoginStep? Step { get; set; }
    }

    /// <summary>
    ///     A step the account owes: "tou" and "pp" are the terms and privacy consents, accepted in
    ///     the LibreLinkUp app; "verifyEmail" is an emailed code. None can be completed from here.
    /// </summary>
    private class LibreLoginStep
    {
        public string? Type { get; set; }
    }

    private class LibreAuthTicket
    {
        public string? Token { get; set; }
    }

    #endregion
}
