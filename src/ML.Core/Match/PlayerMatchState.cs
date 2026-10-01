using ML.Core.Domain;

namespace ML.Core.Match;

/// <summary>Everything about one player that changes during the match.</summary>
public sealed class PlayerMatchState
{
    public required MatchPlayer Player { get; init; }
    public required Side Side { get; init; }
    public required int SlotIndex { get; init; }
    public required FormationSlot HomeSlot { get; set; }
    public PlayerInstruction Instruction { get; set; } = PlayerInstruction.Default;

    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>0 at kick-off, rises toward 1 across 90 minutes of work.</summary>
    public double Fatigue { get; set; }

    public int YellowCards { get; set; }
    public bool SentOff { get; set; }
    public bool Injured { get; set; }
    public bool SubbedOff { get; set; }
    public bool OnPitch => !SubbedOff && !SentOff;

    /// <summary>Attribute after fatigue and form, before the action-specific roll. 20 at Fatigue 1.</summary>
    public double Effective(int rawAttribute) =>
        Math.Clamp(rawAttribute * (1.0 - 0.35 * Fatigue) + Player.Form * 3.0, 20, 99);

    /// <summary>How far this player has strayed from his formation slot, for AI-manager checks.</summary>
    public double DistanceFromHome(double atX, double atY) =>
        Math.Sqrt((X - atX) * (X - atX) + (Y - atY) * (Y - atY));
}

public sealed class TeamMatchState
{
    public required TeamSheet Sheet { get; init; }
    public required Side Side { get; init; }
    public MatchTactics Tactics { get; set; } = new();
    public required List<PlayerMatchState> OnPitch { get; init; }
    public required List<MatchPlayer> Bench { get; init; }
    public int SubsUsed { get; set; }

    public double TeamFatigue => OnPitch.Count == 0 ? 0 : OnPitch.Average(p => p.Fatigue);

    public PlayerMatchState? Find(long playerId) => OnPitch.FirstOrDefault(p => p.Player.Id == playerId);

    public PlayerMatchState Goalkeeper => OnPitch.First(p => p.Player.Position.IsGoalkeeper());
}
