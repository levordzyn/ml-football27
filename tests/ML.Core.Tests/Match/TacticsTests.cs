using ML.Core.Domain;
using ML.Core.Match;
using Xunit;

namespace ML.Core.Tests.Match;

public class AutoSelectionTests
{
    private static List<MatchPlayer> Filler(int fromId, int toId, Position position, int overall) =>
        Enumerable.Range(fromId, toId - fromId + 1).Select(i => MatchPlayer.FromOverall(i, $"Filler{i}", position, overall)).ToList();

    [Fact]
    public void FreshLowerRatedPlayerBeatsExhaustedHigherRatedPlayerForTheSameSlot()
    {
        // The spec's own example: an 84-rated midfielder who is exhausted may reasonably lose
        // his place to a 79-rated midfielder in excellent condition.
        var tired = MatchPlayer.FromOverall(1, "Tired84", Position.CMF, 84, startFatigue: 0.40);
        var fresh = MatchPlayer.FromOverall(2, "Fresh79", Position.CMF, 79, startFatigue: 0.03);
        var formation = new Formation("one-cmf", new[] { new FormationSlot(Position.CMF, .45, .5) });

        var xi = AutoSelection.PickXi(new List<MatchPlayer> { tired, fresh }, formation);

        Assert.Equal(fresh.Id, xi.Starters[0].Id);
    }

    [Fact]
    public void NeverPicksTheSamePlayerTwiceAndNeverLeavesAStarterOnTheBenchToo()
    {
        var pool = Enumerable.Range(1, 25)
            .Select(i => MatchPlayer.FromOverall(i, $"P{i}", Formations.F433.Slots[(i - 1) % 11].Position, 70 + i % 10))
            .ToList();

        var xi = AutoSelection.PickXi(pool, Formations.F433);

        Assert.Equal(11, xi.Starters.Select(p => p.Id).Distinct().Count());
        Assert.Empty(xi.Starters.Select(p => p.Id).Intersect(xi.Bench.Select(p => p.Id)));
    }

    [Fact]
    public void HigherMatchImportanceWeighsFatigueMoreHeavily()
    {
        var betterButTired = MatchPlayer.FromOverall(1, "BetterTired", Position.CF, 80, startFatigue: 0.25);
        var worseButFresh = MatchPlayer.FromOverall(2, "WorseFresh", Position.CF, 76, startFatigue: 0.02);
        var formation = new Formation("one-cf", new[] { new FormationSlot(Position.CF, .8, .5) });
        var pool = new List<MatchPlayer> { betterButTired, worseButFresh };

        var deadRubber = AutoSelection.PickXi(pool, formation, importance: 0.1);
        var cupFinal = AutoSelection.PickXi(pool, formation, importance: 0.95);

        Assert.Equal(betterButTired.Id, deadRubber.Starters[0].Id);
        Assert.Equal(worseButFresh.Id, cupFinal.Starters[0].Id);
    }

    [Fact]
    public void ThrowsRatherThanSilentlyUnderfillingAFormation()
    {
        var pool = new List<MatchPlayer> { MatchPlayer.FromOverall(1, "P1", Position.CF, 75) };
        Assert.Throws<ArgumentException>(() => AutoSelection.PickXi(pool, Formations.F442));
    }
}

public class AutoInstructionsTests
{
    private static List<MatchPlayer> StartersFor(Formation f, int overall = 75) =>
        f.Slots.Select((s, i) => MatchPlayer.FromOverall(i + 1, $"P{i}", s.Position, overall)).ToList();

    private static long IdAt(Formation f, List<MatchPlayer> starters, Position position) =>
        starters[f.Slots.ToList().FindIndex(s => s.Position == position)].Id;

    [Fact]
    public void VeryDefensiveMentalityKeepsTheFullbackHome()
    {
        var starters = StartersFor(Formations.F442);
        var ins = AutoInstructions.Assign(starters, Formations.F442, new MatchTactics { Mentality = Mentality.VeryDefensive }, new MatchTactics());
        Assert.Equal(PlayerInstruction.StayBack, ins[IdAt(Formations.F442, starters, Position.LB)]);
    }

    [Fact]
    public void VeryAttackingMentalitySendsTheFullbackForward()
    {
        var starters = StartersFor(Formations.F442);
        var ins = AutoInstructions.Assign(starters, Formations.F442, new MatchTactics { Mentality = Mentality.VeryAttacking }, new MatchTactics());
        Assert.Equal(PlayerInstruction.AggressiveRuns, ins[IdAt(Formations.F442, starters, Position.LB)]);
    }

    [Fact]
    public void TwoStrikersSplitRolesByPaceRatherThanBothPlayingTheSameRole()
    {
        var starters = StartersFor(Formations.F442);
        var cfSlots = Formations.F442.Slots.Select((s, i) => (s, i)).Where(t => t.s.Position == Position.CF).ToList();
        starters[cfSlots[0].i] = starters[cfSlots[0].i] with { Pace = 90, Dribbling = 60 };
        starters[cfSlots[1].i] = starters[cfSlots[1].i] with { Pace = 55, Physical = 85 };

        var ins = AutoInstructions.Assign(starters, Formations.F442, new MatchTactics(), new MatchTactics());

        Assert.Equal(PlayerInstruction.AttackChannel, ins[starters[cfSlots[0].i].Id]);
        Assert.Equal(PlayerInstruction.TargetMan, ins[starters[cfSlots[1].i].Id]);
    }

