using ML.Core.Domain;

namespace ML.Core.Match;

/// <summary>
/// Runs one match, one action at a time. Every call to <see cref="AdvanceTo"/> resumes exactly
/// where the previous one stopped — the spell in progress, the ball position, the clock — so the
/// result of a match does not depend on how often or in what batches the caller advances it.
/// Only the seed, the two team sheets, and the commands applied (and when) can change the outcome.
/// </summary>
public sealed class MatchEngine
{
    private const double PitchLengthMetres = 105.0;
    private const double PitchWidthMetres = 68.0;

    private readonly MatchState _state;
    private readonly MatchRandom _flow;
    private readonly MatchRandom _manager;
    private readonly ManagerPolicy _policy = new();

    // The possession spell currently in progress.
    private Side _spellSide;
    private double _spellX;
    private double _spellY;
    private int _spellCauseId = -1;
    private int _spellActions;

    private int _halfEndSecond = 45 * 60;
    private int _fullEndSecond = 90 * 60;
    private int _foulsThisHalf;
    private int _subsThisHalf;
    private int _injuriesThisHalf;
    private int _nextManagerCheckSecond = 5 * 60;

    public MatchEngine(MatchSetup setup)
    {
        setup.Home.Validate();
        setup.Away.Validate();
        _state = new MatchState { Setup = setup, Teams = new[] { Build(setup.Home, Side.Home), Build(setup.Away, Side.Away) } };
        _flow = MatchRandom.For(setup.Seed, stream: 1);
        _manager = MatchRandom.For(setup.Seed, stream: 2);
    }

    public MatchState State => _state;
    public IReadOnlyList<MatchEvent> Events => _state.Events;
    public bool IsFullTime => _state.Phase == MatchPhase.FullTime;

    private static TeamMatchState Build(TeamSheet sheet, Side side)
    {
        var onPitch = new List<PlayerMatchState>(sheet.Starters.Count);
        for (var i = 0; i < sheet.Starters.Count; i++)
        {
            var slot = sheet.Formation.Slots[i];
            var abs = ToAbsolute(side, slot.X, slot.Y);
            var player = sheet.Starters[i];
            onPitch.Add(new PlayerMatchState
            {
                Player = player,
                Side = side,
                SlotIndex = i,
                HomeSlot = slot,
                X = abs.x,
                Y = abs.y,
                Fatigue = player.StartFatigue,
                Instruction = sheet.Instructions is not null && sheet.Instructions.TryGetValue(player.Id, out var ins)
                    ? ins : PlayerInstruction.Default,
            });
        }

        return new TeamMatchState { Sheet = sheet, Side = side, Tactics = sheet.Tactics.Clamped(), OnPitch = onPitch, Bench = sheet.Bench.ToList() };
    }

    /// <summary>Home attacks increasing X; away is mirrored so both teams "see" X 0..1 as their own goal to the opponent's.</summary>
    private static (double x, double y) ToAbsolute(Side side, double x, double y) =>
        side == Side.Home ? (x, y) : (1.0 - x, 1.0 - y);

    private static double AttackFrameX(Side side, double absX) => side == Side.Home ? absX : 1.0 - absX;
    private static double AttackFrameY(Side side, double absY) => side == Side.Home ? absY : 1.0 - absY;

    public void ApplyCommand(MatchCommand command)
    {
        var team = _state.Of(command.Side);
        switch (command.Kind)
        {
            case CommandKind.Tactics when command.Tactics is not null:
                team.Tactics = command.Tactics.Clamped();
                Log(EventKind.TacticalChange, command.Side, command.Side, 0, 0, causeId: -1);
                break;

            case CommandKind.Instruction:
                var target = team.Find(command.PlayerId);
                if (target is not null) target.Instruction = command.Instruction;
                break;

            case CommandKind.Substitute:
                Substitute(team, command.PlayerId, command.OtherPlayerId);
                break;

            case CommandKind.Resume when _state.Phase == MatchPhase.HalfTime:
                StartSecondHalf();
                break;
        }

        _state.CommandLog.Add(command);
    }

