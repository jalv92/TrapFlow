# TrapFlow — NT8 Order-Flow Trap Indicator (Design)

**Date:** 2026-08-11
**Status:** Approved design, pending implementation plan
**Source:** Strategy of Chris Kmer (Robbins World Cup micro champion, July, +100%), extracted from his interview (youtube.com/watch?v=PL7LKUsCgIQ). GEX/options context deliberately dropped.

## Goal

An NT8 **indicator** (not a strategy — it never places orders) for **MNQ** that mechanizes Kmer's four-layer process: structure → location (fib discount/premium outside value) → order-flow confirmation (absorption + second failure) → signal with suggested stop/targets. Symmetric long/short from day 1. Evaluated by Javier in Market Replay (gate below); only if it passes do we consider a strategy.

## Non-goals (v1)

- No GEX or any volatility-regime proxy.
- No Level 2 features (icebergs, order clusters). The core is fully computable from tick data via Volumetric bars.
- No automated trade management, no auto-bracket, no strategy.
- No historical batch backtest (option A was rejected; gate is manual Replay).

## Architecture

Single NT8 indicator, C#, in its own repo `projects/Trading/TrapFlow`. The chart runs a normal **5-minute MNQ** series; the indicator adds its own **Volumetric 5-min series** via `AddVolumetric()` so the user's chart type doesn't matter. Session profiles (POC/VAH/VAL) are computed internally from the volumetric data — no dependency on NT8's Order Flow Volume Profile drawing tool.

### State machine (long side described; short side is the exact mirror)

**1. STRUCTURE (computed at session start, re-evaluated daily)**
Volume profiles of the last 3 completed RTH sessions.
- *Value-up*: POC and VAL both strictly rising across the 3 sessions → only long setups armed.
- *Value-down*: POC and VAH both strictly falling → only short setups armed.
- Anything else → *lateral*: indicator dormant (label shows why).

**2. ZONE**
Most recent completed swing leg in the structure direction (NT8 `Swing`, strength parameterized): swing low → swing high for longs. Fib retracement levels **0.705 / 0.788 / 0.886** drawn as a box (the trap zone: 0.705 to 0.886).
Validity: the whole zone must sit **outside the value area** — for longs, the 0.705 price must be **below the VAL** of the *developing current-day ETH profile* (session template: ETH starting 18:00 ET prior day). A zone inside value is not drawn.
Swing anchors are bar highs/lows of the volumetric series (not closes).

**3. ARMED**
Price trades inside the zone, AND time is within **09:30–11:00 ET**, AND current 5-min candle volume **≥ 20,000 contracts**. Both filters are hard: outside the window or below the threshold, no state advances.

**4. ABSORPTION (pre-alert)**
A 5-min candle inside the zone with, for longs:
- Negative delta with **|delta| ≥ 15% of candle volume** (parameter `AbsorptionDeltaPct`),
- Candle POC in the **lower third** of the candle's range,
- Recovery: candle closes in the upper half of its range, OR the next candle closes bullish.
Sound pre-alert + chart marker. Starts the signal window.

**5. SIGNAL**
Within the next **3 candles** (parameter `SignalWindowBars`), a candle that:
- Makes its low **above** the absorption candle's low (sellers fail higher),
- Closes bullish,
- Has **≥ 2 price levels in its lower third with buy-side bid×ask imbalance ≥ 400%** (parameters `ImbalanceRatio`, `ImbalanceMinLevels`).
→ Signal arrow at close, suggested **stop 1 tick below that candle's low**, targets drawn: **T1 = the fib anchor swing high**, **T2 = developing session POC** (drawn only if between entry and T1). Sound alert. CSV row written.
If the window expires without a signal, fall back to ARMED.

**6. INVALIDATION**
A 5-min close beyond the 0.886 (below, for longs) while the zone is active → zone killed, box grayed out, back to looking for a new zone. This is the only hard invalidation, per the source strategy.

### Parameters (defaults = Kmer's numbers)

| Parameter | Default |
|---|---|
| VolumeThreshold (contracts / 5-min candle) | 20,000 |
| Session window | 09:30–11:00 ET |
| Fib levels | 0.705 / 0.788 / 0.886 |
| SwingStrength | 5 |
| AbsorptionDeltaPct (abs(delta) / candle volume) | 15% |
| ImbalanceRatio | 4.0 (400%) |
| ImbalanceMinLevels | 2 |
| SignalWindowBars | 3 |
| StructureSessions | 3 |

No tuning before the Replay gate: defaults are frozen for the evaluation.

### Display / UX

- Trap-zone box (long: green tint; short: red tint; invalidated: gray).
- Top-right label: structure verdict, machine state, volume filter OK/KO.
- Absorption marker (small dot), signal arrow, dashed stop/target lines.
- Two sounds: pre-alert (absorption) and signal.

### CSV log

Append-only `TrapFlow_signals.csv` in the NT8 Documents folder. One row per signal: timestamp, direction, structure state, zone prices (705/788/886), entry, stop, T1, T2, absorption candle metrics (volume, delta, delta%, POC position), signal candle metrics (imbalance levels count, max ratio, volume). Purpose: give the Replay evaluation objective numbers alongside Javier's judgment.

## Evaluation gate (option B — manual Market Replay)

- Javier runs **≥ 30 RTH sessions** in Market Replay (NT8ReplayDownloader supplies the days; Replay streams ticks, so the volumetric series builds correctly).
- Quantitative: across all logged signals, a hypothetical bracket (stop as marked, target 1.5R) must reach **WR ≥ 55% and PF ≥ 1.4** (reference: Kmer self-reports 60–65% / 1.8). Minimum sample 30 signals.
- Qualitative (BigPrints pattern): **≥ 22 of 30 sessions** judged "signals were sensible" by Javier.
- Fail → one documented parameter-tuning round max, then kill or archive. Pass → consider a strategy (separate spec).

## Build / deploy

- Compile with `nt8c` (PostToolUse hook auto-validates).
- Deploy the `.cs` to the Windows `NinjaTrader 8/bin/Custom/Indicators/` folder after every closed task (workspace golden rule), with the two post-deploy checks from `[[nt8-deploy-copy-files]]`.
- Own git repo; implementation by the `trading-*` agent team; `nt8-indicator` + `nt8-common` skills read before coding.

## Risks / open questions

1. **Discretion → mechanics.** The absorption and second-failure definitions are a mechanical translation of a discretionary read; the thresholds (15%, 400%, lower third) are educated guesses until Replay says otherwise. This is the most likely failure point.
2. **VA source.** "Outside value" uses the developing ETH profile (matches Kmer's Asia/London example). If zones systematically fail validity, the first alternative is prior-RTH-session VA.
3. **Signal frequency unknown.** Strict 3-session structure + zone-outside-value may yield very few armed days. If Replay sessions average well below 1 signal, loosening structure to a 2-session comparison is the first knob.
4. **Workspace precedent.** Naive pullback/retest studies died (Pullback, break-retest, trendline-retest). This differs by signal class (order-flow absorption inside a located zone, volume-regime filtered) — but the base rate says expect a kill; the gate exists to make that cheap.
