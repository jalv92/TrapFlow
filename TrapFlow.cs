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
// == 1) and does ALL of its work off that series only. Rendering (Task 8) uses the
// DateTime-anchored Draw.* overloads throughout — never the barsAgo overloads — since
// barsAgo is relative to whichever BarsInProgress is active and this indicator always
// runs on series 1 (volumetric) while the chart's primary series can be any bar type.
using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Collections.Generic;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui; // DashStyleHelper lives here, not in DrawingTools
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.BarsTypes;
using NinjaTrader.NinjaScript.DrawingTools;

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

        // ---- Rendering state (Task 8) -----------------------------------------------
        // One zone live at a time (mirrors engine.Zone being a single object): the box is
        // redrawn every bar with its right edge pinned to the current bar, so it grows in
        // place instead of needing a far-future placeholder end time.
        private class ZoneBox
        {
            public string Tag;
            public double Upper, Lower;
            public DateTime CreatedTime;
            public Brush Fill;
        }
        private ZoneBox activeZone;
        private int zoneCounter, signalCounter, preAlertCounter;

        // ponytail: fixed forward window for the one-shot stop/target lines instead of
        // redrawing them every bar to track price — they mark a static trade plan, not a
        // moving zone. Matches the fixed 5-min AddVolumetric() period from Configure.
        private static readonly TimeSpan SignalLineLookahead = TimeSpan.FromMinutes(100);

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

                // F4: real bar extremes, not closes -- Swing(ISeries<double> input, strength)
                // uses the passed series' own values for BOTH swing-high and swing-low
                // detection, which made every fib level / outside-value check / Target1
                // systematically shallow when fed Closes[1]. Swing(Bars, strength) targets
                // BarsArray[1] (the volumetric series) while still resolving to that Bars'
                // real High/Low, matching the parameterless overload's default behavior.
                swing = Swing(BarsArray[1], SwingStrength);

                currentEthDate = DateTime.MinValue;
                currentRthRollDate = DateTime.MinValue;
                structureDate = DateTime.MinValue;
                lastSwingHigh = -1; lastSwingLow = -1;
                lastSwingHighBar = -1; lastSwingLowBar = -1;

                activeZone = null;
                // ponytail: counters reset per-load but old draw-object tags don't disappear
                // on their own -- wipe the chart once here instead. No within-run pruning of
                // the tag counters themselves: zones/signals are scarce by design (0-2
                // signals/day, a handful of zones/day), so unbounded growth within a single
                // load is accepted for v1.
                zoneCounter = 0; signalCounter = 0; preAlertCounter = 0;
                RemoveDrawObjects();
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

            // ZoneInvalidated gets a one-shot gray redraw instead of the normal green/red
            // extension; every other outcome (including Signal, which still wants the box
            // to reach the signal bar before it freezes) extends the box as usual.
            if (evt.Type == TfEventType.ZoneInvalidated)
                FreezeActiveZoneGray();
            else
                ExtendActiveZone();

            if (evt.Type == TfEventType.PreAlert)
            {
                RenderPreAlert(ladder);
            }
            else if (evt.Type == TfEventType.Signal)
            {
                RenderSignal(evt);
                activeZone = null; // setup consumed (Core resets Zone too) -- box stops growing
            }

            RenderStatus(ladder, inWindow);
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
                    // F2: freeze the outgoing zone box before SetStructure nulls engine.Zone --
                    // otherwise ExtendActiveZone() keeps stretching a dead box across a
                    // structure reset (e.g. into a Lateral day).
                    FreezeActiveZoneGray();
                    engine.SetStructure(verdict);
                    Print(string.Format("{0} {1:yyyy-MM-dd} structure: {2} ({3} RTH sessions)",
                        Name, et, verdict, rthHistory.Count));

                    // F3: feed the already-confirmed most recent swing leg immediately so the
                    // first zone of a newly-directional structure doesn't wait >=25 min for a
                    // brand-new swing confirmation. Same ordering + degenerate-leg guards as
                    // TrackSwings/FeedSwingLeg (low must precede high for a long leg, high must
                    // precede low for a short leg).
                    if (verdict == StructureVerdict.ValueUp
                        && lastSwingLowBar >= 0 && lastSwingHighBar >= 0 && lastSwingLowBar < lastSwingHighBar)
                        FeedSwingLeg(lastSwingLow, lastSwingHigh);
                    else if (verdict == StructureVerdict.ValueDown
                        && lastSwingHighBar >= 0 && lastSwingLowBar >= 0 && lastSwingHighBar < lastSwingLowBar)
                        FeedSwingLeg(lastSwingLow, lastSwingHigh);
                }
                else
                {
                    FreezeActiveZoneGray();
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
            if (evt == TfEventType.ZoneBuilt)
                RenderZoneBuilt();
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

        // ---- Rendering (Task 8) ------------------------------------------------------
        // All Draw.* calls below anchor on Times[1][0] (DateTime), never barsAgo: this
        // indicator only ever runs on BarsInProgress == 1 (the volumetric series), and
        // barsAgo is series-relative, so it would misalign against the chart's primary
        // series whenever that series isn't also 5-min bars.

        // Called from FeedSwingLeg right after engine.OnSwingLeg() returns ZoneBuilt, so
        // engine.Zone still reflects the just-built zone.
        private void RenderZoneBuilt()
        {
            // Core's OnSwingLeg overwrites Zone unconditionally on any new valid swing leg
            // -- no invalidation event fires for whatever zone this one replaces. Without
            // this, the old box would stay green/red on the chart forever, looking live
            // when it's actually dead.
            FreezeActiveZoneGray();

            var z = engine.Zone;
            zoneCounter++;
            DateTime now = Times[1][0];
            Brush fill = z.IsLong ? Brushes.LimeGreen : Brushes.Red;
            activeZone = new ZoneBox
            {
                Tag = "tfZone" + zoneCounter,
                Upper = z.UpperEdge,
                Lower = z.LowerEdge,
                CreatedTime = now,
                Fill = fill
            };
            Draw.Rectangle(this, activeZone.Tag, false, activeZone.CreatedTime, activeZone.Upper,
                now, activeZone.Lower, fill, fill, 20);
        }

        // Redraws the live zone box every bar with its right edge pinned to the current
        // bar (same "extend by reusing the tag" idiom as FVGFlow/PullbackZone in this
        // workspace) so it grows in place instead of a needing a far-future placeholder.
        private void ExtendActiveZone()
        {
            if (activeZone == null) return;
            Draw.Rectangle(this, activeZone.Tag, false, activeZone.CreatedTime, activeZone.Upper,
                Times[1][0], activeZone.Lower, activeZone.Fill, activeZone.Fill, 20);
        }

        // One-shot gray redraw, then stop tracking it -- the box freezes at this bar
        // instead of continuing to extend right. Shared by the explicit ZoneInvalidated
        // event and by RenderZoneBuilt (a silent zone replacement, no explicit event).
        private void FreezeActiveZoneGray()
        {
            if (activeZone == null) return;
            Draw.Rectangle(this, activeZone.Tag, false, activeZone.CreatedTime, activeZone.Upper,
                Times[1][0], activeZone.Lower, Brushes.Gray, Brushes.Gray, 20);
            activeZone = null;
        }

        private void RenderPreAlert(CandleLadder c)
        {
            bool isLong = engine.Structure == StructureVerdict.ValueUp;
            double y = isLong ? c.Low : c.High;
            preAlertCounter++;
            Draw.Dot(this, "tfPreAlert" + preAlertCounter, false, Times[1][0], y, Brushes.Yellow);
            if (State == State.Realtime)
                PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert2.wav");
        }

        private void RenderSignal(TfSignal sig)
        {
            signalCounter++;
            DateTime t0 = Times[1][0];
            DateTime t1 = t0 + SignalLineLookahead;
            bool isLong = engine.Structure == StructureVerdict.ValueUp;

            string triTag = "tfSignal" + signalCounter;
            double triY = isLong ? sig.SignalCandle.Low - 4 * TickSize : sig.SignalCandle.High + 4 * TickSize;
            if (isLong)
                Draw.TriangleUp(this, triTag, false, t0, triY, Brushes.Lime);
            else
                Draw.TriangleDown(this, triTag, false, t0, triY, Brushes.Red);

            Draw.Line(this, "tfStop" + signalCounter, false, t0, sig.Stop, t1, sig.Stop,
                Brushes.OrangeRed, DashStyleHelper.Dash, 2);
            Draw.Line(this, "tfTarget1" + signalCounter, false, t0, sig.Target1, t1, sig.Target1,
                Brushes.DeepSkyBlue, DashStyleHelper.Dash, 2);

            // Hot-path discipline: Compute() walks the whole developing-session profile,
            // so it only runs here, at the moment a signal actually fires -- never per bar.
            developingEth.Compute();
            double poc = developingEth.Poc;
            double lo = Math.Min(sig.Entry, sig.Target1);
            double hi = Math.Max(sig.Entry, sig.Target1);
            double? target2 = null;
            if (poc > lo && poc < hi)
            {
                target2 = poc;
                Draw.Line(this, "tfTarget2" + signalCounter, false, t0, poc, t1, poc,
                    Brushes.Gold, DashStyleHelper.Dash, 2);
            }

            if (State == State.Realtime)
            {
                PlaySound(NinjaTrader.Core.Globals.InstallDir + @"\sounds\Alert1.wav");
                AppendSignalToCsv(ToEt(t0), isLong, target2, sig);
            }
        }

        // Replay counts as State.Realtime in NT8 (same policy as the sounds above), which
        // is exactly what the evaluation gate needs; historical backfill must not spam the
        // CSV with every bar re-processed on load.
        private void AppendSignalToCsv(DateTime t0, bool isLong, double? target2, TfSignal sig)
        {
            string row = TrapMath.BuildCsvRow(t0, isLong, engine.Structure, sig, target2,
                ImbalanceRatio, TickSize);
            string path = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "TrapFlow_signals.csv");
            if (!System.IO.File.Exists(path))
                System.IO.File.AppendAllText(path, TrapMath.CsvHeader + Environment.NewLine);
            System.IO.File.AppendAllText(path, row + Environment.NewLine);
        }

        private void RenderStatus(CandleLadder c, bool inWindow)
        {
            string text = string.Format(
                "TrapFlow\nStructure: {0}\nState: {1}\nVolume: {2}\nWindow: {3}",
                engine.Structure, engine.State,
                c.TotalVolume >= VolumeThreshold ? "OK" : "KO",
                inWindow ? "OK" : "KO");
            Draw.TextFixed(this, "tfStatus", text, TextPosition.TopRight);
        }
    }
}
