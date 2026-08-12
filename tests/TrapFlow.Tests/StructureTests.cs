using NinjaTrader.NinjaScript.Indicators;
using Xunit;

public class StructureTests
{
    [Fact]
    public void PocAndValRising_IsValueUp()
    {
        var v = TrapMath.GetStructure(
            pocs: new[] { 100.0, 105.0, 110.0 },
            vahs: new[] { 102.0, 107.0, 112.0 },
            vals: new[] { 98.0, 103.0, 108.0 });
        Assert.Equal(StructureVerdict.ValueUp, v);
    }

    [Fact]
    public void PocAndVahFalling_IsValueDown()
    {
        var v = TrapMath.GetStructure(
            pocs: new[] { 110.0, 105.0, 100.0 },
            vahs: new[] { 112.0, 107.0, 102.0 },
            vals: new[] { 108.0, 103.0, 98.0 });
        Assert.Equal(StructureVerdict.ValueDown, v);
    }

    [Fact]
    public void MixedMigration_IsLateral()
    {
        var v = TrapMath.GetStructure(
            pocs: new[] { 100.0, 105.0, 103.0 },
            vahs: new[] { 102.0, 107.0, 105.0 },
            vals: new[] { 98.0, 103.0, 101.0 });
        Assert.Equal(StructureVerdict.Lateral, v);
    }
}
