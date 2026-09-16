using FluentAssertions;
using Nocturne.Connectors.FreeStyle.Configurations;
using Xunit;

namespace Nocturne.Connectors.FreeStyle.Tests.Configurations;

/// <summary>
/// Abbott gates LibreLinkUp on the client version the caller claims and raises the floor without
/// warning — a stale value is answered with an HTTP 403 carrying the new minimum, which is a total
/// outage for the connector. Baked into the assembly it takes a new image to clear; read from the
/// environment an operator can set it the same hour.
/// </summary>
public class LibreClientVersionTests
{
    [Fact]
    public void WithNothingConfigured_TheKnownGoodVersionIsUsed()
    {
        LibreLinkUpConstants.ResolveClientVersion(null).Should().Be("4.16.0");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WithABlankValue_TheKnownGoodVersionIsUsed(string configured)
    {
        LibreLinkUpConstants.ResolveClientVersion(configured).Should().Be("4.16.0");
    }

    [Fact]
    public void AConfiguredVersion_Wins()
    {
        LibreLinkUpConstants.ResolveClientVersion("4.17.0").Should().Be("4.17.0");
    }

    [Fact]
    public void AConfiguredVersion_IsTrimmed()
    {
        // Environment variables pick up whitespace from compose files and shell quoting, and the
        // vendor compares the header exactly.
        LibreLinkUpConstants.ResolveClientVersion(" 4.17.0 ").Should().Be("4.17.0");
    }

    [Fact]
    public void WithNothingConfigured_TheInstallerSendsTheKnownGoodVersion()
    {
        var options = ConnectorInstallerOptionsProbe.Read(new FreeStyleConnectorInstaller());

        options.AdditionalHeaders!["Version"].Should().Be("4.16.0");
        options.AdditionalHeaders["Product"].Should().Be("llu.android");
    }

    /// <summary>
    /// The User-Agent has to agree with the product being claimed. Sending the platform default
    /// alongside Product: llu.android is a fingerprint no real client produces, in front of a
    /// Cloudflare edge that scores exactly that.
    /// </summary>
    [Fact]
    public void TheUserAgent_MatchesTheClaimedClient()
    {
        var options = ConnectorInstallerOptionsProbe.Read(new FreeStyleConnectorInstaller());

        options.UserAgent.Should().Be("LibreLinkUp/4.16.0");
    }
}
