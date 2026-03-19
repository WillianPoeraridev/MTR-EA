#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.Robots.Models;
using cAlgo.Robots.Utils;

namespace cAlgo.Robots.Core;

/// <summary>
/// Evaluates signal bar quality and counts "reasons to enter" per Al Brooks' methodology.
/// A valid MTR entry requires a quality signal bar (score ≥ threshold) with ≥ 2 reasons.
/// </summary>
public class SignalBarAnalyzer
{
    /// <summary>
    /// Analyzes a candidate signal bar for an MTR entry.
    /// </summary>
    /// <param name="signalBar">The bar being evaluated as a signal bar.</param>
    /// <param name="previousBar">The bar immediately before the signal bar.</param>
    /// <param name="recentBars">Last ~10 bars for context analysis (most recent last).</param>
    /// <param name="atr20">20-period ATR value.</param>
    /// <param name="ema20">20-period EMA value.</param>
    /// <param name="pipSize">Symbol.PipSize from cTrader (e.g., 0.0001 for GBP/USD).</param>
    /// <param name="minScore">Minimum score threshold for IsValid (EA parameter, default 60).</param>
    /// <param name="direction">Whether we're looking for a BullReversal or BearReversal.</param>
    /// <returns>Complete analysis result with score, reasons, and entry/stop levels.</returns>
    public SignalBarResult Analyze(
        BarInfo signalBar,
        BarInfo previousBar,
        List<BarInfo> recentBars,
        double atr20,
        double ema20,
        double pipSize,
        int minScore,
        MtrDirection direction)
    {
        bool isBull = direction == MtrDirection.BullReversal;

        // Detect Barb Wire first — it invalidates everything
        bool isBarbWire = DetectBarbWire(recentBars, ema20);

        // Detect second entry (H2/L2)
        bool isSecondEntry = DetectSecondEntry(recentBars, isBull);

        // Calculate score
        double score = CalculateScore(signalBar, previousBar, recentBars, atr20, isBull, isSecondEntry, isBarbWire);

        // Count reasons
        var reasons = CountReasons(signalBar, recentBars, atr20, ema20, score, isSecondEntry, isBull);

        // Calculate entry and stop prices
        var (entryPrice, stopPrice) = CalculateEntryStop(signalBar, atr20, pipSize, isBull);

        return new SignalBarResult
        {
            Score = score,
            IsValid = score >= minScore && !isBarbWire,
            ReasonCount = reasons.Count,
            Reasons = reasons,
            IsSecondEntry = isSecondEntry,
            IsBarbWire = isBarbWire,
            EntryPrice = entryPrice,
            StopPrice = stopPrice
        };
    }

    /// <summary>
    /// Scores the signal bar from 0–100 using Brooks' quality criteria.
    /// Bull and bear reversals are scored as mirrors of each other.
    /// </summary>
    private double CalculateScore(BarInfo bar, BarInfo prev, List<BarInfo> recentBars,
        double atr20, bool isBull, bool isSecondEntry, bool isBarbWire)
    {
        double score = 0;

        if (isBull)
        {
            // === BULL REVERSAL SCORING ===
            // +15: Close > Open (bullish bar)
            if (bar.IsBull) score += 15;

            // +10: Close in upper third of range
            if (bar.ClosePosition > 0.66) score += 10;

            // +10: Body ≥ 40% of range
            if (bar.BodyPercent >= 0.40) score += 10;

            // +10: Upper tail ≤ 30% — bulls controlled the close (not sold off at top)
            if (bar.UpperTailPercent <= 0.30) score += 10;

            // +10: Range ≥ 50% ATR (not too small to matter)
            if (atr20 > 0 && bar.Range >= 0.50 * atr20) score += 10;

            // +10: Range ≤ 200% ATR (not a climax exhaustion bar)
            if (atr20 > 0 && bar.Range <= 2.00 * atr20) score += 10;

            // +10: Close above midpoint of previous bar
            if (bar.Close > prev.MidPoint) score += 10;

            // +10: Not a doji (has real body)
            if (bar.BodyPercent >= 0.30) score += 10;

            // +5: Low overlap with prior bars (trending, not range-bound)
            if (recentBars.Count >= 2)
            {
                var priorBar = recentBars[recentBars.Count - 2];
                if (bar.OverlapPercent(priorBar) < 0.50) score += 5;
            }

            // +10: Second entry (H2) — more reliable per Brooks
            if (isSecondEntry) score += 10;

            // === PENALTIES ===
            // -20: Pure doji (no conviction)
            if (bar.BodyPercent < 0.10) score -= 20;

            // -15: Large upper tail on bull reversal = sellers pushed price back down
            if (bar.UpperTailPercent > 0.40) score -= 15;

            // -15: High overlap with prior bars (stuck in trading range)
            if (recentBars.Count >= 2)
            {
                var priorBar = recentBars[recentBars.Count - 2];
                if (bar.OverlapPercent(priorBar) > 0.70) score -= 15;
            }
        }
        else
        {
            // === BEAR REVERSAL SCORING (mirror of bull) ===
            if (bar.IsBear) score += 15;
            if (bar.ClosePosition < 0.34) score += 10;
            if (bar.BodyPercent >= 0.40) score += 10;
            // Lower tail ≤ 30% — bears controlled the close
            if (bar.LowerTailPercent <= 0.30) score += 10;
            if (atr20 > 0 && bar.Range >= 0.50 * atr20) score += 10;
            if (atr20 > 0 && bar.Range <= 2.00 * atr20) score += 10;
            if (bar.Close < prev.MidPoint) score += 10;
            if (bar.BodyPercent >= 0.30) score += 10;
            if (recentBars.Count >= 2)
            {
                var priorBar = recentBars[recentBars.Count - 2];
                if (bar.OverlapPercent(priorBar) < 0.50) score += 5;
            }
            if (isSecondEntry) score += 10;

            // Penalties (mirror)
            if (bar.BodyPercent < 0.10) score -= 20;
            // Large lower tail on bear reversal = buyers pushed price back up
            if (bar.LowerTailPercent > 0.40) score -= 15;
            if (recentBars.Count >= 2)
            {
                var priorBar = recentBars[recentBars.Count - 2];
                if (bar.OverlapPercent(priorBar) > 0.70) score -= 15;
            }
        }

        // -30: Barb Wire completely invalidates signal bars
        if (isBarbWire) score -= 30;

        return Math.Clamp(score, 0, 100);
    }