    private void Substitute(TeamMatchState team, long offId, long onId)
    {
        var off = team.Find(offId);
        var on = team.Bench.FirstOrDefault(p => p.Id == onId);
        if (off is null || on is null || team.SubsUsed >= 5) return;

        off.SubbedOff = true;
        team.Bench.Remove(on);
        team.OnPitch.Add(new PlayerMatchState
        {
            Player = on, Side = team.Side, SlotIndex = off.SlotIndex, HomeSlot = off.HomeSlot,
            X = off.X, Y = off.Y, Fatigue = on.StartFatigue,
        });
        team.SubsUsed++;
        _subsThisHalf++;
        Log(EventKind.Substitution, team.Side, team.Side, onId, offId, causeId: -1);
    }

    /// <summary>Simulate up to (but not necessarily exactly to) the given match second.</summary>
    public void AdvanceTo(int targetSecond)
    {
        if (_state.Phase == MatchPhase.PreMatch) KickOff(MatchPhase.FirstHalf, Side.Home);

        var guard = 0;
        while (_state.Second < targetSecond && _state.Phase is MatchPhase.FirstHalf or MatchPhase.SecondHalf && guard++ < 200_000)
        {
            MaybeRunManagers();
            StepOnce();
        }
    }

    /// <summary>Play the whole match at once, letting the AI manager policy run both sides unless marked human-controlled.</summary>
    public void RunToFullTime() => AdvanceTo(int.MaxValue);

    private void MaybeRunManagers()
    {
        if (_state.Second < _nextManagerCheckSecond) return;
        _nextManagerCheckSecond += 5 * 60;
        foreach (var team in _state.Teams)
            if (!team.Sheet.HumanControlled)
                _policy.Review(_state, team, _state.Of(team.Side.Other()), _manager);
    }

    private void StepOnce()
    {
        if (_spellActions == 0) StartSpell(_state.Possession, _state.BallX, _state.BallY, causeId: -1);

        var possessing = _state.Of(_spellSide);
        var defending = _state.Of(_spellSide.Other());
        var progress = AttackFrameX(_spellSide, _spellX);
        var second = _state.Second;

        var actor = PickActor(possessing, _spellX, _spellY, progress);
        actor.X = _spellX; // he's the one on the ball now — his geometry for this action is the ball's
        actor.Y = _spellY;
        var action = ChooseAction(actor, progress, _spellY, possessing.Tactics);

        if (action == Action.Shot)
        {
            RunAction(actor, action, possessing, defending, progress);
        }
        else
        {
            var pressure = PressureOn(actor, defending);
            var quality = PressResistance(actor);
            var failChance = FailChance(action, pressure, quality, defending.Tactics);

            if (_flow.Chance(failChance))
                ResolveTurnover(actor, defending, progress, action);
            else
                RunAction(actor, action, possessing, defending, progress);
        }

        AdvanceClock(2 + _flow.Next(4)); // 2..5s per action
        AccumulateFatigue(second, _state.Second);
        CheckHalfBoundaries();
    }

    /// <summary>
    /// Chance the chosen action breaks down against the defence. Riskier actions (a dribble, a
    /// long ball) fail more even before pressure is considered; pressure and press-resistance
    /// shift it from there, gently — a big attribute gap should sway this, not decide it outright.
    /// </summary>
    private static double FailChance(Action action, double pressure, double quality, MatchTactics defTactics)
    {
        var baseline = action switch
        {
            Action.Pass => 0.15,
            Action.Carry => 0.30,
            Action.Cross => 0.38,
            Action.ThroughBall => 0.34,
            _ => 0.15,
        };
        var attributeShift = (pressure - quality) / 600.0;
        var pressingShift = (defTactics.Pressing - 0.5) * 0.10;
        return Math.Clamp(baseline + attributeShift + pressingShift, 0.04, 0.62);
    }

    private void AdvanceClock(int bySeconds) => _state.Second += bySeconds;

