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
/// The heart of the MTR EA — manages detection of Major Trend Reversal setups
/// using a 7-state machine: Scanning → Trending → TlbActive → Testing → Triggered → InTrade → Cooldown.
/// Each state transition is based on Al Brooks' price action criteria.
/// </summary>
public class MtrDetector
{
    private readonly TrendDetector _trendDetector;
    private readonly TrendLineTracker _tlTracker;
    private readonly SignalBarAnalyzer _signalAnalyzer;
    private readonly SwingPointTracker _swingTracker;
    private readonly Logger _logger;

    // Configuration
    private readonly double _minRewardRisk;
    private readonly int _minSignalScore;
    private readonly int _maxBarsWaitingTest;
    private readonly int _maxBarsWaitingTrigger;
    private readonly int _maxBarsWaitingEntry;
    private readonly int _cooldownBars;
    private readonly int _maxFailedAttempts;

    /// <summary>The currently active MTR setup, or null if in Scanning state with no setup.</summary>
    public MtrSetup? ActiveSetup { get; private set; }

    /// <summary>
    /// Creates a new MtrDetector.
    /// </summary>
    /// <param name="trendDetector">Trend classification module.</param>
    /// <param name="tlTracker">Trendline and TLB detection module.</param>
    /// <param name="signalAnalyzer">Signal bar quality analysis module.</param>
    /// <param name="swingTracker">Swing point detection module.</param>
    /// <param name="logger">Logger for state transition events.</param>
    /// <param name="minRewardRisk">Minimum reward:risk ratio for Trader's Equation (default 2.0).</param>
    /// <param name="minSignalScore">Minimum signal bar score to trigger (default 60).</param>
    /// <param name="maxBarsWaitingTest">Max bars waiting for test after TLB (default 20).</param>
    /// <param name="maxBarsWaitingTrigger">Max bars waiting for signal bar in Testing (default 15).</param>
    /// <param name="maxBarsWaitingEntry">Max bars waiting for entry fill in Triggered (default 3).</param>
    /// <param name="cooldownBars">Bars to pause after trade exit (default 5).</param>
    /// <param name="maxFailedAttempts">Max entry attempts before abandoning setup (default 2).</param>
    public MtrDetector(
        TrendDetector trendDetector,
        TrendLineTracker tlTracker,
        SignalBarAnalyzer signalAnalyzer,
        SwingPointTracker swingTracker,
        Logger logger,
        double minRewardRisk = 2.0,
        int minSignalScore = 60,
        int maxBarsWaitingTest = 20,
        int maxBarsWaitingTrigger = 15,
        int maxBarsWaitingEntry = 3,
        int cooldownBars = 5,
        int maxFailedAttempts = 2)
    {
        _trendDetector = trendDetector;
        _tlTracker = tlTracker;
        _signalAnalyzer = signalAnalyzer;
        _swingTracker = swingTracker;
        _logger = logger;
        _minRewardRisk = minRewardRisk;
        _minSignalScore = minSignalScore;
        _maxBarsWaitingTest = maxBarsWaitingTest;
        _maxBarsWaitingTrigger = maxBarsWaitingTrigger;
        _maxBarsWaitingEntry = maxBarsWaitingEntry;
        _cooldownBars = cooldownBars;
        _maxFailedAttempts = maxFailedAttempts;
    }

