using ML.Core.Domain;

namespace ML.Core.Match;

public enum MatchPhase { PreMatch, FirstHalf, HalfTime, SecondHalf, FullTime }

public sealed class MatchState
{
    public required MatchSetup Setup { get; init; }
    public MatchPhase Phase { get; set; } = MatchPhase.PreMatch;
    public int Second { get; set; }
    public int HomeGoals { get; set; }
    public int AwayGoals { get; set; }
    public Side Possession { get; set; } = Side.Home;
    public double BallX { get; set; } = 0.5;
    public double BallY { get; set; } = 0.5;

    public required TeamMatchState[] Teams { get; init; }

    public TeamMatchState Of(Side side) => Teams[(int)side];

    public List<MatchEvent> Events { get; } = new();
    public List<MatchCommand> CommandLog { get; } = new();

    private int _nextEventId;
    public int NextEventId() => _nextEventId++;
}
