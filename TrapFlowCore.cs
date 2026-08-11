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
}
