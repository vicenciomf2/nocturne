using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Connectors.Core.Extensions;
using Nocturne.Connectors.Core.Services;
using Xunit;

namespace Nocturne.API.Tests.Connectors;

/// <summary>
/// Pins a token provider's cache key to its connector's registered name. A provider caches and
/// invalidates sessions under <c>AuthTokenProviderBase.ConnectorName</c>, while
/// <c>ConnectorConfigurationService.InvalidateCaches</c> passes the registered
/// <see cref="ConnectorRegistrationAttribute.ConnectorName"/>, and
/// <see cref="ConnectorTokenCache"/> keys on the lowercased value of whichever it was handed. The
/// two names are written down in different files, so nothing but this test stops them drifting —
/// and when they drift the cache is written under one key and invalidated under another, leaving a
/// corrected credential unused until the cached session expires on its own.
/// </summary>
public class ConnectorTokenCacheKeyTests
{
    public static TheoryData<string, string, string> TokenProviders()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var (provider, registered, declared) in DiscoverTokenProviders())
            data.Add(provider, registered, declared);
        return data;
    }

    [Theory]
    [MemberData(nameof(TokenProviders))]
    public void TokenProvider_CachesUnderItsRegisteredConnectorName(
        string providerTypeName, string registeredName, string declaredName)
    {
        declaredName.Should().Be(
            registeredName,
            "{0} caches tokens under \"{1}\" but its connector is registered as \"{2}\", so a "
            + "configuration write invalidates a key the provider never wrote",
            providerTypeName, declaredName, registeredName);
    }

    [Fact]
    public void TheDiscoveryFindsProviders()
    {
        // Guards the theory: an empty set would leave every case above vacuously green.
        DiscoverTokenProviders().Should().NotBeEmpty();
    }

    /// <summary>
    /// Returns (provider type name, name from the config's registration attribute, name the
    /// provider declares). <c>AddConnectors</c> is what loads the vendor assemblies, so it runs
    /// first even though its service descriptors are not read here.
    /// </summary>
    private static List<(string Provider, string Registered, string Declared)> DiscoverTokenProviders()
    {
        new ServiceCollection().AddConnectors(new ConfigurationBuilder().Build());

        var results = new List<(string, string, string)>();

        foreach (var type in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(assembly => !assembly.IsDynamic)
                     .SelectMany(SafeGetTypes)
                     .Where(type => type is { IsAbstract: false, IsClass: true }))
        {
            var configType = TokenProviderConfigType(type);
            if (configType is null) continue;

            var registration = configType.GetCustomAttribute<ConnectorRegistrationAttribute>();
            if (registration is null) continue;

            var declared = DeclaredConnectorName(type);
            if (declared is null) continue;

            results.Add((type.Name, registration.ConnectorName, declared));
        }

        return results;
    }

    /// <summary>
    /// The TConfig of the <c>AuthTokenProviderBase&lt;TConfig&gt;</c> this type closes over, or
    /// null when it derives from no such base.
    /// </summary>
    private static Type? TokenProviderConfigType(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType
                && current.GetGenericTypeDefinition() == typeof(AuthTokenProviderBase<>))
                return current.GetGenericArguments()[0];
        }

        return null;
    }

    /// <summary>
    /// Reads the protected <c>ConnectorName</c> without constructing the provider, which would
    /// need its whole dependency graph. Every override is an expression-bodied constant, so an
    /// uninitialized instance answers correctly; one that read instance state would throw here
    /// rather than pass, which is the safe direction.
    /// </summary>
    private static string? DeclaredConnectorName(Type providerType)
    {
        var property = providerType.GetProperty(
            "ConnectorName",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public
            | BindingFlags.FlattenHierarchy);

        if (property?.GetMethod is null) return null;

        var instance = RuntimeHelpers.GetUninitializedObject(providerType);
        return property.GetMethod.Invoke(instance, null) as string;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }
}
