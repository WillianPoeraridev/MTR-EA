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
/// Draws and maintains dynamic trendlines from swing points.
/// Detects Trend Line Breaks (TLBs) — the critical first step in an MTR setup.
/// Only Major TL breaks (≥12 bars between anchor points) qualify for MTR.
/// Requires minimum ATR-based margin for TLB confirmation (hysteresis).
/// </summary>
public class TrendLineTracker
{
    private readonly SwingPointTracker _swingTracker;
    private readonly Logger _logger;

    private const int MajorThresholdBars = 12;   // 1 hour on M5 (was 20)
    private const int MinPointSeparation = 8;     // 40 min on M5
    private const double TlbMarginPercent = 0.5;  // Close must be 0.5*ATR beyond TL

    // Track previous swing point counts to detect when new swings are confirmed
    private int _lastSwingHighCount;
    private int _lastSwingLowCount;

    /// <summary>Active bull trendline connecting swing lows (support line).</summary>
    public TrendLine? ActiveBullTrendLine { get; private set; }

    /// <summary>Active bear trendline connecting swing highs (resistance line).</summary>
    public TrendLine? ActiveBearTrendLine { get; private set; }

    /// <summary>
    /// EVENT flag: bull TL was broken this bar (close below support → bearish signal).
    /// Resets to false on the next Update() call.
    /// </summary>
    public bool HasBullTlb { get; private set; }

    /// <summary>
    /// EVENT flag: bear TL was broken this bar (close above resistance → bullish signal).
    /// Resets to false on the next Update() call.
    /// </summary>
    public bool HasBearTlb { get; private set; }

    /// <summary>Data about the most recent TLB event.</summary>
    public TlbInfo? LastTlb { get; private set; }

    /// <summary>Total number of TLBs detected (used as a strength factor).</summary>
    public int TlbCount { get; private set; }

    /// <summary>
    /// Creates a new TrendLineTracker.
    /// </summary>
    /// <param name="swingTracker">The swing point tracker to source anchor points from.</param>
    /// <param name="logger">Logger for TLB event diagnostics.</param>
    public TrendLineTracker(SwingPointTracker swingTracker, Logger logger)
    {
        _swingTracker = swingTracker;
        _logger = logger;
    }

    /// <summary>
    /// Updates trendlines and checks for breaks. Called once per bar.
    /// TLB flags are EVENT-based: set on the bar the break occurs, reset on the next call.
    /// </summary>
    public void Update(Bars bars, int currentIndex, double ema20, double atr20)
    {
        // Reset TLB event flags from previous bar (they are single-bar events)
        HasBullTlb = false;
        HasBearTlb = false;

        // Rebuild trendlines when new swing points are confirmed
        RebuildTrendLines();

        // Check for TLB on the closed bar
        CheckForBreaks(bars, currentIndex, ema20, atr20);
    }

    /// <summary>
    /// Rebuilds trendlines from swing points with minimum separation.
    /// Selects the 2 most recent swing points that are at least MinPointSeparation bars apart.
    /// If no two swings meet the separation requirement, the TL is set to null.
    /// </summary>
    private void RebuildTrendLines()
    {
        var highs = _swingTracker.GetRecentHighs(5); // Get more to find well-separated pair
        var lows = _swingTracker.GetRecentLows(5);

        // Detect if new swing points were added
        int currentHighCount = _swingTracker.GetRecentHighs(20).Count;
        int currentLowCount = _swingTracker.GetRecentLows(20).Count;
        bool newHighs = currentHighCount != _lastSwingHighCount;
        bool newLows = currentLowCount != _lastSwingLowCount;
        _lastSwingHighCount = currentHighCount;
        _lastSwingLowCount = currentLowCount;

        // Bull TL: connects 2 swing lows with minimum separation
        if (newLows || ActiveBullTrendLine == null)
        {
            var pair = FindSeparatedPair(lows);
            if (pair != null)
            {
                var (p1, p2) = pair.Value;
                int barsBetween = p2.BarIndex - p1.BarIndex;
                double slope = barsBetween > 0 ? (p2.Price - p1.Price) / barsBetween : 0.0;

                ActiveBullTrendLine = new TrendLine
                {
                    Point1 = p1,
                    Point2 = p2,
                    Slope = slope,
                    IsMajor = barsBetween >= MajorThresholdBars
                };
            }
            else
            {
                ActiveBullTrendLine = null; // Not enough well-separated swings
            }
        }

        // Bear TL: connects 2 swing highs with minimum separation
        if (newHighs || ActiveBearTrendLine == null)
        {
            var pair = FindSeparatedPair(highs);
            if (pair != null)
            {
                var (p1, p2) = pair.Value;
                int barsBetween = p2.BarIndex - p1.BarIndex;
                double slope = barsBetween > 0 ? (p2.Price - p1.Price) / barsBetween : 0.0;

                ActiveBearTrendLine = new TrendLine
                {
                    Point1 = p1,
                    Point2 = p2,
                    Slope = slope,
                    IsMajor = barsBetween >= MajorThresholdBars
                };
            }
            else
            {
                ActiveBearTrendLine = null;
            }
        }
    }