    /// <summary>
    /// Main update method — runs the state machine for one bar.
    /// Must be called after TrendDetector and TrendLineTracker have been updated.
    /// </summary>
    /// <param name="bars">The cTrader Bars data series.</param>
    /// <param name="currentIndex">Index of the closed bar being analyzed.</param>
    /// <param name="ema20">Current EMA 20 value.</param>
    /// <param name="atr20">Current ATR 20 value.</param>
    /// <param name="recentBars">Recent BarInfo list for signal bar analysis.</param>
    /// <param name="pipSize">Symbol.PipSize for entry/stop calculation and overshoot tolerance.</param>
    /// <returns>The active setup if one exists, or null if in Scanning with no setup.</returns>
    public MtrSetup? Update(
        Bars bars, int currentIndex, double ema20, double atr20,
        List<BarInfo> recentBars, double pipSize)
    {
        // Increment bar counter BEFORE evaluating transitions
        // This ensures timeouts work correctly
        if (ActiveSetup != null)
            ActiveSetup.BarsSinceStateChange++;

        var currentState = ActiveSetup?.CurrentState ?? MtrState.Scanning;

        switch (currentState)
        {
            case MtrState.Scanning:
                ProcessScanning();
                break;
            case MtrState.Trending:
                ProcessTrending(bars, currentIndex);
                break;
            case MtrState.TlbActive:
                ProcessTlbActive(bars, currentIndex, atr20);
                break;
            case MtrState.Testing:
                ProcessTesting(bars, currentIndex, ema20, atr20, recentBars, pipSize);
                break;
            case MtrState.Triggered:
                ProcessTriggered(bars, currentIndex, atr20, recentBars, pipSize);
                break;
            case MtrState.InTrade:
                // Managed by TradeManager (Phase 3) — caller uses SetClosed()
                break;
            case MtrState.Cooldown:
                ProcessCooldown();
                break;
        }

        return ActiveSetup;
    }

    /// <summary>Caller informs that the pending order was filled and position is open.</summary>
    public void SetFilled()
    {
        if (ActiveSetup?.CurrentState == MtrState.Triggered)
            TransitionTo(MtrState.InTrade, "Order filled — position open");
    }

    /// <summary>Caller informs that the trade was closed (TP, SL, or manual).</summary>
    public void SetClosed()
    {
        if (ActiveSetup?.CurrentState == MtrState.InTrade)
            TransitionTo(MtrState.Cooldown, "Trade closed — entering cooldown");
    }

    /// <summary>Force reset to Scanning state (manual override).</summary>
    public void ForceReset()
    {
        var oldState = ActiveSetup?.CurrentState.ToString() ?? "null";
        ActiveSetup = null;
        _logger.Trade($"MTR State: {oldState} → Scanning | Reason: Manual reset");
    }

    // =========================================
    // STATE PROCESSORS
    // =========================================

    /// <summary>
    /// SCANNING: Initial state — looking for a clear trend to potentially reverse.
    /// Brooks: need a well-established trend before expecting a reversal.
    /// Transition → TRENDING when trend has ≥4 aligned swings, direction ≠ Range, strength ≥ 0.3.
    /// </summary>
    private void ProcessScanning()
    {
        var trend = _trendDetector.CurrentTrend;

        // Brooks: MTR requires a CLEAR trend first — at least 2 HH+HL or 2 LH+LL (SwingCount ≥ 4)
        // A weak or ambiguous market does not produce reliable reversals
        if (trend.Direction != TrendDirection.Range
            && trend.SwingCount >= 4
            && trend.Strength >= 0.3)
        {
            ActiveSetup = new MtrSetup
            {
                CurrentState = MtrState.Scanning, // Will be set by TransitionTo
                DetectedAt = DateTime.UtcNow
            };
            TransitionTo(MtrState.Trending, $"Clear {trend.Direction} trend detected (swings={trend.SwingCount}, strength={trend.Strength:F2})");
        }
    }

