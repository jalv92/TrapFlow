using System;
using NinjaTrader.NinjaScript.Indicators;
using Xunit;

public class CsvTests
{
    [Fact]
    public void Row_IsInvariantCulture_AndMatchesHeaderArity()
    {
        var s = new TfSignal
        {
            Type = TfEventType.Signal, Entry = 130, Stop = 121.75, Target1 = 200,
            Zone705 = 129.5, Zone788 = 121.2, Zone886 = 111.4,
            AbsorptionCandle = Mk.Candle(129, 130, 120, 126, 30000, -6000, 121),
            SignalCandle = Mk.Candle(126, 131, 122, 130, 25000, 2500, 124,
                (122.00, 100, 500), (122.25, 50, 450), (122.50, 40, 250))
        };
        string row = TrapMath.BuildCsvRow(new DateTime(2026, 8, 11, 9, 45, 0), true,
            StructureVerdict.ValueUp, s, target2: null, imbalanceRatio: 4.0, tickSize: 0.25);
        Assert.Equal(TrapMath.CsvHeader.Split(',').Length, row.Split(',').Length);
        Assert.Contains("2026-08-11 09:45,LONG,ValueUp", row);
        Assert.Contains("121.75", row);
        Assert.DoesNotContain(";", row); // decimal separator must be '.', fields ','
    }
}
