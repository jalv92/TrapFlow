// TrapFlowCore.cs — pure logic, ZERO NinjaTrader dependencies.
// Lives in the Indicators namespace only so it deploys to Custom/Indicators
// alongside TrapFlow.cs without cross-namespace usings.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NinjaTrader.NinjaScript.Indicators
{
    public enum StructureVerdict { ValueUp, ValueDown, Lateral }

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
}
