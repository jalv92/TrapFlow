using NinjaTrader.NinjaScript.Indicators;
using Xunit;

public class ZoneTests
{
    // Leg 100 -> 200, long: retracement levels measured down from the high.
    [Fact]
    public void LongZone_FibPrices()
    {
        var z = TrapZone.Build(100, 200, isLong: true);
        Assert.Equal(129.5, z.P705, 3);   // 200 - 0.705*100
        Assert.Equal(121.2, z.P788, 3);
        Assert.Equal(111.4, z.P886, 3);
        Assert.Equal(129.5, z.UpperEdge, 3);
        Assert.Equal(111.4, z.LowerEdge, 3);
    }

    [Fact]
    public void ShortZone_IsMirror()
    {
        var z = TrapZone.Build(100, 200, isLong: false);
        Assert.Equal(170.5, z.P705, 3);   // 100 + 0.705*100
        Assert.Equal(188.6, z.P886, 3);
        Assert.Equal(188.6, z.UpperEdge, 3);
        Assert.Equal(170.5, z.LowerEdge, 3);
    }

    [Fact]
    public void LongZone_ValidOnlyBelowVal()
    {
        var z = TrapZone.Build(100, 200, isLong: true);
        Assert.True(z.IsOutsideValue(val: 130.0, vah: 180.0));  // whole zone under VAL
        Assert.False(z.IsOutsideValue(val: 125.0, vah: 180.0)); // 0.705 inside value
    }

    [Fact]
    public void Invalidation_CloseBeyond886()
    {
        var z = TrapZone.Build(100, 200, isLong: true);
        Assert.True(z.CloseBeyond886(111.0));
        Assert.False(z.CloseBeyond886(112.0));
        var s = TrapZone.Build(100, 200, isLong: false);
        Assert.True(s.CloseBeyond886(189.0));
    }

    [Fact]
    public void Intersects_CandleRangeVsZoneBand()
    {
        var z = TrapZone.Build(100, 200, isLong: true); // band [111.4, 129.5]
        Assert.True(z.Intersects(128.0, 140.0));
        Assert.False(z.Intersects(130.0, 140.0));
    }
}