    /// <summary>
    /// TRENDING: Confirmed trend — monitoring for a Trend Line Break.
    /// Brooks: the TLB is the first signal that the trend may be ending.
    /// Bull trend + Bull TL break (support broken) → BearReversal setup.
    /// Bear trend + Bear TL break (resistance broken) → BullReversal setup.
    /// Only MAJOR TL breaks qualify — micro TL breaks are With Trend entries.
    /// </summary>
    private void ProcessTrending(Bars bars, int currentIndex)
    {
        var trend = _trendDetector.CurrentTrend;

        // Transition → SCANNING: trend collapsed to Range or reversed without TLB
        if (trend.Direction == TrendDirection.Range)
        {
            TransitionTo(MtrState.Scanning, "Trend collapsed to Range");
            ActiveSetup = null;
            return;
        }

        // Check for TLB events
        // Bull trend → Bull TL (support) broken → bearish signal → BearReversal MTR
        if (trend.Direction == TrendDirection.Bull && _tlTracker.HasBullTlb)
        {
            var tlb = _tlTracker.LastTlb;
            if (tlb != null && tlb.IsMajorBreak)
            {
                // Brooks: only Major TL breaks initiate MTR — micro breaks are With Trend
                ActiveSetup!.Direction = MtrDirection.BearReversal;
                ActiveSetup.TlbBarIndex = tlb.BarIndex;
                ActiveSetup.TlbStrength = tlb.Strength;
                // Old extreme = the highest point of the bull trend being reversed
                var lastHigh = _swingTracker.LastSwingHigh;
                ActiveSetup.OldExtremePrice = lastHigh?.Price ?? bars.HighPrices[currentIndex];
                TransitionTo(MtrState.TlbActive, $"Major Bull TL broken → BearReversal (TLB strength={tlb.Strength:F2})");
                return;
            }
        }

        // Bear trend → Bear TL (resistance) broken → bullish signal → BullReversal MTR
        if (trend.Direction == TrendDirection.Bear && _tlTracker.HasBearTlb)
        {
            var tlb = _tlTracker.LastTlb;
            if (tlb != null && tlb.IsMajorBreak)
            {
                ActiveSetup!.Direction = MtrDirection.BullReversal;
                ActiveSetup.TlbBarIndex = tlb.BarIndex;
                ActiveSetup.TlbStrength = tlb.Strength;
                // Old extreme = the lowest point of the bear trend being reversed
                var lastLow = _swingTracker.LastSwingLow;
                ActiveSetup.OldExtremePrice = lastLow?.Price ?? bars.LowPrices[currentIndex];
                TransitionTo(MtrState.TlbActive, $"Major Bear TL broken → BullReversal (TLB strength={tlb.Strength:F2})");
                return;
            }
        }
    }

    /// <summary>
    /// TLB_ACTIVE: Trend line was broken — waiting for the market to test the old extreme.
    /// Brooks: after a TLB, the market almost always tests the old trend extreme.
    /// The test can be an overshoot, undershoot, or exact double.
    /// Minimum 3 bars in this state for the TLB to "settle" before looking for test.
    /// </summary>
    private void ProcessTlbActive(Bars bars, int currentIndex, double atr20)
    {
        // Timeout → SCANNING: market didn't test the extreme in time
        if (ActiveSetup!.BarsSinceStateChange > _maxBarsWaitingTest)
        {
            TransitionTo(MtrState.Scanning, $"TLB_ACTIVE timeout ({_maxBarsWaitingTest} bars) — no test of extreme");
            ActiveSetup = null;
            return;
        }

        // Brooks: need minimum 3 bars after TLB before looking for test
        // The TLB move needs time to "settle" — immediate tests are less reliable
        if (ActiveSetup.BarsSinceStateChange < 3)
            return;

        // Check if price is returning toward the old extreme
        double close = bars.ClosePrices[currentIndex];
        double high = bars.HighPrices[currentIndex];
        double low = bars.LowPrices[currentIndex];
        double distanceToExtreme;

        if (ActiveSetup.Direction == MtrDirection.BullReversal)
        {
            // Bear→Bull MTR: price dropping to test the old LOW
            distanceToExtreme = low - ActiveSetup.OldExtremePrice;
            // Price is within 2x ATR of the old low
            if (Math.Abs(distanceToExtreme) < 2.0 * atr20)
            {
                TransitionTo(MtrState.Testing, $"Price approaching old low ({ActiveSetup.OldExtremePrice:F5}), distance={distanceToExtreme:F5}");
            }
        }
        else
        {
            // Bull→Bear MTR: price rising to test the old HIGH
            distanceToExtreme = ActiveSetup.OldExtremePrice - high;
            if (Math.Abs(distanceToExtreme) < 2.0 * atr20)
            {
                TransitionTo(MtrState.Testing, $"Price approaching old high ({ActiveSetup.OldExtremePrice:F5}), distance={distanceToExtreme:F5}");
            }
        }
    }

