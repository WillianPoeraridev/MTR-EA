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

    [Parameter("Swing Strength", DefaultValue = 5, MinValue = 2, MaxValue = 10)]
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

    // === Visual Debug Parameter ===

    [Parameter("Show Visual Debug", DefaultValue = true)]
    public bool ShowVisualDebug { get; set; }

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

    // === Visual Debug State ===
    private MtrState _previousMtrState = MtrState.Scanning;
    private int _logThrottle;

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
        _tlTracker = new TrendLineTracker(_swingTracker, _logger);
        _signalAnalyzer = new SignalBarAnalyzer();
        _mtrDetector = new MtrDetector(
            _trendDetector, _tlTracker, _signalAnalyzer, _swingTracker, _logger,
            minRewardRisk: MinRewardRisk,
            minSignalScore: SignalBarMinScore,
            maxBarsWaitingTest: 20,
            maxBarsWaitingTrigger: 15,
            maxBarsWaitingEntry: 3,
            cooldownBars: 5,
            maxFailedAttempts: 2,
            minBarsInTrending: 6,
            minSwingTemporalSpan: 20);

        _logger.Info("MTR-EA started — Calibrated Detection");
    }

    protected override void OnBar()
    {
        // Guard: need enough bars for EMA20, ATR20, and SwingPointTracker
        if (Bars.Count < 30) return;

        int closedBarIndex = Bars.Count - 2;
        int currentIndex = Bars.Count - 1;
        double ema = _ema20.Result[closedBarIndex];
        double atr = _atr20.Result[closedBarIndex];

        // Build recent BarInfos for context analysis
        var recentBars = BuildRecentBars(10);

        // Capture swing point count BEFORE update to detect new ones
        int swingCountBefore = _swingTracker.GetAll().Count;

        // Detection pipeline (ORDER MATTERS)
        _swingTracker.Update(Bars, currentIndex, atr);
        _trendDetector.Update(Bars, closedBarIndex, ema, atr);
        _tlTracker.Update(Bars, closedBarIndex, ema, atr);

        var setup = _mtrDetector.Update(Bars, closedBarIndex, ema, atr, recentBars, Symbol.PipSize);

        // FIX 5d: Throttled logging — only log trend every 12 bars (1 hour) or on transitions
        _logThrottle++;
        MtrState currentMtrState = setup?.CurrentState ?? MtrState.Scanning;
        bool isTransition = currentMtrState != _previousMtrState;

        if (isTransition || _logThrottle >= 12)
        {
            var trend = _trendDetector.CurrentTrend;
            _logger.Debug($"Trend: {trend.Direction} strength={trend.Strength:F2} AI={trend.AlwaysIn}");
            _logThrottle = 0;
        }

        // Only log setup info on state transitions (not every bar)
        // State machine transitions are already logged by MtrDetector.TransitionTo()

        // === Visual Debug Markers ===
        if (ShowVisualDebug)
        {
            DrawNewSwingPoints(swingCountBefore, atr, closedBarIndex);
            DrawTrendLines();
            DrawTlbEvents(closedBarIndex, atr);
            DrawMtrStateTransitions(closedBarIndex, atr, setup);
            DrawSignalBar(closedBarIndex, atr, setup);
            DrawDebugPanel(setup);
        }

        // Update previous state for transition tracking
        _previousMtrState = currentMtrState;
    }

    protected override void OnStop()
    {
        _logger.Info("MTR-EA stopped");
    }

    // =========================================
    // VISUAL DEBUG — Drawing Methods
    // =========================================

    /// <summary>
    /// Draws icons for newly confirmed swing points.
    /// FIX 5a: Skip swing points from the first 50 bars (warm-up period).
    /// </summary>
    private void DrawNewSwingPoints(int countBefore, double atr, int closedBarIndex)
    {
        var allSwings = _swingTracker.GetAll();
        if (allSwings.Count <= countBefore) return;

        for (int i = countBefore; i < allSwings.Count; i++)
        {
            var sp = allSwings[i];

            // FIX 5a: Don't draw swing points from warm-up period
            if (sp.BarIndex < 50) continue;

            if (sp.Type == SwingType.High)
            {
                Chart.DrawIcon(
                    $"SH_{sp.BarIndex}",
                    ChartIconType.DownTriangle,
                    Bars.OpenTimes[sp.BarIndex],
                    sp.Price + (atr * 0.3),
                    Color.DodgerBlue);
            }
            else
            {
                Chart.DrawIcon(
                    $"SL_{sp.BarIndex}",
                    ChartIconType.UpTriangle,
                    Bars.OpenTimes[sp.BarIndex],
                    sp.Price - (atr * 0.3),
                    Color.OrangeRed);
            }
        }
    }

    /// <summary>
    /// Draws active trendlines on the chart.
    /// FIX 5c: Only draw Major trendlines (Micro TLs are omitted to reduce noise).
    /// </summary>
    private void DrawTrendLines()
    {
        // Remove stale TL objects if the TL became null or changed to Micro
        if (_tlTracker.ActiveBullTrendLine == null || !_tlTracker.ActiveBullTrendLine.IsMajor)
            Chart.RemoveObject("BullTL");
        if (_tlTracker.ActiveBearTrendLine == null || !_tlTracker.ActiveBearTrendLine.IsMajor)
            Chart.RemoveObject("BearTL");

        // Only draw Major TLs
        if (_tlTracker.ActiveBullTrendLine is { IsMajor: true } bullTl)
        {
            Chart.DrawTrendLine(
                "BullTL",
                Bars.OpenTimes[bullTl.Point1.BarIndex],
                bullTl.Point1.Price,
                Bars.OpenTimes[bullTl.Point2.BarIndex],
                bullTl.Point2.Price,
                Color.Lime, 2, LineStyle.Solid);
            var tlObj = Chart.FindObject("BullTL") as ChartTrendLine;
            if (tlObj != null) tlObj.ExtendToInfinity = true;
        }

        if (_tlTracker.ActiveBearTrendLine is { IsMajor: true } bearTl)
        {
            Chart.DrawTrendLine(
                "BearTL",
                Bars.OpenTimes[bearTl.Point1.BarIndex],
                bearTl.Point1.Price,
                Bars.OpenTimes[bearTl.Point2.BarIndex],
                bearTl.Point2.Price,
                Color.Red, 2, LineStyle.Solid);
            var tlObj = Chart.FindObject("BearTL") as ChartTrendLine;
            if (tlObj != null) tlObj.ExtendToInfinity = true;
        }
    }

    /// <summary>
    /// Draws TLB markers only for Major TL breaks (FIX 5b).
    /// </summary>
    private void DrawTlbEvents(int closedBarIndex, double atr)
    {
        // FIX 5b: Only draw markers for Major TLBs
        var tlb = _tlTracker.LastTlb;
        if (tlb == null || !tlb.IsMajorBreak) return;

        if (_tlTracker.HasBullTlb)
        {
            Chart.DrawIcon(
                $"TLB_{closedBarIndex}",
                ChartIconType.DownArrow,
                Bars.OpenTimes[closedBarIndex],
                Bars.HighPrices[closedBarIndex] + (atr * 0.5),
                Color.Red);
            Chart.DrawText(
                $"TLBtxt_{closedBarIndex}",
                "TLB\u2193",
                Bars.OpenTimes[closedBarIndex],
                Bars.HighPrices[closedBarIndex] + (atr * 0.7),
                Color.Red);
        }

        if (_tlTracker.HasBearTlb)
        {
            Chart.DrawIcon(
                $"TLB_{closedBarIndex}",
                ChartIconType.UpArrow,
                Bars.OpenTimes[closedBarIndex],
                Bars.LowPrices[closedBarIndex] - (atr * 0.5),
                Color.Lime);
            Chart.DrawText(
                $"TLBtxt_{closedBarIndex}",
                "TLB\u2191",
                Bars.OpenTimes[closedBarIndex],
                Bars.LowPrices[closedBarIndex] - (atr * 0.7),
                Color.Lime);
        }
    }

    /// <summary>
    /// Draws state labels ONLY on state transitions (not every bar).
    /// </summary>
    private void DrawMtrStateTransitions(int closedBarIndex, double atr, MtrSetup? setup)
    {
        MtrState currentMtrState = setup?.CurrentState ?? MtrState.Scanning;

        if (currentMtrState == _previousMtrState) return;

        if (_previousMtrState == MtrState.Triggered)
        {
            Chart.RemoveObject("ActiveEntry");
            Chart.RemoveObject("ActiveStop");
            Chart.RemoveObject("ActiveTarget");
        }

        if (currentMtrState != MtrState.Scanning && setup != null)
        {
            string stateLabel = currentMtrState switch
            {
                MtrState.Trending  => "TREND",
                MtrState.TlbActive => "TLB!",
                MtrState.Testing   => $"TEST ({setup.TestType})",
                MtrState.Triggered => "\u25B6 TRIGGERED",
                MtrState.InTrade   => "IN TRADE",
                MtrState.Cooldown  => "cooldown",
                _                  => ""
            };

            Color stateColor = currentMtrState switch
            {
                MtrState.Trending  => Color.Gray,
                MtrState.TlbActive => Color.Yellow,
                MtrState.Testing   => Color.Orange,
                MtrState.Triggered => Color.Cyan,
                MtrState.InTrade   => Color.Lime,
                MtrState.Cooldown  => Color.DarkGray,
                _                  => Color.White
            };

            double yPosition = setup.Direction == MtrDirection.BullReversal
                ? Bars.LowPrices[closedBarIndex] - (atr * 1.0)
                : Bars.HighPrices[closedBarIndex] + (atr * 1.0);

            Chart.DrawText(
                $"State_{closedBarIndex}",
                stateLabel,
                Bars.OpenTimes[closedBarIndex],
                yPosition,
                stateColor);
        }

        // Note: _previousMtrState is updated in OnBar() after all drawing methods
    }

    /// <summary>
    /// Highlights the signal bar with a diamond and draws entry/stop/target lines.
    /// </summary>
    private void DrawSignalBar(int closedBarIndex, double atr, MtrSetup? setup)
    {
        if (setup == null || setup.CurrentState != MtrState.Triggered || setup.SignalBarIndex != closedBarIndex)
            return;

        bool isBull = setup.Direction == MtrDirection.BullReversal;
        Color sigColor = isBull ? Color.Lime : Color.Red;

        Chart.DrawIcon(
            $"SIG_{closedBarIndex}",
            ChartIconType.Diamond,
            Bars.OpenTimes[closedBarIndex],
            isBull
                ? Bars.LowPrices[closedBarIndex] - (atr * 0.5)
                : Bars.HighPrices[closedBarIndex] + (atr * 0.5),
            sigColor);

        Chart.DrawHorizontalLine("ActiveEntry", setup.EntryPrice, Color.Cyan, 1, LineStyle.Dots);
        Chart.DrawHorizontalLine("ActiveStop", setup.StopPrice, Color.Red, 1, LineStyle.Dots);
        Chart.DrawHorizontalLine("ActiveTarget", setup.TargetPrice, Color.Lime, 1, LineStyle.Dots);
    }

    /// <summary>
    /// Draws a fixed info panel in the top-left corner.
    /// </summary>
    private void DrawDebugPanel(MtrSetup? setup)
    {
        var trend = _trendDetector.CurrentTrend;

        string trendInfo = $"Trend: {trend.Direction} " +
            $"| Str: {trend.Strength:F2} " +
            $"| AlwaysIn: {trend.AlwaysIn}";

        string mtrInfo = setup != null
            ? $"MTR: {setup.CurrentState} | {setup.Direction} | Reasons: {setup.ReasonCount}"
            : "MTR: Scanning";

        Chart.DrawStaticText(
            "DebugPanel",
            trendInfo + "\n" + mtrInfo,
            VerticalAlignment.Top,
            HorizontalAlignment.Left,
            Color.White);
    }

    // =========================================
    // HELPERS
    // =========================================

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
