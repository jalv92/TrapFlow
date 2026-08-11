# TrapFlow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** NT8 indicator for MNQ that mechanizes Chris Kmer's trap strategy (structure → fib zone outside value → absorption → second-failure signal), per `docs/specs/2026-08-11-trapflow-design.md`.

**Architecture:** Two C# files. `TrapFlowCore.cs` is pure logic with ZERO NinjaTrader dependencies (profiles, structure verdict, fib zones, absorption/signal predicates, state machine) — unit-tested with xunit on .NET 8. `TrapFlow.cs` is the NT8 shell (volumetric data extraction, session tracking, swings, drawing, sounds, CSV) — validated by staged `nt8c build`. Both files live in `namespace NinjaTrader.NinjaScript.Indicators` so they deploy together to `Custom/Indicators/` with no cross-namespace usings.

**Tech Stack:** C# (net48-compatible — no net8-only APIs in core), NinjaTrader 8 Volumetric bars, xunit + .NET 8 SDK for tests, `nt8c` for NinjaScript compilation.

## Global Constraints

- Everything produced in English (code, comments, commits, docs).
- Parameter defaults frozen per spec: VolumeThreshold=20000, fibs 0.705/0.788/0.886, SwingStrength=5, AbsorptionDeltaPct=0.15, ImbalanceRatio=4.0, ImbalanceMinLevels=2, SignalWindowBars=3, StructureSessions=3, window 09:30–11:00 ET. No tuning before the Replay gate.
- Symmetric long/short: every predicate takes `bool isLong`; short is the exact mirror.
- Indicator only — never places orders.
- `TrapFlowCore.cs` must compile under BOTH net8 (tests) and net48 (NT8): only System, System.Collections.Generic, System.Linq, System.Globalization usings. No NinjaTrader types.
- Ladder dictionary keys are prices normalized with `Math.Round(price, 10)` at insertion AND lookup (floating-point key discipline).
- **nt8c gotchas (from workspace memory, non-negotiable):** per-file `nt8c check` gives false positives/negatives on cross-file and `Draw.*` references. The real gate for every NT8 task is a **staged build**: copy both `.cs` into `<stage>/Indicators/` and run `nt8c build --custom-dir <stage> --no-emit`. A residual `CS1503` on `TextPosition` (Vendor.dll vs Custom.dll duplicate enum) counts as PASSING. Any file calling `Draw.*` MUST carry `using NinjaTrader.NinjaScript.DrawingTools;` even if the per-file check doesn't require it.
- **Deploy after every NT8 task (orchestrator's job, not the subagent's):** copy both `.cs` to `/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom/Indicators/` (real copy, never symlink), then run the two post-deploy checks: no duplicate basenames across `Custom/Indicators` + `Custom/Strategies`, and `cmp` each repo file against its Custom copy.
- Verify NT8 API signatures (`AddVolumetric`, `VolumetricBarsType`, `Swing`) against the `nt8-indicator` / `nt8-common` skills before coding NT8 tasks — snippets below are the intended shape, not gospel.

## File Structure

- `TrapFlowCore.cs` — pure logic: `VolumeProfile`, `StructureVerdict`/`GetStructure`, `TrapZone`, `LadderRow`/`CandleLadder`, `TrapMath` predicates, `TrapFlowEngine` state machine, CSV row builder.
- `TrapFlow.cs` — NT8 indicator shell.
- `tests/TrapFlow.Tests/TrapFlow.Tests.csproj` — xunit, links `../../TrapFlowCore.cs`.
- `tests/TrapFlow.Tests/*Tests.cs` — one test file per core unit.
- `docs/specs/`, `docs/plans/` — this documentation.

---

### Task 1: Test harness + VolumeProfile (POC/VAH/VAL)

**Files:**
- Create: `tests/TrapFlow.Tests/TrapFlow.Tests.csproj`
- Create: `tests/TrapFlow.Tests/ProfileTests.cs`
- Create: `TrapFlowCore.cs`

**Interfaces:**
- Produces: `class VolumeProfile { void Add(double price, long volume); void Compute(double valueAreaPct = 0.70); double Poc; double Vah; double Val; long TotalVolume; }` in `namespace NinjaTrader.NinjaScript.Indicators`.

- [ ] **Step 1: Create the test project**

```bash
cd tests && dotnet new xunit -n TrapFlow.Tests && cd TrapFlow.Tests && rm UnitTest1.cs
```

