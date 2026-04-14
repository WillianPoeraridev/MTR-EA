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
/// Multi-trendline system that replaces the old TrendLineTracker.
/// Generates all valid candidates, scores them, maintains Top N per direction,
/// and detects TLBs on the highest-quality lines.
/// </summary>
public class TrendLineManager
{
    private readonly SwingPointTracker _swingTracker;
    private readonly TrendLineCandidateGenerator _generator;
    private readonly TrendLineScorer _scorer;
    private readonly Logger _logger;

    private readonly int _topN;
    private readonly double _tlbMinBreakAtr;
    private readonly int _recalcIntervalBars;

    private int _barsSinceLastRecalc;
    private int _lastSwingCount;

    /// <summary>Top N bull trendlines (support), ordered by score descending.</summary>
    public List<TrendLineCandidate> ActiveBullLines { get; private set; } = new();

    /// <summary>Top N bear trendlines (resistance), ordered by score descending.</summary>
    public List<TrendLineCandidate> ActiveBearLines { get; private set; } = new();

    /// <summary>Best bull trendline (highest score), or null if none.</summary>
    public TrendLineCandidate? BestBullLine => ActiveBullLines.Count > 0 ? ActiveBullLines[0] : null;

    /// <summary>Best bear trendline (highest score), or null if none.</summary>
    public TrendLineCandidate? BestBearLine => ActiveBearLines.Count > 0 ? ActiveBearLines[0] : null;

    /// <summary>EVENT flag: bull TL broken this bar (close below support → bearish).</summary>
    public bool HasBullTlb { get; private set; }

    /// <summary>EVENT flag: bear TL broken this bar (close above resistance → bullish).</summary>
    public bool HasBearTlb { get; private set; }

    /// <summary>Data about the most recent TLB event.</summary>
    public TlbEvent? LastTlb { get; private set; }

    /// <summary>Total TLBs detected.</summary>
    public int TlbCount { get; private set; }

    /// <summary>
    /// Creates a new TrendLineManager.
    /// </summary>
    /// <param name="swingTracker">Swing point source.</param>
    /// <param name="logger">Logger for TLB events.</param>
    /// <param name="topN">Number of lines to keep per direction (default 3).</param>
    /// <param name="tlbMinBreakAtr">Minimum break distance as ATR fraction (default 0.5).</param>
    /// <param name="recalcIntervalBars">Bars between full recalculations (default 6 = 30min on M5).</param>
    public TrendLineManager(
        SwingPointTracker swingTracker,
        Logger logger,
        int topN = 3,
        double tlbMinBreakAtr = 0.5,
        int recalcIntervalBars = 6)
    {
        _swingTracker = swingTracker;
        _logger = logger;
        _topN = topN;
        _tlbMinBreakAtr = tlbMinBreakAtr;
        _recalcIntervalBars = recalcIntervalBars;
        _generator = new TrendLineCandidateGenerator();
        _scorer = new TrendLineScorer();
    }

    /// <summary>
    /// Main update — recalculates candidates periodically, detects TLBs every bar.
    /// </summary>
    public void Update(Bars bars, int currentBarIndex, double ema20, double atr20)
    {
        // 1. Reset event flags from previous bar
        HasBullTlb = false;
        HasBearTlb = false;

        // 2. Check if recalculation is needed
        _barsSinceLastRecalc++;
        int currentSwingCount = _swingTracker.GetAll().Count;
        bool newSwingAppeared = currentSwingCount != _lastSwingCount;
        _lastSwingCount = currentSwingCount;

        if (_barsSinceLastRecalc >= _recalcIntervalBars || newSwingAppeared)
        {
            RecalculateCandidates(bars, currentBarIndex, atr20);
            _barsSinceLastRecalc = 0;
        }

        // 3. Update recency of active lines
        UpdateActiveLines(currentBarIndex);

        // 4. Detect TLBs on active lines
        DetectTlbs(bars, currentBarIndex, atr20);

        // 5. Remove broken lines
        ActiveBullLines.RemoveAll(l => l.IsBroken);
        ActiveBearLines.RemoveAll(l => l.IsBroken);
    }

    /// <summary>
    /// Generates all candidates, scores them, and keeps the top N per direction.
    /// </summary>
    private void RecalculateCandidates(Bars bars, int currentBarIndex, double atr20)
    {
        var candidates = _generator.Generate(_swingTracker, bars, currentBarIndex, atr20);

        foreach (var c in candidates)
            _scorer.Score(c, _swingTracker, bars, currentBarIndex, atr20);

        // Filter inactive, split by direction, sort by score, take top N
        ActiveBullLines = candidates
            .Where(c => c.Direction == TrendLineDirection.Bull && c.IsActive)
            .OrderByDescending(c => c.Score)
            .Take(_topN)
            .ToList();

        ActiveBearLines = candidates
            .Where(c => c.Direction == TrendLineDirection.Bear && c.IsActive)
            .OrderByDescending(c => c.Score)
            .Take(_topN)
            .ToList();
    }

