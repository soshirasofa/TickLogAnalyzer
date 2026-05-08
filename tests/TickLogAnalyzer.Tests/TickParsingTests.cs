using TUnit.Assertions.Extensions;

namespace TickLogAnalyzer.Tests;

public class ParseDurationMsTests
{
    [Test]
    [Arguments("1s", 1_000L)]
    [Arguments("10s", 10_000L)]
    [Arguments("1m", 60_000L)]
    [Arguments("5m", 300_000L)]
    [Arguments("1h", 3_600_000L)]
    [Arguments("100ms", 100L)]
    [Arguments("1S", 1_000L)]
    [Arguments("1M", 60_000L)]
    [Arguments("1H", 3_600_000L)]
    public async Task ValidDuration_ReturnsCorrectMs(string input, long expected)
    {
        var result = TickParsing.ParseDurationMs(input);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    [Arguments("0s")]
    [Arguments("0m")]
    [Arguments("0ms")]
    [Arguments("-1s")]
    [Arguments("abc")]
    [Arguments("1x")]
    [Arguments("")]
    [Arguments("s")]
    public async Task InvalidDuration_ThrowsArgumentException(string input)
    {
        await Assert.That(() => TickParsing.ParseDurationMs(input))
            .Throws<ArgumentException>();
    }
}

public class ParseInstantTests
{
    private static readonly TimeZoneInfo JstZone = TickParsing.ResolveTimeZone("Asia/Tokyo");

    [Test]
    public async Task UtcSuffix_ParsesAsUtc()
    {
        var result = TickParsing.ParseInstant("2024-01-01T00:00:00Z", TimeZoneInfo.Utc);
        await Assert.That(result).IsEqualTo(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task ExplicitPlusNineOffset_ConvertsToUtc()
    {
        var result = TickParsing.ParseInstant("2024-01-01T09:00:00+09:00", TimeZoneInfo.Utc);
        await Assert.That(result).IsEqualTo(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task NoOffset_UsesProvidedTimezone()
    {
        var result = TickParsing.ParseInstant("2024-01-01 09:00:00", JstZone);
        await Assert.That(result).IsEqualTo(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }
}

public class HasExplicitOffsetTests
{
    [Test]
    [Arguments("2024-01-01T00:00:00Z", true)]
    [Arguments("2024-01-01T00:00:00z", true)]
    [Arguments("2024-01-01T09:00:00+09:00", true)]
    [Arguments("2024-01-01T09:00:00-05:00", true)]
    [Arguments("2024-01-01T09:00:00", false)]
    [Arguments("2024-01-01 09:00:00", false)]
    public async Task VariousFormats_DetectsOffsetCorrectly(string input, bool expected)
    {
        var result = TickParsing.HasExplicitOffset(input);
        await Assert.That(result).IsEqualTo(expected);
    }
}

public class ParseCadencesTests
{
    [Test]
    public async Task NullInput_ReturnsEmpty()
    {
        var result = TickParsing.ParseCadences(null);
        await Assert.That(result.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ValidCommaList_ReturnsSortedDistinct()
    {
        var result = TickParsing.ParseCadences("100,10,33,10");
        await Assert.That(result).IsEquivalentTo(new[] { 10, 33, 100 });
    }

    [Test]
    public async Task InvalidValue_ThrowsArgumentException()
    {
        await Assert.That(() => TickParsing.ParseCadences("10,abc"))
            .Throws<ArgumentException>();
    }
}
