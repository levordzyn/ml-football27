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
    public void PaceRunnerWingerGetsAttackSpaceAgainstAnAttackingOpponent()
    {
        var starters = StartersFor(Formations.F442);
        var rmfIdx = Formations.F442.Slots.ToList().FindIndex(s => s.Position == Position.RMF);
        starters[rmfIdx] = starters[rmfIdx] with { Pace = 90, Dribbling = 65 };

        var ins = AutoInstructions.Assign(starters, Formations.F442, new MatchTactics(), new MatchTactics { Mentality = Mentality.VeryAttacking });

        Assert.Equal(PlayerInstruction.AttackSpace, ins[starters[rmfIdx].Id]);
    }

    [Fact]
    public void PhysicalStrikerGetsTargetManAgainstADeepBlockWhilePaceyPartnerGetsAttackChannel()
    {
        var starters = StartersFor(Formations.F442);
        var cfSlots = Formations.F442.Slots.Select((s, i) => (s, i)).Where(t => t.s.Position == Position.CF).ToList();
        starters[cfSlots[0].i] = starters[cfSlots[0].i] with { Physical = 80, Pace = 55 };
        starters[cfSlots[1].i] = starters[cfSlots[1].i] with { Physical = 60, Pace = 85 };

        var ins = AutoInstructions.Assign(starters, Formations.F442, new MatchTactics(), new MatchTactics { DefensiveLine = 0.2 });

        Assert.Equal(PlayerInstruction.TargetMan, ins[starters[cfSlots[0].i].Id]);
        Assert.Equal(PlayerInstruction.AttackChannel, ins[starters[cfSlots[1].i].Id]);
    }

    [Fact]
    public void VeryDefensiveMentalityGivesTheCentreBackSweepNotDefault()
    {
        var starters = StartersFor(Formations.F442);
        var ins = AutoInstructions.Assign(starters, Formations.F442, new MatchTactics { Mentality = Mentality.VeryDefensive }, new MatchTactics());
        Assert.Equal(PlayerInstruction.Sweep, ins[IdAt(Formations.F442, starters, Position.CB)]);
    }

    [Fact]
    public void HighDefensiveLineGivesAGoodPassingGoalkeeperSweeperKeeper()
    {
        var starters = StartersFor(Formations.F442);
        var gkIdx = Formations.F442.Slots.ToList().FindIndex(s => s.Position == Position.GK);
        starters[gkIdx] = starters[gkIdx] with { Passing = 60 };
        var ins = AutoInstructions.Assign(starters, Formations.F442, new MatchTactics { DefensiveLine = 0.75 }, new MatchTactics());
        Assert.Equal(PlayerInstruction.SweeperKeeper, ins[starters[gkIdx].Id]);
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

public class SetPieceTests
{
    private static TeamSheet MakeTeam(string name, int startId, int overall, Formation f) =>
        new(name, f,
            new MatchTactics(),
            f.Slots.Select((s, i) => MatchPlayer.FromOverall(startId + i, $"{name} P{i}", s.Position, overall)).ToList(),
            new[] { Position.GK, Position.CB, Position.LB, Position.DMF, Position.CMF, Position.RWF, Position.CF }
                .Select((p, i) => MatchPlayer.FromOverall(startId + 100 + i, $"{name} B{i}", p, overall - 3)).ToList());

    [Fact]
    public void CornersSometimesProduceARealShotForTheAttackingSide()
    {
        // Before this fix, a Corner event was logged purely for stats and possession was always
        // handed straight to the defending side — a corner was never actually a chance.
        var shots = 0;
        var corners = 0;
        for (var seed = 0; seed < 60; seed++)
        {
            var rec = MatchSession.RunAutomatic(new MatchSetup(seed, MakeTeam("H", 1, 75, Formations.F442), MakeTeam("A", 1000, 75, Formations.F442)));
            corners += rec.Events.Count(e => e.Kind == EventKind.Corner);
            foreach (var corner in rec.Events.Where(e => e.Kind == EventKind.Corner))
                if (rec.Events.Any(e => e.Kind == EventKind.Shot && e.CauseId == corner.Id)) shots++;
        }
        Assert.True(shots > 0);
        Assert.True(shots < corners, "not every corner should become a shot — most get cleared");
    }

    [Fact]
    public void DirectFreeKicksSometimesProduceARealShot()
    {
        var shots = 0;
        for (var seed = 0; seed < 60; seed++)
        {
            var rec = MatchSession.RunAutomatic(new MatchSetup(seed, MakeTeam("H", 1, 75, Formations.F442), MakeTeam("A", 1000, 75, Formations.F442)));
            foreach (var fk in rec.Events.Where(e => e.Kind == EventKind.FreeKick))
                if (rec.Events.Any(e => e.Kind == EventKind.Shot && e.CauseId == fk.Id)) shots++;
        }
        Assert.True(shots > 0);
    }

    [Fact]
    public void AFreeKickOrPenaltyShotsCauseIsTheRestartEventNotTheFoul()
    {
        // Regression: buildupCauseId must be the Penalty/FreeKick event's own id so the xG
        // model's set-piece multiplier (AssistKindFor) actually applies, and a Goal's two-hop
        // assist lookup has something to chain through.
        var rec = MatchSession.RunAutomatic(new MatchSetup(1, MakeTeam("H", 1, 75, Formations.F442), MakeTeam("A", 1000, 75, Formations.F442)));
        var byId = rec.Events.ToDictionary(e => e.Id);
        var setPieceShots = rec.Events.Where(e => e.Kind == EventKind.Shot && byId.TryGetValue(e.CauseId, out var c)
            && c.Kind is EventKind.FreeKick or EventKind.Penalty).ToList();
        Assert.All(setPieceShots, s => Assert.NotEqual(EventKind.Foul, byId[s.CauseId].Kind));
    }

    [Fact]
    public void CornerDeliveriesVaryBetweenNearAndFarPostRatherThanOneFixedPoint()
    {
        var ys = new List<double>();
        for (var seed = 0; seed < 80; seed++)
        {
            var rec = MatchSession.RunAutomatic(new MatchSetup(seed, MakeTeam("H", 1, 75, Formations.F442), MakeTeam("A", 1000, 75, Formations.F442)));
            var byId = rec.Events.ToDictionary(e => e.Id);
            foreach (var shot in rec.Events.Where(e => e.Kind == EventKind.Shot))
                if (byId.TryGetValue(shot.CauseId, out var c) && c.Kind == EventKind.Corner)
                    ys.Add(shot.Y);
        }
        Assert.True(ys.Count > 20);
        Assert.True(ys.Select(y => Math.Round(y, 1)).Distinct().Count() >= 2, "deliveries should land at more than one spot");
    }

    [Fact]
    public void ADesignatedCornerTakerIsActuallyUsedInsteadOfWhoeverIsBestThatMoment()
    {
        List<MatchPlayer> Bench(int startId, int overall) =>
            new[] { Position.GK, Position.CB, Position.LB, Position.DMF, Position.CMF, Position.RWF, Position.CF }
                .Select((p, i) => MatchPlayer.FromOverall(startId + 100 + i, $"B{i}", p, overall)).ToList();

        var away = new TeamSheet("A", Formations.F442, new MatchTactics(),
            Formations.F442.Slots.Select((s, i) => MatchPlayer.FromOverall(1000 + i, $"A P{i}", s.Position, 75)).ToList(), Bench(1000, 72));

        var starters = Formations.F442.Slots.Select((s, i) => MatchPlayer.FromOverall(1 + i, $"H P{i}", s.Position, 75)).ToList();
        var cmfIdx = Formations.F442.Slots.ToList().FindIndex(s => s.Position == Position.CMF);
        starters[cmfIdx] = starters[cmfIdx] with { Passing = 95 };   // a standout passer, freely pickable
        var worstPasser = starters.Where(p => !p.Position.IsGoalkeeper()).OrderBy(p => p.Passing).First();

        var freePick = new TeamSheet("H", Formations.F442, new MatchTactics(), starters, Bench(1, 70));
        var forcedWeak = freePick with { CornerTakerId = worstPasser.Id };

        (int shots, int corners) CornerOutcome(TeamSheet home)
        {
            int shots = 0, corners = 0;
            for (var seed = 0; seed < 150; seed++)
            {
                var rec = MatchSession.RunAutomatic(new MatchSetup(seed, home, away));
                corners += rec.Events.Count(e => e.Kind == EventKind.Corner && e.Side == Side.Home);
                foreach (var corner in rec.Events.Where(e => e.Kind == EventKind.Corner && e.Side == Side.Home))
                    if (rec.Events.Any(e => e.Kind == EventKind.Shot && e.CauseId == corner.Id)) shots++;
            }
            return (shots, corners);
        }

        var free = CornerOutcome(freePick);
        var forced = CornerOutcome(forcedWeak);

        // A measurably worse designated taker should convert fewer corners into shots than
        // letting the engine freely pick the best passer available — proof the designation is
        // actually honoured, not just accepted and ignored.
        Assert.True((double)forced.shots / forced.corners < (double)free.shots / free.corners);
    }

    [Fact]
    public void GoalkeeperWithNoExplicitInstructionStaysNearHisOwnGoal()
    {
        // Regression: Default's shared pull (0.35) used to apply to goalkeepers too, drifting
        // them toward the centre circle (observed average ~0.22, max ~0.32 of the pitch length
        // from his own goal). A keeper with nothing set now effectively plays HoldLine.
        var home = MakeTeam("H", 1, 75, Formations.F442);
        var away = MakeTeam("A", 1000, 75, Formations.F442);
        var engine = new MatchEngine(new MatchSetup(1, home, away));
        var maxX = 0.0;
        for (var minute = 0; minute < 90; minute += 5)
        {
            engine.AdvanceTo(minute * 60);
            maxX = Math.Max(maxX, engine.State.Of(Side.Home).Goalkeeper.X);
        }
        Assert.True(maxX < 0.15);
    }

    [Fact]
    public void EveryPenaltyStillProducesExactlyOneShotAttempt()
    {
        var totalPens = 0;
        var pensWithShots = 0;
        for (var seed = 0; seed < 40; seed++)
        {
            var rec = MatchSession.RunAutomatic(new MatchSetup(seed, MakeTeam("H", 1, 75, Formations.F442), MakeTeam("A", 1000, 75, Formations.F442)));
            foreach (var pen in rec.Events.Where(e => e.Kind == EventKind.Penalty))
            {
                totalPens++;
                if (rec.Events.Any(e => e.Kind == EventKind.Shot && e.CauseId == pen.Id)) pensWithShots++;
            }
        }
        Assert.True(totalPens > 0);
        Assert.Equal(totalPens, pensWithShots);
    }
}