    /// <summary>
    /// Updates BarsSinceLastTouch and expires old lines.
    /// </summary>
    private void UpdateActiveLines(int currentBarIndex)
    {
        foreach (var line in ActiveBullLines.Concat(ActiveBearLines))
        {
            // BarsSinceLastTouch is already computed by the scorer,
            // but increment it by 1 each bar between recalcs
            line.BarsSinceLastTouch++;
        }

        ActiveBullLines.RemoveAll(l => !l.IsActive);
        ActiveBearLines.RemoveAll(l => !l.IsActive);
    }

    /// <summary>
    /// Detects TLBs on all active lines. If multiple break, uses the highest-scored one.
    /// </summary>
    private void DetectTlbs(Bars bars, int currentBarIndex, double atr20)
    {
        if (atr20 <= 0) return;
        double close = bars.ClosePrices[currentBarIndex];
        double minBreakDist = _tlbMinBreakAtr * atr20;

        // Check bull lines (support) for breaks
        TrendLineCandidate? bestBullBreak = null;
        double bestBullBreakDist = 0;

        foreach (var line in ActiveBullLines)
        {
            double linePrice = line.GetPriceAt(currentBarIndex);
            double breakDist = linePrice - close; // Positive = close below support
            if (breakDist > minBreakDist && breakDist > bestBullBreakDist)
            {
                bestBullBreak = line;
                bestBullBreakDist = breakDist;
            }
        }

        if (bestBullBreak != null)
        {
            double strength = (bestBullBreak.Score / 100.0)
                * Math.Min(bestBullBreakDist / (atr20 * 1.5), 1.0);
            bool isMajor = bestBullBreak.Score >= 50 && bestBullBreak.DurationBars >= 12;

            bestBullBreak.IsBroken = true;
            bestBullBreak.BrokenAtBarIndex = currentBarIndex;
            bestBullBreak.BreakDistance = bestBullBreakDist;

            HasBullTlb = true;
            TlbCount++;
            LastTlb = new TlbEvent
            {
                BarIndex = currentBarIndex,
                BreakPrice = close,
                BrokenLine = bestBullBreak,
                LineScore = bestBullBreak.Score,
                BreakDistance = bestBullBreakDist,
                Strength = Math.Clamp(strength, 0.0, 1.0),
                IsMajorBreak = isMajor
            };

            _logger.Trade($"TLB BULL: Line score={bestBullBreak.Score:F0}, break dist={bestBullBreakDist:F5}, strength={strength:F2}, major={isMajor}");
        }

        // Check bear lines (resistance) for breaks
        TrendLineCandidate? bestBearBreak = null;
        double bestBearBreakDist = 0;

        foreach (var line in ActiveBearLines)
        {
            double linePrice = line.GetPriceAt(currentBarIndex);
            double breakDist = close - linePrice; // Positive = close above resistance
            if (breakDist > minBreakDist && breakDist > bestBearBreakDist)
            {
                bestBearBreak = line;
                bestBearBreakDist = breakDist;
            }
        }

        if (bestBearBreak != null)
        {
            double strength = (bestBearBreak.Score / 100.0)
                * Math.Min(bestBearBreakDist / (atr20 * 1.5), 1.0);
            bool isMajor = bestBearBreak.Score >= 50 && bestBearBreak.DurationBars >= 12;

            bestBearBreak.IsBroken = true;
            bestBearBreak.BrokenAtBarIndex = currentBarIndex;
            bestBearBreak.BreakDistance = bestBearBreakDist;

            HasBearTlb = true;
            TlbCount++;
            LastTlb = new TlbEvent
            {
                BarIndex = currentBarIndex,
                BreakPrice = close,
                BrokenLine = bestBearBreak,
                LineScore = bestBearBreak.Score,
                BreakDistance = bestBearBreakDist,
                Strength = Math.Clamp(strength, 0.0, 1.0),
                IsMajorBreak = isMajor
            };

            _logger.Trade($"TLB BEAR: Line score={bestBearBreak.Score:F0}, break dist={bestBearBreakDist:F5}, strength={strength:F2}, major={isMajor}");

            // Log if multiple lines broke simultaneously
            if (bestBullBreak != null)
                _logger.Trade("MULTI-TLB: Both bull and bear lines broke on same bar — very strong signal!");
        }
    }

    /// <summary>
    /// TLB event data combining line quality with break strength.
    /// </summary>
    public class TlbEvent
    {
        /// <summary>Bar index where the break occurred.</summary>
        public int BarIndex { get; set; }

        /// <summary>Close price of the breaking bar.</summary>
        public double BreakPrice { get; set; }

        /// <summary>The line that was broken.</summary>
        public TrendLineCandidate BrokenLine { get; set; } = null!;

        /// <summary>Score of the broken line at time of break.</summary>
        public double LineScore { get; set; }

        /// <summary>Distance of close beyond the line.</summary>
        public double BreakDistance { get; set; }

        /// <summary>
        /// Combined strength: (LineScore/100) * min(BreakDist/(1.5*ATR), 1.0).
        /// High-score line + strong break = high strength.
        /// </summary>
        public double Strength { get; set; }

        /// <summary>True if LineScore ≥ 50 AND DurationBars ≥ 12.</summary>
        public bool IsMajorBreak { get; set; }
    }
}