    private void CheckHalfBoundaries()
    {
        if (_state.Phase == MatchPhase.FirstHalf && _state.Second >= _halfEndSecond)
        {
            Log(EventKind.HalfTime, Side.Home, _state.Possession, 0, 0, causeId: -1);
            _state.Phase = MatchPhase.HalfTime;
            _spellActions = 0;
        }
        else if (_state.Phase == MatchPhase.SecondHalf && _state.Second >= _fullEndSecond)
        {
            Log(EventKind.FullTime, Side.Home, _state.Possession, 0, 0, causeId: -1);
            _state.Phase = MatchPhase.FullTime;
        }
    }

    private void KickOff(MatchPhase phase, Side kicksOff)
    {
        _state.Phase = phase;
        _state.Possession = kicksOff;
        _state.BallX = 0.5;
        _state.BallY = 0.5;
        _spellActions = 0;
        Log(EventKind.KickOff, kicksOff, kicksOff, 0, 0, causeId: -1);
    }

    private void StartSecondHalf()
    {
        _foulsThisHalf = 0;
        _subsThisHalf = 0;
        _injuriesThisHalf = 0;
        KickOff(MatchPhase.SecondHalf, Side.Away);
        _fullEndSecond = 90 * 60 + StoppageSeconds();
    }

    private int StoppageSeconds()
    {
        var minutes = 1 + _foulsThisHalf / 7 + _subsThisHalf / 2 + _injuriesThisHalf;
        return 60 * Math.Clamp(minutes, 1, 6);
    }

    private void StartSpell(Side side, double x, double y, int causeId)
    {
        _spellSide = side;
        _spellX = x;
        _spellY = y;
        _spellCauseId = causeId;
        _spellActions = 1;
        _state.Possession = side;
    }

    private static PlayerMatchState PickActor(TeamMatchState team, double x, double y, double progress)
    {
        var eligible = team.OnPitch.Where(p => p.OnPitch && !p.Player.Position.IsGoalkeeper()).ToList();
        if (eligible.Count == 0) return team.Goalkeeper;

        return eligible
            .OrderBy(p => Math.Sqrt(Sq(p.X - x) + Sq(p.Y - y) * 0.5)
                          - RoleAffinity(p.Player.Position, progress) * 0.15)
            .First();
    }

    private static double Sq(double v) => v * v;

    private static double RoleAffinity(Position position, double progress) => position.Group() switch
    {
        PositionGroup.Forward => progress,
        PositionGroup.Defender => 1.0 - progress,
        _ => 1.0 - Math.Abs(progress - 0.5),
    };

    private static double PressureOn(PlayerMatchState actor, TeamMatchState defending)
    {
        var nearby = defending.OnPitch.Where(p => p.OnPitch)
            .OrderBy(p => Sq(p.X - actor.X) + Sq(p.Y - actor.Y))
            .Take(2).ToList();
        if (nearby.Count == 0) return 20;

        var pressing = defending.Tactics.Pressing;
        return nearby.Average(p =>
            (p.Effective(p.Player.Defending) + InstructionProfiles.Get(p.Instruction).PressBias) * (0.7 + 0.6 * pressing));
    }

    private static double PressResistance(PlayerMatchState actor) =>
        (actor.Effective(actor.Player.Vision) + actor.Effective(actor.Player.Passing) + actor.Effective(actor.Player.Dribbling)) / 3.0;

