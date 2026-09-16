using System.Reflection;
using FluentAssertions;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.FreeStyle.Configurations;
using Xunit;

namespace Nocturne.Connectors.FreeStyle.Tests.Configurations;

/// <summary>
/// The configurable surface of the LibreLinkUp connector: what a tenant can pick in the UI has to
/// match what the installer can actually resolve, and what the vendor tolerates.
/// </summary>
public class LibreConfigurationTests
{
    /// <summary>
    /// Every region the installer maps to an endpoint must be selectable. EU2 was reachable by the
    /// server resolver and absent from the allowed values, so an account on that endpoint could not
    /// be configured through the UI at all.
    /// </summary>
    [Fact]
    public void EveryMappedRegion_CanBeSelected()
    {
        RegionProperty().AllowedValues.Should().BeEquivalentTo(MappedRegions());
    }

    [Fact]
    public void TheDefaultRegion_IsSelectable()
    {
        var region = RegionProperty();

        region.AllowedValues.Should().Contain((string)region.DefaultValue!);
    }

    private static IEnumerable<string> MappedRegions()
    {
        var installer = new FreeStyleConnectorInstaller();
        var options = (ConnectorOptions)typeof(ConnectorInstallerOptionsProbe)
            .GetMethod(nameof(ConnectorInstallerOptionsProbe.Read))!
            .Invoke(null, [installer])!;

        return options.ServerMapping!.Keys;
    }

    private static ConnectorPropertyAttribute RegionProperty() =>
        PropertyFor(nameof(LibreLinkUpConnectorConfiguration.Region));

    private static ConnectorPropertyAttribute PropertyFor(string name) =>
        typeof(LibreLinkUpConnectorConfiguration)
            .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!
            .GetCustomAttribute<ConnectorPropertyAttribute>()!;
}

/// <summary>
/// Reads the options an installer was built with. They are held privately because nothing in
/// production needs them back; the test needs them to compare the two lists that must agree.
/// </summary>
internal static class ConnectorInstallerOptionsProbe
{
    public static ConnectorOptions Read(object installer)
    {
        for (var type = installer.GetType(); type is not null; type = type.BaseType)
        {
            var field = type
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
                .FirstOrDefault(candidate => candidate.FieldType == typeof(ConnectorOptions));

            if (field?.GetValue(installer) is ConnectorOptions options)
                return options;
        }

        throw new InvalidOperationException(
            $"{installer.GetType().Name} holds no {nameof(ConnectorOptions)}");
    }
}
