using ML.Core.Domain;

namespace ML.Core.Match;

/// <summary>
/// In-match decisions for a CPU-controlled side: substitutions and tactical shifts driven by
/// what's actually happening — fatigue, scoreline, time left — not an "optimal AI" that never
/// misreads a game. <see cref="Decisiveness"/> on the team sheet governs how promptly a given
/// manager acts on these same signals, so two CPU sides can read an identical situation
/// differently without the reasons themselves being arbitrary.
/// </summary>
public sealed class ManagerPolicy
{
    public void Review(MatchState state, TeamMatchState team, TeamMatchState opponent, MatchRandom rng)
    {
        var decisiveness = team.Sheet.ManagerDecisiveness;
        var minute = state.Second / 60.0;
        var goalDiff = team.Side == Side.Home ? state.HomeGoals - state.AwayGoals : state.AwayGoals - state.HomeGoals;

        ReviewSubstitutions(state, team, minute, rng, decisiveness);
        ReviewMentality(state, team, minute, goalDiff, rng, decisiveness);
    }

    private static void ReviewSubstitutions(MatchState state, TeamMatchState team, double minute, MatchRandom rng, double decisiveness)
    {
        if (team.SubsUsed >= 5 || minute < 55) return;

        var tired = team.OnPitch.Where(p => p.OnPitch && p.Fatigue > 0.62)
            .OrderByDescending(p => p.Fatigue).FirstOrDefault();
        if (tired is null) return;
        if (!rng.Chance(decisiveness * (0.10 + (tired.Fatigue - 0.62) * 0.9))) return;

        var replacement = team.Bench
            .Where(b => b.Position == tired.Player.Position || b.Position.Group() == tired.Player.Position.Group())
            .OrderByDescending(b => b.RoleRating)
            .FirstOrDefault();
        if (replacement is null) return;

        state.Events.Add(new MatchEvent(state.NextEventId(), state.Second, EventKind.Substitution,
            team.Side, team.Side, replacement.Id, tired.Player.Id, tired.X, tired.Y, 0, 0, 0, true, -1, 0));

        tired.SubbedOff = true;
        team.Bench.Remove(replacement);
        team.OnPitch.Add(new PlayerMatchState
        {
            Player = replacement, Side = team.Side, SlotIndex = tired.SlotIndex, HomeSlot = tired.HomeSlot,
            X = tired.X, Y = tired.Y, Fatigue = replacement.StartFatigue,
        });
        team.SubsUsed++;
    }

    private static void ReviewMentality(MatchState state, TeamMatchState team, double minute, int goalDiff, MatchRandom rng, double decisiveness)
    {
        if (minute < 65 || !rng.Chance(decisiveness * 0.5)) return;

        var current = team.Tactics.Mentality;
        Mentality? target = (minute, goalDiff) switch
        {
            ( >= 75, > 0) => Mentality.Defensive,          // protect a lead late on
            ( >= 85, < -1) => Mentality.VeryAttacking,      // throwing men forward, run out of time otherwise
            ( >= 70, < 0) => Mentality.Attacking,           // chasing the game
            _ => null,
        };
        if (target is null || target == current) return;

        team.Tactics = team.Tactics with { Mentality = target.Value, Pressing = target.Value >= Mentality.Attacking ? Math.Min(1, team.Tactics.Pressing + 0.15) : team.Tactics.Pressing };
        state.Events.Add(new MatchEvent(state.NextEventId(), state.Second, EventKind.TacticalChange,
            team.Side, team.Side, 0, 0, 0, 0, 0, 0, 0, true, -1, (int)target.Value));
    }
}
