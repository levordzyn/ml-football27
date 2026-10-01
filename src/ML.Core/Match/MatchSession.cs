namespace ML.Core.Match;

/// <summary>
/// Entry point for playing one match. <see cref="Version"/> is stored on every
/// <see cref="MatchRecord"/>; bump it whenever the engine's behaviour changes, so a replay
/// knows whether it can reproduce the original result or only re-derive the stats.
/// </summary>
public static class MatchSession
{
    public const string Version = "1.0.0";

    /// <summary>Play the whole match with no interactive commands beyond what the AI managers issue themselves.</summary>
    public static MatchRecord RunAutomatic(MatchSetup setup)
    {
        var engine = new MatchEngine(setup);
        while (!engine.IsFullTime)
        {
            engine.AdvanceTo(int.MaxValue);
            if (engine.State.Phase == MatchPhase.HalfTime)
                engine.ApplyCommand(new MatchCommand(engine.State.Second, Side.Home, CommandKind.Resume));
        }
        return ToRecord(engine);
    }

    public static MatchRecord ToRecord(MatchEngine engine)
    {
        var state = engine.State;
        var firstHalfEnd = state.Events.FirstOrDefault(e => e.Kind == EventKind.HalfTime).Second;
        var fullTime = state.Events.LastOrDefault(e => e.Kind == EventKind.FullTime).Second;
        return new MatchRecord(state.Setup, state.CommandLog, state.Events, state.HomeGoals, state.AwayGoals, firstHalfEnd, fullTime);
    }
}
