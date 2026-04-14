#nullable enable

using System;
using cAlgo.Robots.NAS100.Models;

namespace cAlgo.Robots.NAS100.Core;

/// <summary>
/// Calculates Stop Loss and Take Profit as absolute prices using ATR-based sizing.
/// SL distance is clamped between 70% and 150% of DefaultSlPips.
/// TP distance = SL distance * RRRatio.
/// </summary>
public class AtrRiskManager
{
    private readonly int _defaultSlPips;
    private readonly int _defaultTpPips;
    private readonly double _minRR;
    private readonly double _atrSlMultiplier;
    private readonly double _pipSize;

    public AtrRiskManager(
        int defaultSlPips,
        int defaultTpPips,
        double minRR,
        double atrSlMultiplier,
        double pipSize)
    {
        _defaultSlPips = defaultSlPips;
        _defaultTpPips = defaultTpPips;
        _minRR = minRR;
        _atrSlMultiplier = atrSlMultiplier;
        _pipSize = pipSize;
    }

    /// <summary>
    /// Returns (stopPrice, targetPrice) as absolute prices.
    /// Uses the last closed bar's ATR (caller must pass Bars.Count-2 result).
    /// </summary>
    public (double stopPrice, double targetPrice) Calculate(
        ReversalDirection direction,
        double entryPrice,
        double atr)
    {
        double minSlDistance = _defaultSlPips * 0.7 * _pipSize;
        double maxSlDistance = _defaultSlPips * 1.5 * _pipSize;

        double atrSlDistance = atr * _atrSlMultiplier;
        double slDistance = Math.Clamp(atrSlDistance, minSlDistance, maxSlDistance);
        double tpDistance = slDistance * _minRR;

        if (direction == ReversalDirection.Buy)
        {
            double stopPrice   = entryPrice - slDistance;
            double targetPrice = entryPrice + tpDistance;
            return (stopPrice, targetPrice);
        }
        else
        {
            double stopPrice   = entryPrice + slDistance;
            double targetPrice = entryPrice - tpDistance;
            return (stopPrice, targetPrice);
        }
    }
}
