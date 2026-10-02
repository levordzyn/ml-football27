using ML.Core.Domain;

namespace ML.Core.Match;

/// <summary>
/// Works out a sensible individual instruction for each starter from his position, his own
/// attributes, his team's tactics, and the opponent's — the same inputs a human assistant
/// manager would use. Every branch below is tied to an attribute or a tactic setting; none of
/// it is a coin flip standing in for judgement. The caller can override any single player
/// afterward.
/// </summary>
public static class AutoInstructions
{
    public static IReadOnlyDictionary<long, PlayerInstruction> Assign(
        IReadOnlyList<MatchPlayer> starters, Formation formation, MatchTactics own, MatchTactics opponent)
    {
        ArgumentNullException.ThrowIfNull(starters);
        if (starters.Count != formation.Slots.Count)
            throw new ArgumentException($"{starters.Count} starters for a {formation.Slots.Count}-player formation.");

        var strikerSlots = formation.Slots
            .Select((slot, i) => (slot, player: starters[i]))
            .Where(t => t.slot.Position is Position.CF or Position.SS)
            .ToList();

        var result = new Dictionary<long, PlayerInstruction>();
        for (var i = 0; i < formation.Slots.Count; i++)
        {
            var player = starters[i];
            var position = formation.Slots[i].Position;
            result[player.Id] = position switch
            {
                Position.LB or Position.RB => Fullback(player, own, opponent),
                Position.LWF or Position.RWF or Position.LMF or Position.RMF => Winger(player, own),
                Position.DMF => DefensiveMidfielder(player, own, opponent),
                Position.CMF => CentralMidfielder(player, own),
                Position.AMF => AttackingMidfielder(player, own),
                Position.CF or Position.SS => Striker(player, own, strikerSlots),
                _ => PlayerInstruction.Default,   // GK, CB: no instruction axis modelled yet
            };
        }
        return result;
    }

    private static PlayerInstruction Fullback(MatchPlayer p, MatchTactics own, MatchTactics opponent)
    {
        if (own.Mentality is Mentality.VeryDefensive or Mentality.Defensive) return PlayerInstruction.StayBack;
        if (own.Mentality == Mentality.VeryAttacking) return PlayerInstruction.AggressiveRuns;

        // An opponent who plays wide stretches your own flank — a fullback with nothing special
        // to offer going forward stays home against that threat rather than leaving it open.
        var attackMinded = p.Dribbling - p.Defending > 6;
        if (!attackMinded) return opponent.Width > 0.65 ? PlayerInstruction.StayBack : PlayerInstruction.Default;

        if (own.Width < 0.4)
            return p.Passing > 60 && own.BuildUp == BuildUp.Short ? PlayerInstruction.Invert : PlayerInstruction.Underlap;
        return PlayerInstruction.Overlap;
    }

    private static PlayerInstruction Winger(MatchPlayer p, MatchTactics own)
    {
        if (own.Mentality is Mentality.VeryDefensive or Mentality.Defensive) return PlayerInstruction.TrackBack;
        if (p.Pace - p.Dribbling > 8) return PlayerInstruction.AttackSpace;   // a runner, not a dribbler
        if (own.Width < 0.4) return PlayerInstruction.CutInside;
        if (own.Width > 0.6) return PlayerInstruction.StayWide;
        return PlayerInstruction.Default;
    }

    private static PlayerInstruction DefensiveMidfielder(MatchPlayer p, MatchTactics own, MatchTactics opponent)
    {
        if (own.Mentality is Mentality.VeryDefensive or Mentality.Defensive) return PlayerInstruction.HoldPosition;
        if (p.Defending - p.Passing > 10) return PlayerInstruction.HoldPosition;   // built to sit, not carry
        if (opponent.Mentality is Mentality.Attacking or Mentality.VeryAttacking) return PlayerInstruction.CoverCenter;
        return PlayerInstruction.Default;
    }

    private static PlayerInstruction CentralMidfielder(MatchPlayer p, MatchTactics own)
    {
        if (own.Mentality == Mentality.VeryAttacking) return PlayerInstruction.Advance;
        if (own.Mentality is Mentality.VeryDefensive or Mentality.Defensive) return PlayerInstruction.HoldPosition;
        if (p.Vision > 70 && p.Passing > 70) return PlayerInstruction.Roam;   // enough of a playmaker to go looking for it
        return PlayerInstruction.Default;
    }

    private static PlayerInstruction AttackingMidfielder(MatchPlayer p, MatchTactics own)
    {
        if (own.Mentality == Mentality.VeryAttacking || (p.Dribbling > 70 && p.Vision > 70))
            return PlayerInstruction.Roam;
        return PlayerInstruction.Advance;   // an AMF's baseline job is to be the advanced out-ball
    }

    private static PlayerInstruction Striker(MatchPlayer p, MatchTactics own, List<(FormationSlot slot, MatchPlayer player)> strikers)
    {
        if (strikers.Count >= 2)
        {
            // Two forwards split roles rather than doing the same job twice: the quicker one
            // runs the channels, the other holds the line up front — target man if he has the
            // physicality for it, a false nine dropping deep if his passing says he's more
            // creator than finisher.
            var quicker = strikers.OrderByDescending(s => s.player.Pace).First().player.Id == p.Id;
            if (quicker) return PlayerInstruction.AttackChannel;
            return p.Physical > 70 ? PlayerInstruction.TargetMan
                : p.Vision > 68 && p.Passing > 65 && p.Passing > p.Shooting ? PlayerInstruction.FalseNine
                : PlayerInstruction.Default;
        }

        if (p.Physical > 70 && p.Pace < 65) return PlayerInstruction.TargetMan;
        if (p.Vision > 68 && p.Passing > 65 && p.Passing > p.Shooting) return PlayerInstruction.FalseNine;
        if (own.Mentality is Mentality.Attacking or Mentality.VeryAttacking && p.Pace > 70) return PlayerInstruction.AdvancedForward;
        if (own.Pressing > 0.65) return PlayerInstruction.PressingForward;
        return PlayerInstruction.Default;
    }
}
