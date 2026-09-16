using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Models;
using Nocturne.Core.Constants;

namespace Nocturne.Connectors.FreeStyle.Configurations;

/// <summary>
///     Configuration specific to LibreLinkUp connector
/// </summary>
[ConnectorRegistration(
    "LibreLinkUp",
    ServiceNames.LibreConnector,
    "LIBRE",
    "ConnectSource.LibreLinkUp",
    "libre-connector",
    "libre",
    ConnectorCategory.Cgm,
    "Connect to LibreView for CGM data",
    "FreeStyle Libre",
    SupportsHistoricalSync = false,
    // The graph endpoint takes no range and returns the vendor's own rolling window of roughly
    // twelve hours, and nothing else is fetched — so there is no depth of history to advertise. A
    // non-zero figure here is only rendered, never honoured, and promised a recovery the connector
    // cannot perform: an outage longer than that window loses its readings permanently.
    MaxHistoricalDays = 0,
    SupportsManualSync = true,
    SupportedDataTypes = [SyncDataType.Glucose]
)]
public class LibreLinkUpConnectorConfiguration : BaseConnectorConfiguration
{
    public LibreLinkUpConnectorConfiguration()
    {
        ConnectSource = ConnectSource.LibreLinkUp;
    }

    /// <summary>
    ///     LibreLinkUp username
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Username, Required = true)]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    ///     LibreLinkUp password
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Password, Required = true, Secret = true)]
    public string Password { get; set; } = string.Empty;

    /// <summary>
    ///     LibreLinkUp region
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.Region, DefaultValue = "EU", AllowedValues = ["AE", "AP", "AU", "CA", "DE", "EU", "EU2", "FR", "JP", "US"])]
    public string Region { get; set; } = "EU";

    /// <summary>
    ///     Patient ID for LibreLinkUp (for caregiver accounts)
    /// </summary>
    [ConnectorProperty(ConnectorPropertyKey.PatientId)]
    public string PatientId { get; set; } = string.Empty;
}
