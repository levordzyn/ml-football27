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
| Home / Draw / Away | 33% / 27% / 40% | ~45% / 25% / 30% | draw rate good; away runs a bit hot, home a bit cold with 4-4-2 on both sides (see below) |
| Goals per match (combined) | 3.10 | ~2.7 | close |
| Shots per team | 16.4 | ~12–14 | ~20% high |
| xG per shot (mean) | ~0.10 | ~0.10–0.12 | good |
| Fouls per match (combined) | 20.5 | ~22 | close |
| Yellow cards (combined) | 3.10 | ~3.5 | close |
| Red cards (combined) | 0.10 | ~0.06 | close |
| Corners (combined) | 8.9 | ~10 | close |
| Passes per team | ~418 | ~400–550 | good |
| Possession split | ~50/50 | ~50/50 | good |

A strength-gap sweep (same formation both sides, 75 overall baseline):

| Gap | Stronger side win rate |
|---|---|
| 0 (equal) | ~33–40% each way (draws ~27%) |
| 6 points | ~50–58% |
| 10 points | ~65–66% |
| 20 points | ~90% |

This now reads as a believable curve — a 6-point edge helps but doesn't dominate, a
20-point gap (a genuinely one-sided fixture) wins the vast majority without being a
mathematical certainty (`HigherOverallTeamWinsMoreOftenButNotEveryTime` holds at ~86% in
the committed test run, never 100%).

**One residual oddity:** with 4-4-2 on both sides, Away's win share runs a few points above
Home's even though the engine is confirmed side-symmetric (`SideDoesNotSystematically
DetermineTheWinnerWhenTeamsAreEqual` passes comfortably, 30–65% band, typically ~35–45%).
This looks like formation-specific noise in how 4-4-2's specific coordinates interact with
the current pass/turnover model rather than a Home/Away bug — worth a follow-up isolation
test the same way the original Home-bug was caught (both sides same formation, vary which
side plays which formation, across more seeds) before trusting it fully.

## What changed in this calibration pass

Three more real issues, not just constants, surfaced while chasing the numbers above:

- **Red cards spiraled when yellow-card frequency went up.** Raising the base yellow
  probability to hit the 3.5-combined target initially pushed reds to 5x target, because
  fouls concentrate on whichever defender is nearest the ball when a turnover fires — the
  same player could rack up a second yellow far more than real foul distribution supports.
  Fixed with real football logic, not a fudge: a player already on a yellow now fouls far
  less often on the next last-ditch challenge (he's visibly more careful), which is
  specifically what keeps a single booked defender from stacking two cards. That's in
  `MatchEngine.ResolveTurnover`, not a global red-card suppressor.
- **Goals ran ~60% hot.** `ShotQuality.BaseXg`'s damping constant and the in-box
  shot-trigger probability both needed roughly another 25–30% cut beyond the previous
  pass's numbers to land mean shot xG near 0.10.
- **Strength-gap determinism** needed a further widened attribute divisor in
  `MatchEngine.FailChance` (600, up from 420) to stop many small per-action attribute edges
  compounding into near-certainty over a full match.

## What's still worth another pass

- **Shots/team (~16) still runs ~20% above the 12–14 real-football range.** Goals and xG
  are now well calibrated *given* this shot volume, so cutting shots further would need a
  matching reduction elsewhere to avoid re-dropping goals below target — a paired tweak,
  not a one-line fix.
- **The Away-leaning split under matched 4-4-2** flagged above.
- Everything else — fouls, cards, corners, passes, possession, the strength-gap curve — is
  now close enough that further tuning should happen against real `dotnet test` runs and
  larger batches (the project's own Phase 11 scope), not single-session guesswork.

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