    private void ResolveTurnover(PlayerMatchState actor, TeamMatchState defending, double progress, Action? attemptedAction = null)
    {
        var defender = defending.OnPitch.Where(p => p.OnPitch)
            .OrderBy(p => Sq(p.X - actor.X) + Sq(p.Y - actor.Y)).First();

        // A mistimed challenge is more likely the deeper and more dangerous the situation, but
        // even a last-ditch tackle is clean far more often than not. A player already on a
        // yellow holds back from the next one, which is also what keeps fouls from piling up
        // onto a single booked defender.
        var desperation = Math.Clamp(0.05 + 0.16 * Math.Max(0, progress - 0.55), 0.05, 0.22)
            * (defender.YellowCards >= 1 ? 0.12 : 1.0);
        if (_flow.Chance(desperation))
        {
            var evId = Log(EventKind.Foul, defending.Side, _spellSide, defender.Player.Id, actor.Player.Id, actor.X, actor.Y,
                value: attemptedAction is { } fa ? (int)fa + 1 : 0);
            _foulsThisHalf++;
            MaybeCard(defender, defending.Side, denyingCleanChance: progress > 0.80);

            var isPenalty = progress > 0.85 && Math.Abs(_spellY - 0.5) < 0.28;
            var restartId = Log(isPenalty ? EventKind.Penalty : EventKind.FreeKick, _spellSide, _spellSide,
                actor.Player.Id, 0, actor.X, actor.Y, causeId: evId);

            // buildupCauseId is the Penalty/FreeKick event's own id, not the Foul's — that's what
            // lets AssistKindFor resolve to Penalty/FreeKick and the xG model's set-piece
            // multiplier actually apply, and what a Goal's two-hop assist lookup expects to chain
            // through.
            if (isPenalty) TakeShot(actor, _state.Of(_spellSide), defending, oneOnOne: true, buildupCauseId: restartId);
            else TakeFreeKick(_state.Of(_spellSide), defending, actor.X, actor.Y, progress, restartId);
            return;
        }

        var evKind = progress > 0.55 ? EventKind.Tackle : EventKind.Interception;
        var id = Log(evKind, defending.Side, defending.Side, defender.Player.Id, actor.Player.Id, actor.X, actor.Y,
            value: attemptedAction is { } fa2 ? (int)fa2 + 1 : 0);
        StartSpell(defending.Side, defender.X, defender.Y, id);
    }

    private void MaybeCard(PlayerMatchState defender, Side side, bool denyingCleanChance)
    {
        var straightRed = denyingCleanChance && _flow.Chance(0.004);
        if (straightRed)
        {
            defender.SentOff = true;
            Log(EventKind.RedCard, side, side, defender.Player.Id, 0, defender.X, defender.Y, causeId: -1);
            return;
        }

        if (!_flow.Chance(denyingCleanChance ? 0.24 : 0.13)) return;

        defender.YellowCards++;
        Log(EventKind.YellowCard, side, side, defender.Player.Id, 0, defender.X, defender.Y, causeId: -1);
        if (defender.YellowCards >= 2)
        {
            defender.SentOff = true;
            Log(EventKind.RedCard, side, side, defender.Player.Id, 0, defender.X, defender.Y, causeId: -1);
        }
    }

    private enum Action { Pass, Carry, Cross, ThroughBall, Shot }

    private Action ChooseAction(PlayerMatchState actor, double progress, double y, MatchTactics tactics)
    {
        var wide = y < 0.22 || y > 0.78;
        var inBox = progress > 0.87 && Math.Abs(y - 0.5) < 0.25;

        if (inBox && actor.Effective(actor.Player.Shooting) > 40
            && _flow.Chance(0.13 + tactics.Risk * 0.11 + InstructionProfiles.Get(actor.Instruction).ShotBias))
            return Action.Shot;

        var weights = new (Action a, double w)[]
        {
            (Action.Pass, 1.0),
            (Action.Carry, 0.35 + actor.Effective(actor.Player.Dribbling) / 200.0),
            (Action.Cross, wide && progress > 0.55 ? 0.6 : 0.0),
            (Action.ThroughBall, progress is > 0.35 and < 0.85 ? 0.15 + tactics.Directness * 0.3 + actor.Effective(actor.Player.Vision) / 300.0 : 0.0),
        };

        var total = weights.Sum(w => w.w);
        var roll = _flow.NextDouble() * total;
        foreach (var (a, w) in weights)
        {
            if (roll < w) return a;
            roll -= w;
        }
        return Action.Pass;
    }

