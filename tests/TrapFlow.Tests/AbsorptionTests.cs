using System.Collections.Generic;
using TrapFlowCore;
using Xunit;

public static class Mk
{
    public static CandleLadder Candle(double o, double h, double l, double c,
        long totalVol, long delta, double poc,
        params (double price, long bid, long ask)[] rows)
    {
        var cl = new CandleLadder { Open = o, High = h, Low = l, Close = c,
            TotalVolume = totalVol, Delta = delta, Poc = poc,
            Rows = new SortedDictionary<double, LadderRow>() };
        foreach (var r in rows)
            cl.Rows[System.Math.Round(r.price, 10)] = new LadderRow { Bid = r.bid, Ask = r.ask };
        return cl;
    }
}

public class AbsorptionTests
{
    // Long absorption: heavy negative delta, candle POC in lower third, close recovers upper half.
    [Fact]
    public void LongAbsorption_OwnCloseRecovery()
    {
        var c = Mk.Candle(o: 105, h: 106, l: 100, c: 104, totalVol: 30000, delta: -6000, poc: 100.5);
        Assert.True(TrapMath.IsAbsorption(c, null, isLong: true, deltaPct: 0.15));
    }

    [Fact]
    public void LongAbsorption_WeakDelta_Fails()
    {
        var c = Mk.Candle(105, 106, 100, 104, 30000, -3000, 100.5); // 10% < 15%
        Assert.False(TrapMath.IsAbsorption(c, null, true, 0.15));
    }

    [Fact]
    public void LongAbsorption_PocNotAtExtreme_Fails()
    {
        var c = Mk.Candle(105, 106, 100, 104, 30000, -6000, poc: 104.0); // POC in upper part
        Assert.False(TrapMath.IsAbsorption(c, null, true, 0.15));
    }

    [Fact]
    public void LongAbsorption_BearishClose_RecoveredByNextBullishCandle()
    {
        var c = Mk.Candle(o: 105, h: 106, l: 100, c: 101, totalVol: 30000, delta: -6000, poc: 100.5);
        var next = Mk.Candle(101, 103, 100.5, 102.5, 25000, 2000, 102);
        Assert.False(TrapMath.IsAbsorption(c, null, true, 0.15));   // no recovery on its own
        Assert.True(TrapMath.IsAbsorption(c, next, true, 0.15));    // next closes bullish
    }

    [Fact]
    public void ShortAbsorption_IsMirror()
    {
        var c = Mk.Candle(o: 101, h: 106, l: 100, c: 102, totalVol: 30000, delta: 6000, poc: 105.5);
        Assert.True(TrapMath.IsAbsorption(c, null, isLong: false, deltaPct: 0.15));
    }
}
