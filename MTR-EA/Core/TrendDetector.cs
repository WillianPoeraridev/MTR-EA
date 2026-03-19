#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.Robots.Models;
using cAlgo.Robots.Utils;

namespace cAlgo.Robots.Core;

/// <summary>
/// Classifies the market as Bull Trend, Bear Trend, or Trading Range on each bar.
/// Computes trend strength (0.0–1.0) based on Al Brooks' Signs of Strength,
/// Always In direction, and Two-Hour Move detection.
/// </summary>
public class TrendDetector
{
    private readonly SwingPointTracker _swingTracker;
    private readonly int _lookbackBars;

    private TrendDirection _previousDirection = TrendDirection.Range;
    private int _barsSinceTrendStart;

    /// <summary>Current trend assessment, updated on each call to <see cref="Update"/>.</summary>
    public TrendState CurrentTrend { get; private set; } = new();

    /// <summary>
    /// Creates a new TrendDetector.
    /// </summary>
    /// <param name="swingTracker">The swing point tracker to read swing structure from.</param>
    /// <param name="lookbackBars">Number of bars to look back for analysis (default 50).</param>
    public TrendDetector(SwingPointTracker swingTracker, int lookbackBars = 50)
    {
        _swingTracker = swingTracker;
        _lookbackBars = lookbackBars;
    }

    /// <summary>
    /// Updates the trend classification based on current market data.
    /// Should be called once per bar after SwingPointTracker.Update().
    /// </summary>
    /// <param name="bars">The cTrader Bars data series.</param>
    /// <param name="currentIndex">Index of the closed bar being analyzed.</param>
    /// <param name="ema20">Current EMA 20 value.</param>
    /// <param name="atr20">Current ATR 20 value.</param>
    public void Update(Bars bars, int currentIndex, double ema20, double atr20)
    {
        var direction = DetectDirection();
        var swingCount = CountAlignedSwings(direction);

        // Track bars since trend started
        if (direction == _previousDirection && direction != TrendDirection.Range)
        {
            _barsSinceTrendStart++;
        }
        else if (direction != _previousDirection)
        {
            _barsSinceTrendStart = 0;
        }
        _previousDirection = direction;

        var strength = CalculateStrength(bars, currentIndex, direction, ema20, atr20);
        var isTwoHourMove = DetectTwoHourMove(bars, currentIndex, ema20);

        // Brooks' Always In: strong trend = forced side
        var alwaysIn = AlwaysInDirection.None;
        if (direction != TrendDirection.Range && strength > 0.5)
            alwaysIn = direction == TrendDirection.Bull ? AlwaysInDirection.Long : AlwaysInDirection.Short;
        else if (strength < 0.3)
            alwaysIn = AlwaysInDirection.None;

        CurrentTrend = new TrendState
        {
            Direction = direction,
            Strength = strength,
            BarsSinceTrendStart = _barsSinceTrendStart,
            SwingCount = swingCount,
            IsTwoHourMove = isTwoHourMove,
            AlwaysIn = alwaysIn
        };
    }

    /// <summary>
    /// Determines trend direction from swing point structure.
    /// Bull = HH + HL, Bear = LH + LL, otherwise Range.
    /// </summary>
    private TrendDirection DetectDirection()
    {
        bool hasHH = _swingTracker.HasHigherHighs(2);
        bool hasHL = _swingTracker.HasHigherLows(2);
        bool hasLH = _swingTracker.HasLowerHighs(2);
        bool hasLL = _swingTracker.HasLowerLows(2);

        if (hasHH && hasHL) return TrendDirection.Bull;
        if (hasLH && hasLL) return TrendDirection.Bear;
        return TrendDirection.Range;
    }

    /// <summary>
    /// Counts aligned swing points (HH+HL pairs for bull, LH+LL pairs for bear).
    /// </summary>
    private int CountAlignedSwings(TrendDirection direction)
    {
        if (direction == TrendDirection.Range) return 0;

        int count = 0;
        if (direction == TrendDirection.Bull)
        {
            // Count how many consecutive HH we have (up to 5)
            for (int n = 2; n <= 5; n++)
            {
                if (_swingTracker.HasHigherHighs(n) && _swingTracker.HasHigherLows(n))
                    count = n * 2; // Each n means n highs + n lows aligned
                else break;
            }
        }
        else
        {
            for (int n = 2; n <= 5; n++)
            {
                if (_swingTracker.HasLowerHighs(n) && _swingTracker.HasLowerLows(n))
                    count = n * 2;
                else break;
            }
        }

        return Math.Max(count, direction != TrendDirection.Range ? 4 : 0);
    }