    private void RunAction(PlayerMatchState actor, Action action, TeamMatchState possessing, TeamMatchState defending, double progress)
    {
        switch (action)
        {
            case Action.Shot:
                var defendersBetween = defending.OnPitch.Count(p => p.OnPitch && AttackFrameX(possessing.Side, p.X) > progress);
                var oneOnOne = defendersBetween == 0;
                TakeShot(actor, possessing, defending, oneOnOne, buildupCauseId: _spellCauseId, defendersBetween: defendersBetween);
                return;

            case Action.Cross:
            {
                var toY = 0.5 + (_flow.NextDouble() - 0.5) * 0.2;
                var toProgress = Math.Min(progress + 0.18, 0.95);
                var id = Log(EventKind.Cross, possessing.Side, possessing.Side, actor.Player.Id, 0, actor.X, actor.Y, causeId: _spellCauseId);
                MoveSpell(possessing.Side, AttackFrameX(possessing.Side, toProgress), toY, id); // AttackFrameX is its own inverse: progress -> absolute
                return;
            }

            case Action.ThroughBall:
            {
                var receiver = possessing.OnPitch.Where(p => p.OnPitch && p != actor && !p.Player.Position.IsGoalkeeper()
                        && AttackFrameX(possessing.Side, p.X) > progress)
                    .OrderByDescending(p => AttackFrameX(possessing.Side, p.X)).FirstOrDefault() ?? actor;
                var lastDefenderX = defending.OnPitch.Where(p => p.OnPitch).Select(p => AttackFrameX(possessing.Side, p.X)).DefaultIfEmpty(1.0).Min();
                var receiverProgress = AttackFrameX(possessing.Side, receiver.X);
                var offside = receiverProgress > lastDefenderX && defending.Tactics.DefensiveLine > 0.55 && _flow.Chance(0.10);
                var id = Log(EventKind.ThroughBall, possessing.Side, possessing.Side, actor.Player.Id, receiver.Player.Id, actor.X, actor.Y, causeId: _spellCauseId);
                if (offside)
                {
                    Log(EventKind.Offside, possessing.Side, defending.Side, receiver.Player.Id, 0, receiver.X, receiver.Y, causeId: id);
                    StartSpell(defending.Side, receiver.X, receiver.Y, id);
                    return;
                }
                MoveSpell(possessing.Side, AttackFrameX(possessing.Side, Math.Min(receiverProgress, 0.97)), receiver.Y, id);
                return;
            }

            case Action.Carry:
            {
                var gain = 0.05 + actor.Effective(actor.Player.Dribbling) / 900.0;
                var toX = possessing.Side == Side.Home ? actor.X + gain : actor.X - gain;
                var id = Log(EventKind.Dribble, possessing.Side, possessing.Side, actor.Player.Id, 0, actor.X, actor.Y, causeId: _spellCauseId);
                MoveSpell(possessing.Side, Math.Clamp(toX, 0, 1), actor.Y, id);
                return;
            }

            default:
            {
                var mate = possessing.OnPitch.Where(p => p.OnPitch && p != actor && !p.Player.Position.IsGoalkeeper())
                    .OrderBy(p => Sq(p.X - (actor.X + (possessing.Side == Side.Home ? 0.08 : -0.08))) + Sq(p.Y - actor.Y))
                    .First();
                var id = Log(EventKind.Pass, possessing.Side, possessing.Side, actor.Player.Id, mate.Player.Id, actor.X, actor.Y, causeId: _spellCauseId);
                MoveSpell(possessing.Side, mate.X, mate.Y, id);
                return;
            }
        }
    }

    private void MoveSpell(Side side, double x, double y, int causeId)
    {
        _spellSide = side;
        _spellX = Math.Clamp(x, 0, 1);
        _spellY = Math.Clamp(y, 0, 1);
        _spellCauseId = causeId;
        _spellActions++;
        _state.BallX = _spellX;
        _state.BallY = _spellY;
    }

    /// <summary>The event kind that created this chance, read back from the log — a through ball, a cross, a loose ball off a save, whatever it actually was.</summary>
    private EventKind AssistKindFor(int causeId) =>
        causeId >= 0 && causeId < _state.Events.Count ? _state.Events[causeId].Kind : EventKind.Pass;

