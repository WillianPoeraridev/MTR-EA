#nullable enable

using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.Robots.Models;

namespace cAlgo.Robots.Core;

/// <summary>
/// Generates all valid trendline candidates from combinations of swing points.
/// Filters by separation, slope, and ensures lines don't cut through price action.
/// </summary>
public class TrendLineCandidateGenerator
{
    private readonly int _minBarsBetweenPoints;
    private readonly int _maxBarsBetweenPoints;
    private readonly double _maxSlopePerBar;
    private const int MaxSwingPoints = 15;
    private const double SlopeTolerance = 0.00005; // Allow slightly counter-directional slopes

    /// <summary>
    /// Creates a new candidate generator.
    /// </summary>
    /// <param name="minBarsBetweenPoints">Minimum bars between anchor points (default 8 = 40min on M5).</param>
    /// <param name="maxBarsBetweenPoints">Maximum bars between anchor points (default 200).</param>
    /// <param name="maxSlopePerBar">Maximum absolute slope per bar (default 0.001 ≈ 10 pips/bar on GBP/USD).</param>
    public TrendLineCandidateGenerator(
        int minBarsBetweenPoints = 8,
        int maxBarsBetweenPoints = 200,
        double maxSlopePerBar = 0.001)
    {
        _minBarsBetweenPoints = minBarsBetweenPoints;
        _maxBarsBetweenPoints = maxBarsBetweenPoints;
        _maxSlopePerBar = maxSlopePerBar;
    }

    /// <summary>
    /// Generates all valid trendline candidates from current swing points.
    /// </summary>
    public List<TrendLineCandidate> Generate(
        SwingPointTracker swingTracker, Bars bars, int currentBarIndex, double atr20)
    {
        var candidates = new List<TrendLineCandidate>();

        // Bull TLs from swing lows
        var lows = swingTracker.GetRecentLows(MaxSwingPoints);
        GenerateCandidates(candidates, lows, TrendLineDirection.Bull, bars, currentBarIndex, atr20);

        // Bear TLs from swing highs
        var highs = swingTracker.GetRecentHighs(MaxSwingPoints);
        GenerateCandidates(candidates, highs, TrendLineDirection.Bear, bars, currentBarIndex, atr20);

        return candidates;
    }

    private void GenerateCandidates(
        List<TrendLineCandidate> candidates, List<SwingPoint> points,
        TrendLineDirection direction, Bars bars, int currentBarIndex, double atr20)
    {
        if (points.Count < 2) return;

        for (int i = 0; i < points.Count - 1; i++)
        {
            for (int j = i + 1; j < points.Count; j++)
            {
                var p1 = points[i];
                var p2 = points[j];
                int barsBetween = p2.BarIndex - p1.BarIndex;

                // Check separation
                if (barsBetween < _minBarsBetweenPoints || barsBetween > _maxBarsBetweenPoints)
                    continue;

                double slope = (p2.Price - p1.Price) / barsBetween;

                // Check slope direction
                if (direction == TrendLineDirection.Bull)
                {
                    // Bull TL (support) should have slope >= 0 or very slightly negative
                    if (slope < -SlopeTolerance) continue;
                }
                else
                {
                    // Bear TL (resistance) should have slope <= 0 or very slightly positive
                    if (slope > SlopeTolerance) continue;
                }

                // Check max slope
                if (Math.Abs(slope) > _maxSlopePerBar) continue;

                // Check that the line doesn't cut through price action between the two points
                if (!IsCleanLine(p1, p2, slope, direction, bars, atr20))
                    continue;

                candidates.Add(new TrendLineCandidate
                {
                    Point1 = p1,
                    Point2 = p2,
                    Direction = direction,
                    Slope = slope,
                    DurationBars = barsBetween
                });
            }
        }
    }

    /// <summary>
    /// Verifies that no bar between Point1 and Point2 significantly violates the trendline.
    /// For Bull TL: no bar's Low should be significantly below the line.
    /// For Bear TL: no bar's High should be significantly above the line.
    /// Tolerance: 0.3 * ATR.
    /// </summary>
    private bool IsCleanLine(SwingPoint p1, SwingPoint p2, double slope,
        TrendLineDirection direction, Bars bars, double atr20)
    {
        double tolerance = 0.3 * atr20;

        for (int k = p1.BarIndex + 1; k < p2.BarIndex; k++)
        {
            double linePrice = p1.Price + slope * (k - p1.BarIndex);

            if (direction == TrendLineDirection.Bull)
            {
                // Bar's Low significantly below the support line = line cuts through price
                if (bars.LowPrices[k] < linePrice - tolerance)
                    return false;
            }
            else
            {
                // Bar's High significantly above the resistance line = line cuts through price
                if (bars.HighPrices[k] > linePrice + tolerance)
                    return false;
            }
        }

        return true;
    }
}