    /// <summary>
    /// Finds the 2 most recent swing points separated by at least MinPointSeparation bars.
    /// Returns null if no valid pair exists.
    /// </summary>
    private (SwingPoint p1, SwingPoint p2)? FindSeparatedPair(List<SwingPoint> points)
    {
        if (points.Count < 2) return null;

        // Try pairs from most recent backwards
        for (int j = points.Count - 1; j >= 1; j--)
        {
            for (int i = j - 1; i >= 0; i--)
            {
                if (points[j].BarIndex - points[i].BarIndex >= MinPointSeparation)
                    return (points[i], points[j]);
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if the closed bar breaks any active trendline by CLOSE with ATR margin.
    /// Bull TL break: trendlinePrice - close > 0.5 * ATR (close significantly BELOW support).
    /// Bear TL break: close - trendlinePrice > 0.5 * ATR (close significantly ABOVE resistance).
    /// </summary>
    private void CheckForBreaks(Bars bars, int currentIndex, double ema20, double atr20)
    {
        double close = bars.ClosePrices[currentIndex];
        double margin = TlbMarginPercent * atr20;

        // Bull TL break: close BELOW the bull trendline with margin → bearish signal
        if (ActiveBullTrendLine != null && atr20 > 0)
        {
            double tlPrice = ActiveBullTrendLine.GetPriceAt(currentIndex);
            double breakDistance = tlPrice - close; // Positive = close is below TL
            if (breakDistance > margin)
            {
                int barsBetween = ActiveBullTrendLine.Point2.BarIndex - ActiveBullTrendLine.Point1.BarIndex;
                HasBullTlb = true;
                TlbCount++;
                LastTlb = new TlbInfo
                {
                    BarIndex = currentIndex,
                    BreakPrice = close,
                    BrokenLine = ActiveBullTrendLine,
                    IsMajorBreak = ActiveBullTrendLine.IsMajor,
                    Strength = CalculateTlbStrength(bars, currentIndex, close, ActiveBullTrendLine, ema20, atr20, isBullBreak: false)
                };
                _logger.Debug($"TLB detected: {(LastTlb.IsMajorBreak ? "MAJOR" : "MICRO")} Bull TL broken | Strength={LastTlb.Strength:F2} | Bars between points={barsBetween}");
            }
        }

        // Bear TL break: close ABOVE the bear trendline with margin → bullish signal
        if (ActiveBearTrendLine != null && atr20 > 0)
        {
            double tlPrice = ActiveBearTrendLine.GetPriceAt(currentIndex);
            double breakDistance = close - tlPrice; // Positive = close is above TL
            if (breakDistance > margin)
            {
                int barsBetween = ActiveBearTrendLine.Point2.BarIndex - ActiveBearTrendLine.Point1.BarIndex;
                HasBearTlb = true;
                TlbCount++;
                LastTlb = new TlbInfo
                {
                    BarIndex = currentIndex,
                    BreakPrice = close,
                    BrokenLine = ActiveBearTrendLine,
                    IsMajorBreak = ActiveBearTrendLine.IsMajor,
                    Strength = CalculateTlbStrength(bars, currentIndex, close, ActiveBearTrendLine, ema20, atr20, isBullBreak: true)
                };
                _logger.Debug($"TLB detected: {(LastTlb.IsMajorBreak ? "MAJOR" : "MICRO")} Bear TL broken | Strength={LastTlb.Strength:F2} | Bars between points={barsBetween}");
            }
        }
    }

    /// <summary>
    /// Calculates TLB strength (0.0–1.0) based on Brooks' criteria.
    /// </summary>
    private double CalculateTlbStrength(Bars bars, int currentIndex, double breakPrice,
        TrendLine brokenLine, double ema20, double atr20, bool isBullBreak)
    {
        if (atr20 <= 0) return 0.0;
        double strength = 0.0;

        double tlPrice = brokenLine.GetPriceAt(currentIndex);
        double moveDistance = Math.Abs(breakPrice - tlPrice);

        if (moveDistance > 2.0 * atr20) strength += 0.15;

        bool beyondEma = isBullBreak ? breakPrice > ema20 : breakPrice < ema20;
        if (beyondEma) strength += 0.15;

        if (isBullBreak)
        {
            var lastHigh = _swingTracker.LastSwingHigh;
            if (lastHigh != null && breakPrice > lastHigh.Price) strength += 0.15;
        }
        else
        {
            var lastLow = _swingTracker.LastSwingLow;
            if (lastLow != null && breakPrice < lastLow.Price) strength += 0.15;
        }

        int barsSinceTl = currentIndex - brokenLine.Point2.BarIndex;
        if (barsSinceTl > 10) strength += 0.10;

        if (TlbCount > 1) strength += 0.10;

        int lookback = Math.Min(10, currentIndex);
        int trendBarCount = 0;
        for (int i = currentIndex - lookback + 1; i <= currentIndex; i++)
        {
            double barClose = bars.ClosePrices[i];
            double barOpen = bars.OpenPrices[i];
            bool isTrendBar = isBullBreak ? (barClose > barOpen) : (barClose < barOpen);
            if (isTrendBar) trendBarCount++;
        }
        if (lookback > 0 && (double)trendBarCount / lookback > 0.60)
            strength += 0.20;

        return Math.Clamp(strength, 0.0, 1.0);
    }

    /// <summary>
    /// Represents a trendline connecting two swing points.
    /// </summary>
    public class TrendLine
    {
        /// <summary>First anchor swing point (older).</summary>
        public SwingPoint Point1 { get; set; } = null!;

        /// <summary>Second anchor swing point (newer).</summary>
        public SwingPoint Point2 { get; set; } = null!;

        /// <summary>Price change per bar (slope).</summary>
        public double Slope { get; set; }

        /// <summary>
        /// True if this is a Major trendline (≥12 bars between anchor points on M5 = 1 hour).
        /// Only Major TL breaks qualify for MTR setups.
        /// </summary>
        public bool IsMajor { get; set; }

        /// <summary>Extrapolates the trendline price at the given bar index.</summary>
        public double GetPriceAt(int barIndex)
        {
            int barsDiff = barIndex - Point1.BarIndex;
            return Point1.Price + Slope * barsDiff;
        }
    }

    /// <summary>
    /// Information about a Trend Line Break event.
    /// </summary>
    public class TlbInfo
    {
        /// <summary>Bar index where the break occurred.</summary>
        public int BarIndex { get; set; }

        /// <summary>Close price of the bar that broke the trendline.</summary>
        public double BreakPrice { get; set; }

        /// <summary>The trendline that was broken.</summary>
        public TrendLine BrokenLine { get; set; } = null!;

        /// <summary>Strength of the break (0.0–1.0) based on Brooks' criteria.</summary>
        public double Strength { get; set; }

        /// <summary>True if this is a Major TL break. Only Major breaks qualify for MTR.</summary>
        public bool IsMajorBreak { get; set; }
    }
}
