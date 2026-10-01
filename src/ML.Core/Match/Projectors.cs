namespace ML.Core.Match;

public sealed record TeamMatchStats(
    double PossessionPct, int Shots, int ShotsOnTarget, double Xg, int Passes, double PassAccuracyPct,
    int Tackles, int Interceptions, int Clearances, int Fouls, int Offsides, int Corners, int Saves,
    int YellowCards, int RedCards);

public sealed record PlayerMatchStats(
    long PlayerId, int Goals, int Assists, int Shots, int ShotsOnTarget, double Xg,
    int Passes, int PassesCompleted, int Tackles, int Interceptions, int Saves,
    int YellowCards, int RedCards, double Rating);

/// <summary>
/// Folds team and player stats, and post-match ratings, out of the event log — nothing here is
/// computed separately from what the match engine actually produced.
/// </summary>
public static class Projectors
{
    public static TeamMatchStats TeamStats(IReadOnlyList<MatchEvent> events, Side side)
    {
        var possSeconds = new double[2];
        for (var i = 0; i < events.Count; i++)
        {
            var span = i + 1 < events.Count ? events[i + 1].Second - events[i].Second : 0;
            possSeconds[(int)events[i].Poss] += Math.Max(span, 0);
        }
        var totalPoss = possSeconds[0] + possSeconds[1];
        var possessionPct = totalPoss > 0 ? 100.0 * possSeconds[(int)side] / totalPoss : 50.0;

        bool Mine(MatchEvent e) => e.Side == side;
        var shots = events.Count(e => e.Kind == EventKind.Shot && Mine(e));
        var onTarget = events.Count(e => e.Kind == EventKind.Shot && Mine(e)
            && e.Value is (int)ShotResult.Goal or (int)ShotResult.Saved);
        var xg = events.Where(e => e.Kind == EventKind.Shot && Mine(e)).Sum(e => e.Xg);
        var passes = events.Count(e => e.Kind is EventKind.Pass or EventKind.ThroughBall or EventKind.Cross && Mine(e));
        var completed = events.Count(e => e.Kind is EventKind.Pass or EventKind.ThroughBall or EventKind.Cross && Mine(e) && e.Success);

        return new TeamMatchStats(
            Math.Round(possessionPct, 1), shots, onTarget, Math.Round(xg, 2), passes,
            passes > 0 ? Math.Round(100.0 * completed / passes, 1) : 0,
            events.Count(e => e.Kind == EventKind.Tackle && e.Side == side),
            events.Count(e => e.Kind == EventKind.Interception && e.Side == side),
            events.Count(e => e.Kind == EventKind.Clearance && e.Side == side),
            events.Count(e => e.Kind == EventKind.Foul && e.Side == side),
            events.Count(e => e.Kind == EventKind.Offside && Mine(e)),
            events.Count(e => e.Kind == EventKind.Corner && Mine(e)),
            events.Count(e => e.Kind == EventKind.Save && e.Side == side),
            events.Count(e => e.Kind == EventKind.YellowCard && e.Side == side),
            events.Count(e => e.Kind == EventKind.RedCard && e.Side == side));
    }

    public static IReadOnlyList<PlayerMatchStats> PlayerStats(IReadOnlyList<MatchEvent> events, IEnumerable<long> playerIds)
    {
        var byId = events.Where(e => e.CauseId >= 0).ToDictionary(e => e.Id, e => e);
        var results = new List<PlayerMatchStats>();

        foreach (var id in playerIds)
        {
            var goals = events.Count(e => e.Kind == EventKind.Goal && e.ActorId == id);
            var assists = events.Count(e =>
                e.Kind == EventKind.Goal && byId.TryGetValue(e.CauseId, out var shot)
                && byId.TryGetValue(shot.CauseId, out var buildUp)
                && buildUp.Kind is EventKind.Pass or EventKind.ThroughBall or EventKind.Cross && buildUp.ActorId == id);
            var shots = events.Count(e => e.Kind == EventKind.Shot && e.ActorId == id);
            var onTarget = events.Count(e => e.Kind == EventKind.Shot && e.ActorId == id
                && e.Value is (int)ShotResult.Goal or (int)ShotResult.Saved);
            var xg = events.Where(e => e.Kind == EventKind.Shot && e.ActorId == id).Sum(e => e.Xg);
            var passes = events.Count(e => e.Kind is EventKind.Pass or EventKind.ThroughBall or EventKind.Cross && e.ActorId == id);
            var completed = events.Count(e => e.Kind is EventKind.Pass or EventKind.ThroughBall or EventKind.Cross && e.ActorId == id && e.Success);
            var tackles = events.Count(e => e.Kind == EventKind.Tackle && e.ActorId == id);
            var interceptions = events.Count(e => e.Kind == EventKind.Interception && e.ActorId == id);
            var saves = events.Count(e => e.Kind == EventKind.Save && e.ActorId == id);
            var yellows = events.Count(e => e.Kind == EventKind.YellowCard && e.ActorId == id);
            var reds = events.Count(e => e.Kind == EventKind.RedCard && e.ActorId == id);
            var conceded = events.Count(e => e.Kind == EventKind.Goal && byId.TryGetValue(e.CauseId, out var s) && s.OtherId == id);
            var lostPossession = events.Count(e => e.Kind is EventKind.Tackle or EventKind.Interception && e.OtherId == id);

            var rating = 6.0
                + goals * 1.0 + assists * 0.6
                + Math.Min(shots, 5) * 0.08
                + (passes > 0 ? (completed / (double)passes - 0.75) * 1.6 : 0)
                + tackles * 0.10 + interceptions * 0.10 + saves * 0.18
                - lostPossession * 0.06 - conceded * 0.12
                - yellows * 0.3 - reds * 1.5;

            results.Add(new PlayerMatchStats(id, goals, assists, shots, onTarget, Math.Round(xg, 2),
                passes, completed, tackles, interceptions, saves, yellows, reds, Math.Round(Math.Clamp(rating, 2.0, 10.0), 1)));
        }

        return results;
    }
}
