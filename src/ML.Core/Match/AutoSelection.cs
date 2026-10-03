using ML.Core.Domain;

namespace ML.Core.Match;

public sealed record XiResult(IReadOnlyList<MatchPlayer> Starters, IReadOnlyList<MatchPlayer> Bench);

/// <summary>
/// Builds the best XI for a formation out of a squad pool — not the eleven highest overalls.
/// Condition, form and position fit can all move a player below someone rated lower but fresher
/// and in form, same as a real selection meeting. The caller can override every slot afterward;
/// this only picks a sensible starting point.
/// </summary>
public static class AutoSelection
{
    /// <param name="pool">Every player available for selection (suspended/injured players should
    /// already be filtered out by the caller — this makes no availability judgement of its own).
    /// </param>
    /// <param name="importance">0..1. Raises the fatigue penalty for a big match: a manager who'd
    /// rest a tired starter in a dead rubber plays him through the pain for a final.</param>
    public static XiResult PickXi(IReadOnlyList<MatchPlayer> pool, Formation formation, double importance = 0.5)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (pool.Count < formation.Slots.Count)
            throw new ArgumentException($"Pool has {pool.Count} players for a {formation.Slots.Count}-player formation.");

        var remaining = pool.ToList();
        var starters = new List<MatchPlayer>(formation.Slots.Count);

        foreach (var slot in formation.Slots)
        {
            var best = remaining
                .OrderByDescending(p => SlotScore(p, slot.Position, importance))
                .First();
            starters.Add(best);
            remaining.Remove(best);
        }

        var bench = remaining.OrderByDescending(p => p.RoleRating).ToList();
        return new XiResult(starters, bench);
    }

    /// <summary>
    /// How well a player fits a slot right now. Fatigue costs more than a modest rating gap —
    /// the spec example (an 84-rated midfielder exhausted, a 79-rated one fresh) is the
    /// calibration target: roughly 0.35 fatigue costs about 7 points, enough to flip a 5-point
    /// rating gap. Importance scales that penalty up for a final, down for a dead rubber.
    /// </summary>
    private static double SlotScore(MatchPlayer p, Position slotPosition, double importance)
    {
        var positionFit = p.Position == slotPosition ? 6.0
            : p.Position.Group() == slotPosition.Group() ? 2.0
            : -10.0;

        var fatiguePenalty = p.StartFatigue * (14.0 + 10.0 * importance);
        var formBonus = p.Form * 7.0;

        return positionFit + p.RoleRating + formBonus - fatiguePenalty;
    }

    /// <summary>
    /// Picks a standing corner-kick and free-kick taker from the XI, by the same attribute
    /// blend the engine falls back to when no one is designated — the best passer for corners,
    /// the best shooting/passing blend for direct free kicks. A real team settles on these once
    /// rather than picking fresh for every set piece; this gives a caller that starting point.
    /// </summary>
    public static (long CornerTakerId, long FreeKickTakerId) PickSetPieceTakers(IReadOnlyList<MatchPlayer> starters)
    {
        var outfielders = starters.Where(p => !p.Position.IsGoalkeeper()).ToList();
        if (outfielders.Count == 0) throw new ArgumentException("No outfielders to pick a set-piece taker from.");

        var cornerTaker = outfielders.OrderByDescending(p => p.Passing).First();
        var freeKickTaker = outfielders.OrderByDescending(p => p.Shooting * 0.6 + p.Passing * 0.4).First();
        return (cornerTaker.Id, freeKickTaker.Id);
    }
}
