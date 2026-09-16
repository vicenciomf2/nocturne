using System.Globalization;
using FluentAssertions;
using Nocturne.Connectors.FreeStyle.Utilities;
using Xunit;

namespace Nocturne.Connectors.FreeStyle.Tests.Utilities;

/// <summary>
/// LibreLinkUp's <c>FactoryTimestamp</c> is a formatted string rather than an instant, and the
/// formats it can take are mutually ambiguous: "3/4/2026" is the 4th of March read month-first and
/// the 3rd of April read day-first. Nothing in the payload says which, so the parser resolves it by
/// fixed order — and the consequence of resolving it wrongly is a reading landing up to eleven
/// months from where it belongs, silently.
/// </summary>
public class LibreTimestampParserTests
{
    [Theory]
    [InlineData("3/14/2026 3:30:00 PM", "2026-03-14T15:30:00")]
    [InlineData("3/14/2026 3:30 PM", "2026-03-14T15:30:00")]
    [InlineData("3/14/2026 15:30:00", "2026-03-14T15:30:00")]
    [InlineData("2026-03-14T15:30:00", "2026-03-14T15:30:00")]
    [InlineData("2026-03-14 15:30:00", "2026-03-14T15:30:00")]
    public void Parse_ReadsTheVendorFormats(string value, string expected)
    {
        LibreTimestampParser.Parse(value)
            .Should().Be(DateTime.Parse(expected, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
    }

    [Fact]
    public void Parse_TreatsFactoryTimestampsAsUtc()
    {
        // FactoryTimestamp is the vendor's UTC field; the sibling Timestamp is local. Reading it as
        // local would shift every reading by the server's offset.
        LibreTimestampParser.Parse("3/14/2026 3:30:00 PM").Kind.Should().Be(DateTimeKind.Utc);
    }

    /// <summary>
    /// The parse must not depend on where the container happens to be running. A culture-sensitive
    /// fallback made the same payload parse to different instants on two deployments of the same
    /// image, which is the one failure that cannot be reproduced from the data.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("de-DE")]
    public void Parse_DoesNotDependOnTheAmbientCulture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            LibreTimestampParser.Parse("3/4/2026 10:00:00")
                .Should().Be(new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData("not a timestamp")]
    [InlineData("")]
    [InlineData("14/14/2026 10:00:00")]
    public void Parse_RejectsWhatItCannotRead(string value)
    {
        var parse = () => LibreTimestampParser.Parse(value);

        parse.Should().Throw<FormatException>();
    }

    /// <summary>
    /// The connector reads a rolling window about twelve hours wide, so a reading it produces is
    /// always near now. A timestamp months away is the signature of the month/day ambiguity
    /// resolving the wrong way, and is rejected rather than stored where it does not belong.
    /// </summary>
    [Theory]
    [InlineData(-400)]
    [InlineData(-31)]
    [InlineData(2)]
    public void IsPlausible_RejectsTimestampsTheGraphWindowCannotProduce(int daysFromNow)
    {
        var now = new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc);

        LibreTimestampParser.IsPlausible(now.AddDays(daysFromNow), now).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-29)]
    public void IsPlausible_AcceptsTheWindowTheConnectorReads(int daysFromNow)
    {
        var now = new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc);

        LibreTimestampParser.IsPlausible(now.AddDays(daysFromNow), now).Should().BeTrue();
    }

    /// <summary>
    /// Clocks drift, and a reading a few minutes ahead of the poller's own clock is ordinary.
    /// </summary>
    [Fact]
    public void IsPlausible_AllowsASmallClockSkew()
    {
        var now = new DateTime(2026, 3, 14, 12, 0, 0, DateTimeKind.Utc);

        LibreTimestampParser.IsPlausible(now.AddMinutes(30), now).Should().BeTrue();
    }
}
