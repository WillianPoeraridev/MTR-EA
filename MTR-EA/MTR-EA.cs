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

        _logger.Info("MTR-EA started — Phase 2 (Detection + Visual Debug)");
    }

    protected override void OnBar()
    {
        // IMPORTANT: OnBar() fires when a NEW bar opens → the CLOSED bar is Bars.Count - 2
        // Bars.Count - 1 = current bar (just opened, not yet closed)
        // All price action analysis must use the CLOSED bar

        // Guard: need enough bars for EMA20, ATR20, and SwingPointTracker
        if (Bars.Count < 30) return;

        int closedBarIndex = Bars.Count - 2;
        int currentIndex = Bars.Count - 1;
        double ema = _ema20.Result[closedBarIndex];
        double atr = _atr20.Result[closedBarIndex];

        // Build recent BarInfos for context analysis (based on CLOSED bars)
        var recentBars = BuildRecentBars(10);

        // Capture swing point count BEFORE update to detect new ones
        int swingCountBefore = _swingTracker.GetAll().Count;

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

        // === Visual Debug Markers ===
        if (ShowVisualDebug)
        {
            DrawNewSwingPoints(swingCountBefore, atr);
            DrawTrendLines();
            DrawTlbEvents(closedBarIndex, atr);
            DrawMtrStateTransitions(closedBarIndex, atr, setup);
            DrawSignalBar(closedBarIndex, atr, setup);
            DrawDebugPanel(setup);
        }

        // Phase 3 will add: SessionFilter + EntryManager + TradeManager here
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
    /// SH = blue down-triangle above the bar, SL = red up-triangle below the bar.
    /// </summary>
    private void DrawNewSwingPoints(int countBefore, double atr)
    {
        var allSwings = _swingTracker.GetAll();
        if (allSwings.Count <= countBefore) return;

        // Draw only the NEW swing points added this bar
        for (int i = countBefore; i < allSwings.Count; i++)
        {
            var sp = allSwings[i];
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
    /// Bull TL (support) = green, Bear TL (resistance) = red.
    /// Major TLs are thick solid lines, Micro TLs are thin dotted.
    /// </summary>
    private void DrawTrendLines()
    {
        if (_tlTracker.ActiveBullTrendLine != null)
        {
            var tl = _tlTracker.ActiveBullTrendLine;
            Chart.DrawTrendLine(
                "BullTL",
                Bars.OpenTimes[tl.Point1.BarIndex],
                tl.Point1.Price,
                Bars.OpenTimes[tl.Point2.BarIndex],
                tl.Point2.Price,
                tl.IsMajor ? Color.Lime : Color.DarkGreen,
                tl.IsMajor ? 2 : 1,
                tl.IsMajor ? LineStyle.Solid : LineStyle.Dots);
            var tlObj = Chart.FindObject("BullTL") as ChartTrendLine;
            if (tlObj != null) tlObj.ExtendToInfinity = true;
        }

        if (_tlTracker.ActiveBearTrendLine != null)
        {
            var tl = _tlTracker.ActiveBearTrendLine;
            Chart.DrawTrendLine(
                "BearTL",
                Bars.OpenTimes[tl.Point1.BarIndex],
                tl.Point1.Price,
                Bars.OpenTimes[tl.Point2.BarIndex],
                tl.Point2.Price,
                tl.IsMajor ? Color.Red : Color.DarkRed,
                tl.IsMajor ? 2 : 1,
                tl.IsMajor ? LineStyle.Solid : LineStyle.Dots);
            var tlObj = Chart.FindObject("BearTL") as ChartTrendLine;
            if (tlObj != null) tlObj.ExtendToInfinity = true;
        }
    }

    /// <summary>
    /// Draws arrows and text labels when a Trend Line Break event fires.
    /// Bull TLB (bearish) = red down-arrow, Bear TLB (bullish) = green up-arrow.
    /// </summary>
    private void DrawTlbEvents(int closedBarIndex, double atr)
    {
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
    /// Cleans up entry/stop/target lines when leaving Triggered state.
    /// </summary>
    private void DrawMtrStateTransitions(int closedBarIndex, double atr, MtrSetup? setup)
    {
        MtrState currentMtrState = setup?.CurrentState ?? MtrState.Scanning;

        if (currentMtrState == _previousMtrState) return;

        // Clean up entry/stop/target lines when leaving Triggered state
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

        _previousMtrState = currentMtrState;
    }

    /// <summary>
    /// Highlights the signal bar with a diamond and draws entry/stop/target horizontal lines
    /// when the setup transitions to Triggered.
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

        // Fixed names for easy removal on state change
        Chart.DrawHorizontalLine("ActiveEntry", setup.EntryPrice, Color.Cyan, 1, LineStyle.Dots);
        Chart.DrawHorizontalLine("ActiveStop", setup.StopPrice, Color.Red, 1, LineStyle.Dots);
        Chart.DrawHorizontalLine("ActiveTarget", setup.TargetPrice, Color.Lime, 1, LineStyle.Dots);
    }

    /// <summary>
    /// Draws a fixed info panel in the top-left corner showing trend and MTR status.
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
