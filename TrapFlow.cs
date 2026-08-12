// TrapFlow.cs — NT8 indicator shell: volumetric ladder plumbing, session profiles,
// structure, and swing-leg wiring around TrapFlowCore's pure logic (TrapFlowEngine).
//
// Lives in the Indicators namespace (same as TrapFlowCore.cs) so both files deploy
// together to Custom/Indicators and TrapFlowCore's types resolve without cross-file
// usings. The per-file nt8c PostToolUse hook cannot see TrapFlowCore.cs and WILL flag
// false CS0246/CS0234 on VolumeProfile/CandleLadder/TrapFlowEngine/etc — that is
// expected; the real gate is the staged multi-file build (see task-7 report).
//
// Architecture: the chart's primary series can be anything (any bar type); this
// indicator adds its own 5-min Volumetric series via AddVolumetric() (BarsInProgress
// == 1) and does ALL of its work off that series only. No drawing here (Task 8);
// Print() diagnostics only, fired on TrapFlowEngine state events and once-per-day
// structure re-evaluation.
using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Collections.Generic;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;

namespace NinjaTrader.NinjaScript.Indicators
{
    public class TrapFlow : Indicator
    {
        // ---- Engine + profiles -----------------------------------------------------
        private TrapFlowEngine engine;
        private VolumeProfile developingEth;                // resets at 18:00 ET
        private VolumeProfile currentRth;                   // accumulates 09:30-16:00 ET
        private readonly List<double[]> rthHistory = new List<double[]>(); // {poc,vah,val}, oldest first
        private Swing swing;

        // ---- Swing-leg tracking (most recent CONFIRMED high/low on the vol series) --
        private double lastSwingHigh = -1, lastSwingLow = -1;
        private int lastSwingHighBar = -1, lastSwingLowBar = -1;

        // ---- Session-roll bookkeeping (calendar date of the last time each fired) ---
        private DateTime currentEthDate = DateTime.MinValue;
        private DateTime currentRthRollDate = DateTime.MinValue;
        private DateTime structureDate = DateTime.MinValue;

        private static readonly TimeZoneInfo TzEt =
            TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        private static readonly TimeSpan RthStartEt = new TimeSpan(9, 30, 0);
        private static readonly TimeSpan RthEndEt   = new TimeSpan(16, 0, 0);
        private static readonly TimeSpan EthResetEt = new TimeSpan(18, 0, 0);