    private void TakeShot(PlayerMatchState shooter, TeamMatchState attacking, TeamMatchState defending,
        bool oneOnOne, int buildupCauseId, int defendersBetween = 0)
    {
        var progress = AttackFrameX(shooter.Side, shooter.X);
        var y = AttackFrameY(shooter.Side, shooter.Y);
        var geometryXg = ShotQuality.BaseXg(progress, y, defendersBetween, oneOnOne, AssistKindFor(buildupCauseId));

        // Finishing and shot-stopping both matter, but neither should swing a chance by more than
        // a third either way — a golden opportunity stays golden even against a great keeper.
        var keeper = defending.Goalkeeper;
        var finishingFactor = 0.85 + shooter.Effective(shooter.Player.Shooting) / 330.0;
        var keeperFactor = 1.18 - keeper.Effective(keeper.Player.Goalkeeping) / 285.0;
        var xg = Math.Clamp(geometryXg * finishingFactor * keeperFactor, 0.01, 0.88);

        var isGoal = _flow.Chance(xg);
        PlayerMatchState? blocker = null;
        var result = ShotResult.OffTarget;

        if (!isGoal)
        {
            // A miss still has to land somewhere. Closer, better chances are more often kept out
            // by the keeper; efforts from tighter angles or further out more often miss the
            // target entirely or run into a defender first.
            var offTargetChance = Math.Clamp(0.55 - xg * 0.7, 0.18, 0.55);
            var roll = _flow.NextDouble();
            if (roll < offTargetChance) result = ShotResult.OffTarget;
            else if (roll < offTargetChance + 0.20)
            {
                result = ShotResult.Blocked;
                blocker = defending.OnPitch.Where(p => p.OnPitch).OrderBy(p => Sq(p.X - shooter.X) + Sq(p.Y - shooter.Y)).First();
            }
            else result = ShotResult.Saved;
        }

        var shotId = Log(EventKind.Shot, shooter.Side, defending.Side, shooter.Player.Id, blocker?.Player.Id ?? 0,
            shooter.X, shooter.Y, causeId: buildupCauseId, xg: xg, value: (int)(isGoal ? ShotResult.Goal : result));

        if (isGoal)
        {
            var goalId = Log(EventKind.Goal, shooter.Side, defending.Side, shooter.Player.Id, 0, shooter.X, shooter.Y, causeId: shotId, xg: xg, value: (int)ShotResult.Goal);
            if (shooter.Side == Side.Home) _state.HomeGoals++; else _state.AwayGoals++;
            StartSpell(shooter.Side.Other(), 0.5, 0.5, goalId);
            return;
        }

        if (result == ShotResult.Saved)
        {
            Log(EventKind.Save, defending.Side, defending.Side, keeper.Player.Id, shooter.Player.Id, keeper.X, keeper.Y, causeId: shotId, xg: xg);
            if (_flow.Chance(0.09))
            {
                var rebounder = attacking.OnPitch.Where(p => p.OnPitch && p != shooter)
                    .OrderBy(p => Sq(p.X - keeper.X) + Sq(p.Y - keeper.Y)).First();
                TakeShot(rebounder, attacking, defending, oneOnOne: true, buildupCauseId: shotId);
                return;
            }
        }
        else if ((result == ShotResult.OffTarget && _flow.Chance(0.4)) || (result == ShotResult.Blocked && _flow.Chance(0.5)))
        {
            var cornerId = Log(EventKind.Corner, shooter.Side, defending.Side, 0, 0, causeId: shotId);
            TakeCorner(attacking, defending, cornerId);
            return;
        }

        StartSpell(defending.Side, defending.Side == Side.Home ? 0.06 : 0.94, 0.5, shotId);
    }

    /// <summary>
    /// A corner is a chance for the side that won it, not a handover to the defence. The best
    /// deliverer on the attacking team puts it in; most of the time a crowded box means it's
    /// cleared before anyone gets a real sight of goal, but when it isn't, the best aerial
    /// presence available gets a header at it.
    /// </summary>
    private void TakeCorner(TeamMatchState attacking, TeamMatchState defending, int causeId)
    {
        var deliverer = attacking.OnPitch.Where(p => p.OnPitch && !p.Player.Position.IsGoalkeeper())
            .OrderByDescending(p => p.Effective(p.Player.Passing)).First();
        if (!_flow.Chance(0.60 + deliverer.Effective(deliverer.Player.Passing) / 500.0))
        {
            // Cleared before it ever became a real chance.
            StartSpell(defending.Side, defending.Side == Side.Home ? 0.06 : 0.94, 0.5, causeId);
            return;
        }

        var target = attacking.OnPitch.Where(p => p.OnPitch && !p.Player.Position.IsGoalkeeper())
            .OrderByDescending(p => p.Effective(p.Player.Physical)).First();
        var attackFrameTarget = AttackFrameX(attacking.Side, 0.95);
        target.X = attackFrameTarget;
        target.Y = AttackFrameY(attacking.Side, 0.5 + (_flow.NextDouble() - 0.5) * 0.3);

        // A packed box, not a precisely tracked one — three defenders between ball and goal is a
        // reasonable stand-in for how crowded a defended corner actually is.
        TakeShot(target, attacking, defending, oneOnOne: false, buildupCauseId: causeId, defendersBetween: 3);
    }

