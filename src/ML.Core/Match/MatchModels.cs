using ML.Core.Domain;

namespace ML.Core.Match;

public enum Side { Home = 0, Away = 1 }

public static class SideExtensions
{
    public static Side Other(this Side side) => side == Side.Home ? Side.Away : Side.Home;
}

public enum Mentality { VeryDefensive, Defensive, Balanced, Attacking, VeryAttacking }

public enum BuildUp { Short, Mixed, Direct }

/// <summary>
/// Individual instruction. Each one moves the player's target position or how far he leaves it,
/// so it changes where he is when the ball arrives, not a number added to his rating.
/// </summary>
public enum PlayerInstruction { Default, StayBack, Advance, StayWide, CutInside, Roam }

/// <summary>
/// Team instructions, every value 0..1 unless it is an enum. Read on every tick, so a change
/// during a match acts from the next second.
/// </summary>
public sealed record MatchTactics
{
    public Mentality Mentality { get; init; } = Mentality.Balanced;
    public BuildUp BuildUp { get; init; } = BuildUp.Mixed;
    public double Width { get; init; } = 0.5;
    public double DefensiveLine { get; init; } = 0.5;
    public double Compactness { get; init; } = 0.5;
    public double Pressing { get; init; } = 0.5;
    public double Tempo { get; init; } = 0.5;

    /// <summary>0 = play out through short passes, 1 = go long and early.</summary>
    public double Directness => BuildUp switch { BuildUp.Short => 0.2, BuildUp.Mixed => 0.5, _ => 0.85 };

    /// <summary>How much a carrier is willing to lose the ball for a better chance, 0..1.</summary>
    public double Risk => Math.Clamp(
        0.5 + 0.11 * ((int)Mentality - 2) + 0.18 * (Tempo - 0.5) + 0.12 * (Directness - 0.5), 0.05, 0.95);

    public MatchTactics Clamped() => this with
    {
        Width = Math.Clamp(Width, 0, 1),
        DefensiveLine = Math.Clamp(DefensiveLine, 0, 1),
        Compactness = Math.Clamp(Compactness, 0, 1),
        Pressing = Math.Clamp(Pressing, 0, 1),
        Tempo = Math.Clamp(Tempo, 0, 1),
    };
}

/// <summary>Home position in the team's own attacking frame: X 0 = own goal, 1 = opponent goal; Y 0 = left touchline.</summary>
public sealed record FormationSlot(Position Position, double X, double Y);

public sealed record Formation(string Name, IReadOnlyList<FormationSlot> Slots);

public static class Formations
{
    private static FormationSlot S(Position p, double x, double y) => new(p, x, y);

    public static readonly Formation F442 = new("4-4-2", new[]
    {
        S(Position.GK, .05, .50),
        S(Position.LB, .25, .12), S(Position.CB, .22, .37), S(Position.CB, .22, .63), S(Position.RB, .25, .88),
        S(Position.LMF, .50, .10), S(Position.CMF, .45, .37), S(Position.CMF, .45, .63), S(Position.RMF, .50, .90),
        S(Position.CF, .76, .38), S(Position.CF, .76, .62),
    });

    public static readonly Formation F433 = new("4-3-3", new[]
    {
        S(Position.GK, .05, .50),
        S(Position.LB, .26, .12), S(Position.CB, .22, .37), S(Position.CB, .22, .63), S(Position.RB, .26, .88),
        S(Position.DMF, .38, .50), S(Position.CMF, .50, .30), S(Position.CMF, .50, .70),
        S(Position.LWF, .76, .14), S(Position.CF, .80, .50), S(Position.RWF, .76, .86),
    });

    public static readonly Formation F4231 = new("4-2-3-1", new[]
    {
        S(Position.GK, .05, .50),
        S(Position.LB, .26, .12), S(Position.CB, .22, .37), S(Position.CB, .22, .63), S(Position.RB, .26, .88),
        S(Position.DMF, .40, .38), S(Position.DMF, .40, .62),
        S(Position.LMF, .64, .14), S(Position.AMF, .64, .50), S(Position.RMF, .64, .86),
        S(Position.CF, .80, .50),
    });

    public static readonly Formation F352 = new("3-5-2", new[]
    {
        S(Position.GK, .05, .50),
        S(Position.CB, .22, .25), S(Position.CB, .20, .50), S(Position.CB, .22, .75),
        S(Position.LMF, .50, .08), S(Position.CMF, .44, .34), S(Position.DMF, .38, .50), S(Position.CMF, .44, .66), S(Position.RMF, .50, .92),
        S(Position.CF, .76, .38), S(Position.CF, .76, .62),
    });

    public static IReadOnlyList<Formation> All { get; } = new[] { F442, F433, F4231, F352 };
}

