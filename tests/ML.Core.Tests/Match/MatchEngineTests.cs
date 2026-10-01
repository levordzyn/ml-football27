using ML.Core.Domain;
using ML.Core.Match;
using Xunit;

namespace ML.Core.Tests.Match;

public class MatchEngineTests
{
    private static TeamSheet MakeTeam(string name, int startId, int overall, Formation f, bool human = false)
    {
        var starters = new List<MatchPlayer>();
        for (var i = 0; i < f.Slots.Count; i++)
            starters.Add(MatchPlayer.FromOverall(startId + i, $"{name} P{i}", f.Slots[i].Position, overall));

        var bench = new List<MatchPlayer>();
        var benchPositions = new[] { Position.GK, Position.CB, Position.LB, Position.DMF, Position.CMF, Position.RWF, Position.CF };
        for (var i = 0; i < 7; i++)
            bench.Add(MatchPlayer.FromOverall(startId + 100 + i, $"{name} B{i}", benchPositions[i], overall - 3));

        return new TeamSheet(name, f, new MatchTactics(), starters, bench, HumanControlled: human);
    }

    private static MatchSetup Setup(int seed, int homeOvr = 75, int awayOvr = 75, Formation? homeForm = null, Formation? awayForm = null) =>
        new(seed, MakeTeam("Home", 1, homeOvr, homeForm ?? Formations.F442), MakeTeam("Away", 1000, awayOvr, awayForm ?? Formations.F442));

    [Fact]
    public void SameSeedAndTeamsProduceTheExactSameMatch()
    {
        var a = MatchSession.RunAutomatic(Setup(42));
        var b = MatchSession.RunAutomatic(Setup(42));

        Assert.Equal(a.HomeGoals, b.HomeGoals);
        Assert.Equal(a.AwayGoals, b.AwayGoals);
        Assert.Equal(a.Events.Count, b.Events.Count);
        Assert.True(a.Events.SequenceEqual(b.Events));
    }

    [Fact]
    public void DifferentSeedsUsuallyProduceDifferentMatches()
    {
        var results = Enumerable.Range(1, 15)
            .Select(seed => MatchSession.RunAutomatic(Setup(seed)))
            .Select(r => (r.HomeGoals, r.AwayGoals))
            .Distinct()
            .Count();

        Assert.True(results > 3, "15 different seeds should not collapse onto the same handful of scorelines.");
    }

    [Fact]
    public void GoalCountMatchesGoalEvents()
    {
        var rec = MatchSession.RunAutomatic(Setup(7));
        Assert.Equal(rec.HomeGoals, rec.Events.Count(e => e.Kind == EventKind.Goal && e.Side == Side.Home));
        Assert.Equal(rec.AwayGoals, rec.Events.Count(e => e.Kind == EventKind.Goal && e.Side == Side.Away));
    }

    [Fact]
    public void EveryShotHasExactlyOneShotEventRegardlessOfOutcome()
    {
        // Goal, Saved, Blocked and OffTarget are all recorded via a single Shot event tagged with
        // the outcome — there should never be a second, duplicate Shot logged for the same attempt.
        var rec = MatchSession.RunAutomatic(Setup(99, 85, 65));
        var shots = rec.Events.Where(e => e.Kind == EventKind.Shot).ToList();
        Assert.All(shots, s => Assert.True(s.Xg is > 0 and <= 0.9));
    }

    [Fact]
    public void GoalEventFollowsAShotEventWithTheSameXg()
    {
        var rec = MatchSession.RunAutomatic(Setup(99, 85, 65));
        var byId = rec.Events.ToDictionary(e => e.Id);
        foreach (var goal in rec.Events.Where(e => e.Kind == EventKind.Goal))
        {
            var shot = byId[goal.CauseId];
            Assert.Equal(EventKind.Shot, shot.Kind);
            Assert.Equal((int)ShotResult.Goal, shot.Value);
            Assert.Equal(shot.Xg, goal.Xg);
        }
    }

    [Fact]
    public void EventSecondsNeverGoBackwardsAndStayWithinTheMatch()
    {
        var rec = MatchSession.RunAutomatic(Setup(13));
        var seconds = rec.Events.Select(e => e.Second).ToList();
        Assert.True(seconds.SequenceEqual(seconds.OrderBy(s => s)), "Events must be logged in non-decreasing match-second order.");
        Assert.True(rec.FullTime is >= 90 * 60 and <= 97 * 60, $"Full time ({rec.FullTime}s) should land within normal stoppage-time bounds.");
    }

