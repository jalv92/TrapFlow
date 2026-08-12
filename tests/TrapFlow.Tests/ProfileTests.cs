using NinjaTrader.NinjaScript.Indicators;
using Xunit;

public class ProfileTests
{
    [Fact]
    public void Poc_IsHeaviestRow()
    {
        var p = new VolumeProfile();
        p.Add(100.00, 10); p.Add(100.25, 50); p.Add(100.50, 20);
        p.Compute();
        Assert.Equal(100.25, p.Poc);
        Assert.Equal(80, p.TotalVolume);
    }

    [Fact]
    public void ValueArea_CoversSeventyPercent_ExpandingTowardHeavierNeighbor()
    {
        var p = new VolumeProfile();
        // 5 rows, total 100: VA should be the contiguous block around POC reaching >= 70
        p.Add(100.00, 5); p.Add(100.25, 20); p.Add(100.50, 40); p.Add(100.75, 25); p.Add(101.00, 10);
        p.Compute();
        Assert.Equal(100.50, p.Poc);
        // POC(40) + above(25) = 65 < 70 -> add below(20) = 85 >= 70
        Assert.Equal(100.25, p.Val);
        Assert.Equal(100.75, p.Vah);
    }

    [Fact]
    public void EmptyProfile_ComputesZeros()
    {
        var p = new VolumeProfile();
        p.Compute();
        Assert.Equal(0, p.Poc);
    }
}