    /// <summary>
    /// Calculates trend strength (0.0–1.0) based on 11 active Signs of Strength from Al Brooks.
    /// Factor #3 removed from MVP (would create circular dependency with TrendLineTracker).
    /// </summary>
    private double CalculateStrength(Bars bars, int currentIndex, TrendDirection direction, double ema20, double atr20)
    {
        if (direction == TrendDirection.Range || atr20 <= 0)
            return 0.0;

        bool isBull = direction == TrendDirection.Bull;
        double strength = 0.0;

        // Build recent BarInfos for analysis
        int lookback = Math.Min(_lookbackBars, currentIndex);
        var recentBars = new List<BarInfo>();
        BarInfo? prev = null;
        int startIndex = Math.Max(0, currentIndex - lookback + 1);
        for (int i = startIndex; i <= currentIndex; i++)
        {
            var bar = BarInfo.FromBars(bars, i, prev);
            recentBars.Add(bar);
            prev = bar;
        }

        if (recentBars.Count < 5) return 0.0;

        // Factor 1: Trending highs and lows (swings aligned) → +0.10
        if (isBull ? (_swingTracker.HasHigherHighs(2) && _swingTracker.HasHigherLows(2))
                   : (_swingTracker.HasLowerHighs(2) && _swingTracker.HasLowerLows(2)))
            strength += 0.10;

        // Factor 2: No climaxes (few bars with range > 2x ATR) → +0.08
        int climaxCount = recentBars.TakeLast(20).Count(b => b.Range > 2.0 * atr20);
        if (climaxCount <= 1)
            strength += 0.08;

        // Factor 3: REMOVED from MVP (TrendLineTracker dependency)

        // Factor 4: 2HM — price far from EMA for many bars (>24 bars = 2h on M5) → +0.10
        if (DetectTwoHourMove(bars, currentIndex, ema20))
            strength += 0.10;

        // Factor 5: Small pullbacks (each pullback < 40% of ATR * 3) → +0.10
        if (HasSmallPullbacks(recentBars, atr20, isBull))
            strength += 0.10;

        // Factor 6: No two consecutive closes on the wrong side of EMA → +0.10
        if (!HasConsecutiveClosesWrongSide(recentBars, ema20, isBull))
            strength += 0.10;

        // Factor 7: Small tails (average tail < 30% of range) → +0.08
        var last20 = recentBars.TakeLast(20).Where(b => b.Range > 0).ToList();
        if (last20.Count > 0)
        {
            double avgTailPercent = isBull
                ? last20.Average(b => b.UpperTailPercent)
                : last20.Average(b => b.LowerTailPercent);
            if (avgTailPercent < 0.30)
                strength += 0.08;
        }

        // Factor 8: Trending closes (last 5 bars with progressive closes) → +0.08
        if (BarHelpers.HasTrendingCloses(recentBars, 5, isBull))
            strength += 0.08;

        // Factor 9: Shrinking Stairs → -0.15 (REDUCES strength — momentum decay)
        if (_swingTracker.HasShrinkingStairs())
            strength -= 0.15;

        // Factor 10: Many trend bars in direction (>60% of last 20) → +0.10
        var last20Bars = recentBars.TakeLast(20).ToList();
        if (last20Bars.Count >= 10)
        {
            int trendBarCount = isBull
                ? last20Bars.Count(b => b.IsBull && b.BodyPercent > 0.40)
                : last20Bars.Count(b => b.IsBear && b.BodyPercent > 0.40);
            if ((double)trendBarCount / last20Bars.Count > 0.60)
                strength += 0.10;
        }

        // Factor 11: EMA slope aligned with direction → +0.08
        if (currentIndex >= 5)
        {
            double emaSlope = ema20 - bars.ClosePrices[currentIndex - 5]; // Approximate slope
            // Use EMA values would be ideal, but we approximate with close vs current EMA
            double emaCurrent = ema20;
            double emaPast = bars.ClosePrices[currentIndex - 5]; // Rough proxy
            if ((isBull && emaCurrent > emaPast) || (!isBull && emaCurrent < emaPast))
                strength += 0.08;
        }

        // Factor 12: Consecutive bars in trend direction (>5 without pullback) → +0.10
        int consecutive = 0;
        for (int i = recentBars.Count - 1; i >= 0; i--)
        {
            if (isBull ? recentBars[i].IsBull : recentBars[i].IsBear)
                consecutive++;
            else
                break;
        }
        if (consecutive > 5)
            strength += 0.10;

        return Math.Clamp(strength, 0.0, 1.0);
    }

    /// <summary>
    /// Detects Two-Hour Move: price hasn't touched EMA20 for 24+ bars (2 hours on M5).
    /// Brooks considers this a sign of strong trend — institutions are in control.
    /// </summary>
    private bool DetectTwoHourMove(Bars bars, int currentIndex, double ema20)
    {
        const int twoHourBars = 24; // 24 x 5min = 2 hours
        int barsSinceEmaTouch = 0;

        for (int i = currentIndex; i >= Math.Max(0, currentIndex - twoHourBars * 2); i--)
        {
            double low = bars.LowPrices[i];
            double high = bars.HighPrices[i];

            // Bar touched EMA if EMA is between low and high
            if (low <= ema20 && high >= ema20)
                break;

            barsSinceEmaTouch++;
        }

        return barsSinceEmaTouch > twoHourBars;
    }

    /// <summary>
    /// Checks if pullbacks are small relative to ATR.
    /// Brooks: in a strong trend, pullbacks should be < 40% of recent swing distance.
    /// </summary>
    private bool HasSmallPullbacks(List<BarInfo> recentBars, double atr20, bool isBull)
    {
        double maxPullback = atr20 * 3.0 * 0.40; // 40% of 3x ATR

        // Find the deepest pullback in recent bars
        double extremePrice = isBull ? recentBars.Max(b => b.High) : recentBars.Min(b => b.Low);
        double worstPullback = 0;

        foreach (var bar in recentBars.TakeLast(20))
        {
            double pullback = isBull
                ? extremePrice - bar.Low
                : bar.High - extremePrice;

            if (pullback > worstPullback)
                worstPullback = pullback;
        }

        return worstPullback < maxPullback;
    }

    /// <summary>
    /// Checks if there are two consecutive closes on the wrong side of EMA.
    /// Brooks: this weakens the trend case significantly.
    /// </summary>
    private bool HasConsecutiveClosesWrongSide(List<BarInfo> recentBars, double ema20, bool isBull)
    {
        var last10 = recentBars.TakeLast(10).ToList();
        for (int i = 1; i < last10.Count; i++)
        {
            bool prevWrong = isBull ? last10[i - 1].Close < ema20 : last10[i - 1].Close > ema20;
            bool currWrong = isBull ? last10[i].Close < ema20 : last10[i].Close > ema20;
            if (prevWrong && currWrong)
                return true;
        }
        return false;
    }
}
