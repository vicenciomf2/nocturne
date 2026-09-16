using Microsoft.Extensions.Logging;
using Nocturne.Connectors.FreeStyle.Configurations;
using Nocturne.Connectors.FreeStyle.Models;
using Nocturne.Connectors.FreeStyle.Utilities;
using Nocturne.Core.Constants;
using Nocturne.Core.Models.V4;

namespace Nocturne.Connectors.FreeStyle.Mappers;

public class LibreSensorGlucoseMapper(ILogger? logger = null)
{
    private static readonly Dictionary<int, GlucoseDirection> TrendArrowMap = new()
    {
        { 1, GlucoseDirection.SingleDown },
        { 2, GlucoseDirection.FortyFiveDown },
        { 3, GlucoseDirection.Flat },
        { 4, GlucoseDirection.FortyFiveUp },
        { 5, GlucoseDirection.SingleUp }
    };

    /// <param name="patientId">
    ///     The followed patient the reading was fetched for. It scopes the sync identifier, so a
    ///     tenant reconfigured from one followed person to another does not collapse the two onto
    ///     one key.
    /// </param>
    public SensorGlucose? ConvertMeasurement(
        LibreGlucoseMeasurement measurement, string patientId)
    {
        try
        {
            var timestamp = LibreTimestampParser.Parse(measurement.FactoryTimestamp);
            var now = DateTime.UtcNow;

            // The vendor's date format is month/day ambiguous and nothing in the payload resolves
            // it, so a day-first account is read month-first and lands months from the twelve-hour
            // window this connector fetched. Drop it rather than store a reading in the wrong month.
            if (!LibreTimestampParser.IsPlausible(timestamp, now))
            {
                logger?.LogWarning(
                    "Discarding LibreLinkUp reading timestamped {Timestamp} from {Raw}: outside the "
                    + "window the graph endpoint can return",
                    timestamp, measurement.FactoryTimestamp);
                return null;
            }

            var direction = TrendArrowMap.GetValueOrDefault(measurement.TrendArrow, GlucoseDirection.NotComputable);
            var mgdl = (double)measurement.ValueInMgPerDl;

            return new SensorGlucose
            {
                Id = Guid.CreateVersion7(),
                Timestamp = timestamp,
                // LegacyId keys every Libre row already stored, and it was built from the vendor's
                // formatted string. Changing its shape would make that history unreachable and
                // re-insert all of it, so it stays as it is and SyncIdentifier carries the stable
                // key: derived from the parsed instant, it survives a change in that formatting and
                // is what lets a corrected value replace a stored one instead of being dropped.
                LegacyId = $"libre_{measurement.FactoryTimestamp}",
                SyncIdentifier =
                    $"llu:{patientId}:{new DateTimeOffset(timestamp, TimeSpan.Zero).ToUnixTimeMilliseconds()}",
                Device = LibreLinkUpConstants.Configuration.DeviceIdentifier,
                DataSource = DataSources.LibreConnector,
                Mgdl = mgdl,
                Direction = direction,
                CreatedAt = now,
                ModifiedAt = now
            };
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Error converting LibreLinkUp measurement: {@Measurement}", measurement);
            return null;
        }
    }
}
