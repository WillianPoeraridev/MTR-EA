#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.Robots.Models;

namespace cAlgo.Robots.Core;

/// <summary>
/// Detects and tracks swing highs and swing lows on the price chart.
/// A swing point is confirmed when it has <see cref="_strength"/> bars on each side
/// that do not exceed its price level, AND the swing has significant size relative to ATR.
/// </summary>
public class SwingPointTracker
{
    private readonly int _strength;
    private readonly int _maxHistory;
    private readonly double _minSwingSizePercent;
    private readonly List<SwingPoint> _swingPoints;

    /// <summary>
    /// Creates a new SwingPointTracker.
    /// </summary>
    /// <param name="strength">Number of bars before and after required for confirmation (default 5).</param>
    /// <param name="maxHistory">Maximum number of swing points to retain (default 20).</param>
    /// <param name="minSwingSizePercent">Minimum swing size as fraction of ATR to filter noise (default 0.30).</param>
    public SwingPointTracker(int strength = 5, int maxHistory = 20, double minSwingSizePercent = 0.30)
    {
        _strength = Math.Max(1, strength);
        _maxHistory = Math.Max(4, maxHistory);
        _minSwingSizePercent = minSwingSizePercent;
        _swingPoints = new List<SwingPoint>();
    }

    /// <summary>The most recent confirmed swing high, or null if none exists.</summary>
    public SwingPoint? LastSwingHigh =>
        _swingPoints.LastOrDefault(sp => sp.Type == SwingType.High);

    /// <summary>The most recent confirmed swing low, or null if none exists.</summary>
    public SwingPoint? LastSwingLow =>
        _swingPoints.LastOrDefault(sp => sp.Type == SwingType.Low);

    /// <summary>
    /// Called on each new bar. Checks whether the bar at [currentIndex - _strength]
    /// qualifies as a confirmed swing point, given that _strength bars have now formed after it.
    /// Swing points with insufficient size relative to ATR are discarded as noise.
    /// </summary>
    /// <param name="bars">The cTrader Bars data series.</param>
    /// <param name="currentIndex">The index of the latest available bar.</param>
    /// <param name="atr20">Current 20-period ATR for minimum swing size filtering.</param>
    public void Update(Bars bars, int currentIndex, double atr20)
    {
        // The candidate bar is _strength bars back from the current index
        var candidateIndex = currentIndex - _strength;
        if (candidateIndex < _strength)
            return; // Not enough bars before the candidate

        // Check for swing high
        if (IsSwingHigh(bars, candidateIndex))
        {
            // Filter: swing must be significant relative to ATR
            // Measure: swingHigh.Price - MIN(Low of surrounding bars)
            if (atr20 > 0 && !HasMinimumSwingSize(bars, candidateIndex, SwingType.High, atr20))
            {
                // Swing is too small — just noise, discard
            }
            else
            {
                var sp = new SwingPoint
                {
                    BarIndex = candidateIndex,
                    Time = bars.OpenTimes[candidateIndex],
                    Price = bars.HighPrices[candidateIndex],
                    Type = SwingType.High,
                    Strength = _strength,
                    IsConfirmed = true
                };

                // Avoid duplicate at same bar index
                if (!_swingPoints.Any(x => x.BarIndex == candidateIndex && x.Type == SwingType.High))
                {
                    _swingPoints.Add(sp);
                    TrimHistory();
                }
            }
        }

        // Check for swing low
        if (IsSwingLow(bars, candidateIndex))
        {
            if (atr20 > 0 && !HasMinimumSwingSize(bars, candidateIndex, SwingType.Low, atr20))
            {
                // Swing is too small — just noise, discard
            }
            else
            {
                var sp = new SwingPoint
                {
                    BarIndex = candidateIndex,
                    Time = bars.OpenTimes[candidateIndex],
                    Price = bars.LowPrices[candidateIndex],
                    Type = SwingType.Low,
                    Strength = _strength,
                    IsConfirmed = true
                };

                if (!_swingPoints.Any(x => x.BarIndex == candidateIndex && x.Type == SwingType.Low))
                {
                    _swingPoints.Add(sp);
                    TrimHistory();
                }
            }
        }
    }

    /// <summary>Returns the most recent N confirmed swing highs (most recent last).</summary>
    public List<SwingPoint> GetRecentHighs(int count) =>
        _swingPoints.Where(sp => sp.Type == SwingType.High)
                    .TakeLast(count)
                    .ToList();

    /// <summary>Returns the most recent N confirmed swing lows (most recent last).</summary>
    public List<SwingPoint> GetRecentLows(int count) =>
        _swingPoints.Where(sp => sp.Type == SwingType.Low)
                    .TakeLast(count)
                    .ToList();

    /// <summary>Returns all swing points in chronological order.</summary>
    public List<SwingPoint> GetAll() => _swingPoints.ToList();

