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
/// Uses hysteresis to prevent rapid oscillation between states:
/// - Enter trend (from Range): requires strength ≥ 0.40
/// - Exit trend (to Range): requires strength to drop below 0.20
/// - Bull↔Bear direct flip: allowed without hysteresis when swing structure changes clearly.
/// </summary>
public class TrendDetector
{
    private readonly SwingPointTracker _swingTracker;
    private readonly int _lookbackBars;

    // Hysteresis thresholds
    private const double EnterTrendThreshold = 0.40;
    private const double ExitTrendThreshold = 0.20;

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
    public void Update(Bars bars, int currentIndex, double ema20, double atr20)
    {
        var rawDirection = DetectRawDirection();
        var strength = CalculateStrength(bars, currentIndex, rawDirection, ema20, atr20);

        // Apply hysteresis to prevent rapid Scanning↔Trending oscillation
        var direction = ApplyHysteresis(rawDirection, strength);

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
    /// Determines raw trend direction from swing point structure (no hysteresis).
    /// Bull = HH + HL, Bear = LH + LL, otherwise Range.
    /// </summary>
    private TrendDirection DetectRawDirection()
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
    /// Applies hysteresis to direction changes to prevent rapid oscillation.
    /// - Range → Bull/Bear: requires strength ≥ 0.40
    /// - Bull/Bear → Range: requires strength to drop below 0.20
    /// - Bull ↔ Bear: direct flip allowed when swing structure changes clearly (no hysteresis)
    /// </summary>
    private TrendDirection ApplyHysteresis(TrendDirection rawDirection, double strength)
    {
        // Direct Bull↔Bear flip: swing structure changed clearly, allow without hysteresis
        if (_previousDirection == TrendDirection.Bull && rawDirection == TrendDirection.Bear)
            return TrendDirection.Bear;
        if (_previousDirection == TrendDirection.Bear && rawDirection == TrendDirection.Bull)
            return TrendDirection.Bull;

        // Range → Trend: need strong enough signal to enter
        if (_previousDirection == TrendDirection.Range && rawDirection != TrendDirection.Range)
        {
            return strength >= EnterTrendThreshold ? rawDirection : TrendDirection.Range;
        }

        // Trend → Range: need weakness to exit (maintain trend if strength is still moderate)
        if (_previousDirection != TrendDirection.Range && rawDirection == TrendDirection.Range)
        {
            return strength >= ExitTrendThreshold ? _previousDirection : TrendDirection.Range;
        }

        // Same direction or same state — keep raw
        return rawDirection;
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
            for (int n = 2; n <= 5; n++)
            {
                if (_swingTracker.HasHigherHighs(n) && _swingTracker.HasHigherLows(n))
                    count = n * 2;
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
    /// </summary>
    private double CalculateStrength(Bars bars, int currentIndex, TrendDirection direction, double ema20, double atr20)
    {
        if (direction == TrendDirection.Range || atr20 <= 0)
            return 0.0;

        bool isBull = direction == TrendDirection.Bull;
        double strength = 0.0;

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

        // Factor 1: Trending highs and lows → +0.10
        if (isBull ? (_swingTracker.HasHigherHighs(2) && _swingTracker.HasHigherLows(2))
                   : (_swingTracker.HasLowerHighs(2) && _swingTracker.HasLowerLows(2)))
            strength += 0.10;

        // Factor 2: No climaxes → +0.08
        int climaxCount = recentBars.TakeLast(20).Count(b => b.Range > 2.0 * atr20);
        if (climaxCount <= 1) strength += 0.08;

        // Factor 4: 2HM → +0.10
        if (DetectTwoHourMove(bars, currentIndex, ema20)) strength += 0.10;

        // Factor 5: Small pullbacks → +0.10
        if (HasSmallPullbacks(recentBars, atr20, isBull)) strength += 0.10;

        // Factor 6: No consecutive closes wrong side of EMA → +0.10
        if (!HasConsecutiveClosesWrongSide(recentBars, ema20, isBull)) strength += 0.10;

        // Factor 7: Small tails → +0.08
        var last20 = recentBars.TakeLast(20).Where(b => b.Range > 0).ToList();
        if (last20.Count > 0)
        {
            double avgTailPercent = isBull
                ? last20.Average(b => b.UpperTailPercent)
                : last20.Average(b => b.LowerTailPercent);
            if (avgTailPercent < 0.30) strength += 0.08;
        }

        // Factor 8: Trending closes → +0.08
        if (BarHelpers.HasTrendingCloses(recentBars, 5, isBull)) strength += 0.08;

        // Factor 9: Shrinking Stairs → -0.15
        if (_swingTracker.HasShrinkingStairs()) strength -= 0.15;

        // Factor 10: Many trend bars (>60% of last 20) → +0.10
        var last20Bars = recentBars.TakeLast(20).ToList();
        if (last20Bars.Count >= 10)
        {
            int trendBarCount = isBull
                ? last20Bars.Count(b => b.IsBull && b.BodyPercent > 0.40)
                : last20Bars.Count(b => b.IsBear && b.BodyPercent > 0.40);
            if ((double)trendBarCount / last20Bars.Count > 0.60) strength += 0.10;
        }

        // Factor 11: EMA slope aligned → +0.08
        if (currentIndex >= 5)
        {
            double emaCurrent = ema20;
            double emaPast = bars.ClosePrices[currentIndex - 5];
            if ((isBull && emaCurrent > emaPast) || (!isBull && emaCurrent < emaPast))
                strength += 0.08;
        }

        // Factor 12: Consecutive bars in trend direction (>5) → +0.10
        int consecutive = 0;
        for (int i = recentBars.Count - 1; i >= 0; i--)
        {
            if (isBull ? recentBars[i].IsBull : recentBars[i].IsBear)
                consecutive++;
            else break;
        }
        if (consecutive > 5) strength += 0.10;

        return Math.Clamp(strength, 0.0, 1.0);
    }

    private bool DetectTwoHourMove(Bars bars, int currentIndex, double ema20)
    {
        const int twoHourBars = 24;
        int barsSinceEmaTouch = 0;

        for (int i = currentIndex; i >= Math.Max(0, currentIndex - twoHourBars * 2); i--)
        {
            double low = bars.LowPrices[i];
            double high = bars.HighPrices[i];
            if (low <= ema20 && high >= ema20) break;
            barsSinceEmaTouch++;
        }

        return barsSinceEmaTouch > twoHourBars;
    }

    private bool HasSmallPullbacks(List<BarInfo> recentBars, double atr20, bool isBull)
    {
        double maxPullback = atr20 * 3.0 * 0.40;
        double extremePrice = isBull ? recentBars.Max(b => b.High) : recentBars.Min(b => b.Low);
        double worstPullback = 0;

        foreach (var bar in recentBars.TakeLast(20))
        {
            double pullback = isBull ? extremePrice - bar.Low : bar.High - extremePrice;
            if (pullback > worstPullback) worstPullback = pullback;
        }

        return worstPullback < maxPullback;
    }

    private bool HasConsecutiveClosesWrongSide(List<BarInfo> recentBars, double ema20, bool isBull)
    {
        var last10 = recentBars.TakeLast(10).ToList();
        for (int i = 1; i < last10.Count; i++)
        {
            bool prevWrong = isBull ? last10[i - 1].Close < ema20 : last10[i - 1].Close > ema20;
            bool currWrong = isBull ? last10[i].Close < ema20 : last10[i].Close > ema20;
            if (prevWrong && currWrong) return true;
        }
        return false;
    }
}
