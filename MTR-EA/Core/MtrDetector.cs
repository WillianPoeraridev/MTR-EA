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
    private readonly TrendLineManager _tlManager;
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
    private readonly int _minBarsInTrending;
    private readonly int _minSwingTemporalSpan;

    /// <summary>The currently active MTR setup, or null if in Scanning state with no setup.</summary>
    public MtrSetup? ActiveSetup { get; private set; }

    /// <summary>
    /// Creates a new MtrDetector.
    /// </summary>
    public MtrDetector(
        TrendDetector trendDetector,
        TrendLineManager tlManager,
        SignalBarAnalyzer signalAnalyzer,
        SwingPointTracker swingTracker,
        Logger logger,
        double minRewardRisk = 2.0,
        int minSignalScore = 60,
        int maxBarsWaitingTest = 20,
        int maxBarsWaitingTrigger = 15,
        int maxBarsWaitingEntry = 3,
        int cooldownBars = 5,
        int maxFailedAttempts = 2,
        int minBarsInTrending = 6,
        int minSwingTemporalSpan = 20)
    {
        _trendDetector = trendDetector;
        _tlManager = tlManager;
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
        _minBarsInTrending = minBarsInTrending;
        _minSwingTemporalSpan = minSwingTemporalSpan;
    }

    /// <summary>
    /// Main update method — runs the state machine for one bar.
    /// </summary>
    public MtrSetup? Update(
        Bars bars, int currentIndex, double ema20, double atr20,
        List<BarInfo> recentBars, double pipSize)
    {
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
                break;
            case MtrState.Cooldown:
                ProcessCooldown();
                break;
        }

        return ActiveSetup;
    }

    public void SetFilled()
    {
        if (ActiveSetup?.CurrentState == MtrState.Triggered)
            TransitionTo(MtrState.InTrade, "Order filled — position open");
    }

    public void SetClosed()
    {
        if (ActiveSetup?.CurrentState == MtrState.InTrade)
            TransitionTo(MtrState.Cooldown, "Trade closed — entering cooldown");
    }

    public void ForceReset()
    {
        var oldState = ActiveSetup?.CurrentState.ToString() ?? "null";
        ActiveSetup = null;
        _logger.Trade($"MTR State: {oldState} → Scanning | Reason: Manual reset");
    }

    // =========================================
    // STATE PROCESSORS
    // =========================================

    private void ProcessScanning()
    {
        var trend = _trendDetector.CurrentTrend;

        if (trend.Direction != TrendDirection.Range
            && trend.SwingCount >= 4
            && trend.Strength >= 0.40)
        {
            if (!HasSufficientSwingSpan())
                return;

            ActiveSetup = new MtrSetup
            {
                CurrentState = MtrState.Scanning,
                DetectedAt = DateTime.UtcNow,
                Direction = trend.Direction == TrendDirection.Bull
                    ? MtrDirection.BearReversal
                    : MtrDirection.BullReversal
            };
            TransitionTo(MtrState.Trending, $"Clear {trend.Direction} trend detected → expecting {ActiveSetup.Direction} (swings={trend.SwingCount}, strength={trend.Strength:F2})");
        }
    }

    private void ProcessTrending(Bars bars, int currentIndex)
    {
        var trend = _trendDetector.CurrentTrend;

        if (trend.Direction == TrendDirection.Range)
        {
            if (ActiveSetup!.BarsSinceStateChange < _minBarsInTrending)
                return;
            TransitionTo(MtrState.Scanning, "Trend collapsed to Range");
            ActiveSetup = null;
            return;
        }

        // Bull trend → Bull TL (support) broken → BearReversal MTR
        if (trend.Direction == TrendDirection.Bull && _tlManager.HasBullTlb)
        {
            var tlb = _tlManager.LastTlb;
            if (tlb != null && tlb.IsMajorBreak)
            {
                ActiveSetup!.Direction = MtrDirection.BearReversal;
                ActiveSetup.TlbBarIndex = tlb.BarIndex;
                ActiveSetup.TlbStrength = tlb.Strength;
                var lastHigh = _swingTracker.LastSwingHigh;
                ActiveSetup.OldExtremePrice = lastHigh?.Price ?? bars.HighPrices[currentIndex];
                TransitionTo(MtrState.TlbActive, $"Major Bull TL broken → BearReversal (line score={tlb.LineScore:F0}, TLB strength={tlb.Strength:F2})");
                return;
            }
        }

        // Bear trend → Bear TL (resistance) broken → BullReversal MTR
        if (trend.Direction == TrendDirection.Bear && _tlManager.HasBearTlb)
        {
            var tlb = _tlManager.LastTlb;
            if (tlb != null && tlb.IsMajorBreak)
            {
                ActiveSetup!.Direction = MtrDirection.BullReversal;
                ActiveSetup.TlbBarIndex = tlb.BarIndex;
                ActiveSetup.TlbStrength = tlb.Strength;
                var lastLow = _swingTracker.LastSwingLow;
                ActiveSetup.OldExtremePrice = lastLow?.Price ?? bars.LowPrices[currentIndex];
                TransitionTo(MtrState.TlbActive, $"Major Bear TL broken → BullReversal (line score={tlb.LineScore:F0}, TLB strength={tlb.Strength:F2})");
                return;
            }
        }
    }

    private void ProcessTlbActive(Bars bars, int currentIndex, double atr20)
    {
        if (ActiveSetup!.BarsSinceStateChange > _maxBarsWaitingTest)
        {
            TransitionTo(MtrState.Scanning, $"TLB_ACTIVE timeout ({_maxBarsWaitingTest} bars) — no test of extreme");
            ActiveSetup = null;
            return;
        }

        if (ActiveSetup.BarsSinceStateChange < 3)
            return;

        double high = bars.HighPrices[currentIndex];
        double low = bars.LowPrices[currentIndex];

        if (ActiveSetup.Direction == MtrDirection.BullReversal)
        {
            double distanceToExtreme = low - ActiveSetup.OldExtremePrice;
            if (Math.Abs(distanceToExtreme) < 2.0 * atr20)
                TransitionTo(MtrState.Testing, $"Price approaching old low ({ActiveSetup.OldExtremePrice:F5}), distance={distanceToExtreme:F5}");
        }
        else
        {
            double distanceToExtreme = ActiveSetup.OldExtremePrice - high;
            if (Math.Abs(distanceToExtreme) < 2.0 * atr20)
                TransitionTo(MtrState.Testing, $"Price approaching old high ({ActiveSetup.OldExtremePrice:F5}), distance={distanceToExtreme:F5}");
        }
    }

    private void ProcessTesting(Bars bars, int currentIndex, double ema20, double atr20,
        List<BarInfo> recentBars, double pipSize)
    {
        double high = bars.HighPrices[currentIndex];
        double low = bars.LowPrices[currentIndex];

        ClassifyTest(high, low, atr20, pipSize);

        double overshootDistance = 0;
        if (ActiveSetup!.Direction == MtrDirection.BullReversal)
        {
            if (low < ActiveSetup.OldExtremePrice)
                overshootDistance = ActiveSetup.OldExtremePrice - low;
        }
        else
        {
            if (high > ActiveSetup.OldExtremePrice)
                overshootDistance = high - ActiveSetup.OldExtremePrice;
        }

        if (overshootDistance > 1.5 * atr20)
        {
            TransitionTo(MtrState.Scanning, $"Extreme overshoot ({overshootDistance / atr20:F1}x ATR) — trend resumed");
            ActiveSetup = null;
            return;
        }

        if (ActiveSetup.BarsSinceStateChange > _maxBarsWaitingTrigger)
        {
            TransitionTo(MtrState.Scanning, $"TESTING timeout ({_maxBarsWaitingTrigger} bars) — no signal bar");
            ActiveSetup = null;
            return;
        }

        if (recentBars.Count < 2) return;

        var signalBar = recentBars[recentBars.Count - 1];
        var previousBar = recentBars[recentBars.Count - 2];

        var result = _signalAnalyzer.Analyze(
            signalBar, previousBar, recentBars,
            atr20, ema20, pipSize, _minSignalScore,
            ActiveSetup.Direction);

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

    private void ProcessTriggered(Bars bars, int currentIndex, double atr20,
        List<BarInfo> recentBars, double pipSize)
    {
        if (ActiveSetup!.EntryPrice == 0)
        {
            CalculateEntryStopTarget(bars, currentIndex, atr20, pipSize);

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

        if (ActiveSetup.FailedAttempts >= _maxFailedAttempts)
        {
            TransitionTo(MtrState.Scanning, $"Max failed attempts reached ({_maxFailedAttempts})");
            ActiveSetup = null;
            return;
        }

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
                ActiveSetup.EntryPrice = 0;
                ActiveSetup.StopPrice = 0;
                ActiveSetup.TargetPrice = 0;
                TransitionTo(MtrState.Testing, $"Entry timeout — retry #{ActiveSetup.FailedAttempts} (looking for new signal bar)");
            }
        }
    }

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

    private bool HasSufficientSwingSpan()
    {
        var highs = _swingTracker.GetRecentHighs(2);
        var lows = _swingTracker.GetRecentLows(2);
        var swings = highs.Concat(lows).OrderBy(s => s.BarIndex).ToList();

        if (swings.Count < 2) return false;

        int span = swings.Last().BarIndex - swings.First().BarIndex;
        return span >= _minSwingTemporalSpan;
    }

    private void ClassifyTest(double high, double low, double atr20, double pipSize)
    {
        double extreme = ActiveSetup!.OldExtremePrice;
        double tolerance = 3 * pipSize;

        if (ActiveSetup.Direction == MtrDirection.BullReversal)
        {
            double testPrice = low;
            double diff = testPrice - extreme;

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
            double testPrice = high;
            double diff = testPrice - extreme;

            if (Math.Abs(diff) < 2 * pipSize)
                ActiveSetup.TestType = TestType.DoubleExact;
            else if (diff < 0)
                ActiveSetup.TestType = TestType.Undershoot;
            else if (diff >= 0)
                ActiveSetup.TestType = TestType.Overshoot;

            ActiveSetup.TestPrice = testPrice;
        }
    }

    private void CalculateEntryStopTarget(Bars bars, int currentIndex, double atr20, double pipSize)
    {
        var setup = ActiveSetup!;
        int sigIdx = setup.SignalBarIndex;

        if (sigIdx < 0 || sigIdx >= bars.Count) return;

        double sigHigh = bars.HighPrices[sigIdx];
        double sigLow = bars.LowPrices[sigIdx];
        double sigRange = sigHigh - sigLow;

        if (setup.Direction == MtrDirection.BullReversal)
        {
            setup.EntryPrice = sigHigh + pipSize;
            setup.StopPrice = sigLow - pipSize;
        }
        else
        {
            setup.EntryPrice = sigLow - pipSize;
            setup.StopPrice = sigHigh + pipSize;
        }

        double stopDistance = Math.Abs(setup.EntryPrice - setup.StopPrice);
        if (atr20 > 0 && stopDistance > 2.0 * atr20)
        {
            double moneyStop = sigRange * 0.60;
            if (setup.Direction == MtrDirection.BullReversal)
                setup.StopPrice = setup.EntryPrice - moneyStop;
            else
                setup.StopPrice = setup.EntryPrice + moneyStop;
        }

        double tlbBreakPrice = _tlManager.LastTlb?.BreakPrice ?? setup.EntryPrice;
        double moveDistance = Math.Abs(setup.OldExtremePrice - tlbBreakPrice);
        double risk = Math.Abs(setup.EntryPrice - setup.StopPrice);

        double targetMM = setup.Direction == MtrDirection.BullReversal
            ? setup.TestPrice + moveDistance
            : setup.TestPrice - moveDistance;

        double maxTarget = setup.Direction == MtrDirection.BullReversal
            ? setup.EntryPrice + 3.0 * risk
            : setup.EntryPrice - 3.0 * risk;
        double minTarget = setup.Direction == MtrDirection.BullReversal
            ? setup.EntryPrice + _minRewardRisk * risk
            : setup.EntryPrice - _minRewardRisk * risk;

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
