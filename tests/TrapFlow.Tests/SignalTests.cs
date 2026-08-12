using NinjaTrader.NinjaScript.Indicators;
using Xunit;

public class SignalTests
{
    // Diagonal buy imbalance at price p: ask(p) >= ratio * bid(p - tick).
    // Only rows in the candle's lower third count for longs.
    [Fact]
    public void CountImbalances_Long_DiagonalInLowerThird()
    {
        var c = Mk.Candle(o: 100.5, h: 103.0, l: 100.0, c: 102.5, totalVol: 25000, delta: 3000, poc: 101,
            (100.00, 500, 100),
            (100.25, 100, 2100),   // ask 2100 >= 4 * bid(100.00)=500*4=2000 -> imbalance
            (100.50, 50, 300),     // ask 300 >= 4 * bid(100.25)=100*4=400? no
            (100.75, 80, 400),     // ask 400 >= 4 * 50*4=200 -> yes (row 100.75 still lower third: low 100, range 3 -> third at 101.0)
            (102.75, 10, 900));    // outside lower third -> ignored
        Assert.Equal(2, TrapMath.CountImbalances(c, isLong: true, ratio: 4.0, tickSize: 0.25));
    }

    [Fact]
    public void Signal_Long_RequiresHigherLow_BullishClose_AndImbalances()
    {
        var abs = Mk.Candle(105, 106, 100, 104, 30000, -6000, 100.5);
        var sig = Mk.Candle(o: 101.0, h: 103.0, l: 100.5, c: 102.5, totalVol: 25000, delta: 2500, poc: 101,
            (100.50, 100, 500),
            (100.75, 50, 450),     // ask 450 >= 4*100=400 -> imbalance
            (101.00, 40, 250));    // ask 250 >= 4*50=200 -> imbalance
        Assert.True(TrapMath.IsSignal(sig, abs, true, 4.0, 2, 0.25));
    }

    [Fact]
    public void Signal_Long_LowBelowAbsorptionLow_Fails()
    {
        var abs = Mk.Candle(105, 106, 100, 104, 30000, -6000, 100.5);
        var sig = Mk.Candle(101, 103, 99.5, 102.5, 25000, 2500, 101,
            (99.50, 100, 500), (99.75, 50, 450));
        Assert.False(TrapMath.IsSignal(sig, abs, true, 4.0, 2, 0.25));
    }

    [Fact]
    public void Signal_Long_BearishClose_Fails()
    {
        var abs = Mk.Candle(105, 106, 100, 104, 30000, -6000, 100.5);
        var sig = Mk.Candle(103, 103.5, 100.5, 101, 25000, 2500, 101,
            (100.50, 100, 500), (100.75, 50, 450));
        Assert.False(TrapMath.IsSignal(sig, abs, true, 4.0, 2, 0.25));
    }

    [Fact]
    public void Signal_Short_IsMirror()
    {
        var abs = Mk.Candle(o: 101, h: 106, l: 100, c: 102, totalVol: 30000, delta: 6000, poc: 105.5);
        // Sell imbalance at p: bid(p) >= ratio * ask(p + tick); rows in upper third.
        var sig = Mk.Candle(o: 105.0, h: 105.5, l: 103.0, c: 103.5, totalVol: 25000, delta: -2500, poc: 105,
            (105.50, 500, 100),
            (105.25, 450, 50),     // bid 450 >= 4 * ask(105.50)=100*4=400 -> imbalance
            (105.00, 250, 40));    // bid 250 >= 4 * 50*4=200 -> imbalance
        Assert.True(TrapMath.IsSignal(sig, abs, isLong: false, ratio: 4.0, minLevels: 2, tickSize: 0.25));
    }
}
