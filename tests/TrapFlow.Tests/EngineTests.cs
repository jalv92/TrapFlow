using NinjaTrader.NinjaScript.Indicators;
using Xunit;

public class EngineTests
{
    private static TrapFlowEngine LongEngine()
    {
        var e = new TrapFlowEngine();
        e.SetStructure(StructureVerdict.ValueUp);
        // leg 100 -> 200; VAL 140 so the zone band [111.4, 129.5] is fully below value
        Assert.Equal(TfEventType.ZoneBuilt, e.OnSwingLeg(100, 200, val: 140, vah: 180));
        return e;
    }

    private static CandleLadder Absorb() =>
        Mk.Candle(o: 129, h: 130, l: 120, c: 126, totalVol: 30000, delta: -6000, poc: 121);

    private static CandleLadder Sig() =>
        Mk.Candle(o: 126, h: 131, l: 122, c: 130, totalVol: 25000, delta: 2500, poc: 124,
            (122.00, 100, 500),
            (122.25, 50, 450),
            (122.50, 40, 250));

    private static TrapFlowEngine ShortEngine()
    {
        var e = new TrapFlowEngine();
        e.SetStructure(StructureVerdict.ValueDown);
        // leg 90 -> 110; VAH 95 so the zone band [104.1, 107.72] is fully above value
        Assert.Equal(TfEventType.ZoneBuilt, e.OnSwingLeg(90, 110, val: 80, vah: 95));
        return e;
    }

    [Fact]
    public void HappyPath_ArmAbsorbSignal()
    {
        var e = LongEngine();
        var r1 = e.OnCandleClose(Absorb(), inWindow: true);
        Assert.Equal(TfEventType.PreAlert, r1.Type);
        Assert.Equal(TfState.AbsorptionSeen, e.State);

        var r2 = e.OnCandleClose(Sig(), inWindow: true);
        Assert.Equal(TfEventType.Signal, r2.Type);
        Assert.Equal(130, r2.Entry);
        Assert.Equal(122 - 0.25, r2.Stop);
        Assert.Equal(200, r2.Target1);         // fib anchor high
        Assert.Equal(TfState.Dormant, e.State); // zone consumed
    }

    [Fact]
    public void LateralStructure_BuildsNoZone()
    {
        var e = new TrapFlowEngine();
        e.SetStructure(StructureVerdict.Lateral);
        Assert.Equal(TfEventType.None, e.OnSwingLeg(100, 200, 140, 180));
    }

    [Fact]
    public void ZoneInsideValue_Rejected()
    {
        var e = new TrapFlowEngine();
        e.SetStructure(StructureVerdict.ValueUp);
        Assert.Equal(TfEventType.None, e.OnSwingLeg(100, 200, val: 120, vah: 180)); // 129.5 > VAL
    }

    [Fact]
    public void CloseBeyond886_Invalidates_EvenOutsideWindow()
    {
        var e = LongEngine();
        var crash = Mk.Candle(120, 121, 110, 110.5, 30000, -8000, 111); // close < 111.4
        var r = e.OnCandleClose(crash, inWindow: false);
        Assert.Equal(TfEventType.ZoneInvalidated, r.Type);
        Assert.Equal(TfState.Dormant, e.State);
    }

    [Fact]
    public void LowVolume_BlocksAllProgress()
    {
        var e = LongEngine();
        var thin = Mk.Candle(129, 130, 120, 126, totalVol: 5000, delta: -1500, poc: 121);
        Assert.Equal(TfEventType.None, e.OnCandleClose(thin, true).Type);
        Assert.Equal(TfState.ZoneBuilt, e.State);
    }

    [Fact]
    public void SignalWindow_Expires_BackToArmed()
    {
        var e = LongEngine();
        e.OnCandleClose(Absorb(), true);
        var dull = Mk.Candle(126, 128, 124, 125, 25000, 100, 126); // never a signal
        e.OnCandleClose(dull, true);   // age 1
        e.OnCandleClose(dull, true);   // age 2
        e.OnCandleClose(dull, true);   // age 3
        e.OnCandleClose(dull, true);   // age 4 -> expired
        Assert.Equal(TfState.Armed, e.State);
    }

    [Fact]
    public void AbsorptionConfirmedByNextCandle_ThatCandleCanBeTheSignal()
    {
        var e = LongEngine();
        // bearish close, no own recovery -> stays Armed on this candle
        var abs = Mk.Candle(o: 129, h: 130, l: 120, c: 121, totalVol: 30000, delta: -6000, poc: 121);
        Assert.Equal(TfEventType.None, e.OnCandleClose(abs, true).Type);
        Assert.Equal(TfState.Armed, e.State);
        // next candle flips bullish (confirms absorption) AND meets signal conditions
        var r = e.OnCandleClose(Sig(), true);
        Assert.Equal(TfEventType.Signal, r.Type);
    }