Edit `TrapFlow.Tests.csproj` to link the core file — add inside `<Project>`:

```xml
<ItemGroup>
  <Compile Include="../../TrapFlowCore.cs" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing test**

`tests/TrapFlow.Tests/ProfileTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: FAIL — `VolumeProfile` does not exist.

- [ ] **Step 4: Implement VolumeProfile in TrapFlowCore.cs**

```csharp
// TrapFlowCore.cs — pure logic, ZERO NinjaTrader dependencies.
// Lives in the Indicators namespace only so it deploys to Custom/Indicators
// alongside TrapFlow.cs without cross-namespace usings.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NinjaTrader.NinjaScript.Indicators
{
    public class VolumeProfile
    {
        private readonly SortedDictionary<double, long> rows = new SortedDictionary<double, long>();
        public double Poc { get; private set; }
        public double Vah { get; private set; }
        public double Val { get; private set; }
        public long TotalVolume { get; private set; }

        public void Add(double price, long volume)
        {
            if (volume <= 0) return;
            double key = Math.Round(price, 10);
            long cur;
            rows.TryGetValue(key, out cur);
            rows[key] = cur + volume;
            TotalVolume += volume;
        }

        // Classic value area: start at POC, expand one row at a time toward the
        // heavier neighbor until the accumulated volume reaches the target pct.
        public void Compute(double valueAreaPct = 0.70)
        {
            if (rows.Count == 0) { Poc = Vah = Val = 0; return; }
            var prices = rows.Keys.ToList();
            int pocIdx = 0;
            for (int i = 1; i < prices.Count; i++)
                if (rows[prices[i]] > rows[prices[pocIdx]]) pocIdx = i;
            Poc = prices[pocIdx];
            long acc = rows[Poc];
            long target = (long)Math.Ceiling(TotalVolume * valueAreaPct);
            int lo = pocIdx, hi = pocIdx;
            while (acc < target && (lo > 0 || hi < prices.Count - 1))
            {
                long below = lo > 0 ? rows[prices[lo - 1]] : -1;
                long above = hi < prices.Count - 1 ? rows[prices[hi + 1]] : -1;
                if (above >= below) { hi++; acc += rows[prices[hi]]; }
                else { lo--; acc += rows[prices[lo]]; }
            }
            Val = prices[lo];
            Vah = prices[hi];
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add TrapFlowCore.cs tests/
git commit -m "feat: VolumeProfile with POC/VAH/VAL + xunit harness"
```

---

### Task 2: Structure verdict

**Files:**
- Modify: `TrapFlowCore.cs`
- Create: `tests/TrapFlow.Tests/StructureTests.cs`

**Interfaces:**
- Produces: `enum StructureVerdict { ValueUp, ValueDown, Lateral }` and `static class TrapMath` with `static StructureVerdict GetStructure(double[] pocs, double[] vahs, double[] vals)` (arrays oldest → newest, uses the last 3 entries).

- [ ] **Step 1: Write the failing test**

`tests/TrapFlow.Tests/StructureTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: FAIL — `TrapMath` / `StructureVerdict` do not exist.

- [ ] **Step 3: Implement in TrapFlowCore.cs**

```csharp
public enum StructureVerdict { ValueUp, ValueDown, Lateral }