    /// <summary>
    /// A foul given in shooting range doesn't always go direct — most of the time the award just
    /// restarts open play from that spot. When it does go direct, the taker is whoever on the
    /// pitch blends shooting and passing (technique) best, and he faces an assembled wall on top
    /// of whatever defenders were already back.
    /// </summary>
    private void TakeFreeKick(TeamMatchState attacking, TeamMatchState defending, double x, double y, double progress, int causeId)
    {
        var inRange = progress > 0.68 && Math.Abs(AttackFrameY(attacking.Side, y) - 0.5) < 0.35;
        if (!inRange || !_flow.Chance(0.35))
        {
            StartSpell(attacking.Side, x, y, causeId);
            return;
        }

        var taker = attacking.OnPitch.Where(p => p.OnPitch && !p.Player.Position.IsGoalkeeper())
            .OrderByDescending(p => p.Effective(p.Player.Shooting) * 0.6 + p.Effective(p.Player.Passing) * 0.4)
            .First();
        taker.X = x;
        taker.Y = y;

        var defendersAlreadyBack = defending.OnPitch.Count(p => p.OnPitch && AttackFrameX(attacking.Side, p.X) > progress);
        var wallDefenders = Math.Clamp(defendersAlreadyBack + 3, 3, 7);   // +3 for the wall assembled specifically for the kick
        TakeShot(taker, attacking, defending, oneOnOne: false, buildupCauseId: causeId, defendersBetween: wallDefenders);
    }

    private void AccumulateFatigue(int fromSecond, int toSecond)
    {
        var elapsedMinutes = (toSecond - fromSecond) / 60.0;
        foreach (var team in _state.Teams)
            foreach (var p in team.OnPitch.Where(p => p.OnPitch))
            {
                var profile = InstructionProfiles.Get(p.Instruction);
                var pressLoad = 0.6 + team.Tactics.Pressing * 0.5;
                var recovery = p.Effective(p.Player.Stamina) / 99.0;
                p.Fatigue = Math.Clamp(p.Fatigue + elapsedMinutes * 0.0085 * profile.WorkRateBias * pressLoad / recovery, 0, 1);
                p.X += (RoleTargetX(team.Side, p) - p.X) * 0.10;
                var currentAttackY = AttackFrameY(team.Side, p.Y);
                var widthTarget = Math.Clamp(p.HomeSlot.Y + profile.WidthBias, 0.02, 0.98);
                var newAttackY = currentAttackY + (widthTarget - currentAttackY) * 0.05;
                p.Y = AttackFrameY(team.Side, newAttackY); // self-inverse: attack-frame Y -> absolute Y
            }
    }

    private double RoleTargetX(Side side, PlayerMatchState p)
    {
        var ballProgress = AttackFrameX(side, _state.BallX);
        var pull = InstructionProfiles.Get(p.Instruction).XPull;
        var attackFrameHome = Math.Clamp(p.HomeSlot.X + (ballProgress - p.HomeSlot.X) * pull, 0.02, 0.98);
        return side == Side.Home ? attackFrameHome : 1.0 - attackFrameHome;
    }

    private int Log(EventKind kind, Side side, Side poss, long actorId, long otherId,
        double x = 0, double y = 0, double toX = 0, double toY = 0,
        double xg = 0, bool success = true, int causeId = -1, int value = 0)
    {
        var id = _state.NextEventId();
        _state.Events.Add(new MatchEvent(id, _state.Second, kind, side, poss, actorId, otherId, x, y, toX, toY, xg, success, causeId, value));
        return id;
    }
}