        #region Properties
        [NinjaScriptProperty]
        [Display(Name = "Volume Threshold", Order = 1, GroupName = "1 - Trap Parameters",
            Description = "Minimum total contracts in a 5-min volumetric candle for the volume filter to pass.")]
        public long VolumeThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 1.0)]
        [Display(Name = "Absorption Delta Pct", Order = 2, GroupName = "1 - Trap Parameters",
            Description = "Minimum |delta| / total volume for a candle to qualify as absorption.")]
        public double AbsorptionDeltaPct { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 20.0)]
        [Display(Name = "Imbalance Ratio", Order = 3, GroupName = "1 - Trap Parameters",
            Description = "Minimum ask/bid (or bid/ask) ratio at a diagonal price level to count as an imbalance.")]
        public double ImbalanceRatio { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Imbalance Min Levels", Order = 4, GroupName = "1 - Trap Parameters",
            Description = "Minimum imbalanced price levels required on the signal candle.")]
        public int ImbalanceMinLevels { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Signal Window Bars", Order = 5, GroupName = "1 - Trap Parameters",
            Description = "Bars after absorption within which a second-failure signal can still fire.")]
        public int SignalWindowBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Swing Strength", Order = 1, GroupName = "2 - Structure",
            Description = "Bars required on each side to confirm a swing point (NT8 Swing indicator).")]
        public int SwingStrength { get; set; }

        [NinjaScriptProperty]
        [Range(3, 10)]
        [Display(Name = "Structure Sessions", Order = 2, GroupName = "2 - Structure",
            Description = "Completed RTH sessions retained for the structure verdict. TrapFlowCore's " +
                "GetStructure always compares the LAST 3 of whatever is retained here, so values below 3 " +
                "keep structure permanently dormant (Lateral).")]
        public int StructureSessions { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Window Start (ET HHmm)", Order = 1, GroupName = "3 - Signal Window (ET)",
            Description = "Signal window start, Eastern Time, HHmm (930 = 09:30).")]
        public int WindowStartEt { get; set; }

        [NinjaScriptProperty]
        [Range(0, 2359)]
        [Display(Name = "Window End (ET HHmm)", Order = 2, GroupName = "3 - Signal Window (ET)",
            Description = "Signal window end, Eastern Time, HHmm (1100 = 11:00).")]
        public int WindowEndEt { get; set; }
        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "TrapFlow";
                Calculate = Calculate.OnBarClose;
                IsOverlay = true;

                VolumeThreshold = 20000;
                AbsorptionDeltaPct = 0.15;
                ImbalanceRatio = 4.0;
                ImbalanceMinLevels = 2;
                SignalWindowBars = 3;
                SwingStrength = 5;
                StructureSessions = 3;
                WindowStartEt = 930;
                WindowEndEt = 1100;
            }
            else if (State == State.Configure)
            {
                // 5-minute volumetric series on the SAME instrument/contract as the chart
                // (BarsInProgress == 1) — 1 tick per level so the ladder is per-price.
                AddVolumetric(Instrument.FullName, BarsPeriodType.Minute, 5, VolumetricDeltaType.BidAsk, 1);
            }
            else if (State == State.DataLoaded)
            {
                engine = new TrapFlowEngine
                {
                    TickSize = TickSize,
                    VolumeThreshold = VolumeThreshold,
                    AbsorptionDeltaPct = AbsorptionDeltaPct,
                    ImbalanceRatio = ImbalanceRatio,
                    ImbalanceMinLevels = ImbalanceMinLevels,
                    SignalWindowBars = SignalWindowBars,
                };
                // StructureVerdict's default enum value is ValueUp (0), NOT Lateral — set
                // explicitly so the engine starts genuinely dormant until 3 RTH sessions arm it.
                engine.SetStructure(StructureVerdict.Lateral);

                developingEth = new VolumeProfile();
                currentRth = new VolumeProfile();
                rthHistory.Clear();

                swing = Swing(Closes[1], SwingStrength);

                currentEthDate = DateTime.MinValue;
                currentRthRollDate = DateTime.MinValue;
                structureDate = DateTime.MinValue;
                lastSwingHigh = -1; lastSwingLow = -1;
                lastSwingHighBar = -1; lastSwingLowBar = -1;
            }
        }

        protected override void OnBarUpdate()
        {
            // Only the added volumetric series drives this indicator; ignore the primary.
            if (BarsInProgress != 1 || CurrentBars[1] < 1) return;

            var volBars = BarsArray[1].BarsType as VolumetricBarsType;
            if (volBars == null) return;

            int idx = CurrentBars[1];
            var ladder = ExtractLadder(volBars, idx);
            DateTime et = ToEt(Times[1][0]);

            TrackSessions(ladder, et);
            TrackSwings();

            bool inWindow = InEtWindow(et);
            var evt = engine.OnCandleClose(ladder, inWindow);
            if (evt.Type != TfEventType.None)
                Print(string.Format("{0} {1:yyyy-MM-dd HH:mm} ET {2}", Name, et, evt.Type));
        }

        private CandleLadder ExtractLadder(VolumetricBarsType volBars, int idx)
        {
            var v = volBars.Volumes[idx];
            var c = new CandleLadder
            {
                Open = Opens[1][0],
                High = Highs[1][0],
                Low = Lows[1][0],
                Close = Closes[1][0],
                TotalVolume = (long)v.TotalVolume,
                Delta = (long)v.BarDelta
            };

            double poc;
            v.GetMaximumVolume(null, out poc);
            c.Poc = poc;

            double tick = Instrument.MasterInstrument.TickSize;
            for (double p = c.Low; p <= c.High + tick / 2.0; p += tick)
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

        // Rolls the developing ETH profile (resets 18:00 ET) and the completed-RTH-session
        // history (rolls 16:00 ET, feeds structure at 09:30 ET). Called once per closed
        // volumetric bar, in that chronological order, so a single bar can never straddle
        // two roll events.
        private void TrackSessions(CandleLadder ladder, DateTime et)
        {
            if (et.TimeOfDay >= EthResetEt && et.Date != currentEthDate)
            {
                developingEth = new VolumeProfile();
                currentEthDate = et.Date;
            }
            foreach (var row in ladder.Rows)
                developingEth.Add(row.Key, row.Value.Bid + row.Value.Ask);

            bool isRth = et.TimeOfDay >= RthStartEt && et.TimeOfDay < RthEndEt;
            if (isRth)
            {
                foreach (var row in ladder.Rows)
                    currentRth.Add(row.Key, row.Value.Bid + row.Value.Ask);
            }

            if (et.TimeOfDay >= RthEndEt && et.Date != currentRthRollDate)
            {
                currentRthRollDate = et.Date;
                if (currentRth.TotalVolume > 0)
                {
                    currentRth.Compute();
                    rthHistory.Add(new[] { currentRth.Poc, currentRth.Vah, currentRth.Val });
                    while (rthHistory.Count > StructureSessions)
                        rthHistory.RemoveAt(0);
                }
                currentRth = new VolumeProfile();
            }

            if (et.TimeOfDay >= RthStartEt && et.Date != structureDate)
            {
                structureDate = et.Date;
                if (rthHistory.Count >= 3)
                {
                    var pocs = rthHistory.Select(h => h[0]).ToArray();
                    var vahs = rthHistory.Select(h => h[1]).ToArray();
                    var vals = rthHistory.Select(h => h[2]).ToArray();
                    var verdict = TrapMath.GetStructure(pocs, vahs, vals);
                    engine.SetStructure(verdict);
                    Print(string.Format("{0} {1:yyyy-MM-dd} structure: {2} ({3} RTH sessions)",
                        Name, et, verdict, rthHistory.Count));
                }
                else
                {
                    engine.SetStructure(StructureVerdict.Lateral);
                    Print(string.Format("{0} {1:yyyy-MM-dd} structure: insufficient RTH history ({2}/3) - dormant",
                        Name, et, rthHistory.Count));
                }
            }
        }

        // Tracks the latest CONFIRMED swing high/low on the volumetric series and feeds
        // engine.OnSwingLeg() when a fresh swing extends the structure's directional leg.
        // Bookkeeping runs every bar regardless of structure (so it's current the moment
        // structure turns directional); only the engine feed is gated on directionality.
        private void TrackSwings()
        {
            int hb = swing.SwingHighBar(0, 1, CurrentBars[1]);
            if (hb >= 0)
            {
                int hbAbs = CurrentBars[1] - hb;
                if (hbAbs != lastSwingHighBar)
                {
                    lastSwingHighBar = hbAbs;
                    lastSwingHigh = swing.SwingHigh[hb];

                    if (engine.Structure == StructureVerdict.ValueUp
                        && lastSwingLowBar >= 0 && lastSwingLowBar < hbAbs)
                        FeedSwingLeg(lastSwingLow, lastSwingHigh);
                }
            }

            int lb = swing.SwingLowBar(0, 1, CurrentBars[1]);
            if (lb >= 0)
            {
                int lbAbs = CurrentBars[1] - lb;
                if (lbAbs != lastSwingLowBar)
                {
                    lastSwingLowBar = lbAbs;
                    lastSwingLow = swing.SwingLow[lb];

                    if (engine.Structure == StructureVerdict.ValueDown
                        && lastSwingHighBar >= 0 && lastSwingHighBar < lbAbs)
                        FeedSwingLeg(lastSwingLow, lastSwingHigh);
                }
            }
        }

        private void FeedSwingLeg(double lowPrice, double highPrice)
        {
            if (highPrice <= lowPrice) return; // degenerate leg guard (Task 3 carry-over)
            developingEth.Compute();
            var evt = engine.OnSwingLeg(lowPrice, highPrice, developingEth.Val, developingEth.Vah);
            if (evt != TfEventType.None)
                Print(string.Format("{0} swing leg {1}-{2} -> {3}", Name, lowPrice, highPrice, evt));
        }

        private bool InEtWindow(DateTime et)
        {
            var t = et.TimeOfDay;
            return t >= HhmmToTimeSpan(WindowStartEt) && t < HhmmToTimeSpan(WindowEndEt);
        }

        private static TimeSpan HhmmToTimeSpan(int hhmm)
        {
            return new TimeSpan(hhmm / 100, hhmm % 100, 0);
        }

        private static DateTime ToEt(DateTime barTime)
        {
            return TimeZoneInfo.ConvertTime(barTime, TimeZoneInfo.Local, TzEt);
        }
    }
}
