#nullable enable

using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.Robots.NAS100.Models;
using cAlgo.Robots.Models;

namespace cAlgo.Robots.NAS100.Core;

/// <summary>
/// Detects Rising Wedge (sell setup) and Falling Wedge (buy setup) from active trendlines.
/// </summary>
public class WedgeDetector
{
    private const int MinConvergenceBars = 5;
    private const int MaxConvergenceBars = 40;

    /// <summary>
    /// Evaluates the active bull and bear trendlines for a wedge pattern.
    /// Returns an OpeningLevel if a valid wedge is found, otherwise null.
    /// </summary>
    public OpeningLevel? Detect(
        List<TrendLineCandidate> activeBullLines,
        List<TrendLineCandidate> activeBearLines,
        int currentBarIndex)
    {
        foreach (var bearTL in activeBearLines)
        {
            foreach (var bullTL in activeBullLines)
            {
                // Rising Wedge: both slopes positive, bear slope less steep than bull slope
                if (bullTL.Slope > 0 && bearTL.Slope > 0 && bearTL.Slope < bullTL.Slope)
                {
                    double conv = ConvergenceInBars(bullTL, bearTL, currentBarIndex);
                    if (conv >= MinConvergenceBars && conv <= MaxConvergenceBars)
                    {
                        return new OpeningLevel
                        {
                            LevelType = LevelType.Wedge,
                            ReversalDirection = ReversalDirection.Sell,
                            TriggerPrice = bearTL.GetPriceAt(currentBarIndex),
                            BarIndexCreated = currentBarIndex,
                            IsActive = true
                        };
                    }
                }

                // Falling Wedge: both slopes negative, bear slope less negative than bull slope
                if (bullTL.Slope < 0 && bearTL.Slope < 0 && bearTL.Slope > bullTL.Slope)
                {
                    double conv = ConvergenceInBars(bullTL, bearTL, currentBarIndex);
                    if (conv >= MinConvergenceBars && conv <= MaxConvergenceBars)
                    {
                        return new OpeningLevel
                        {
                            LevelType = LevelType.Wedge,
                            ReversalDirection = ReversalDirection.Buy,
                            TriggerPrice = bullTL.GetPriceAt(currentBarIndex),
                            BarIndexCreated = currentBarIndex,
                            IsActive = true
                        };
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Calculates how many bars until the two trendlines converge (cross).
    /// Positive = they will cross in the future; negative = already crossed.
    /// </summary>
    private double ConvergenceInBars(TrendLineCandidate tl1, TrendLineCandidate tl2, int refBar)
    {
        double slopeDiff = tl1.Slope - tl2.Slope;
        if (Math.Abs(slopeDiff) < 1e-10)
            return double.MaxValue;

        double priceDiff = tl2.GetPriceAt(refBar) - tl1.GetPriceAt(refBar);
        return priceDiff / slopeDiff;
    }
}