/// <summary>
/// One player as the engine sees him: attributes 0..99. The career database stores far more;
/// this is the subset that changes what happens on a pitch.
/// </summary>
public sealed record MatchPlayer(
    long Id, string Name, Position Position,
    int Pace, int Stamina, int Passing, int Vision, int Dribbling,
    int Shooting, int Defending, int Physical, int Goalkeeping,
    double StartFatigue = 0.0, double Form = 0.0)
{
    /// <summary>
    /// Attributes for a player the career only knows by overall rating and position.
    /// Deterministic, so the same player is the same player every match.
    /// </summary>
    public static MatchPlayer FromOverall(long id, string name, Position position, int overall,
        double startFatigue = 0.0, double form = 0.0)
    {
        int A(int offset) => Math.Clamp(overall + offset, 20, 99);
        return position switch
        {
            Position.GK => new(id, name, position, A(-25), A(-5), A(-15), A(-15), A(-35), A(-45), A(-20), A(-5), A(0), startFatigue, form),
            Position.CB => new(id, name, position, A(-8), A(0), A(-10), A(-10), A(-22), A(-32), A(6), A(6), 20, startFatigue, form),
            Position.LB or Position.RB => new(id, name, position, A(4), A(4), A(-4), A(-6), A(-6), A(-26), A(0), A(-2), 20, startFatigue, form),
            Position.DMF => new(id, name, position, A(-6), A(4), A(0), A(-2), A(-14), A(-18), A(4), A(4), 20, startFatigue, form),
            Position.CMF => new(id, name, position, A(-2), A(4), A(4), A(2), A(-2), A(-10), A(-4), A(-2), 20, startFatigue, form),
            Position.AMF => new(id, name, position, A(0), A(-2), A(4), A(6), A(4), A(0), A(-18), A(-8), 20, startFatigue, form),
            Position.LMF or Position.RMF => new(id, name, position, A(6), A(2), A(0), A(-2), A(4), A(-8), A(-14), A(-8), 20, startFatigue, form),
            Position.LWF or Position.RWF => new(id, name, position, A(8), A(0), A(-4), A(-2), A(6), A(0), A(-24), A(-10), 20, startFatigue, form),
            Position.SS => new(id, name, position, A(2), A(-2), A(0), A(2), A(4), A(4), A(-26), A(-6), 20, startFatigue, form),
            _ => new(id, name, position, A(0), A(-4), A(-10), A(-8), A(-2), A(6), A(-30), A(4), 20, startFatigue, form),
        };
    }

    /// <summary>A single number for bench comparisons, weighted by what the position needs.</summary>
    public double RoleRating => Position.Group() switch
    {
        PositionGroup.Goalkeeper => Goalkeeping,
        PositionGroup.Defender => (2.0 * Defending + Physical + Pace + Passing) / 5.0,
        PositionGroup.Midfielder => (2.0 * Passing + Vision + Dribbling + Defending + Stamina) / 6.0,
        _ => (2.0 * Shooting + Pace + Dribbling + Physical + Vision) / 6.0,
    };
}

public sealed record TeamSheet(
    string Name, Formation Formation, MatchTactics Tactics,
    IReadOnlyList<MatchPlayer> Starters, IReadOnlyList<MatchPlayer> Bench,
    IReadOnlyDictionary<long, PlayerInstruction>? Instructions = null,
    bool HumanControlled = false, double ManagerDecisiveness = 0.85)
{
    public void Validate()
    {
        if (Starters.Count != Formation.Slots.Count)
            throw new ArgumentException($"{Name}: {Starters.Count} starters for {Formation.Slots.Count} slots.");
        var ids = Starters.Concat(Bench).Select(p => p.Id).ToList();
        if (ids.Distinct().Count() != ids.Count)
            throw new ArgumentException($"{Name}: a player appears twice in the squad.");
    }
}

/// <param name="Importance">0 = dead rubber, 1 = final. Feeds manager risk-taking, never player attributes.</param>
/// <param name="TickSeconds">Positions and fatigue advance in steps of this many seconds. Actions fall between ticks at their true time, so a coarse tick changes the geometry, not how many actions happen.</param>
public sealed record MatchSetup(
    int Seed, TeamSheet Home, TeamSheet Away, double Importance = 0.5, int TickSeconds = 1)
{
    public string EngineVersion { get; init; } = MatchSession.Version;
}

public enum EventKind
{
    KickOff, HalfTime, FullTime,
    Pass, ThroughBall, Cross, Dribble, Tackle, Interception, Clearance,
    Shot, Save, Goal, Foul, YellowCard, RedCard, Offside, Corner, FreeKick, Penalty,
    Substitution, Injury, TacticalChange,
}

public enum ShotResult { Goal, Saved, Blocked, OffTarget }

/// <summary>
/// One thing that happened. <see cref="CauseId"/> is the event this one followed from
/// (a goal's cause is the shot, the shot's is the pass), so the log can explain itself.
/// <see cref="Poss"/> is who has the ball once it is over, which is what possession is folded from.
/// </summary>
public readonly record struct MatchEvent(
    int Id, int Second, EventKind Kind, Side Side, Side Poss,
    long ActorId, long OtherId,
    double X, double Y, double ToX, double ToY,
    double Xg, bool Success, int CauseId, int Value);

public enum CommandKind { Tactics, Substitute, Instruction, Resume }

public sealed record MatchCommand(
    int Second, Side Side, CommandKind Kind,
    long PlayerId = 0, long OtherPlayerId = 0,
    MatchTactics? Tactics = null, PlayerInstruction Instruction = PlayerInstruction.Default);

public sealed record MatchRecord(
    MatchSetup Setup, IReadOnlyList<MatchCommand> Commands, IReadOnlyList<MatchEvent> Events,
    int HomeGoals, int AwayGoals, int FirstHalfEnd, int FullTime);