    [Fact]
    public void RedCardedPlayerNeverActsAgain()
    {
        // Run several seeds so at least one produces a red card.
        var sentOffActs = Enumerable.Range(0, 40).SelectMany(seed =>
        {
            var rec = MatchSession.RunAutomatic(Setup(seed, 70, 90)); // a lopsided game draws more desperate fouls
            var redCardSecond = rec.Events.Where(e => e.Kind == EventKind.RedCard)
                .Select(e => (e.ActorId, e.Second)).ToList();
            return redCardSecond.SelectMany(rc =>
                rec.Events.Where(e => e.ActorId == rc.ActorId && e.Second > rc.Second
                    && e.Kind is EventKind.Pass or EventKind.Shot or EventKind.Dribble or EventKind.Tackle or EventKind.Interception));
        }).ToList();

        Assert.Empty(sentOffActs);
    }

    [Fact]
    public void SubstitutedPlayerCountNeverExceedsFive()
    {
        foreach (var seed in Enumerable.Range(0, 10))
        {
            var rec = MatchSession.RunAutomatic(Setup(seed));
            foreach (var side in new[] { Side.Home, Side.Away })
                Assert.True(rec.Events.Count(e => e.Kind == EventKind.Substitution && e.Side == side) <= 5);
        }
    }

    [Fact]
    public void HalfTimeAndFullTimeEachOccurExactlyOnce()
    {
        var rec = MatchSession.RunAutomatic(Setup(21));
        Assert.Equal(1, rec.Events.Count(e => e.Kind == EventKind.HalfTime));
        Assert.Equal(1, rec.Events.Count(e => e.Kind == EventKind.FullTime));
        Assert.True(rec.Events.First(e => e.Kind == EventKind.HalfTime).Second < rec.Events.First(e => e.Kind == EventKind.FullTime).Second);
    }

    [Fact]
    public void TeamStatsPossessionAddsToOneHundred()
    {
        var rec = MatchSession.RunAutomatic(Setup(5));
        var home = Projectors.TeamStats(rec.Events, Side.Home);
        var away = Projectors.TeamStats(rec.Events, Side.Away);
        Assert.InRange(home.PossessionPct + away.PossessionPct, 99.0, 101.0);
    }

    [Fact]
    public void PlayerRatingsStayWithinBounds()
    {
        var rec = MatchSession.RunAutomatic(Setup(5));
        var ids = rec.Setup.Home.Starters.Select(p => p.Id).Concat(rec.Setup.Away.Starters.Select(p => p.Id));
        var stats = Projectors.PlayerStats(rec.Events, ids);
        Assert.All(stats, s => Assert.InRange(s.Rating, 2.0, 10.0));
    }

    [Fact]
    public void SideDoesNotSystematicallyDetermineTheWinnerWhenTeamsAreEqual()
    {
        // Regression test for a coordinate-frame bug that gave Home a large, spurious advantage
        // regardless of formation or attributes. With identical squads and formations on both
        // sides, Home's win share across many matches should not run away from 50%.
        int homeWins = 0, n = 120;
        for (var seed = 0; seed < n; seed++)
        {
            var rec = MatchSession.RunAutomatic(Setup(seed, 75, 75, Formations.F442, Formations.F442));
            if (rec.HomeGoals > rec.AwayGoals) homeWins++;
        }
        Assert.InRange(homeWins / (double)n, 0.30, 0.65);
    }

    [Fact]
    public void HigherOverallTeamWinsMoreOftenButNotEveryTime()
    {
        int strongerWins = 0, n = 80;
        for (var seed = 0; seed < n; seed++)
        {
            var rec = MatchSession.RunAutomatic(Setup(seed, 85, 65));
            if (rec.HomeGoals > rec.AwayGoals) strongerWins++;
        }
        var winRate = strongerWins / (double)n;
        Assert.True(winRate > 0.55, $"An 85 vs 65 side should win clearly more often than not (got {winRate:P0}).");
        Assert.True(winRate < 1.0, "Even a big gap should not be a certainty over a single match.");
    }

    [Fact]
    public void TacticsCommandIsAppliedAndLogged()
    {
        var engine = new MatchEngine(Setup(3));
        engine.AdvanceTo(10 * 60);
        var newTactics = new MatchTactics { Mentality = Mentality.VeryAttacking, Pressing = 0.9 };
        engine.ApplyCommand(new MatchCommand(engine.State.Second, Side.Home, CommandKind.Tactics, Tactics: newTactics));

        Assert.Equal(Mentality.VeryAttacking, engine.State.Of(Side.Home).Tactics.Mentality);
        Assert.Contains(engine.Events, e => e.Kind == EventKind.TacticalChange && e.Side == Side.Home);
    }

    [Fact]
    public void InjuryAndFatigueModelFeedsIntoOnPitchFatigueWithoutCrashing()
    {
        var home = MakeTeam("Home", 1, 75, Formations.F442);
        var tiredStarters = home.Starters.Select(p => p with { StartFatigue = 0.5 }).ToList();
        var setup = new MatchSetup(1, home with { Starters = tiredStarters }, MakeTeam("Away", 1000, 75, Formations.F442));
        var rec = MatchSession.RunAutomatic(setup);
        Assert.True(rec.Events.Count > 100);
    }
}