    /// <summary>
    /// TESTING: Price is testing the old trend extreme — classify test type and look for signal bar.
    /// Brooks: the test determines entry quality. Overshoot with reversal = strong signal.
    /// Undershoot = trend couldn't even reach the old extreme = strong counter-trend.
    /// Overshoot tolerance: up to 3 pips beyond extreme is valid (one-tick traps).
    /// If overshoot > 1.5x ATR → trend resumed, invalidate.
    /// </summary>
    private void ProcessTesting(Bars bars, int currentIndex, double ema20, double atr20,
        List<BarInfo> recentBars, double pipSize)
    {
        double close = bars.ClosePrices[currentIndex];
        double high = bars.HighPrices[currentIndex];
        double low = bars.LowPrices[currentIndex];

        // Classify the test type
        ClassifyTest(high, low, atr20, pipSize);

        // Check for invalidation: extreme overshoot means trend resumed
        // Brooks: if price goes > 1.5x ATR beyond the old extreme, the trend is continuing
        double overshootDistance = 0;
        if (ActiveSetup!.Direction == MtrDirection.BullReversal)
        {
            // Testing old low — overshoot = price went well below
            if (low < ActiveSetup.OldExtremePrice)
                overshootDistance = ActiveSetup.OldExtremePrice - low;
        }
        else
        {
            // Testing old high — overshoot = price went well above
            if (high > ActiveSetup.OldExtremePrice)
                overshootDistance = high - ActiveSetup.OldExtremePrice;
        }

        if (overshootDistance > 1.5 * atr20)
        {
            TransitionTo(MtrState.Scanning, $"Extreme overshoot ({overshootDistance / atr20:F1}x ATR) — trend resumed");
            ActiveSetup = null;
            return;
        }

        // Timeout → SCANNING: no signal bar found in time
        if (ActiveSetup.BarsSinceStateChange > _maxBarsWaitingTrigger)
        {
            TransitionTo(MtrState.Scanning, $"TESTING timeout ({_maxBarsWaitingTrigger} bars) — no signal bar");
            ActiveSetup = null;
            return;
        }

        // Look for signal bar
        if (recentBars.Count < 2) return;

        var signalBar = recentBars[recentBars.Count - 1];
        var previousBar = recentBars[recentBars.Count - 2];

        var result = _signalAnalyzer.Analyze(
            signalBar, previousBar, recentBars,
            atr20, ema20, pipSize, _minSignalScore,
            ActiveSetup.Direction);

        // Transition → TRIGGERED: valid signal bar with ≥ 2 reasons
        // Brooks: need multiple reasons for a trade — single-reason entries are low probability
        if (result.IsValid && result.ReasonCount >= 2)
        {
            ActiveSetup.SignalBarIndex = signalBar.Index;
            ActiveSetup.SignalBarScore = result.Score;
            ActiveSetup.ReasonCount = result.ReasonCount;
            ActiveSetup.Reasons = result.Reasons;
            ActiveSetup.IsSecondEntry = result.IsSecondEntry;
            ActiveSetup.TestBarIndex = signalBar.Index;
            ActiveSetup.TestPrice = ActiveSetup.Direction == MtrDirection.BullReversal ? low : high;
            TransitionTo(MtrState.Triggered, $"Signal bar found (score={result.Score:F0}, reasons={result.ReasonCount}: {string.Join(", ", result.Reasons)})");
        }
    }

