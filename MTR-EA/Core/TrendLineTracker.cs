#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;
using cAlgo.Robots.Models;

namespace cAlgo.Robots.Core;

/// <summary>
/// Draws and maintains dynamic trendlines from swing points.
/// Detects Trend Line Breaks (TLBs) — the critical first step in an MTR setup.
/// Only Major TL breaks (≥20 bars between anchor points) qualify for MTR.
/// </summary>
public class TrendLineTracker
{
    private readonly SwingPointTracker _swingTracker;

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

    /// <summary>Total number of TLBs detected (used as a strength factor — prior TLBs increase MTR reliability).</summary>
    public int TlbCount { get; private set; }

    /// <summary>
    /// Creates a new TrendLineTracker.
    /// </summary>
    /// <param name="swingTracker">The swing point tracker to source anchor points from.</param>
    public TrendLineTracker(SwingPointTracker swingTracker)
    {
        _swingTracker = swingTracker;
    }

    /// <summary>
    /// Updates trendlines and checks for breaks. Called once per bar.
    /// TLB flags are EVENT-based: set on the bar the break occurs, reset on the next call.
    /// </summary>
    /// <param name="bars">The cTrader Bars data series.</param>
    /// <param name="currentIndex">Index of the closed bar being analyzed.</param>
    /// <param name="ema20">Current EMA 20 value.</param>
    /// <param name="atr20">Current ATR 20 value.</param>
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
    /// Rebuilds trendlines from the most recent swing points when new swings appear.
    /// Bull TL: connects the 2 most recent swing lows.
    /// Bear TL: connects the 2 most recent swing highs.
    /// </summary>
    private void RebuildTrendLines()
    {
        var highs = _swingTracker.GetRecentHighs(2);
        var lows = _swingTracker.GetRecentLows(2);

        // Detect if new swing points were added
        bool newHighs = highs.Count != _lastSwingHighCount;
        bool newLows = lows.Count != _lastSwingLowCount;
        _lastSwingHighCount = highs.Count;
        _lastSwingLowCount = lows.Count;

        // Bull TL: connects 2 swing lows (support line)
        if (lows.Count >= 2 && (newLows || ActiveBullTrendLine == null))
        {
            var p1 = lows[lows.Count - 2];
            var p2 = lows[lows.Count - 1];
            int barsBetween = p2.BarIndex - p1.BarIndex;
            double slope = barsBetween > 0
                ? (p2.Price - p1.Price) / barsBetween
                : 0.0;

            ActiveBullTrendLine = new TrendLine
            {
                Point1 = p1,
                Point2 = p2,
                Slope = slope,
                // Brooks: Major TL has ≥20 bars between anchor points
                IsMajor = barsBetween >= 20
            };
        }

        // Bear TL: connects 2 swing highs (resistance line)
        if (highs.Count >= 2 && (newHighs || ActiveBearTrendLine == null))
        {
            var p1 = highs[highs.Count - 2];
            var p2 = highs[highs.Count - 1];
            int barsBetween = p2.BarIndex - p1.BarIndex;
            double slope = barsBetween > 0
                ? (p2.Price - p1.Price) / barsBetween
                : 0.0;

            ActiveBearTrendLine = new TrendLine
            {
                Point1 = p1,
                Point2 = p2,
                Slope = slope,
                IsMajor = barsBetween >= 20
            };
        }
    }

    /// <summary>
    /// Checks if the closed bar breaks any active trendline by CLOSE (not wick).
    /// A break means the close is beyond the extrapolated trendline price.
    /// </summary>
    private void CheckForBreaks(Bars bars, int currentIndex, double ema20, double atr20)
    {
        double close = bars.ClosePrices[currentIndex];

        // Bull TL break: close BELOW the bull trendline → bearish signal
        if (ActiveBullTrendLine != null)
        {
            double tlPrice = ActiveBullTrendLine.GetPriceAt(currentIndex);
            if (close < tlPrice)
            {
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
            }
        }

        // Bear TL break: close ABOVE the bear trendline → bullish signal
        if (ActiveBearTrendLine != null)
        {
            double tlPrice = ActiveBearTrendLine.GetPriceAt(currentIndex);
            if (close > tlPrice)
            {
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
            }
        }
    }

    /// <summary>
    /// Calculates TLB strength (0.0–1.0) based on Brooks' criteria.
    /// Stronger TLBs make the subsequent MTR setup more reliable.
    /// </summary>
    private double CalculateTlbStrength(Bars bars, int currentIndex, double breakPrice,
        TrendLine brokenLine, double ema20, double atr20, bool isBullBreak)
    {
        if (atr20 <= 0) return 0.0;
        double strength = 0.0;

        double tlPrice = brokenLine.GetPriceAt(currentIndex);
        double moveDistance = Math.Abs(breakPrice - tlPrice);

        // Move covers many pips (> 2x ATR) → +0.15
        if (moveDistance > 2.0 * atr20)
            strength += 0.15;

        // Goes well beyond the EMA → +0.15
        bool beyondEma = isBullBreak
            ? breakPrice > ema20
            : breakPrice < ema20;
        if (beyondEma)
            strength += 0.15;

        // Extends beyond the last opposing swing → +0.15
        if (isBullBreak)
        {
            // Bear TLB (bullish): extends above the last Lower High
            var lastHigh = _swingTracker.LastSwingHigh;
            if (lastHigh != null && breakPrice > lastHigh.Price)
                strength += 0.15;
        }
        else
        {
            // Bull TLB (bearish): extends below the last Higher Low
            var lastLow = _swingTracker.LastSwingLow;
            if (lastLow != null && breakPrice < lastLow.Price)
                strength += 0.15;
        }

        // Lasts many bars (> 10 bars since TL was drawn) → +0.10
        int barsSinceTl = currentIndex - brokenLine.Point2.BarIndex;
        if (barsSinceTl > 10)
            strength += 0.10;

        // Prior TLBs exist (not the first one) → +0.10
        // Brooks: second and third TLBs are more significant
        if (TlbCount > 1)
            strength += 0.10;

        // Momentum: majority of recent bars are trend bars in break direction → +0.20
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
    /// Extrapolates price at any bar index using linear projection.
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
        /// True if this is a Major trendline (≥20 bars between anchor points).
        /// Only Major TL breaks qualify for MTR setups.
        /// Micro TL breaks (IsMajor=false) are With Trend entries per Brooks.
        /// </summary>
        public bool IsMajor { get; set; }

        /// <summary>
        /// Extrapolates the trendline price at the given bar index.
        /// </summary>
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

        /// <summary>
        /// True if this is a Major TL break (≥20 bars between anchor points).
        /// Only Major breaks qualify for MTR. Micro breaks are With Trend entries.
        /// </summary>
        public bool IsMajorBreak { get; set; }
    }
}
