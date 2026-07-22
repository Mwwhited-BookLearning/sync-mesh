using SyncMesh.Contracts;

namespace SyncMesh.Daemon.Tests;

public class HlcGeneratorTests
{
    [Fact]
    public void Next_ProducesStrictlyIncreasingValues()
    {
        var generator = new HlcGenerator();

        var first = generator.Next();
        var second = generator.Next();
        var third = generator.Next();

        Assert.True(second > first);
        Assert.True(third > second);
    }

    [Fact]
    public void Merge_WithEarlierReceivedClock_StillAdvances()
    {
        var generator = new HlcGenerator();
        var local = generator.Next();

        var earlierReceived = new HybridLogicalClock(local.PhysicalTicks - 1, 0);
        var merged = generator.Merge(earlierReceived);

        Assert.True(merged > local);
    }

    [Fact]
    public void Merge_WithLaterReceivedClock_AdvancesPastIt()
    {
        var generator = new HlcGenerator();
        var local = generator.Next();

        var laterReceived = new HybridLogicalClock(local.PhysicalTicks + TimeSpan.FromMinutes(5).Ticks, 3);
        var merged = generator.Merge(laterReceived);

        Assert.True(merged > laterReceived);

        var subsequent = generator.Next();
        Assert.True(subsequent > merged);
    }
}
