#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.Robots.Models;

namespace cAlgo.Robots.Core;

/// <summary>
/// Scores trendline candidates (0–100) based on 6 quality criteria:
/// touches, duration, respect events, recency, slope consistency, and cleanliness.
/// Higher-scoring lines correspond to trendlines that institutional traders would draw.
/// </summary>
public class TrendLineScorer
{
    private readonly double _touchToleranceAtr;
    private readonly double _respectToleranceAtr;
    private readonly int _expirationBars;
    private const int MaxLookbackBars = 200;

    /// <summary>
    /// Creates a new scorer.
    /// </summary>
    /// <param name="touchToleranceAtr">Max distance (as ATR fraction) to count as a touch (default 0.3).</param>
    /// <param name="respectToleranceAtr">Max distance (as ATR fraction) to count as respect (default 0.5).</param>
    /// <param name="expirationBars">Bars without a touch before a line expires (default 100).</param>
    public TrendLineScorer(
        double touchToleranceAtr = 0.3,
        double respectToleranceAtr = 0.5,
        int expirationBars = 100)
    {
        _touchToleranceAtr = touchToleranceAtr;
        _respectToleranceAtr = respectToleranceAtr;
        _expirationBars = expirationBars;
    }

    /// <summary>
    /// Scores a single candidate in the context of current market data.
    /// Fills in Score, TouchCount, RespectCount, BarsSinceLastTouch, IsActive.
    /// </summary>
    public void Score(
        TrendLineCandidate candidate,
        SwingPointTracker swingTracker,
        Bars bars,
        int currentBarIndex,
        double atr20)
    {
        if (atr20 <= 0)
        {
            candidate.Score = 0;
            candidate.IsActive = false;
            return;
        }

        double touchScore = ScoreTouches(candidate, swingTracker, atr20);
        double durationScore = ScoreDuration(candidate);
        double respectScore = ScoreRespect(candidate, bars, currentBarIndex, atr20);
        double recencyScore = ScoreRecency(candidate, currentBarIndex);
        double slopeScore = ScoreSlopeConsistency(candidate, swingTracker);
        double cleanScore = ScoreCleanLine(candidate, bars, atr20);

        double total = touchScore + durationScore + respectScore + recencyScore + slopeScore + cleanScore;
        candidate.Score = Math.Clamp(total, 0, 100);

        // Expire lines with no recent activity
        if (candidate.BarsSinceLastTouch > _expirationBars)
            candidate.IsActive = false;
    }

    /// <summary>
    /// Touch Count (weight: 30) — how many swing points (beyond anchor points) touch the line.
    /// </summary>
    private double ScoreTouches(TrendLineCandidate candidate, SwingPointTracker swingTracker, double atr20)
    {
        double tolerance = _touchToleranceAtr * atr20;
        var allSwings = candidate.Direction == TrendLineDirection.Bull
            ? swingTracker.GetRecentLows(20)
            : swingTracker.GetRecentHighs(20);

        int extraTouches = 0;
        int lastTouchBar = candidate.Point2.BarIndex;

        foreach (var sp in allSwings)
        {
            // Skip the anchor points themselves
            if (sp.BarIndex == candidate.Point1.BarIndex || sp.BarIndex == candidate.Point2.BarIndex)
                continue;

            double linePrice = candidate.GetPriceAt(sp.BarIndex);
            double distance = Math.Abs(sp.Price - linePrice);

            if (distance <= tolerance)
            {
                extraTouches++;
                if (sp.BarIndex > lastTouchBar)
                    lastTouchBar = sp.BarIndex;
            }
        }

        // Total touch count includes the 2 anchor points
        candidate.TouchCount = 2 + extraTouches;
        // Update last touch tracking (used by recency scoring)
        candidate.BarsSinceLastTouch = Math.Max(0, candidate.Point2.BarIndex - lastTouchBar);

        return extraTouches switch
        {
            0 => 0,
            1 => 10,
            2 => 20,
            _ => 30
        };
    }

    /// <summary>
    /// Duration (weight: 20) — how many bars the line covers.
    /// </summary>
    private double ScoreDuration(TrendLineCandidate candidate)
    {
        int bars = candidate.DurationBars;

        if (bars < 12) return 0;
        if (bars < 24) return 5;
        if (bars < 48) return 10;
        if (bars < 96) return 15;
        return 20;
    }

