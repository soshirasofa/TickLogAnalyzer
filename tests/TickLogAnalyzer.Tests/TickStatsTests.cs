using TUnit.Assertions.Extensions;

namespace TickLogAnalyzer.Tests;

public class PercentileSortedTests
{
    [Test]
    public async Task EmptyList_ReturnsZero()
    {
        var result = TickStats.PercentileSorted([], 0.5);
        await Assert.That(result).IsEqualTo(0d);
    }

    [Test]
    public async Task SingleElement_ReturnsThatElement()
    {
        var result = TickStats.PercentileSorted([5.0], 0.5);
        await Assert.That(result).IsEqualTo(5.0);
    }

    [Test]
    public async Task FiveElements_P50_ReturnsMedian()
    {
        // [1, 2, 3, 4, 5]: position = 4 * 0.5 = 2.0 → index 2 → 3.0
        var result = TickStats.PercentileSorted([1, 2, 3, 4, 5], 0.5);
        await Assert.That(result).IsEqualTo(3.0);
    }

    [Test]
    public async Task FiveElements_P0_ReturnsMin()
    {
        var result = TickStats.PercentileSorted([1, 2, 3, 4, 5], 0.0);
        await Assert.That(result).IsEqualTo(1.0);
    }

    [Test]
    public async Task FiveElements_P100_ReturnsMax()
    {
        var result = TickStats.PercentileSorted([1, 2, 3, 4, 5], 1.0);
        await Assert.That(result).IsEqualTo(5.0);
    }

    [Test]
    public async Task TwoElements_P50_ReturnsInterpolated()
    {
        // [0, 10]: position = 1 * 0.5 = 0.5 → lower=0, upper=1, weight=0.5 → 5.0
        var result = TickStats.PercentileSorted([0, 10], 0.5);
        await Assert.That(result).IsEqualTo(5.0);
    }
}

public class BuildTimestampGapsTests
{
    private static TickData MakeTick(long ms) =>
        new(ms, DateTimeOffset.FromUnixTimeMilliseconds(ms), 1.0, 1.0001);

    [Test]
    public async Task SingleTick_ReturnsNoGaps()
    {
        var gaps = TickStats.BuildTimestampGaps([MakeTick(1000)]);
        await Assert.That(gaps.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TwoTicks_ReturnsOneGap()
    {
        var gaps = TickStats.BuildTimestampGaps([MakeTick(1000), MakeTick(1500)]);
        await Assert.That(gaps.Count).IsEqualTo(1);
        await Assert.That(gaps[0].GapMs).IsEqualTo(500L);
    }

    [Test]
    public async Task ThreeTicks_ReturnsTwoGaps()
    {
        var gaps = TickStats.BuildTimestampGaps([MakeTick(0), MakeTick(1000), MakeTick(3000)]);
        await Assert.That(gaps.Count).IsEqualTo(2);
        await Assert.That(gaps[0].GapMs).IsEqualTo(1000L);
        await Assert.That(gaps[1].GapMs).IsEqualTo(2000L);
    }

    [Test]
    public async Task GapTimestamps_AreCorrect()
    {
        var t1 = MakeTick(1_000);
        var t2 = MakeTick(2_500);
        var gaps = TickStats.BuildTimestampGaps([t1, t2]);
        await Assert.That(gaps[0].PreviousUtc).IsEqualTo(t1.TimestampUtc);
        await Assert.That(gaps[0].CurrentUtc).IsEqualTo(t2.TimestampUtc);
    }
}