    /// <summary>
    /// TRIGGERED: Setup complete — pending order will be placed by the caller.
    /// Calculate entry, stop, and target prices. Validate Trader's Equation.
    /// Brooks: the math must work — if R:R is unfavorable, skip the trade.
    /// Timeout goes to TESTING (retry), not SCANNING — unless max attempts exceeded.
    /// </summary>
    private void ProcessTriggered(Bars bars, int currentIndex, double atr20,
        List<BarInfo> recentBars, double pipSize)
    {
        // Calculate entry, stop, and target if not yet set
        if (ActiveSetup!.EntryPrice == 0)
        {
            CalculateEntryStopTarget(bars, currentIndex, atr20, pipSize);

            // Validate Trader's Equation (works for both LONG and SHORT)
            // Brooks: if the math doesn't work, don't take the trade
            double risk = Math.Abs(ActiveSetup.EntryPrice - ActiveSetup.StopPrice);
            double reward = Math.Abs(ActiveSetup.TargetPrice - ActiveSetup.EntryPrice);

            if (risk > 0)
            {
                double riskReward = reward / risk;
                if (riskReward < _minRewardRisk)
                {
                    TransitionTo(MtrState.Scanning, $"Trader's Equation failed (R:R={riskReward:F2} < {_minRewardRisk:F1})");
                    ActiveSetup = null;
                    return;
                }
            }
        }

        // Brooks: max 2 entry attempts — after that, the setup has "expired"
        if (ActiveSetup.FailedAttempts >= _maxFailedAttempts)
        {
            TransitionTo(MtrState.Scanning, $"Max failed attempts reached ({_maxFailedAttempts})");
            ActiveSetup = null;
            return;
        }

        // Timeout → TESTING (retry with new signal bar), not SCANNING
        // Brooks: the setup context is still valid — just need a better entry bar
        if (ActiveSetup.BarsSinceStateChange > _maxBarsWaitingEntry)
        {
            ActiveSetup.FailedAttempts++;
            if (ActiveSetup.FailedAttempts >= _maxFailedAttempts)
            {
                TransitionTo(MtrState.Scanning, $"Entry timeout + max attempts ({_maxFailedAttempts}) — abandoning setup");
                ActiveSetup = null;
            }
            else
            {
                // Reset entry prices for recalculation with new signal bar
                ActiveSetup.EntryPrice = 0;
                ActiveSetup.StopPrice = 0;
                ActiveSetup.TargetPrice = 0;
                TransitionTo(MtrState.Testing, $"Entry timeout — retry #{ActiveSetup.FailedAttempts} (looking for new signal bar)");
            }
        }
    }

    /// <summary>
    /// COOLDOWN: Pause after trade exit to avoid emotional re-entry.
    /// Brooks: traders who re-enter immediately often get trapped.
    /// </summary>
    private void ProcessCooldown()
    {
        if (ActiveSetup!.BarsSinceStateChange > _cooldownBars)
        {
            TransitionTo(MtrState.Scanning, $"Cooldown complete ({_cooldownBars} bars)");
            ActiveSetup = null;
        }
    }

    // =========================================
    // HELPER METHODS
    // =========================================

    /// <summary>
    /// Classifies how the market tests the old trend extreme.
    /// Brooks: overshoot with reversal is the strongest MTR signal (traps breakout traders).
    /// Tolerance: up to 3 pips beyond = valid (one-tick traps are common).
    /// </summary>
    private void ClassifyTest(double high, double low, double atr20, double pipSize)
    {
        double extreme = ActiveSetup!.OldExtremePrice;
        double tolerance = 3 * pipSize; // Brooks' one-tick trap tolerance

        if (ActiveSetup.Direction == MtrDirection.BullReversal)
        {
            // Testing old low — bear→bull MTR
            double testPrice = low;
            double diff = testPrice - extreme; // positive = undershoot, negative = overshoot

            if (Math.Abs(diff) < 2 * pipSize)
                ActiveSetup.TestType = TestType.DoubleExact;
            else if (diff > 0)
                ActiveSetup.TestType = TestType.Undershoot;
            else if (Math.Abs(diff) <= tolerance || diff < 0)
                ActiveSetup.TestType = TestType.Overshoot;

            ActiveSetup.TestPrice = testPrice;
        }
        else
        {
            // Testing old high — bull→bear MTR
            double testPrice = high;
            double diff = testPrice - extreme; // positive = overshoot, negative = undershoot

            if (Math.Abs(diff) < 2 * pipSize)
                ActiveSetup.TestType = TestType.DoubleExact;
            else if (diff < 0)
                ActiveSetup.TestType = TestType.Undershoot;
            else if (diff >= 0)
                ActiveSetup.TestType = TestType.Overshoot;

            ActiveSetup.TestPrice = testPrice;
        }
    }

