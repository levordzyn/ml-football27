namespace ML.Core.Match;

/// <summary>
/// SplitMix64 stream. <see cref="System.Random"/> is only stable for a given seed within one
/// runtime version; a replayable match needs a generator whose output never changes, so the
/// match engine owns its own. Fork a new stream per concern (physics, manager) so adding a
/// draw in one place does not shift every later draw in another.
/// </summary>
public sealed class MatchRandom : IRandomSource
{
    private ulong _state;

    public MatchRandom(ulong seed) => _state = seed;

    public static MatchRandom For(int seed, ulong stream) =>
        new(unchecked((ulong)(long)seed * 0x9E3779B97F4A7C15UL ^ (stream + 1) * 0xD1B54A32D192ED03UL));

    public ulong NextUInt64()
    {
        unchecked
        {
            _state += 0x9E3779B97F4A7C15UL;
            var z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / 9007199254740992.0);

    public int Next(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);
        return (int)(NextDouble() * maxExclusive);
    }

    public bool Chance(double p) => NextDouble() < p;

    /// <summary>Standard normal via Box-Muller.</summary>
    public double NextGaussian()
    {
        var u1 = 1.0 - NextDouble();
        var u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