    /// <summary>
    /// Respect (weight: 25) — how many times price reversed near the line.
    /// Groups consecutive bars near the line into single "respect events".
    /// </summary>
    private double ScoreRespect(TrendLineCandidate candidate, Bars bars, int currentBarIndex, double atr20)
    {
        double tolerance = _respectToleranceAtr * atr20;
        int respectCount = 0;
        bool inRespectZone = false;
        int lastRespectBar = candidate.Point1.BarIndex;

        int endBar = Math.Min(currentBarIndex, candidate.Point1.BarIndex + MaxLookbackBars);

        for (int k = candidate.Point1.BarIndex; k <= endBar; k++)
        {
            double linePrice = candidate.GetPriceAt(k);
            double close = bars.ClosePrices[k];

            bool nearLine;
            bool respected;

            if (candidate.Direction == TrendLineDirection.Bull)
            {
                // Bull TL (support): Low near line AND close above line = respected
                nearLine = Math.Abs(bars.LowPrices[k] - linePrice) <= tolerance;
                respected = nearLine && close > linePrice;
            }
            else
            {
                // Bear TL (resistance): High near line AND close below line = respected
                nearLine = Math.Abs(bars.HighPrices[k] - linePrice) <= tolerance;
                respected = nearLine && close < linePrice;
            }

            if (respected)
            {
                if (!inRespectZone)
                {
                    respectCount++;
                    lastRespectBar = k;
                    inRespectZone = true;
                }
            }
            else
            {
                inRespectZone = false;
            }
        }

        candidate.RespectCount = respectCount;

        // Update last touch to include respect events
        if (lastRespectBar > candidate.Point2.BarIndex)
            candidate.BarsSinceLastTouch = Math.Max(0, currentBarIndex - lastRespectBar);
        else
            candidate.BarsSinceLastTouch = Math.Max(0, currentBarIndex - candidate.Point2.BarIndex);

        return respectCount switch
        {
            0 => 0,
            1 => 8,
            2 => 16,
            _ => 25
        };
    }

    /// <summary>
    /// Recency (weight: 10) — how recently the line was touched or respected.
    /// </summary>
    private double ScoreRecency(TrendLineCandidate candidate, int currentBarIndex)
    {
        int barsSince = candidate.BarsSinceLastTouch;

        if (barsSince < 12) return 10;
        if (barsSince < 24) return 7;
        if (barsSince < 48) return 4;
        return 0;
    }

    /// <summary>
    /// Slope Consistency (weight: 10) — is the slope aligned with the local swing trend?
    /// </summary>
    private double ScoreSlopeConsistency(TrendLineCandidate candidate, SwingPointTracker swingTracker)
    {
        // Determine local trend from swing points directly
        bool hasHH = swingTracker.HasHigherHighs(2);
        bool hasHL = swingTracker.HasHigherLows(2);
        bool hasLH = swingTracker.HasLowerHighs(2);
        bool hasLL = swingTracker.HasLowerLows(2);

        bool isBullContext = hasHH && hasHL;
        bool isBearContext = hasLH && hasLL;

        if (candidate.Direction == TrendLineDirection.Bull)
        {
            if (isBullContext && candidate.Slope >= 0) return 10; // Perfect alignment
            if (isBearContext) return 3; // Counter-trend line
            return 5; // Indefinite context
        }
        else
        {
            if (isBearContext && candidate.Slope <= 0) return 10;
            if (isBullContext) return 3;
            return 5;
        }
    }

    /// <summary>
    /// Clean Line (weight: 5) — how many bars between anchor points violate the line.
    /// </summary>
    private double ScoreCleanLine(TrendLineCandidate candidate, Bars bars, double atr20)
    {
        double tolerance = 0.3 * atr20;
        int violations = 0;

        for (int k = candidate.Point1.BarIndex + 1; k < candidate.Point2.BarIndex; k++)
        {
            double linePrice = candidate.GetPriceAt(k);

            if (candidate.Direction == TrendLineDirection.Bull)
            {
                if (bars.LowPrices[k] < linePrice - tolerance)
                    violations++;
            }
            else
            {
                if (bars.HighPrices[k] > linePrice + tolerance)
                    violations++;
            }
        }

        if (violations == 0) return 5;
        if (violations == 1) return 3;
        return 0;
    }
}
