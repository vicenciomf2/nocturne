namespace Nocturne.Connectors.FreeStyle.Configurations;

/// <summary>
///     Constants specific to LibreLinkUp/FreeStyle connector
/// </summary>
public static class LibreLinkUpConstants
{
    /// <summary>
    ///     Known LibreLinkUp regional endpoints
    /// </summary>
    public static class Endpoints
    {
        public const string Ae = "api-ae.libreview.io";
        public const string Ap = "api-ap.libreview.io";
        public const string Au = "api-au.libreview.io";
        public const string Ca = "api-ca.libreview.io";
        public const string De = "api-de.libreview.io";
        public const string Eu = "api-eu.libreview.io";
        public const string Eu2 = "api-eu2.libreview.io";
        public const string Fr = "api-fr.libreview.io";
        public const string Jp = "api-jp.libreview.io";
        public const string Us = "api-us.libreview.io";
    }

    /// <summary>
    ///     API endpoints for LibreLinkUp
    /// </summary>
    public static class ApiPaths
    {
        public const string Login = "/llu/auth/login";
        public const string Connections = "/llu/connections";
        public const string GraphData = "/llu/connections/{0}/graph";
    }

    /// <summary>
    ///     The client version LibreLinkUp is known to accept. Abbott rejects callers claiming less
    ///     than its current floor with an HTTP 403 naming the new minimum, and raises that floor
    ///     without notice, so this is a default rather than a constant: see
    ///     <see cref="ClientVersionVariable"/>.
    /// </summary>
    public const string DefaultClientVersion = "4.16.0";

    /// <summary>
    ///     Environment variable an operator sets to claim a different client version, following the
    ///     connector's CONNECT_LIBRE_* prefix. It is deployment-wide rather than per tenant because
    ///     the vendor's floor applies to every caller at once.
    /// </summary>
    public const string ClientVersionVariable = "CONNECT_LIBRE_CLIENT_VERSION";

    /// <summary>
    ///     The client LibreLinkUp is told it is talking to. Both "llu.android" and "llu.ios" are
    ///     accepted; the Android one is what the reference clients send.
    /// </summary>
    public const string AndroidProduct = "llu.android";

    /// <summary>
    ///     The client version to claim, falling back to <see cref="DefaultClientVersion"/> when
    ///     nothing usable is configured.
    /// </summary>
    public static string ResolveClientVersion(string? configured) =>
        string.IsNullOrWhiteSpace(configured) ? DefaultClientVersion : configured.Trim();

    /// <summary>
    ///     The client version to claim, read from the environment.
    /// </summary>
    public static string ClientVersion() =>
        ResolveClientVersion(Environment.GetEnvironmentVariable(ClientVersionVariable));

    /// <summary>
    ///     Configuration specific to LibreLinkUp
    /// </summary>
    public static class Configuration
    {
        public const string DefaultRegion = "EU";
        public const string DeviceIdentifier = "libre-connector";
        public const string EntryType = "sgv";
    }
}