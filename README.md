<div align="center">

<h1>TrapFlow</h1>

<p>
  <b>An NT8 indicator that mechanizes a discretionary order-flow trap setup for MNQ.</b><br>
  It never places an order — it locates a zone, watches order flow inside it, and marks a
  signal candle for a human to decide on. Unvalidated until the Replay gate below passes.
</p>

<p>
  <a href="#what-it-does">What it does</a> ·
  <a href="#the-source-and-what-this-is-not">Source</a> ·
  <a href="#install">Install</a> ·
  <a href="#how-it-works">How it works</a> ·
  <a href="#evaluation-gate">Evaluation gate</a> ·
  <a href="#parameters">Parameters</a>
</p>

<p>
  <img src="https://img.shields.io/badge/status-unvalidated-orange?style=flat-square" alt="">
  <img src="https://img.shields.io/badge/type-indicator-lightgrey?style=flat-square" alt="">
  <img src="https://img.shields.io/badge/platform-NinjaTrader%208-1f6feb?style=flat-square" alt="">
  <img src="https://img.shields.io/badge/instrument-MNQ-f7931a?style=flat-square" alt="">
  <img src="https://img.shields.io/badge/license-MIT-blue?style=flat-square" alt="">
</p>

<img src="docs/assets/hero.png" width="100%" alt="TrapFlow — MNQ 5-minute Market Replay chart with a trap zone box, signal arrow, and stop/target lines">