    /// <summary>
    /// Checks if the last N swing highs form an ascending sequence (Higher Highs).
    /// </summary>
    public bool HasHigherHighs(int count = 2)
    {
        var highs = GetRecentHighs(count);
        if (highs.Count < count) return false;

        for (int i = 1; i < highs.Count; i++)
        {
            if (highs[i].Price <= highs[i - 1].Price)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if the last N swing lows form an ascending sequence (Higher Lows).
    /// </summary>
    public bool HasHigherLows(int count = 2)
    {
        var lows = GetRecentLows(count);
        if (lows.Count < count) return false;

        for (int i = 1; i < lows.Count; i++)
        {
            if (lows[i].Price <= lows[i - 1].Price)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if the last N swing highs form a descending sequence (Lower Highs).
    /// </summary>
    public bool HasLowerHighs(int count = 2)
    {
        var highs = GetRecentHighs(count);
        if (highs.Count < count) return false;

        for (int i = 1; i < highs.Count; i++)
        {
            if (highs[i].Price >= highs[i - 1].Price)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Checks if the last N swing lows form a descending sequence (Lower Lows).
    /// </summary>
    public bool HasLowerLows(int count = 2)
    {
        var lows = GetRecentLows(count);
        if (lows.Count < count) return false;

        for (int i = 1; i < lows.Count; i++)
        {
            if (lows[i].Price >= lows[i - 1].Price)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Detects shrinking stairs: each successive swing push is smaller than the previous one,
    /// indicating decaying momentum.
    /// </summary>
    public bool HasShrinkingStairs(int count = 3)
    {
        return HasShrinkingStairsForType(SwingType.High, count)
            || HasShrinkingStairsForType(SwingType.Low, count);
    }

    private bool HasShrinkingStairsForType(SwingType type, int count)
    {
        var points = type == SwingType.High ? GetRecentHighs(count + 1) : GetRecentLows(count + 1);
        if (points.Count < count + 1) return false;

        var deltas = new List<double>();
        for (int i = 1; i < points.Count; i++)
        {
            deltas.Add(Math.Abs(points[i].Price - points[i - 1].Price));
        }

        for (int i = 1; i < deltas.Count; i++)
        {
            if (deltas[i] >= deltas[i - 1])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if the swing has minimum size relative to ATR.
    /// For SH: swingHigh - MIN(lows of surrounding bars) must be >= minSwingSizePercent * ATR.
    /// For SL: MAX(highs of surrounding bars) - swingLow must be >= minSwingSizePercent * ATR.
    /// This filters out micro-undulations that are just noise on M5.
    /// </summary>
    private bool HasMinimumSwingSize(Bars bars, int candidateIndex, SwingType type, double atr20)
    {
        int from = candidateIndex - _strength;
        int to = candidateIndex + _strength;

        if (type == SwingType.High)
        {
            double swingPrice = bars.HighPrices[candidateIndex];
            double minLow = double.MaxValue;
            for (int i = from; i <= to; i++)
            {
                if (i != candidateIndex && bars.LowPrices[i] < minLow)
                    minLow = bars.LowPrices[i];
            }
            return (swingPrice - minLow) >= _minSwingSizePercent * atr20;
        }
        else
        {
            double swingPrice = bars.LowPrices[candidateIndex];
            double maxHigh = double.MinValue;
            for (int i = from; i <= to; i++)
            {
                if (i != candidateIndex && bars.HighPrices[i] > maxHigh)
                    maxHigh = bars.HighPrices[i];
            }
            return (maxHigh - swingPrice) >= _minSwingSizePercent * atr20;
        }
    }

    private bool IsSwingHigh(Bars bars, int candidateIndex)
    {
        var candidateHigh = bars.HighPrices[candidateIndex];

        for (int i = candidateIndex - _strength; i < candidateIndex; i++)
        {
            if (bars.HighPrices[i] > candidateHigh)
                return false;
            if (bars.HighPrices[i] == candidateHigh)
                return false;
        }

        for (int i = candidateIndex + 1; i <= candidateIndex + _strength; i++)
        {
            if (bars.HighPrices[i] >= candidateHigh)
                return false;
        }

        return true;
    }

    private bool IsSwingLow(Bars bars, int candidateIndex)
    {
        var candidateLow = bars.LowPrices[candidateIndex];

        for (int i = candidateIndex - _strength; i < candidateIndex; i++)
        {
            if (bars.LowPrices[i] < candidateLow)
                return false;
            if (bars.LowPrices[i] == candidateLow)
                return false;
        }

        for (int i = candidateIndex + 1; i <= candidateIndex + _strength; i++)
        {
            if (bars.LowPrices[i] <= candidateLow)
                return false;
        }

        return true;
    }

    private void TrimHistory()
    {
        while (_swingPoints.Count > _maxHistory)
            _swingPoints.RemoveAt(0);
    }
}