    /// <summary>
    /// Counts and lists reasons to enter the trade. Brooks requires minimum 2 reasons.
    /// </summary>
    private List<string> CountReasons(BarInfo signalBar, List<BarInfo> recentBars,
        double atr20, double ema20, double score, bool isSecondEntry, bool isBull)
    {
        var reasons = new List<string>();

        // 1. Strong reversal bar — score ≥ 70
        if (score >= 70)
            reasons.Add("Strong reversal bar");

        // 2. Second entry (H2/L2) — more reliable per Brooks
        if (isSecondEntry)
            reasons.Add("Second entry (H2/L2)");

        // 3. EMA test — price touched/crossed EMA20 recently
        bool emaTested = recentBars.TakeLast(5).Any(b =>
            (b.Low <= ema20 && b.High >= ema20));
        if (emaTested)
            reasons.Add("EMA test");

        // 4. Breakout pullback — price retested a breakout zone
        // Detect by checking if price reversed from near a recent swing extreme
        if (recentBars.Count >= 5)
        {
            var mid = recentBars[recentBars.Count - 3];
            bool pullbackToBreakout = isBull
                ? signalBar.Low <= mid.High && signalBar.Close > mid.High
                : signalBar.High >= mid.Low && signalBar.Close < mid.Low;
            if (pullbackToBreakout)
                reasons.Add("Breakout pullback");
        }

        // 5. Failed breakout — recent new extreme that immediately reversed
        if (recentBars.Count >= 3)
        {
            var recent3 = recentBars.TakeLast(3).ToList();
            if (isBull)
            {
                // New low was made but price reversed up
                bool failedBreakdown = recent3[0].Low < recent3[1].Low && recent3[2].Close > recent3[0].High;
                if (failedBreakdown) reasons.Add("Failed breakout");
            }
            else
            {
                bool failedBreakout = recent3[0].High > recent3[1].High && recent3[2].Close < recent3[0].Low;
                if (failedBreakout) reasons.Add("Failed breakout");
            }
        }

        // 6. Double bottom/top pattern — 2 swing points at similar price level
        if (recentBars.Count >= 6)
        {
            var lows = recentBars.TakeLast(10).Select(b => b.Low).ToList();
            var highs = recentBars.TakeLast(10).Select(b => b.High).ToList();
            if (isBull && lows.Count >= 2)
            {
                double min1 = lows.Min();
                var withoutMin = lows.Where(l => l != min1).ToList();
                if (withoutMin.Count > 0)
                {
                    double min2 = withoutMin.Min();
                    if (atr20 > 0 && Math.Abs(min1 - min2) < atr20 * 0.3)
                        reasons.Add("Double bottom/top pattern");
                }
            }
            else if (!isBull && highs.Count >= 2)
            {
                double max1 = highs.Max();
                var withoutMax = highs.Where(h => h != max1).ToList();
                if (withoutMax.Count > 0)
                {
                    double max2 = withoutMax.Max();
                    if (atr20 > 0 && Math.Abs(max1 - max2) < atr20 * 0.3)
                        reasons.Add("Double bottom/top pattern");
                }
            }
        }

        // 7. Wedge/Three pushes — 3 pushes with decreasing momentum
        // Simplified: check if there are 3 progressively smaller moves in the trend direction
        if (recentBars.Count >= 8)
        {
            var bars = recentBars.TakeLast(8).ToList();
            var extremes = new List<double>();
            for (int i = 1; i < bars.Count - 1; i++)
            {
                if (isBull)
                {
                    // Looking for 3 lower lows with decreasing distance
                    if (bars[i].Low < bars[i - 1].Low && bars[i].Low < bars[i + 1].Low)
                        extremes.Add(bars[i].Low);
                }
                else
                {
                    if (bars[i].High > bars[i - 1].High && bars[i].High > bars[i + 1].High)
                        extremes.Add(bars[i].High);
                }
            }
            if (extremes.Count >= 3)
            {
                var deltas = new List<double>();
                for (int i = 1; i < extremes.Count; i++)
                    deltas.Add(Math.Abs(extremes[i] - extremes[i - 1]));
                bool shrinking = deltas.Count >= 2 && deltas.Zip(deltas.Skip(1), (a, b) => b < a).All(x => x);
                if (shrinking) reasons.Add("Wedge/Three pushes");
            }
        }

        // 8. TCL overshoot reversal — price overshot trend channel line and reversed
        // Simplified: price went beyond 2 standard deviations and reversed back
        if (recentBars.Count >= 5 && atr20 > 0)
        {
            var prev = recentBars[recentBars.Count - 2];
            bool overshoot = isBull
                ? prev.Low < ema20 - 2.0 * atr20 && signalBar.Close > prev.Low
                : prev.High > ema20 + 2.0 * atr20 && signalBar.Close < prev.High;
            if (overshoot)
                reasons.Add("TCL overshoot reversal");
        }

        return reasons;
    }

