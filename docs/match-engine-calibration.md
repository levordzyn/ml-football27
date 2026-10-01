# Match engine calibration — Phase 2/3 vertical slice

What's here: `src/ML.Core/Match/` — a self-contained match engine (no I/O, no UI) that
simulates one fixture tick by tick: possession, passing, dribbling, crosses, through balls,
shots, fouls, cards, offside, corners, substitutions, fatigue, and a bounded-imperfection AI
manager for CPU sides. Every match is seeded and fully reproducible (`MatchEngineTests.
SameSeedAndTeamsProduceTheExactSameMatch`). Team and player stats, xG, and post-match
ratings are folded from the event log by `Projectors`, not computed separately.

This is the vertical slice proposed in the Phase 1 audit: one match, two formations, full
event pipeline, calibration harness. Tactics depth, the AI-manager sophistication, the
viewer, and career-layer wiring are later phases.

## How these numbers were produced

`dotnet test` can't run here (NuGet is blocked in this environment — see the chat for the
full explanation). Every number below, and every assertion in
`tests/ML.Core.Tests/Match/MatchEngineTests.cs`, was verified by compiling `ML.Core`
directly and replaying the exact same checks and batch simulations in a throwaway harness.
Run `dotnet test` yourself to confirm; if anything in this doc turns out to be inconsistent
with a local `dotnet test` run, trust the local run.

## Three real bugs this surfaced

Calibration caught these; they're fixed in the current code, not just worked around:

1. **Shot geometry used the wrong position.** The chosen actor's own slowly-drifting body
   marker was used for shot location instead of the ball's actual spell position, so every
   shot computed as if taken from near midfield. Fixed by snapping the acting player to the
   ball when he's selected to act (`MatchEngine.StepOnce`).
2. **A striker and his own keeper could loop passes forever near their own goal.** The
   default pass target pool didn't exclude the goalkeeper, so under the wrong conditions a
   CF and his GK would ping-pong the ball deep in their own third indefinitely, handing the
   opponent trivial chances. Fixed by excluding the keeper from pass/through-ball receiver
   pools.
3. **Home had a large, spurious structural advantage, independent of attributes or
   formation.** A positional-drift line mixed attack-frame and absolute coordinates — it
   happened to be a no-op for Home (whose frame equals absolute) but corrupted Away's shape
   continuously throughout the match. Confirmed via a formation-swap isolation test (same
   formation both sides, home win rate stayed pinned near 65–80% regardless of which side
   got which formation) before the fix, and verified gone after
   (`SideDoesNotSystematicallyDetermineTheWinnerWhenTeamsAreEqual`).

## Current numbers vs. real football

Matched formations both sides (4-4-2 v 4-4-2), 75 overall each, 400 simulated matches:

| Metric | Engine | Real football | Gap |
|---|---|---|---|
| Home / Draw / Away | 40% / 19% / 41% | ~45% / 25% / 30% | draws low, otherwise close |
| Goals per match (combined) | 4.3 | ~2.7 | **~60% too high** |
| Shots per team | 16.6 | ~12–14 | ~20% too high |
| xG per shot (mean) | 0.15 | ~0.10–0.12 | somewhat high |
| Fouls per match (combined) | 20.6 | ~22 | close |
| Yellow cards (combined) | 1.7 | ~3.5 | low |
| Red cards (combined) | 0.18 | ~0.06 | **~3x too high** |
| Corners (combined) | 8.1 | ~10 | close |
| Passes per team | ~365 | ~400–550 | slightly low |
| Possession split | ~50/50 | ~50/50 | good |

A strength-gap test (85 vs 65 overall, same formation) gives the stronger side an 87–96%
win rate. That's too deterministic — real upsets at that gap are rarer than even, not
essentially never.

## Why goals and reds are still off, and what would fix them

- **Goals too high:** shots are in the right range, so this is conversion, not shot volume.
  `ShotQuality.BaseXg`'s damping constant (0.24) and the in-box shot-trigger probability in
  `MatchEngine.ChooseAction` both need another downward pass — I made three successive cuts
  during this session and ran out of budget before landing in range. A ~15–20% further cut
  to both should get combined goals under 3.5.
- **Reds too high:** fouls concentrate on whichever defender happens to be nearest the ball
  when a turnover fires, so the same player can pick up two yellows more often than real
  football's foul distribution would produce. The yellow-card probability is already tuned
  down to compensate, which is why yellows are now *under* target. A structural fix (the
  AI manager keeping a booked player away from last-ditch challenges, or spreading
  defensive duty more evenly) would let both numbers hit target at once.
- **Strength-gap too deterministic:** this is the classic many-small-decisions-compound-to-
  certainty effect of a high-tick-count simulation. `MatchEngine.FailChance`'s attribute
  divisor (420) already got one increase during this session; it likely needs another, or a
  injected noise term independent of attributes, to preserve match-to-match variance at a
  given attribute gap.

None of these are structural problems — they're constants that need more of the batch-and-
retune cycle this file documents. That's exactly what the project's own Phase 11
("Calibration": thousands of matches, batch testing, tune, regression-test) is for.

## What's deliberately simplified in this slice

- Every modelled shot comes from inside the box (`ChooseAction`'s `inBox` test) — there's no
  long-range or speculative-shot model yet, so the shot population is already better quality
  than a real match's full shot mix. `ShotQuality.BaseXg`'s damping factor exists to
  compensate for this.
- The AI manager (`ManagerPolicy`) only handles fatigue-based substitutions and a few
  scoreline/time tactical triggers — no transfer-window-aware squad logic, no set-piece
  specialism, no opponent-scouting adjustments.
- Penalties, corners, and free kicks get a shot opportunity but no distinct set-piece routine
  (no dedicated near-post/far-post/short-corner model).
- Player positions update via a single exponential pull toward a role target; there's no
  explicit off-ball movement model (overlaps, underlaps, rotations) beyond what the
  `PlayerInstruction` pull weights encode.

## Running it yourself

```csharp
var home = /* build a TeamSheet */;
var away = /* build a TeamSheet */;
var record = MatchSession.RunAutomatic(new MatchSetup(seed, home, away));
// record.HomeGoals, record.AwayGoals, record.Events (full causal log)
var homeStats = Projectors.TeamStats(record.Events, Side.Home);
var playerStats = Projectors.PlayerStats(record.Events, home.Starters.Select(p => p.Id));
```

For an interactive/half-time-aware game, construct `MatchEngine` directly, call
`AdvanceTo` in whatever increments the UI wants (speed never changes the outcome — only the
seed and the commands applied, and when, can), and call `ApplyCommand` for tactics changes,
substitutions, or `CommandKind.Resume` to leave half-time.