> **Screenshots pending — nothing above is a stand-in.** TrapFlow needs a live or Market
> Replay MNQ chart in NinjaTrader 8 to produce anything worth showing, and that only happens
> once the Replay evaluation (below) is under way. Needed in `docs/assets/`:
> - `hero.png` — MNQ 5-min Replay chart with a trap zone box, a signal arrow, and the
>   stop/target lines all visible.
> - `absorption.png` — a zoomed absorption pre-alert marker with the state label in frame.
> - `properties.png` — the indicator's Properties panel showing the frozen defaults.
>
> Capture from **Windows NT8, not WSL** (this repo's tooling has no GUI access to NT8), at
> ≥1440px wide and **devicePixelRatio 2** — WSL reports 1 and the captures come out soft.

</div>

---

## What it does

TrapFlow watches a 5-minute MNQ chart and, on days where recent value-area structure agrees
on a direction, draws a fibonacci "trap zone" outside that value area and waits for order-flow
evidence — an absorption candle followed by a failed second push — before marking a signal.
It is read-only: a chart marker, a suggested stop, two targets, and a CSV row. Nothing is
sent to the broker.

- Computes its own 5-minute **Volumetric** series internally via `AddVolumetric()`, so the
  chart's own bar type doesn't matter.
- Builds session volume profiles (POC/VAH/VAL) from that series — no dependency on NT8's
  Order Flow Volume Profile drawing tool.
- Runs a 6-state machine per direction (long and short are exact mirrors) described below.
- Logs every signal to a CSV with the metrics behind it, so the Replay evaluation has numbers
  to check against judgment.

## The source and what this is not

The process comes from **Chris Kmer**, winner of the Robbins World Cup (micro futures
division, July), as he described it in a public interview. TrapFlow is an independent
mechanization of that process for personal research — **it is not affiliated with, reviewed
by, or endorsed by Chris Kmer**, and it necessarily simplifies a discretionary read into fixed
rules. It deliberately drops the GEX / options-flow context he also references; this is the
order-flow half only.

This is a research tool, not financial advice. Trading futures carries substantial risk of
loss. Nothing here is a recommendation to trade any instrument.

## Install

1. Copy both `TrapFlow.cs` and `TrapFlowCore.cs` into
   `Documents\NinjaTrader 8\bin\Custom\Indicators\`.
2. Open the NinjaScript Editor in NT8 and press **F5** to compile.
3. Add `TrapFlow` to an MNQ chart. The chart's own bar type doesn't matter — the indicator
   builds its own 5-minute volumetric series internally.

Requires **NinjaTrader 8** with a data feed that supports Volumetric bars (bid/ask tick data)
on MNQ.

## How it works

Full state machine, thresholds, and rationale: [`docs/specs/2026-08-11-trapflow-design.md`](docs/specs/2026-08-11-trapflow-design.md).
Short version — long side described, short is the exact mirror:

1. **Structure** — at each session start, compare the last 3 completed RTH sessions' volume
   profiles. **POC & VAL** strictly rising across all 3 → long armed. **POC & VAH** strictly
   falling across all 3 → short armed. Anything else → dormant.
2. **Zone** — the most recent swing leg in the structure direction, fib-retraced at
   0.705 / 0.788 / 0.886. Only drawn if the whole zone sits outside the developing session's
   value area.
3. **Armed** — price is trading inside the zone, inside the 09:30–11:00 ET window, on a
   5-min candle with volume above the threshold.
4. **Absorption** (pre-alert) — a candle inside the zone with heavy opposing delta, a POC in
   the wrong third of its range, and a recovery close. Sound + marker.
5. **Signal** — within the next few candles, one that fails to make a new extreme and closes
   with strong buy-side (or sell-side) imbalance at multiple price levels. Arrow, stop, two
   targets, sound, CSV row.
6. **Invalidation** — a close beyond the 0.886 level kills the zone; back to looking for a
   new one.

<img src="docs/assets/absorption.png" width="100%" alt="Absorption pre-alert marker with the state label in frame">

## Evaluation gate

TrapFlow is **unvalidated**. Before any of this becomes a trading decision, let alone a
strategy, it has to clear a manual Market Replay gate:

- **≥ 30 RTH sessions** run in Market Replay.
- **Quantitative** — across all logged signals, a hypothetical bracket (stop as marked,
  target at 1.5R) reaches **win rate ≥ 55% and profit factor ≥ 1.4**, minimum sample 30
  signals. (Reference point: Kmer self-reports 60–65% / 1.8.)
- **Qualitative** — at least **22 of the 30 sessions** judged "the signals were sensible" by
  the person running Replay.
- Fail → one documented parameter-tuning round, then kill or archive. Pass → a strategy spec
  gets considered separately — TrapFlow itself stays an indicator either way.

The CSV log (`TrapFlow_signals.csv` in the NT8 Documents folder) exists to make that
evaluation objective rather than a gut call.

## Parameters

Defaults are Kmer's numbers, frozen for the evaluation — do not tune before the gate above
has run.

| Parameter | Default | Meaning |
|---|---|---|
| Volume Threshold | 20,000 | Min. contracts in a 5-min candle for the volume filter to pass |
| Absorption Delta Pct | 0.15 | Min. `\|delta\| / candle volume` on the absorption candle |
| Imbalance Ratio | 4.0 (400%) | Min. bid×ask imbalance ratio counted at a price level |
| Imbalance Min Levels | 2 | Min. price levels meeting that ratio on the signal candle |
| Signal Window Bars | 3 | Candles after absorption in which a signal can still fire |
| Swing Strength | 5 | `Swing()` strength used to find the zone's anchor leg |
| Structure Sessions | 3 | RTH sessions compared for the structure verdict |
| Window Start / End (ET) | 0930 / 1100 | Time-of-day filter for arming a zone |
| Fib levels | 0.705 / 0.788 / 0.886 | Zone boundaries and invalidation level (fixed, not exposed) |

<img src="docs/assets/properties.png" width="100%" alt="TrapFlow Properties panel showing the frozen parameter defaults">

## Limits

- No automated trade management — it is an indicator, not a strategy. It never places,
  modifies, or cancels an order.
- No Level 2 features (icebergs, order clusters) and no GEX/volatility-regime context — both
  out of scope for v1, see [the design doc](docs/specs/2026-08-11-trapflow-design.md#non-goals-v1).
- No historical batch backtest engine — the evaluation method is manual Market Replay, because
  the signal depends on tick-built volumetric bars that a bar-replay backtest can't reproduce
  faithfully.
- Absorption and second-failure thresholds are a mechanical translation of a discretionary
  read; they are educated guesses until Replay says otherwise.

## Tests

`TrapFlowCore.cs` — the pure logic (profiles, structure verdict, zone math, absorption/signal
predicates, the state machine, CSV row building) — has zero NinjaTrader dependencies and is
covered by 30 xunit tests:

```console
$ dotnet test tests/TrapFlow.Tests -v q
Passed! - Failed: 0, Passed: 30, Skipped: 0
```

## License

MIT — see [LICENSE](LICENSE).
