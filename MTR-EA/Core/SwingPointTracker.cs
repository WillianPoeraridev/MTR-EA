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
/// that do not exceed its price level.
/// </summary>
public class SwingPointTracker
{
    private readonly int _strength;
    private readonly int _maxHistory;
    private readonly List<SwingPoint> _swingPoints;

    /// <summary>
    /// Creates a new SwingPointTracker.
    /// </summary>
    /// <param name="strength">Number of bars before and after required for confirmation (default 3).</param>
    /// <param name="maxHistory">Maximum number of swing points to retain (default 20).</param>
    public SwingPointTracker(int strength = 3, int maxHistory = 20)
    {
        _strength = Math.Max(1, strength);
        _maxHistory = Math.Max(4, maxHistory);
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
    /// </summary>
    /// <param name="bars">The cTrader Bars data series.</param>
    /// <param name="currentIndex">The index of the latest available bar.</param>
    public void Update(Bars bars, int currentIndex)
    {
        // The candidate bar is _strength bars back from the current index
        var candidateIndex = currentIndex - _strength;
        if (candidateIndex < _strength)
            return; // Not enough bars before the candidate

        // Check for swing high
        if (IsSwingHigh(bars, candidateIndex))
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

        // Check for swing low
        if (IsSwingLow(bars, candidateIndex))
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
    /// indicating decaying momentum. Checks the specified swing type (highs for bull, lows for bear).
    /// Example: SH1→SH2 = +10 pips, SH2→SH3 = +7 pips, SH3→SH4 = +4 pips.
    /// </summary>
    /// <param name="count">Minimum number of swing points to analyze (default 3).</param>
    /// <returns>True if the distance between consecutive swings is decreasing.</returns>
    public bool HasShrinkingStairs(int count = 3)
    {
        // Check both highs and lows — return true if either shows shrinking
        return HasShrinkingStairsForType(SwingType.High, count)
            || HasShrinkingStairsForType(SwingType.Low, count);
    }

    private bool HasShrinkingStairsForType(SwingType type, int count)
    {
        var points = type == SwingType.High ? GetRecentHighs(count + 1) : GetRecentLows(count + 1);
        if (points.Count < count + 1) return false;

        // Take the last (count + 1) points to get 'count' deltas
        var deltas = new List<double>();
        for (int i = 1; i < points.Count; i++)
        {
            deltas.Add(Math.Abs(points[i].Price - points[i - 1].Price));
        }

        // Each delta must be smaller than the previous
        for (int i = 1; i < deltas.Count; i++)
        {
            if (deltas[i] >= deltas[i - 1])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if the bar at the given index is a swing high:
    /// its High is greater than the High of the _strength bars before AND after it.
    /// Ties: if two bars share the same high, the first one wins.
    /// </summary>
    private bool IsSwingHigh(Bars bars, int candidateIndex)
    {
        var candidateHigh = bars.HighPrices[candidateIndex];

        // Check bars before
        for (int i = candidateIndex - _strength; i < candidateIndex; i++)
        {
            if (bars.HighPrices[i] > candidateHigh)
                return false;
            // Tie: earlier bar already claimed it
            if (bars.HighPrices[i] == candidateHigh)
                return false;
        }

        // Check bars after
        for (int i = candidateIndex + 1; i <= candidateIndex + _strength; i++)
        {
            if (bars.HighPrices[i] >= candidateHigh)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if the bar at the given index is a swing low:
    /// its Low is less than the Low of the _strength bars before AND after it.
    /// Ties: if two bars share the same low, the first one wins.
    /// </summary>
    private bool IsSwingLow(Bars bars, int candidateIndex)
    {
        var candidateLow = bars.LowPrices[candidateIndex];

        // Check bars before
        for (int i = candidateIndex - _strength; i < candidateIndex; i++)
        {
            if (bars.LowPrices[i] < candidateLow)
                return false;
            // Tie: earlier bar already claimed it
            if (bars.LowPrices[i] == candidateLow)
                return false;
        }

        // Check bars after
        for (int i = candidateIndex + 1; i <= candidateIndex + _strength; i++)
        {
            if (bars.LowPrices[i] <= candidateLow)
                return false;
        }

        return true;
    }

    /// <summary>Trims history to maxHistory entries, removing the oldest.</summary>
    private void TrimHistory()
    {
        while (_swingPoints.Count > _maxHistory)
            _swingPoints.RemoveAt(0);
    }
}
