using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.Robots.Models;

namespace cAlgo.Robots.Utils;

/// <summary>
/// Extension methods for bar-by-bar price action analysis based on Al Brooks' methodology.
/// </summary>
public static class BarHelpers
{
    /// <summary>
    /// Determines if a bar is a strong bull bar per Brooks' criteria:
    /// body > 50% of range, range > 50% of ATR, close in the upper third.
    /// </summary>
    public static bool IsStrongBullBar(this BarInfo bar, double atr)
    {
        if (atr <= 0) return false;

        return bar.IsBull
            && bar.BodyPercent > 0.50
            && bar.Range > 0.50 * atr
            && bar.ClosePosition >= 0.67;
    }

    /// <summary>
    /// Determines if a bar is a strong bear bar per Brooks' criteria:
    /// body > 50% of range, range > 50% of ATR, close in the lower third.
    /// </summary>
    public static bool IsStrongBearBar(this BarInfo bar, double atr)
    {
        if (atr <= 0) return false;

        return bar.IsBear
            && bar.BodyPercent > 0.50
            && bar.Range > 0.50 * atr
            && bar.ClosePosition <= 0.33;
    }

    /// <summary>
    /// Determines if a bar qualifies as a bull reversal bar per Brooks' criteria:
    /// 1. Close > Open OR close in the upper third
    /// 2. Body >= 40% of range
    /// 3. Lower tail &lt;= 30% of range
    /// 4. Range >= 50% of ATR (not too small)
    /// 5. Range &lt;= 200% of ATR (not a climax)
    /// 6. Close above the midpoint of the previous bar
    /// </summary>
    public static bool IsBullReversalBar(this BarInfo bar, BarInfo previous, double atr)
    {
        if (atr <= 0 || previous == null) return false;

        var hasGoodClose = bar.IsBull || bar.ClosePosition >= 0.67;
        var hasAdequateBody = bar.BodyPercent >= 0.40;
        var hasSmallLowerTail = bar.LowerTailPercent <= 0.30;
        var hasMinRange = bar.Range >= 0.50 * atr;
        var isNotClimax = bar.Range <= 2.00 * atr;
        var closesAbovePrevMid = bar.Close > previous.MidPoint;

        return hasGoodClose
            && hasAdequateBody
            && hasSmallLowerTail
            && hasMinRange
            && isNotClimax
            && closesAbovePrevMid;
    }

    /// <summary>
    /// Determines if a bar qualifies as a bear reversal bar per Brooks' criteria.
    /// Mirror of <see cref="IsBullReversalBar"/>.
    /// </summary>
    public static bool IsBearReversalBar(this BarInfo bar, BarInfo previous, double atr)
    {
        if (atr <= 0 || previous == null) return false;

        var hasGoodClose = bar.IsBear || bar.ClosePosition <= 0.33;
        var hasAdequateBody = bar.BodyPercent >= 0.40;
        var hasSmallUpperTail = bar.UpperTailPercent <= 0.30;
        var hasMinRange = bar.Range >= 0.50 * atr;
        var isNotClimax = bar.Range <= 2.00 * atr;
        var closesBelowPrevMid = bar.Close < previous.MidPoint;

        return hasGoodClose
            && hasAdequateBody
            && hasSmallUpperTail
            && hasMinRange
            && isNotClimax
            && closesBelowPrevMid;
    }

    /// <summary>
    /// Calculates the percentage of vertical overlap between two bars (0.0 to 1.0).
    /// High overlap (>70%) indicates a trading range; low overlap indicates a trend.
    /// </summary>
    public static double OverlapPercent(this BarInfo bar, BarInfo other)
    {
        if (other == null) return 0.0;

        var overlapHigh = Math.Min(bar.High, other.High);
        var overlapLow = Math.Max(bar.Low, other.Low);
        var overlap = Math.Max(0, overlapHigh - overlapLow);

        var combinedRange = Math.Max(bar.Range, other.Range);
        if (combinedRange <= 0) return 0.0;

        return overlap / combinedRange;
    }

    /// <summary>
    /// Detects Barb Wire: 3+ overlapping bars where at least one is a doji, near the EMA.
    /// Barb Wire = no-trade zone for MTR setups.
    /// </summary>
    /// <param name="recentBars">List of recent BarInfo (most recent last).</param>
    /// <param name="count">Minimum number of overlapping bars to qualify (default 3).</param>
    /// <returns>True if Barb Wire is detected.</returns>
    public static bool IsBarbWire(List<BarInfo> recentBars, int count = 3)
    {
        if (recentBars == null || recentBars.Count < count)
            return false;

        var barsToCheck = recentBars.Skip(recentBars.Count - count).ToList();

        // At least one doji required
        if (!barsToCheck.Any(b => b.IsDoji))
            return false;

        // All consecutive pairs must have significant overlap (>50%)
        for (int i = 1; i < barsToCheck.Count; i++)
        {
            if (barsToCheck[i].OverlapPercent(barsToCheck[i - 1]) < 0.50)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks whether the closes of the last N bars are trending in one direction.
    /// Bull: each close >= previous close. Bear: each close &lt;= previous close.
    /// </summary>
    /// <param name="bars">List of recent BarInfo (most recent last).</param>
    /// <param name="count">Number of bars to check.</param>
    /// <param name="bullish">True to check for bullish trending closes, false for bearish.</param>
    /// <returns>True if closes are trending in the specified direction.</returns>
    public static bool HasTrendingCloses(List<BarInfo> bars, int count, bool bullish)
    {
        if (bars == null || bars.Count < count || count < 2)
            return false;

        var barsToCheck = bars.Skip(bars.Count - count).ToList();

        for (int i = 1; i < barsToCheck.Count; i++)
        {
            if (bullish && barsToCheck[i].Close < barsToCheck[i - 1].Close)
                return false;
            if (!bullish && barsToCheck[i].Close > barsToCheck[i - 1].Close)
                return false;
        }

        return true;
    }
}
