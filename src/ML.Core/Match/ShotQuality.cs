namespace ML.Core.Match;

/// <summary>
/// Turns a shot's situation into a goal probability. Every input is something that happened in
/// the match (location, angle, pressure, how the chance arrived) — nothing here is a flat random
/// number standing in for "quality". The shot-selection logic upstream only offers a Shot action
/// from inside the box, so this deliberately damps the geometric estimate: a modelled "shot" is
/// the whole in-box population (rushed and congested efforts included), not just clean look.
/// </summary>
public static class ShotQuality
{
    private const double PitchLengthMetres = 105.0;

    /// <param name="x">0..1, own goal to opponent goal, attacking frame.</param>
    /// <param name="y">0..1, left to right touchline.</param>
    /// <param name="defendersBetween">Outfield defenders between the ball and the goal line.</param>
    /// <param name="oneOnOne">Keeper is the only defender left to beat.</param>
    public static double BaseXg(double x, double y, int defendersBetween, bool oneOnOne, EventKind assistKind)
    {
        var dx = 1.0 - x;
        var dy = Math.Abs(y - 0.5);
        var distance = Math.Sqrt(dx * dx + dy * dy * 0.65 * 0.65) * PitchLengthMetres;

        var goalHalfWidth = 3.65;
        var angle = Math.Atan2(goalHalfWidth, Math.Max(distance, 0.5)) * 2.0;

        var xg = 1.0 / (1.0 + Math.Exp((distance - 8.0) / 3.2 - angle * 1.05));
        xg *= Math.Pow(0.72, defendersBetween);
        if (oneOnOne) xg *= 1.3;
        xg *= assistKind switch
        {
            EventKind.ThroughBall => 1.25,
            EventKind.Cross => 0.75,
            EventKind.Corner or EventKind.FreeKick => 0.85,
            _ => 1.0,
        };

        // Damping factor: every modelled shot already cleared "is this worth shooting?", so the
        // raw geometric estimate over-represents clean, unpressured efforts.
        return Math.Clamp(xg * 0.175, 0.01, 0.85);
    }
}
