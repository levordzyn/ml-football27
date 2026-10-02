namespace ML.Core.Match;

/// <summary>
/// What a <see cref="PlayerInstruction"/> actually changes on the pitch, in four independent
/// axes. <see cref="Default"/> is the all-zero/neutral profile by construction — every axis
/// resolves to exactly what the engine did before individual instructions existed, so leaving a
/// player on Default never shifts a match's outcome.
/// </summary>
/// <param name="XPull">How far toward the ball (0 = own goal, 1 = opponent's goal) this player's
/// home position gets pulled when the ball is advanced. Replaces a fixed home slot with a
/// role: a winger told to AttackSpace drifts far higher than one told to TrackBack.</param>
/// <param name="WidthBias">Added to the player's home Y (0 = left touchline, 1 = right), inverted
/// for the away side same as everything else. Positive pushes toward his own flank, negative
/// pulls infield.</param>
/// <param name="PressBias">Added to this player's effective Defending when he's the one closing
/// down an opponent. A positive value is a player working harder without the ball; negative is
/// one who conserves himself or simply isn't primarily a presser.</param>
/// <param name="ShotBias">Added to the probability he pulls the trigger once he's in a shooting
/// position. Small by design — a striker told to shoot more still needs the chance to arrive.</param>
/// <param name="WorkRateBias">Multiplies how fast fatigue accumulates. Covering more ground costs
/// more stamina; holding a line costs less.</param>
public readonly record struct InstructionProfile(
    double XPull, double WidthBias, double PressBias, double ShotBias, double WorkRateBias);

public static class InstructionProfiles
{
    private static readonly InstructionProfile Neutral = new(0.35, 0, 0, 0, 1.0);

    public static InstructionProfile Get(PlayerInstruction instruction) => instruction switch
    {
        PlayerInstruction.Default => Neutral,

        // Fullback: how far forward he gets pulled, and whether he stays wide doing it.
        PlayerInstruction.StayBack => new(0.15, 0, +3, 0, 1.00),
        PlayerInstruction.Overlap => new(0.44, +0.08, 0, 0, 1.15),
        PlayerInstruction.Underlap => new(0.40, -0.06, 0, +0.01, 1.15),
        PlayerInstruction.Invert => new(0.32, -0.14, +2, 0, 1.05),
        PlayerInstruction.AggressiveRuns => new(0.48, +0.05, -2, +0.01, 1.15),

        // Winger: width and defensive effort.
        PlayerInstruction.StayWide => new(0.35, +0.10, 0, 0, 1.00),
        PlayerInstruction.CutInside => new(0.38, -0.12, 0, +0.02, 1.00),
        PlayerInstruction.AttackSpace => new(0.52, +0.04, -2, +0.02, 1.15),
        PlayerInstruction.TrackBack => new(0.20, 0, +5, -0.03, 1.00),

        // Midfielder: how advanced, how disciplined without the ball.
        PlayerInstruction.HoldPosition => new(0.26, 0, +4, -0.02, 1.00),
        PlayerInstruction.Roam => new(0.35, 0, -2, 0, 1.15),
        PlayerInstruction.Advance => new(0.55, 0, 0, 0, 1.15),
        PlayerInstruction.CoverCenter => new(0.28, -0.05, +5, -0.02, 1.00),
        PlayerInstruction.PressAggressively => new(0.38, 0, +7, 0, 1.15),

        // Striker: where he plays and how trigger-happy he is.
        PlayerInstruction.TargetMan => new(0.42, 0, 0, +0.02, 1.00),
        PlayerInstruction.AdvancedForward => new(0.58, 0, -2, +0.04, 1.15),
        PlayerInstruction.FalseNine => new(0.28, -0.06, -1, -0.03, 1.05),
        PlayerInstruction.PressingForward => new(0.44, 0, +6, +0.01, 1.15),
        PlayerInstruction.AttackChannel => new(0.50, +0.10, -1, +0.02, 1.15),

        _ => Neutral,
    };
}
