using ML.Core.Domain;

namespace ML.Core.Match;

/// <summary>
/// Works out a sensible individual instruction for each starter from his position, his own
/// attributes, his team's tactics, and the opponent's — the same inputs a human assistant
/// manager would use. Every branch below is tied to an attribute or a tactic setting, your own
/// or the opponent's; none of it is a coin flip standing in for judgement. The caller can
/// override any single player afterward.
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
                Position.LWF or Position.RWF or Position.LMF or Position.RMF => Winger(player, own, opponent),
                Position.DMF => DefensiveMidfielder(player, own, opponent),
                Position.CMF => CentralMidfielder(player, own, opponent),
                Position.AMF => AttackingMidfielder(player, own, opponent),
                Position.CF or Position.SS => Striker(player, own, opponent, strikerSlots),
                Position.CB => CentreBack(player, own, opponent),
                Position.GK => Goalkeeper(player, own),
                _ => PlayerInstruction.Default,
            };
        }
        return result;
    }

    private static PlayerInstruction Fullback(MatchPlayer p, MatchTactics own, MatchTactics opponent)
    {
        if (own.Mentality is Mentality.VeryDefensive or Mentality.Defensive) return PlayerInstruction.StayBack;
        if (own.Mentality == Mentality.VeryAttacking) return PlayerInstruction.AggressiveRuns;

        // An opponent who plays wide stretches your own flank, and one who commits men forward
        // leaves your fullback more exposed on the counter either way — a fullback with nothing
        // special to offer going forward stays home against either threat.
        var attackMinded = p.Dribbling - p.Defending > 6;
        var opponentThreatensThisFlank = opponent.Width > 0.65 || opponent.Mentality is Mentality.Attacking or Mentality.VeryAttacking;
        if (!attackMinded) return opponentThreatensThisFlank ? PlayerInstruction.StayBack : PlayerInstruction.Default;

        if (own.Width < 0.4)
            return p.Passing > 60 && own.BuildUp == BuildUp.Short ? PlayerInstruction.Invert : PlayerInstruction.Underlap;
        return PlayerInstruction.Overlap;
    }

    private static PlayerInstruction Winger(MatchPlayer p, MatchTactics own, MatchTactics opponent)
    {
        if (own.Mentality is Mentality.VeryDefensive or Mentality.Defensive) return PlayerInstruction.TrackBack;

        var paceRunner = p.Pace - p.Dribbling > 8;

        // A side that commits men forward leaves space behind it — a genuine pace threat should
        // sit on that shoulder rather than track back and waste his one edge; anyone else is
        // better off helping out defensively against the extra numbers coming at him.
        if (opponent.Mentality is Mentality.Attacking or Mentality.VeryAttacking)
            return paceRunner ? PlayerInstruction.AttackSpace : PlayerInstruction.TrackBack;

        // A high defensive line leaves the same kind of space even without an attacking opponent.
        if (opponent.DefensiveLine > 0.65) return PlayerInstruction.AttackSpace;

        if (own.Width < 0.4) return PlayerInstruction.CutInside;
        if (own.Width > 0.6) return PlayerInstruction.StayWide;
        return PlayerInstruction.Default;
    }

    private static PlayerInstruction DefensiveMidfielder(MatchPlayer p, MatchTactics own, MatchTactics opponent)
    {
        if (own.Mentality is Mentality.VeryDefensive or Mentality.Defensive) return PlayerInstruction.HoldPosition;
        if (p.Defending - p.Passing > 10) return PlayerInstruction.HoldPosition;   // built to sit, not carry

        // An opponent committing numbers forward needs the middle screened; one pressing high
        // needs an easy out-ball kept deep and available rather than stepping into that press.
        if (opponent.Mentality is Mentality.Attacking or Mentality.VeryAttacking) return PlayerInstruction.CoverCenter;
        if (opponent.Pressing > 0.7) return PlayerInstruction.HoldPosition;
        return PlayerInstruction.Default;
    }

    private static PlayerInstruction CentralMidfielder(MatchPlayer p, MatchTactics own, MatchTactics opponent)
    {
        if (own.Mentality == Mentality.VeryAttacking) return PlayerInstruction.Advance;
        if (own.Mentality is Mentality.VeryDefensive or Mentality.Defensive) return PlayerInstruction.HoldPosition;

        // Against a team throwing men forward or pressing hard, staying available as an outlet
        // matters more than going looking for the ball — even for a player good enough to roam.
        if (opponent.Mentality is Mentality.Attacking or Mentality.VeryAttacking || opponent.Pressing > 0.7)
            return PlayerInstruction.HoldPosition;

        if (p.Vision > 70 && p.Passing > 70) return PlayerInstruction.Roam;   // enough of a playmaker to go looking for it
        return PlayerInstruction.Default;
    }

    private static PlayerInstruction AttackingMidfielder(MatchPlayer p, MatchTactics own, MatchTactics opponent)
    {
        // A high opponent line opens the space between the lines an AMF lives for; a deep,
        // compact block closes that space down, so staying central and combining in a crowded
        // box beats drifting wide looking for room that isn't there.
        if (opponent.DefensiveLine > 0.65 || own.Mentality == Mentality.VeryAttacking || (p.Dribbling > 70 && p.Vision > 70))
            return PlayerInstruction.Roam;
        return PlayerInstruction.Advance;   // an AMF's baseline job is to be the advanced out-ball
    }

    private static PlayerInstruction Striker(MatchPlayer p, MatchTactics own, MatchTactics opponent,
        List<(FormationSlot slot, MatchPlayer player)> strikers)
    {
        // A deep, low block leaves no space in behind to run into — a lone forward is better
        // used holding the ball up or dropping to link play than chasing space that isn't there.
        // A high line is the opposite: that space is exactly what a quick forward should attack.
        var opponentSitsDeep = opponent.DefensiveLine < 0.35;
        var opponentPlaysHigh = opponent.DefensiveLine > 0.65;
        var pressBack = own.Pressing > 0.65 || opponent.Pressing > 0.65;   // worth pressing their build-up either way

        if (strikers.Count >= 2)
        {
            // Two forwards split roles rather than doing the same job twice: the quicker one
            // runs the channels, the other holds the line up front — target man if he has the
            // physicality for it, a false nine dropping deep if his passing says he's more
            // creator than finisher.
            var quicker = strikers.OrderByDescending(s => s.player.Pace).First().player.Id == p.Id;
            if (quicker) return PlayerInstruction.AttackChannel;
            if (opponentPlaysHigh && p.Pace > 60) return PlayerInstruction.AttackChannel;   // both quick enough to exploit a high line
            return p.Physical > 70 ? PlayerInstruction.TargetMan
                : p.Vision > 68 && p.Passing > 65 && p.Passing > p.Shooting ? PlayerInstruction.FalseNine
                : PlayerInstruction.Default;
        }

        if (opponentSitsDeep)
        {
            if (p.Physical > 65) return PlayerInstruction.TargetMan;
            if (p.Vision > 62 && p.Passing > 58) return PlayerInstruction.FalseNine;
        }
        if (p.Physical > 70 && p.Pace < 65) return PlayerInstruction.TargetMan;
        if (p.Vision > 68 && p.Passing > 65 && p.Passing > p.Shooting) return PlayerInstruction.FalseNine;
        if (opponentPlaysHigh && p.Pace > 65) return PlayerInstruction.AdvancedForward;
        if (own.Mentality is Mentality.Attacking or Mentality.VeryAttacking && p.Pace > 70) return PlayerInstruction.AdvancedForward;
        if (pressBack) return PlayerInstruction.PressingForward;
        return PlayerInstruction.Default;
    }

    private static PlayerInstruction CentreBack(MatchPlayer p, MatchTactics own, MatchTactics opponent)
    {
        // Aggressive, ball-winning centre-backs step into midfield to win it back high; a team
        // sitting deep or facing one throwing men forward wants its centre-backs disciplined and
        // deep instead, not stepping out of the line to chase the game.
        if (own.Mentality is Mentality.VeryDefensive or Mentality.Defensive) return PlayerInstruction.Sweep;
        if (opponent.Mentality is Mentality.Attacking or Mentality.VeryAttacking) return PlayerInstruction.Sweep;
        if (p.Defending - p.Passing > 8 && p.Physical > 65) return PlayerInstruction.StepUp;
        return PlayerInstruction.Default;
    }

    private static PlayerInstruction Goalkeeper(MatchPlayer p, MatchTactics own)
    {
        // A high defensive line needs a keeper willing to come off his line and cover the space
        // behind it — without that cover a high line just invites a clean run in on goal. A team
        // sitting deep keeps its keeper on his line, where a deep block leaves him anyway.
        if (own.DefensiveLine > 0.6 && p.Passing > 45) return PlayerInstruction.SweeperKeeper;
        return PlayerInstruction.HoldLine;
    }
}