public static partial class TrapMath
{
    // Arrays oldest -> newest; only the last 3 sessions are inspected.
    // Value-up: POC and VAL strictly rising across both comparisons.
    // Value-down: POC and VAH strictly falling. Anything else: lateral.
    public static StructureVerdict GetStructure(double[] pocs, double[] vahs, double[] vals)
    {
        int n = pocs.Length;
        if (n < 3) return StructureVerdict.Lateral;
        bool up = pocs[n - 1] > pocs[n - 2] && pocs[n - 2] > pocs[n - 3]
               && vals[n - 1] > vals[n - 2] && vals[n - 2] > vals[n - 3];
        bool down = pocs[n - 1] < pocs[n - 2] && pocs[n - 2] < pocs[n - 3]
                 && vahs[n - 1] < vahs[n - 2] && vahs[n - 2] < vahs[n - 3];
        return up ? StructureVerdict.ValueUp : down ? StructureVerdict.ValueDown : StructureVerdict.Lateral;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add TrapFlowCore.cs tests/
git commit -m "feat: 3-session structure verdict (value-up/value-down/lateral)"
```

---

### Task 3: TrapZone (fib levels, validity, invalidation)

**Files:**
- Modify: `TrapFlowCore.cs`
- Create: `tests/TrapFlow.Tests/ZoneTests.cs`

**Interfaces:**
- Produces:

```csharp
public class TrapZone
{
    public bool IsLong; public double P705, P788, P886, AnchorLow, AnchorHigh;
    public double UpperEdge { get; } public double LowerEdge { get; }
    public static TrapZone Build(double swingLow, double swingHigh, bool isLong);
    public bool IsOutsideValue(double val, double vah);
    public bool Intersects(double lo, double hi);
    public bool CloseBeyond886(double close);
}
```

- [ ] **Step 1: Write the failing test**

`tests/TrapFlow.Tests/ZoneTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: FAIL — `TrapZone` does not exist.

- [ ] **Step 3: Implement in TrapFlowCore.cs**

```csharp
public class TrapZone
{
    public bool IsLong;
    public double P705, P788, P886, AnchorLow, AnchorHigh;
    public double UpperEdge { get { return IsLong ? P705 : P886; } }
    public double LowerEdge { get { return IsLong ? P886 : P705; } }

    public static TrapZone Build(double swingLow, double swingHigh, bool isLong)
    {
        double range = swingHigh - swingLow;
        var z = new TrapZone { IsLong = isLong, AnchorLow = swingLow, AnchorHigh = swingHigh };
        if (isLong)
        {
            z.P705 = swingHigh - 0.705 * range;
            z.P788 = swingHigh - 0.788 * range;
            z.P886 = swingHigh - 0.886 * range;
        }
        else
        {
            z.P705 = swingLow + 0.705 * range;
            z.P788 = swingLow + 0.788 * range;
            z.P886 = swingLow + 0.886 * range;
        }
        return z;
    }

    // Spec: the whole zone must sit outside value — gate on the shallow edge (0.705).
    public bool IsOutsideValue(double val, double vah) { return IsLong ? P705 < val : P705 > vah; }
    public bool Intersects(double lo, double hi) { return lo <= UpperEdge && hi >= LowerEdge; }
    public bool CloseBeyond886(double close) { return IsLong ? close < P886 : close > P886; }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add TrapFlowCore.cs tests/
git commit -m "feat: TrapZone fib band with validity and 0.886 invalidation"
```

---

### Task 4: CandleLadder + absorption predicate

**Files:**
- Modify: `TrapFlowCore.cs`
- Create: `tests/TrapFlow.Tests/AbsorptionTests.cs`

**Interfaces:**
- Produces:

```csharp
public class LadderRow { public long Bid; public long Ask; }
public class CandleLadder
{
    public double Open, High, Low, Close;
    public long TotalVolume; public long Delta; public double Poc;
    public SortedDictionary<double, LadderRow> Rows;
    public double Range { get; } public bool ClosedBullish { get; }
}
// on TrapMath:
public static bool IsAbsorption(CandleLadder c, CandleLadder next, bool isLong, double deltaPct);
```

- [ ] **Step 1: Write the failing test**

`tests/TrapFlow.Tests/AbsorptionTests.cs`:

```csharp
using System.Collections.Generic;
using NinjaTrader.NinjaScript.Indicators;
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: FAIL — `CandleLadder` / `IsAbsorption` do not exist.

- [ ] **Step 3: Implement in TrapFlowCore.cs**

```csharp
public class LadderRow { public long Bid; public long Ask; }

public class CandleLadder
{
    public double Open, High, Low, Close;
    public long TotalVolume;
    public long Delta;   // ask-initiated minus bid-initiated volume
    public double Poc;   // price row with max combined volume
    public SortedDictionary<double, LadderRow> Rows = new SortedDictionary<double, LadderRow>();
    public double Range { get { return High - Low; } }
    public bool ClosedBullish { get { return Close > Open; } }
}

public static partial class TrapMath
{
    // Long absorption: aggressive sellers concentrated at the low with no result.
    // delta strongly negative, candle POC in the lower third, and price recovers
    // (close in upper half of the range, or the next candle closes bullish).
    // Short is the exact mirror.
    public static bool IsAbsorption(CandleLadder c, CandleLadder next, bool isLong, double deltaPct)
    {
        if (c == null || c.TotalVolume <= 0 || c.Range <= 0) return false;
        if (isLong)
        {
            bool deltaOk = c.Delta < 0 && Math.Abs(c.Delta) >= deltaPct * c.TotalVolume;
            bool atExtreme = c.Poc <= c.Low + c.Range / 3.0;
            bool recovered = c.Close >= c.Low + c.Range / 2.0
                          || (next != null && next.ClosedBullish);
            return deltaOk && atExtreme && recovered;
        }
        else
        {
            bool deltaOk = c.Delta > 0 && c.Delta >= deltaPct * c.TotalVolume;
            bool atExtreme = c.Poc >= c.High - c.Range / 3.0;
            bool recovered = c.Close <= c.High - c.Range / 2.0
                          || (next != null && next.Close < next.Open);
            return deltaOk && atExtreme && recovered;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add TrapFlowCore.cs tests/
git commit -m "feat: CandleLadder and absorption predicate (long/short mirror)"
```

---

### Task 5: Diagonal imbalances + signal predicate

**Files:**
- Modify: `TrapFlowCore.cs`
- Create: `tests/TrapFlow.Tests/SignalTests.cs`

**Interfaces:**
- Consumes: `CandleLadder`, `Mk.Candle` test helper from Task 4.
- Produces (on `TrapMath`):

```csharp
public static int CountImbalances(CandleLadder c, bool isLong, double ratio, double tickSize);
public static bool IsSignal(CandleLadder sig, CandleLadder absorption, bool isLong,
                            double ratio, int minLevels, double tickSize);
```

- [ ] **Step 1: Write the failing test**

`tests/TrapFlow.Tests/SignalTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: FAIL — `CountImbalances` / `IsSignal` do not exist.

- [ ] **Step 3: Implement in TrapFlowCore.cs**

```csharp
public static partial class TrapMath
{
    // Diagonal footprint imbalance (industry convention).
    // Long: buy imbalance at price p when ask(p) >= ratio * max(bid(p - tick), 1),
    //   counted only in the lower third of the candle.
    // Short: sell imbalance at p when bid(p) >= ratio * max(ask(p + tick), 1),
    //   counted only in the upper third.
    public static int CountImbalances(CandleLadder c, bool isLong, double ratio, double tickSize)
    {
        if (c == null || c.Range <= 0) return 0;
        int count = 0;
        foreach (var kv in c.Rows)
        {
            double p = kv.Key;
            LadderRow neighbor;
            if (isLong)
            {
                if (p > c.Low + c.Range / 3.0) continue;
                if (!c.Rows.TryGetValue(Math.Round(p - tickSize, 10), out neighbor)) continue;
                if (kv.Value.Ask >= ratio * Math.Max(neighbor.Bid, 1)) count++;
            }
            else
            {
                if (p < c.High - c.Range / 3.0) continue;
                if (!c.Rows.TryGetValue(Math.Round(p + tickSize, 10), out neighbor)) continue;
                if (kv.Value.Bid >= ratio * Math.Max(neighbor.Ask, 1)) count++;
            }
        }
        return count;
    }

    // Second failure: sellers attack again but fail HIGHER than the absorption low
    // (mirror for shorts), the candle flips, and aggressive buyers light up the ladder.
    public static bool IsSignal(CandleLadder sig, CandleLadder absorption, bool isLong,
                                double ratio, int minLevels, double tickSize)
    {
        if (sig == null || absorption == null) return false;
        if (isLong)
        {
            if (sig.Low <= absorption.Low) return false;
            if (!sig.ClosedBullish) return false;
        }
        else
        {
            if (sig.High >= absorption.High) return false;
            if (!(sig.Close < sig.Open)) return false;
        }
        return CountImbalances(sig, isLong, ratio, tickSize) >= minLevels;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add TrapFlowCore.cs tests/
git commit -m "feat: diagonal imbalance counter and second-failure signal predicate"
```

---

### Task 6: TrapFlowEngine state machine

**Files:**
- Modify: `TrapFlowCore.cs`
- Create: `tests/TrapFlow.Tests/EngineTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces:

```csharp
public enum TfState { Dormant, ZoneBuilt, Armed, AbsorptionSeen }
public enum TfEventType { None, ZoneBuilt, PreAlert, Signal, ZoneInvalidated }
public class TfSignal
{
    public TfEventType Type; public double Entry, Stop, Target1;
    public double Zone705, Zone788, Zone886;
    public CandleLadder AbsorptionCandle, SignalCandle;
}
public class TrapFlowEngine
{
    // frozen defaults per spec
    public long VolumeThreshold = 20000; public double AbsorptionDeltaPct = 0.15;
    public double ImbalanceRatio = 4.0; public int ImbalanceMinLevels = 2;
    public int SignalWindowBars = 3; public double TickSize = 0.25;
    public TfState State { get; } public StructureVerdict Structure { get; }
    public TrapZone Zone { get; }
    public void SetStructure(StructureVerdict v);          // resets everything
    public TfEventType OnSwingLeg(double swingLow, double swingHigh, double val, double vah);
    public TfSignal OnCandleClose(CandleLadder c, bool inWindow);
}
```

Semantics locked here (implementers rely on these):
- Invalidation (close beyond 0.886) is checked FIRST and fires even outside the time window / volume floor.
- Time window and volume floor gate every other transition.
- Absorption can confirm two ways: own-close recovery (PreAlert on that candle), or via the next candle's flip — in that second case the same candle is immediately evaluated as the signal candle (age 1).
- Signal window: signal valid at ages 1..SignalWindowBars after the absorption candle; expiry reverts to Armed.
- One signal per zone: after Signal, zone is consumed and state returns to Dormant until a new swing leg builds a new zone.
- Stop = signal candle extreme ± 1 tick; Target1 = fib anchor (swing high for longs, swing low for shorts).

- [ ] **Step 1: Write the failing test**

`tests/TrapFlow.Tests/EngineTests.cs`:

```csharp
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
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: FAIL — `TrapFlowEngine` does not exist.

- [ ] **Step 3: Implement in TrapFlowCore.cs**

```csharp
public enum TfState { Dormant, ZoneBuilt, Armed, AbsorptionSeen }
public enum TfEventType { None, ZoneBuilt, PreAlert, Signal, ZoneInvalidated }

public class TfSignal
{
    public TfEventType Type = TfEventType.None;
    public double Entry, Stop, Target1;
    public double Zone705, Zone788, Zone886;
    public CandleLadder AbsorptionCandle, SignalCandle;
}

public class TrapFlowEngine
{
    public long VolumeThreshold = 20000;
    public double AbsorptionDeltaPct = 0.15;
    public double ImbalanceRatio = 4.0;
    public int ImbalanceMinLevels = 2;
    public int SignalWindowBars = 3;
    public double TickSize = 0.25;

    public TfState State { get; private set; }
    public StructureVerdict Structure { get; private set; }
    public TrapZone Zone { get; private set; }

    private CandleLadder absorption;
    private CandleLadder prev;
    private int absorptionAge;
    private bool IsLong { get { return Structure == StructureVerdict.ValueUp; } }

    public void SetStructure(StructureVerdict v)
    {
        Structure = v;
        Zone = null; absorption = null; prev = null;
        State = TfState.Dormant;
    }

    public TfEventType OnSwingLeg(double swingLow, double swingHigh, double val, double vah)
    {
        if (Structure == StructureVerdict.Lateral) return TfEventType.None;
        var z = TrapZone.Build(swingLow, swingHigh, IsLong);
        if (!z.IsOutsideValue(val, vah)) return TfEventType.None;
        Zone = z; absorption = null;
        State = TfState.ZoneBuilt;
        return TfEventType.ZoneBuilt;
    }

    public TfSignal OnCandleClose(CandleLadder c, bool inWindow)
    {
        var result = new TfSignal();
        var p = prev; prev = c;
        if (Zone == null) return result;

        // 1. Hard invalidation first — fires regardless of window/volume filters.
        if (Zone.CloseBeyond886(c.Close))
        {
            Zone = null; absorption = null; State = TfState.Dormant;
            result.Type = TfEventType.ZoneInvalidated;
            return result;
        }

        // 2. Hard filters: nothing else advances outside the window or under the volume floor.
        if (!inWindow || c.TotalVolume < VolumeThreshold) return result;

        if (State == TfState.ZoneBuilt && Zone.Intersects(c.Low, c.High))
            State = TfState.Armed;

        if (State == TfState.Armed)
        {
            if (TrapMath.IsAbsorption(c, null, IsLong, AbsorptionDeltaPct))
            {
                absorption = c; absorptionAge = 0;
                State = TfState.AbsorptionSeen;
                result.Type = TfEventType.PreAlert;
                return result;
            }
            if (TrapMath.IsAbsorption(p, c, IsLong, AbsorptionDeltaPct))
            {
                // Absorption confirmed by this candle's flip; this candle is age 1
                // and may itself be the signal candle.
                absorption = p; absorptionAge = 1;
                State = TfState.AbsorptionSeen;
                return TrySignal(c, result);
            }
            return result;
        }

        if (State == TfState.AbsorptionSeen)
        {
            absorptionAge++;
            if (absorptionAge > SignalWindowBars)
            {
                absorption = null; State = TfState.Armed;
                return result;
            }
            return TrySignal(c, result);
        }
        return result;
    }

    private TfSignal TrySignal(CandleLadder c, TfSignal result)
    {
        if (!TrapMath.IsSignal(c, absorption, IsLong, ImbalanceRatio, ImbalanceMinLevels, TickSize))
            return result;
        result.Type = TfEventType.Signal;
        result.Entry = c.Close;
        result.Stop = IsLong ? c.Low - TickSize : c.High + TickSize;
        result.Target1 = IsLong ? Zone.AnchorHigh : Zone.AnchorLow;
        result.Zone705 = Zone.P705; result.Zone788 = Zone.P788; result.Zone886 = Zone.P886;
        result.AbsorptionCandle = absorption; result.SignalCandle = c;
        Zone = null; absorption = null; State = TfState.Dormant; // one signal per zone
        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: PASS (all files, ~26 tests).

- [ ] **Step 5: Commit**

```bash
git add TrapFlowCore.cs tests/
git commit -m "feat: TrapFlowEngine state machine (arm/absorb/signal/invalidate)"
```

---

### Task 7: NT8 shell — volumetric plumbing, sessions, structure, swings

**Files:**
- Create: `TrapFlow.cs`

**Interfaces:**
- Consumes: `VolumeProfile`, `TrapMath.GetStructure`, `TrapFlowEngine`, `CandleLadder`, `LadderRow`.
- Produces: a compiling NT8 indicator that (a) builds a `CandleLadder` per closed volumetric 5-min bar, (b) maintains the developing ETH profile + last 3 completed RTH profiles, (c) sets engine structure at each RTH open, (d) feeds swing legs to the engine. No drawing yet — `Print()` diagnostics only.

**Before coding:** read the `nt8-indicator` and `nt8-common` skills and verify: `AddVolumetric` signature, `VolumetricBarsType` accessors (`Volumes[idx].GetBidVolumeForPrice/GetAskVolumeForPrice/BarDelta/TotalVolume/GetMaximumVolume`), `Swing` usage (`SwingHighBar/SwingLowBar` return -1 when none). The snippet below is the intended shape.

- [ ] **Step 1: Write the indicator skeleton**

Key structure for `TrapFlow.cs` (fill in with verified API names):

```csharp
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;

namespace NinjaTrader.NinjaScript.Indicators
{
    public class TrapFlow : Indicator
    {
        private TrapFlowEngine engine;
        private VolumeProfile developingEth;              // resets at 18:00 ET
        private VolumeProfile currentRth;                 // accumulates 09:30-16:00 ET
        private readonly List<double[]> rthHistory = new List<double[]>(); // {poc,vah,val}, newest last
        private Swing swing;
        private double lastSwingHigh = -1, lastSwingLow = -1;
        private int lastSwingHighBar = -1, lastSwingLowBar = -1;
        private DateTime currentEthDate = DateTime.MinValue, currentRthDate = DateTime.MinValue;
        private static readonly TimeZoneInfo TzEt =
            TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

        // Parameters (frozen defaults; [NinjaScriptProperty] + Display attributes)
        // VolumeThreshold=20000, AbsorptionDeltaPct=0.15, ImbalanceRatio=4.0,
        // ImbalanceMinLevels=2, SignalWindowBars=3, SwingStrength=5,
        // WindowStart=930, WindowEnd=1100 (ET, HHmm ints)

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TrapFlow";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;
            }
            else if (State == State.Configure)
            {
                // 5-minute volumetric series, 1 tick per level
                AddVolumetric(Instrument.FullName, BarsPeriodType.Minute, 5,
                              VolumetricDeltaType.BidAsk, 1);
            }
            else if (State == State.DataLoaded)
            {
                engine = new TrapFlowEngine { TickSize = TickSize /* + params */ };
                developingEth = new VolumeProfile();
                currentRth = new VolumeProfile();
                swing = Swing(Closes[1], SwingStrength);
            }
        }

        private static DateTime ToEt(DateTime barTime)
        {
            return TimeZoneInfo.ConvertTime(barTime, TimeZoneInfo.Local, TzEt);
        }

        protected override void OnBarUpdate()
        {
            if (BarsInProgress != 1 || CurrentBars[1] < 1) return;

            var volBars = Bars.BarsSeries.BarsType as VolumetricBarsType; // of BarsArray[1]
            var ladder = ExtractLadder(volBars, CurrentBars[1]);
            DateTime et = ToEt(Times[1][0]);

            TrackSessions(ladder, et);   // ETH reset at 18:00, RTH roll at 16:00, structure at 09:30
            TrackSwings();               // on new confirmed swing pair -> engine.OnSwingLeg(...)

            bool inWindow = InEtWindow(et);       // 09:30 <= t < 11:00 ET
            var evt = engine.OnCandleClose(ladder, inWindow);
            if (evt.Type != TfEventType.None) Print(Name + " " + et + " " + evt.Type);
        }

        private CandleLadder ExtractLadder(VolumetricBarsType volBars, int idx)
        {
            var v = volBars.Volumes[idx];
            var c = new CandleLadder
            {
                Open = Opens[1][0], High = Highs[1][0], Low = Lows[1][0], Close = Closes[1][0],
                TotalVolume = (long)v.TotalVolume, Delta = (long)v.BarDelta
            };
            double poc; v.GetMaximumVolume(null, out poc);
            c.Poc = poc;
            for (double p = Lows[1][0]; p <= Highs[1][0] + TickSize / 2; p += TickSize)
            {
                double key = Math.Round(Instrument.MasterInstrument.RoundToTickSize(p), 10);
                c.Rows[key] = new LadderRow
                {
                    Bid = (long)v.GetBidVolumeForPrice(key),
                    Ask = (long)v.GetAskVolumeForPrice(key)
                };
            }
            return c;
        }
        // TrackSessions: add each ladder row into developingEth and (if RTH) currentRth.
        //   At the first bar with ET time >= 18:00 of a new date: developingEth = new profile.
        //   At the first bar with ET time >= 16:00: currentRth.Compute(); push {poc,vah,val}
        //     into rthHistory (cap 3, drop oldest); currentRth = new profile.
        //   At the first bar with ET time >= 09:30 and rthHistory.Count == 3:
        //     engine.SetStructure(TrapMath.GetStructure(...)) from rthHistory (oldest->newest).
        // TrackSwings: watch swing.SwingHighBar(0,1,int.MaxValue)/SwingLowBar for changes.
        //   ValueUp: when a new swing high confirms and the latest swing low is OLDER than it,
        //     call engine.OnSwingLeg(lowPrice, highPrice, developingEth.Val, developingEth.Vah)
        //     (Compute() the developing profile first). ValueDown mirrors (new low after a high).
    }
}
```

- [ ] **Step 2: Staged build**

```bash
STAGE=$(mktemp -d)/Custom && mkdir -p "$STAGE/Indicators" \
  && cp TrapFlow.cs TrapFlowCore.cs "$STAGE/Indicators/" \
  && nt8c build --custom-dir "$STAGE" --no-emit
```

Expected: compiles OK (0 errors; TextPosition CS1503 not applicable yet — no Draw usage).

- [ ] **Step 3: Run core tests still pass**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: PASS (TrapFlowCore.cs unchanged or still net8-clean).

- [ ] **Step 4: Commit**

```bash
git add TrapFlow.cs
git commit -m "feat: NT8 shell - volumetric ladder, session profiles, structure, swing legs"
```

- [ ] **Step 5 (orchestrator): deploy both .cs to Custom/Indicators + post-deploy checks**

---

### Task 8: NT8 shell — rendering, sounds, state label

**Files:**
- Modify: `TrapFlow.cs`

**Interfaces:**
- Consumes: `TfEventType`, `TfSignal`, `TrapZone` from the engine.

- [ ] **Step 1: Implement rendering**

MUST add `using NinjaTrader.NinjaScript.DrawingTools;` (nt8c false-negative trap). On each engine event:

- `ZoneBuilt`: `Draw.Rectangle(this, "tfZone" + zoneId, ...)` from zone UpperEdge to LowerEdge, extended right; green tint for long, red for short, 20% opacity.
- `ZoneInvalidated`: recolor that rectangle gray.
- `PreAlert`: `Draw.Dot` at the absorption candle low (long) / high (short) + `PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav")`.
- `Signal`: `Draw.TriangleUp/Down` at the signal bar; `Draw.Line` dashed for Stop and Target1; Target2 = developing session POC drawn only if strictly between Entry and Target1; `PlaySound(...\sounds\Alert1.wav")`.
- Every bar: `Draw.TextFixed(this, "tfStatus", ...)` top-right with: structure verdict, engine state, volume filter OK/KO, window OK/KO. (CS1503 on `TextPosition` in staged build = known false positive, counts as pass.)

- [ ] **Step 2: Staged build**

Same command as Task 7 Step 2.
Expected: 0 errors, or ONLY the TextPosition CS1503 residual.

- [ ] **Step 3: Commit**

```bash
git add TrapFlow.cs
git commit -m "feat: zone boxes, signal markers, stop/target lines, sounds, status label"
```

- [ ] **Step 4 (orchestrator): deploy + post-deploy checks**

---

### Task 9: CSV logging

**Files:**
- Modify: `TrapFlowCore.cs` (row builder — testable), `TrapFlow.cs` (file append)
- Create: `tests/TrapFlow.Tests/CsvTests.cs`

**Interfaces:**
- Produces (on `TrapMath`):

```csharp
public const string CsvHeader = "time_et,direction,structure,zone705,zone788,zone886,"
    + "entry,stop,target1,target2,abs_volume,abs_delta,abs_delta_pct,abs_poc,"
    + "sig_volume,sig_delta,sig_imbalance_levels";
public static string BuildCsvRow(DateTime timeEt, bool isLong, StructureVerdict structure,
    TfSignal s, double? target2, double imbalanceRatio, double tickSize);
```

- [ ] **Step 1: Write the failing test**

`tests/TrapFlow.Tests/CsvTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/TrapFlow.Tests -v q`
Expected: FAIL — `BuildCsvRow` does not exist.

- [ ] **Step 3: Implement**

In `TrapFlowCore.cs` (all numbers via `CultureInfo.InvariantCulture`; `abs_delta_pct` = |delta|/volume rounded to 3 decimals; `abs_poc` = absorption candle POC price; `sig_imbalance_levels` via `CountImbalances(s.SignalCandle, isLong, imbalanceRatio, tickSize)`; empty string for null target2). In `TrapFlow.cs`, on Signal:

```csharp
string path = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "TrapFlow_signals.csv");
if (!System.IO.File.Exists(path))
    System.IO.File.AppendAllText(path, TrapMath.CsvHeader + Environment.NewLine);
System.IO.File.AppendAllText(path, row + Environment.NewLine);
```

- [ ] **Step 4: Run tests + staged build**

Run: `dotnet test tests/TrapFlow.Tests -v q` → PASS.
Run staged build (Task 7 Step 2 command) → PASS (TextPosition residual allowed).

- [ ] **Step 5: Commit**

```bash
git add TrapFlowCore.cs TrapFlow.cs tests/
git commit -m "feat: signal CSV logging (invariant culture, tested row builder)"
```

- [ ] **Step 6 (orchestrator): deploy + post-deploy checks**

---

### Task 10: README + final deploy verification

**Files:**
- Create: `README.md`

- [ ] **Step 1: Write README using the `readme-craft` skill**

Must cover: what TrapFlow is (one paragraph, credits the source strategy), the 6-phase state machine, screenshots placeholder, parameters table (frozen defaults), installation (copy both `.cs` to `Custom/Indicators`, F5 compile), the Replay evaluation gate from the spec, and the disclaimer that this is a research indicator, not financial advice.

- [ ] **Step 2: Full verification pass**

```bash
dotnet test tests/TrapFlow.Tests -v q          # all green
# staged build (Task 7 Step 2 command)          # 0 errors or TextPosition residual only
```

- [ ] **Step 3: Commit and push**

```bash
git add README.md
git commit -m "docs: README"
git push origin main
```

- [ ] **Step 4 (orchestrator): final deploy + the two post-deploy checks + remind Javier to F5-compile in the NT8 Editor**

---

## Self-review notes

- Spec coverage: structure (T2/T7), zone+validity+886 (T3/T6), filters (T6/T7), absorption (T4), second failure+imbalance (T5), signal payload stop/T1/T2 (T6/T8), rendering+sounds (T8), CSV (T9), deploy rule (every NT8 task), gate documentation (README T10). GEX/L2/strategy correctly absent (non-goals).
- Types consistent across tasks: `TrapMath` is `static partial` so Tasks 2/4/5/9 each add members without merge conflicts.
- The NT8 API names in Task 7 are flagged as verify-first — the executor must check them against `nt8-indicator`/`nt8-common` before coding; everything else is exact code.
