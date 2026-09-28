using ML.Core.Selection;

namespace ML.Core.Tests;

public class ConditionModelTests
{
    [Fact]
    public void RestAndStartsMoveFatigueWithinTheBand()
    {
        Assert.Equal(12, ConditionModel.AfterRest(20));          // -8 per matchday off
        Assert.Equal(25, ConditionModel.AfterStart(10));         // +15 per start
    }

    [Fact]
    public void FatigueClampsAtZeroAndAtTheCeiling()
    {
        Assert.Equal(0, ConditionModel.AfterRest(5));            // can't rest below fresh
        Assert.Equal(0, ConditionModel.AfterRest(0));
        Assert.Equal(ConditionModel.MaxFatigue, ConditionModel.AfterStart(55));
        Assert.Equal(ConditionModel.MaxFatigue, ConditionModel.AfterStart(ConditionModel.MaxFatigue));
    }

    [Fact]
    public void FormDriftsTowardNeutralOnADraw()
    {
        var hot = ConditionModel.FormAfterResult(8.0, 0);
        Assert.True(hot < 8.0 && hot > 6.5);                     // hot streak cools

        var cold = ConditionModel.FormAfterResult(5.0, 0);
        Assert.True(cold > 5.0 && cold < 6.5);                   // slump recovers

        Assert.Equal(6.5, ConditionModel.FormAfterResult(6.5, 0), 10);
    }

    [Fact]
    public void WinsRaiseFormAndLossesLowerIt()
    {
        Assert.Equal(6.95, ConditionModel.FormAfterResult(6.5, 1), 10);   // +0.5, then 10% drift back
        Assert.Equal(6.05, ConditionModel.FormAfterResult(6.5, -1), 10);
    }

    [Fact]
    public void FormClampsToFourThroughNine()
    {
        Assert.Equal(9.0, ConditionModel.FormAfterResult(9.0, 1));
        Assert.Equal(4.0, ConditionModel.FormAfterResult(4.0, -1));
    }

    [Fact]
    public void InjuryRollIsDeterministicForTheSameInputs()
    {
        for (var pid = 1; pid <= 500; pid++)
            Assert.Equal(ConditionModel.InjuryRoll(3, 7, pid), ConditionModel.InjuryRoll(3, 7, pid));
    }

    [Fact]
    public void InjuryRateIsRoughlyFourAndAHalfPercent()
    {
        var injuries = 0;
        for (var pid = 1; pid <= 10000; pid++)
            if (ConditionModel.InjuryRoll(1, 1, pid) is not null) injuries++;

        Assert.InRange(injuries, 350, 550);                      // 3.5%..5.5% of 10,000 rolls
    }

    [Fact]
    public void MostInjuriesAreShortAndAFewKeepAPlayerOutForMonths()
    {
        const int matchday = 12;
        var lengths = new List<int>();
        for (var pid = 1; pid <= 40000; pid++)
            if (ConditionModel.InjuryRoll(2, matchday, pid) is { } outUntil)
                lengths.Add(outUntil - matchday);

        Assert.All(lengths, l => Assert.InRange(l, 1, 33));
        Assert.InRange(lengths.Count(l => l == 1) / (double)lengths.Count, 0.35, 0.45);   // a week or less
        Assert.InRange(lengths.Count(l => l >= 4) / (double)lengths.Count, 0.20, 0.30);   // a month or more
        Assert.Contains(lengths, l => l >= 24);                                           // season-ending
        Assert.InRange(lengths.Average(), 2.9, 3.7);
    }

    [Fact]
    public void DifferentMatchdaysRollIndependently()
    {
        // A roll must not be a function of the player id alone: a squad that dodges every knock
        // on matchday 1 (200 pids at ~4.5% could) must still pick some up across a 38-matchday
        // season — ~340 expected over the 7,600 rolls.
        var anyInjuredAcrossSeason = Enumerable.Range(1, 200).Any(pid =>
            Enumerable.Range(1, 38).Any(md => ConditionModel.InjuryRoll(1, md, pid) is not null));

        Assert.True(anyInjuredAcrossSeason);
    }
}