    /// <summary>
    /// Calculates entry, stop, and target prices for the triggered setup.
    /// Target uses Measured Move capped at 3x risk, with minimum 2x risk.
    /// </summary>
    private void CalculateEntryStopTarget(Bars bars, int currentIndex, double atr20, double pipSize)
    {
        var setup = ActiveSetup!;
        int sigIdx = setup.SignalBarIndex;

        // Ensure signal bar index is valid
        if (sigIdx < 0 || sigIdx >= bars.Count) return;

        double sigHigh = bars.HighPrices[sigIdx];
        double sigLow = bars.LowPrices[sigIdx];
        double sigRange = sigHigh - sigLow;

        if (setup.Direction == MtrDirection.BullReversal)
        {
            // Bull entry: 1 pip above signal bar high
            setup.EntryPrice = sigHigh + pipSize;
            setup.StopPrice = sigLow - pipSize;
        }
        else
        {
            // Bear entry: 1 pip below signal bar low
            setup.EntryPrice = sigLow - pipSize;
            setup.StopPrice = sigHigh + pipSize;
        }

        // Money stop: if stop distance > 2x ATR, use 60% of signal bar range
        double stopDistance = Math.Abs(setup.EntryPrice - setup.StopPrice);
        if (atr20 > 0 && stopDistance > 2.0 * atr20)
        {
            double moneyStop = sigRange * 0.60;
            if (setup.Direction == MtrDirection.BullReversal)
                setup.StopPrice = setup.EntryPrice - moneyStop;
            else
                setup.StopPrice = setup.EntryPrice + moneyStop;
            stopDistance = moneyStop;
        }

        // Measured Move target calculation
        // Brooks: distance from old extreme to TLB break, projected from test price
        double tlbBreakPrice = _tlTracker.LastTlb?.BreakPrice ?? setup.EntryPrice;
        double moveDistance = Math.Abs(setup.OldExtremePrice - tlbBreakPrice);
        double risk = Math.Abs(setup.EntryPrice - setup.StopPrice);

        double targetMM;
        if (setup.Direction == MtrDirection.BullReversal)
            targetMM = setup.TestPrice + moveDistance; // Project UP from test low
        else
            targetMM = setup.TestPrice - moveDistance; // Project DOWN from test high

        // Cap target at 3x risk, but ensure minimum 2x risk
        double maxTarget = setup.Direction == MtrDirection.BullReversal
            ? setup.EntryPrice + 3.0 * risk
            : setup.EntryPrice - 3.0 * risk;
        double minTarget = setup.Direction == MtrDirection.BullReversal
            ? setup.EntryPrice + _minRewardRisk * risk
            : setup.EntryPrice - _minRewardRisk * risk;

        // Use measured move if within bounds, otherwise clamp
        if (setup.Direction == MtrDirection.BullReversal)
        {
            setup.TargetPrice = Math.Min(targetMM, maxTarget);
            setup.TargetPrice = Math.Max(setup.TargetPrice, minTarget);
        }
        else
        {
            setup.TargetPrice = Math.Max(targetMM, maxTarget);
            setup.TargetPrice = Math.Min(setup.TargetPrice, minTarget);
        }
    }

    /// <summary>
    /// Transitions the setup to a new state, resets the bar counter, and logs the event.
    /// Every state transition is logged for debugging and audit purposes.
    /// </summary>
    private void TransitionTo(MtrState newState, string reason)
    {
        var oldState = ActiveSetup?.CurrentState.ToString() ?? "Scanning";

        if (ActiveSetup != null)
        {
            ActiveSetup.CurrentState = newState;
            ActiveSetup.BarsSinceStateChange = 0;
        }

        _logger.Trade($"MTR State: {oldState} → {newState} | Reason: {reason}");
    }
}