    [Fact]
    public void UngatedAbsorption_CannotFlipConfirm_BreaksAdjacency()
    {
        var e = LongEngine();
        // Arm zone with a fully gated, non-absorption candle that intersects [111.4, 129.5]
        var dull = Mk.Candle(126, 128, 124, 125, 25000, 100, 126);
        Assert.Equal(TfEventType.None, e.OnCandleClose(dull, true).Type);
        Assert.Equal(TfState.Armed, e.State);

        // Thin candle: has absorption signature (delta -30%, POC at low, no own recovery)
        // but FAILS volume gate (5000 < 20000 threshold).
        // This candle WOULD satisfy IsAbsorption(thin, null) if gated:
        // - deltaOk: 1500 >= 0.15 * 5000 = 750? YES
        // - atExtreme: 121 <= 120 + 3.33? YES
        // - recovered: 121 >= 125? NO (but next.ClosedBullish would recover it)
        // Since prev is null'd on gate fail, flip-confirmation can't happen.
        var thin = Mk.Candle(o: 129, h: 130, l: 120, c: 121, totalVol: 5000, delta: -1500, poc: 121);
        var result1 = e.OnCandleClose(thin, true);
        Assert.Equal(TfEventType.None, result1.Type);
        Assert.Equal(TfState.Armed, e.State);

        // Sig candle: bullish close, but positive delta — does NOT satisfy IsAbsorption(c, null).
        // PRE-FIX: thin would be stored as prev; flip-confirmation fires, absorption is confirmed,
        //          and Sig fires Signal → BUG.
        // POST-FIX: thin is not stored (prev is null); Sig cannot flip-confirm, stays Armed.
        var result2 = e.OnCandleClose(Sig(), true);
        Assert.Equal(TfEventType.None, result2.Type);
        Assert.Equal(TfState.Armed, e.State);
    }

    // F1: absorption candidate must trade INSIDE the zone (spec phase 4). A fully gated
    // candle with the absorption signature but trading entirely above the zone band
    // [111.4, 129.5] must not qualify -- pre-fix it would (Armed had no zone gate).
    [Fact]
    public void ArmedAbsorptionCandidate_OutsideZone_NoPreAlert()
    {
        var e = LongEngine();
        var dull = Mk.Candle(126, 128, 124, 125, 25000, 100, 126); // arms zone, not absorption-shaped
        Assert.Equal(TfEventType.None, e.OnCandleClose(dull, true).Type);
        Assert.Equal(TfState.Armed, e.State);

        // Absorption-shaped (delta -20%, POC near low, close recovers upper half) but the
        // whole candle trades above UpperEdge (129.5) -- outside the zone.
        var aboveZone = Mk.Candle(o: 138, h: 140, l: 131, c: 137, totalVol: 30000, delta: -6000, poc: 132);
        var result = e.OnCandleClose(aboveZone, true);
        Assert.Equal(TfEventType.None, result.Type);
        Assert.Equal(TfState.Armed, e.State);
    }

    // F5: on the flip path, entering AbsorptionSeen must surface as a PreAlert even when
    // the flip candle itself fails IsSignal -- otherwise no dot, no sound, and a later
    // signal appears with no absorption marker.
    [Fact]
    public void FlipConfirmedAbsorption_NoSignal_EmitsPreAlert()
    {
        var e = LongEngine();
        var abs = Mk.Candle(o: 129, h: 130, l: 120, c: 121, totalVol: 30000, delta: -6000, poc: 121);
        Assert.Equal(TfEventType.None, e.OnCandleClose(abs, true).Type);
        Assert.Equal(TfState.Armed, e.State);

        // Flips bullish (confirms absorption via next.ClosedBullish) but its low breaks
        // BELOW the absorption low, so it fails IsSignal's "sellers fail higher" check.
        var flip = Mk.Candle(o: 118, h: 125, l: 118, c: 123, totalVol: 25000, delta: 1500, poc: 120);
        var r = e.OnCandleClose(flip, true);
        Assert.Equal(TfEventType.PreAlert, r.Type);
        Assert.Equal(TfState.AbsorptionSeen, e.State);
    }

    // F6: short-side happy path, mirroring HappyPath_ArmAbsorbSignal. Absorption/signal
    // candle shapes reused from AbsorptionTests.ShortAbsorption_IsMirror and
    // SignalTests.Signal_Short_IsMirror.
    [Fact]
    public void ShortEngine_ArmAbsorbSignal()
    {
        var e = ShortEngine();
        var abs = Mk.Candle(o: 101, h: 106, l: 100, c: 102, totalVol: 30000, delta: 6000, poc: 105.5);
        var r1 = e.OnCandleClose(abs, inWindow: true);
        Assert.Equal(TfEventType.PreAlert, r1.Type);
        Assert.Equal(TfState.AbsorptionSeen, e.State);

        var sig = Mk.Candle(o: 105.0, h: 105.5, l: 103.0, c: 103.5, totalVol: 25000, delta: -2500, poc: 105,
            (105.50, 500, 100),
            (105.25, 450, 50),
            (105.00, 250, 40));
        var r2 = e.OnCandleClose(sig, inWindow: true);
        Assert.Equal(TfEventType.Signal, r2.Type);
        Assert.Equal(103.5, r2.Entry);
        Assert.Equal(105.5 + 0.25, r2.Stop);
        Assert.Equal(90, r2.Target1);          // fib anchor low
        Assert.Equal(TfState.Dormant, e.State); // zone consumed
    }
}