    [Fact]
    public void NeverAssignsAWingerInstructionToAFullback()
    {
        // Each position only ever gets instructions from its own catalogue (Section 8's intent):
        // a fullback can get Overlap but never StayWide, which belongs to wingers.
        var fullbackOnly = new HashSet<PlayerInstruction>
        {
            PlayerInstruction.Default, PlayerInstruction.StayBack, PlayerInstruction.Overlap,
            PlayerInstruction.Underlap, PlayerInstruction.Invert, PlayerInstruction.AggressiveRuns,
        };
        foreach (var mentality in Enum.GetValues<Mentality>())
        {
            var starters = StartersFor(Formations.F442);
            var ins = AutoInstructions.Assign(starters, Formations.F442, new MatchTactics { Mentality = mentality }, new MatchTactics());
            Assert.Contains(ins[IdAt(Formations.F442, starters, Position.LB)], fullbackOnly);
        }
    }
}

public class InstructionBehaviorTests
{
    private static TeamSheet TeamWithInstruction(string name, int startId, int overall, Formation f, long targetId, PlayerInstruction instr)
    {
        var starters = f.Slots.Select((s, i) => MatchPlayer.FromOverall(startId + i, $"{name} P{i}", s.Position, overall)).ToList();
        var bench = new[] { Position.GK, Position.CB, Position.LB, Position.DMF, Position.CMF, Position.RWF, Position.CF }
            .Select((p, i) => MatchPlayer.FromOverall(startId + 100 + i, $"{name} B{i}", p, overall - 3)).ToList();
        return new TeamSheet(name, f, new MatchTactics(), starters, bench, new Dictionary<long, PlayerInstruction> { [targetId] = instr });
    }

    [Fact]
    public void DefaultInstructionLeavesTheEngineNumericallyUnchangedFromThePreTacticsCalibration()
    {
        // Regression guard: instruction profiles must resolve to a true no-op for Default, so
        // every match that never sets an individual instruction behaves exactly as it did before
        // this feature existed. If this ever fails, Phase 2/3's calibration has silently drifted.
        var home = TeamWithInstruction("H", 1, 76, Formations.F442, 1, PlayerInstruction.Default);
        var away = TeamWithInstruction("A", 1000, 76, Formations.F442, 1000, PlayerInstruction.Default);
        var a = MatchSession.RunAutomatic(new MatchSetup(777, home, away));
        var b = MatchSession.RunAutomatic(new MatchSetup(777, home, away));
        Assert.True(a.Events.SequenceEqual(b.Events));
    }

    [Fact]
    public void StayWideWingerEndsFartherFromCenterThanCutInsideWinger()
    {
        double FinalY(PlayerInstruction instr)
        {
            var rwfId = 1 + Formations.F433.Slots.ToList().FindIndex(s => s.Position == Position.RWF);
            var home = TeamWithInstruction("H", 1, 75, Formations.F433, rwfId, instr);
            var away = TeamWithInstruction("A", 1000, 75, Formations.F433, 1000, PlayerInstruction.Default);
            var engine = new MatchEngine(new MatchSetup(1, home, away));
            engine.RunToFullTime();
            return engine.State.Of(Side.Home).OnPitch.First(p => p.Player.Id == rwfId).Y;
        }

        var wide = FinalY(PlayerInstruction.StayWide);
        var inside = FinalY(PlayerInstruction.CutInside);
        Assert.True(Math.Abs(wide - 0.5) > Math.Abs(inside - 0.5));
    }

    [Fact]
    public void TrackBackWingerMakesMoreDefensiveActionsThanAttackSpaceWinger()
    {
        int DefensiveActions(PlayerInstruction instr)
        {
            var total = 0;
            for (var seed = 0; seed < 15; seed++)
            {
                var rwfId = 1 + Formations.F433.Slots.ToList().FindIndex(s => s.Position == Position.RWF);
                var home = TeamWithInstruction("H", 1, 75, Formations.F433, rwfId, instr);
                var away = TeamWithInstruction("A", 1000, 75, Formations.F433, 1000, PlayerInstruction.Default);
                var rec = MatchSession.RunAutomatic(new MatchSetup(seed, home, away));
                total += rec.Events.Count(e => e.ActorId == rwfId && e.Kind is EventKind.Tackle or EventKind.Interception);
            }
            return total;
        }

        Assert.True(DefensiveActions(PlayerInstruction.TrackBack) > DefensiveActions(PlayerInstruction.AttackSpace));
    }

    [Fact]
    public void AdvancedForwardShootsMoreOftenPerTouchThanTargetManButGetsFewerTouches()
    {
        // AdvancedForward's much higher forward pull isolates him from the buildup relative to a
        // Target Man who drops into it more — fewer touches, but a higher ShotBias once he does
        // get a sight of goal. Both halves of that trade-off should show up.
        (int touches, int shots) Measure(PlayerInstruction instr)
        {
            int touches = 0, shots = 0;
            for (var seed = 0; seed < 20; seed++)
            {
                var cfId = 1 + Formations.F433.Slots.ToList().FindIndex(s => s.Position == Position.CF);
                var home = TeamWithInstruction("H", 1, 75, Formations.F433, cfId, instr);
                var away = TeamWithInstruction("A", 1000, 75, Formations.F433, 1000, PlayerInstruction.Default);
                var rec = MatchSession.RunAutomatic(new MatchSetup(seed, home, away));
                touches += rec.Events.Count(e => e.ActorId == cfId
                    && e.Kind is EventKind.Pass or EventKind.Dribble or EventKind.Cross or EventKind.ThroughBall or EventKind.Shot);
                shots += rec.Events.Count(e => e.ActorId == cfId && e.Kind == EventKind.Shot);
            }
            return (touches, shots);
        }

        var targetMan = Measure(PlayerInstruction.TargetMan);
        var advancedForward = Measure(PlayerInstruction.AdvancedForward);

        Assert.True(advancedForward.touches < targetMan.touches);
        Assert.True((double)advancedForward.shots / advancedForward.touches > (double)targetMan.shots / targetMan.touches);
    }
}