    /// <summary>
    /// Detects H2/L2 (second entry) in recent bars.
    /// H2: two attempts to break above prior bar's high in a bull pullback (more reliable).
    /// L2: two attempts to break below prior bar's low in a bear pullback (more reliable).
    /// </summary>
    private bool DetectSecondEntry(List<BarInfo> recentBars, bool isBull)
    {
        if (recentBars.Count < 6) return false;

        var bars = recentBars.TakeLast(6).ToList();
        int entryCount = 0;

        for (int i = 1; i < bars.Count; i++)
        {
            if (isBull)
            {
                // H1/H2: bar's high exceeds previous bar's high during a pullback
                if (bars[i].High > bars[i - 1].High && bars[i].IsBull)
                    entryCount++;
            }
            else
            {
                // L1/L2: bar's low goes below previous bar's low during a pullback
                if (bars[i].Low < bars[i - 1].Low && bars[i].IsBear)
                    entryCount++;
            }
        }

        return entryCount >= 2;
    }

    /// <summary>
    /// Detects Barb Wire: 3+ overlapping bars with at least 1 doji, near the EMA.
    /// Barb Wire is a no-trade zone — signal bars here are automatically invalid.
    /// </summary>
    private bool DetectBarbWire(List<BarInfo> recentBars, double ema20)
    {
        if (!BarHelpers.IsBarbWire(recentBars))
            return false;

        // Additional check: barb wire should be near the EMA
        if (recentBars.Count >= 3)
        {
            var last3 = recentBars.TakeLast(3).ToList();
            bool nearEma = last3.Any(b => b.Low <= ema20 && b.High >= ema20);
            return nearEma;
        }

        return false;
    }

    /// <summary>
    /// Calculates entry and stop prices using Symbol.PipSize (not hardcoded).
    /// If stop distance exceeds 2x ATR, applies money stop at 60% of range.
    /// </summary>
    private (double entry, double stop) CalculateEntryStop(BarInfo signalBar, double atr20, double pipSize, bool isBull)
    {
        double entry, stop;

        if (isBull)
        {
            // Bull: enter 1 pip above signal bar high, stop 1 pip below signal bar low
            entry = signalBar.High + pipSize;
            stop = signalBar.Low - pipSize;
        }
        else
        {
            // Bear: enter 1 pip below signal bar low, stop 1 pip above signal bar high
            entry = signalBar.Low - pipSize;
            stop = signalBar.High + pipSize;
        }

        // Money stop: if stop distance > 2x ATR, use 60% of signal bar range
        double stopDistance = Math.Abs(entry - stop);
        if (atr20 > 0 && stopDistance > 2.0 * atr20)
        {
            double moneyStop = signalBar.Range * 0.60;
            if (isBull)
                stop = entry - moneyStop;
            else
                stop = entry + moneyStop;
        }

        return (entry, stop);
    }

    /// <summary>
    /// Result of signal bar analysis including quality score, reasons, and entry/stop levels.
    /// </summary>
    public class SignalBarResult
    {
        /// <summary>Quality score from 0 to 100.</summary>
        public double Score { get; set; }

        /// <summary>True if score ≥ minScore parameter AND no Barb Wire.</summary>
        public bool IsValid { get; set; }

        /// <summary>Number of reasons to enter (Brooks requires ≥ 2).</summary>
        public int ReasonCount { get; set; }

        /// <summary>Descriptive list of detected reasons.</summary>
        public List<string> Reasons { get; set; } = new();

        /// <summary>True if this is a second entry (H2/L2) — more reliable.</summary>
        public bool IsSecondEntry { get; set; }

        /// <summary>True if Barb Wire detected — no-trade zone.</summary>
        public bool IsBarbWire { get; set; }

        /// <summary>Calculated entry price (1 pip beyond signal bar).</summary>
        public double EntryPrice { get; set; }

        /// <summary>Calculated stop price (1 pip beyond opposite side of signal bar).</summary>
        public double StopPrice { get; set; }
    }
}
