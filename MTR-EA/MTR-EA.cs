#nullable enable

using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Robots.Core;
using cAlgo.Robots.Models;
using cAlgo.Robots.Utils;

namespace cAlgo.Robots;

[Robot(AccessRights = AccessRights.None, AddIndicators = true)]
public class MTREA : Robot
{
    // === Phase 1 Parameters ===

    [Parameter("Swing Strength", DefaultValue = 3, MinValue = 1, MaxValue = 10)]
    public int SwingStrength { get; set; }

    [Parameter("Log Level (0=Debug, 4=Error)", DefaultValue = 1, MinValue = 0, MaxValue = 4)]
    public int LogLevelParam { get; set; }

    // === Phase 2 Parameters ===

    [Parameter("Trend Lookback", DefaultValue = 50, MinValue = 20, MaxValue = 200)]
    public int TrendLookback { get; set; }

    [Parameter("Min Reward:Risk", DefaultValue = 2.0, MinValue = 1.5, MaxValue = 5.0)]
    public double MinRewardRisk { get; set; }

    [Parameter("Signal Bar Min Score", DefaultValue = 60, MinValue = 30, MaxValue = 90)]
    public int SignalBarMinScore { get; set; }

    // === Indicators ===
    private ExponentialMovingAverage _ema20 = null!;
    private AverageTrueRange _atr20 = null!;

    // === Phase 1 Modules ===
    private SwingPointTracker _swingTracker = null!;
    private Logger _logger = null!;

    // === Phase 2 Modules ===
    private TrendDetector _trendDetector = null!;
    private TrendLineTracker _tlTracker = null!;
    private SignalBarAnalyzer _signalAnalyzer = null!;
    private MtrDetector _mtrDetector = null!;

    protected override void OnStart()
    {
        _logger = new Logger(this, (LogLevel)LogLevelParam);

        // cTrader built-in indicators
        _ema20 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 20);
        _atr20 = Indicators.AverageTrueRange(20, MovingAverageType.Exponential);

        // Phase 1 modules
        _swingTracker = new SwingPointTracker(SwingStrength);

        // Phase 2 modules
        _trendDetector = new TrendDetector(_swingTracker, TrendLookback);
        _tlTracker = new TrendLineTracker(_swingTracker);
        _signalAnalyzer = new SignalBarAnalyzer();
        _mtrDetector = new MtrDetector(
            _trendDetector, _tlTracker, _signalAnalyzer, _swingTracker, _logger,
            minRewardRisk: MinRewardRisk,
            minSignalScore: SignalBarMinScore,
            maxBarsWaitingTest: 20,
            maxBarsWaitingTrigger: 15,
            maxBarsWaitingEntry: 3,
            cooldownBars: 5,
            maxFailedAttempts: 2);

        _logger.Info("MTR-EA started — Phase 2 (Detection)");
    }

    protected override void OnBar()
    {
        // IMPORTANT: OnBar() fires when a NEW bar opens → the CLOSED bar is Bars.Count - 2
        // Bars.Count - 1 = current bar (just opened, not yet closed)
        // All price action analysis must use the CLOSED bar

        // Guard: need enough bars for EMA20, ATR20, and SwingPointTracker
        if (Bars.Count < 30) return;

        int closedBarIndex = Bars.Count - 2;
        int currentIndex = Bars.Count - 1; // Used by SwingPointTracker for range check
        double ema = _ema20.Result[closedBarIndex];
        double atr = _atr20.Result[closedBarIndex];

        // Build recent BarInfos for context analysis (based on CLOSED bars)
        var recentBars = BuildRecentBars(10);

        // Detection pipeline (ORDER MATTERS)
        _swingTracker.Update(Bars, currentIndex);
        _trendDetector.Update(Bars, closedBarIndex, ema, atr);
        _tlTracker.Update(Bars, closedBarIndex, ema, atr);

        var setup = _mtrDetector.Update(Bars, closedBarIndex, ema, atr, recentBars, Symbol.PipSize);

        // Log current state
        var trend = _trendDetector.CurrentTrend;
        _logger.Debug($"Trend: {trend.Direction} strength={trend.Strength:F2} AI={trend.AlwaysIn}");

        if (setup != null)
        {
            _logger.Info($"[{setup.CurrentState}] Dir={setup.Direction} " +
                $"TLB={setup.TlbStrength:F2} Test={setup.TestType} " +
                $"Reasons={setup.ReasonCount}");
        }

        // Phase 3 will add: SessionFilter + EntryManager + TradeManager here
    }

    protected override void OnStop()
    {
        _logger.Info("MTR-EA stopped");
    }

    /// <summary>
    /// Builds a list of recent BarInfo objects for context analysis.
    /// All bars are CLOSED bars (up to and including Bars.Count - 2).
    /// </summary>
    private List<BarInfo> BuildRecentBars(int count)
    {
        var result = new List<BarInfo>();
        int closedBarIndex = Bars.Count - 2;
        int startIndex = Math.Max(0, closedBarIndex - count + 1);

        BarInfo? prev = null;
        for (int i = startIndex; i <= closedBarIndex; i++)
        {
            var bar = BarInfo.FromBars(Bars, i, prev);
            result.Add(bar);
            prev = bar;
        }

        return result;
    }
}
