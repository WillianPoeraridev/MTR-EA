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
    private TrendLineManager _tlManager = null!;
    private SignalBarAnalyzer _signalAnalyzer = null!;
    private MtrDetector _mtrDetector = null!;

    // === Visual Debug State ===
    private MtrState _previousMtrState = MtrState.Scanning;
    private int _logThrottle;
    private const int MaxTrendLines = 3;

    protected override void OnStart()
    {
        _logger = new Logger(this, (LogLevel)LogLevelParam);

        _ema20 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 20);
        _atr20 = Indicators.AverageTrueRange(20, MovingAverageType.Exponential);

        _swingTracker = new SwingPointTracker(SwingStrength);
        _trendDetector = new TrendDetector(_swingTracker, TrendLookback);
        _tlManager = new TrendLineManager(_swingTracker, _logger, topN: 3, tlbMinBreakAtr: 0.5, recalcIntervalBars: 6);
        _signalAnalyzer = new SignalBarAnalyzer();
        _mtrDetector = new MtrDetector(
            _trendDetector, _tlManager, _signalAnalyzer, _swingTracker, _logger,
            minRewardRisk: MinRewardRisk,
            minSignalScore: SignalBarMinScore,
            maxBarsWaitingTest: 20,
            maxBarsWaitingTrigger: 15,
            maxBarsWaitingEntry: 3,
            cooldownBars: 5,
            maxFailedAttempts: 2,
            minBarsInTrending: 6,
            minSwingTemporalSpan: 20);

        _logger.Info("MTR-EA started — Multi-Trendline Detection");
    }

    protected override void OnBar()
    {
        if (Bars.Count < 30) return;

        int closedBarIndex = Bars.Count - 2;
        int currentIndex = Bars.Count - 1;
        double ema = _ema20.Result[closedBarIndex];
        double atr = _atr20.Result[closedBarIndex];

        var recentBars = BuildRecentBars(10);
        int swingCountBefore = _swingTracker.GetAll().Count;

        // Detection pipeline (ORDER MATTERS)
        _swingTracker.Update(Bars, currentIndex, atr);
        _trendDetector.Update(Bars, closedBarIndex, ema, atr);
        _tlManager.Update(Bars, closedBarIndex, ema, atr);

        var setup = _mtrDetector.Update(Bars, closedBarIndex, ema, atr, recentBars, Symbol.PipSize);

        // Throttled logging
        _logThrottle++;
        MtrState currentMtrState = setup?.CurrentState ?? MtrState.Scanning;
        bool isTransition = currentMtrState != _previousMtrState;

        if (isTransition || _logThrottle >= 12)
        {
            var trend = _trendDetector.CurrentTrend;
            _logger.Debug($"Trend: {trend.Direction} strength={trend.Strength:F2} AI={trend.AlwaysIn}");
            _logThrottle = 0;
        }

        // Visual Debug
        if (ShowVisualDebug)
        {
            DrawNewSwingPoints(swingCountBefore, atr);
            DrawTrendLines();
            DrawTlbEvents(closedBarIndex, atr);
            DrawMtrStateTransitions(closedBarIndex, atr, setup);
            DrawSignalBar(closedBarIndex, atr, setup);
            DrawDebugPanel(setup);
        }

        _previousMtrState = currentMtrState;
    }

    protected override void OnStop()
    {
        _logger.Info("MTR-EA stopped");
    }

    // =========================================
    // VISUAL DEBUG
    // =========================================

    private void DrawNewSwingPoints(int countBefore, double atr)
    {
        var allSwings = _swingTracker.GetAll();
        if (allSwings.Count <= countBefore) return;

        for (int i = countBefore; i < allSwings.Count; i++)
        {
            var sp = allSwings[i];
            if (sp.BarIndex < 50) continue;

            if (sp.Type == SwingType.High)
            {
                Chart.DrawIcon($"SH_{sp.BarIndex}", ChartIconType.DownTriangle,
                    Bars.OpenTimes[sp.BarIndex], sp.Price + (atr * 0.3), Color.DodgerBlue);
            }
            else
            {
                Chart.DrawIcon($"SL_{sp.BarIndex}", ChartIconType.UpTriangle,
                    Bars.OpenTimes[sp.BarIndex], sp.Price - (atr * 0.3), Color.OrangeRed);
            }
        }
    }

    /// <summary>
    /// Draws Top N trendlines per direction with visual ranking.
    /// Best line = thick solid, others = thin dotted, color intensity by rank.
    /// </summary>
    private void DrawTrendLines()
    {
        // Bull trendlines (green tones by rank)
        Color[] bullColors = { Color.Lime, Color.Green, Color.DarkGreen };
        for (int i = 0; i < _tlManager.ActiveBullLines.Count && i < MaxTrendLines; i++)
        {
            var tl = _tlManager.ActiveBullLines[i];
            Chart.DrawTrendLine(
                $"BullTL_{i}",
                Bars.OpenTimes[tl.Point1.BarIndex], tl.Point1.Price,
                Bars.OpenTimes[tl.Point2.BarIndex], tl.Point2.Price,
                bullColors[i],
                i == 0 ? 2 : 1,
                i == 0 ? LineStyle.Solid : LineStyle.Dots);
            var tlObj = Chart.FindObject($"BullTL_{i}") as ChartTrendLine;
            if (tlObj != null) tlObj.ExtendToInfinity = true;
        }

        // Bear trendlines (red tones by rank)
        Color[] bearColors = { Color.Red, Color.DarkRed, Color.Maroon };
        for (int i = 0; i < _tlManager.ActiveBearLines.Count && i < MaxTrendLines; i++)
        {
            var tl = _tlManager.ActiveBearLines[i];
            Chart.DrawTrendLine(
                $"BearTL_{i}",
                Bars.OpenTimes[tl.Point1.BarIndex], tl.Point1.Price,
                Bars.OpenTimes[tl.Point2.BarIndex], tl.Point2.Price,
                bearColors[i],
                i == 0 ? 2 : 1,
                i == 0 ? LineStyle.Solid : LineStyle.Dots);
            var tlObj = Chart.FindObject($"BearTL_{i}") as ChartTrendLine;
            if (tlObj != null) tlObj.ExtendToInfinity = true;
        }

        // Clean up slots that no longer have lines
        for (int i = _tlManager.ActiveBullLines.Count; i < MaxTrendLines; i++)
            Chart.RemoveObject($"BullTL_{i}");
        for (int i = _tlManager.ActiveBearLines.Count; i < MaxTrendLines; i++)
            Chart.RemoveObject($"BearTL_{i}");
    }

    /// <summary>
    /// Draws TLB markers with line score for Major breaks only.
    /// </summary>
    private void DrawTlbEvents(int closedBarIndex, double atr)
    {
        var tlb = _tlManager.LastTlb;
        if (tlb == null || !tlb.IsMajorBreak) return;

        if (_tlManager.HasBullTlb)
        {
            Chart.DrawIcon($"TLB_{closedBarIndex}", ChartIconType.DownArrow,
                Bars.OpenTimes[closedBarIndex], Bars.HighPrices[closedBarIndex] + (atr * 0.5), Color.Red);
            Chart.DrawText($"TLBtxt_{closedBarIndex}", $"TLB\u2193 ({tlb.LineScore:F0})",
                Bars.OpenTimes[closedBarIndex], Bars.HighPrices[closedBarIndex] + (atr * 0.7), Color.Red);
        }

        if (_tlManager.HasBearTlb)
        {
            Chart.DrawIcon($"TLB_{closedBarIndex}", ChartIconType.UpArrow,
                Bars.OpenTimes[closedBarIndex], Bars.LowPrices[closedBarIndex] - (atr * 0.5), Color.Lime);
            Chart.DrawText($"TLBtxt_{closedBarIndex}", $"TLB\u2191 ({tlb.LineScore:F0})",
                Bars.OpenTimes[closedBarIndex], Bars.LowPrices[closedBarIndex] - (atr * 0.7), Color.Lime);
        }
    }

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

            Chart.DrawText($"State_{closedBarIndex}", stateLabel,
                Bars.OpenTimes[closedBarIndex], yPosition, stateColor);
        }
    }

    private void DrawSignalBar(int closedBarIndex, double atr, MtrSetup? setup)
    {
        if (setup == null || setup.CurrentState != MtrState.Triggered || setup.SignalBarIndex != closedBarIndex)
            return;

        bool isBull = setup.Direction == MtrDirection.BullReversal;
        Chart.DrawIcon($"SIG_{closedBarIndex}", ChartIconType.Diamond,
            Bars.OpenTimes[closedBarIndex],
            isBull ? Bars.LowPrices[closedBarIndex] - (atr * 0.5) : Bars.HighPrices[closedBarIndex] + (atr * 0.5),
            isBull ? Color.Lime : Color.Red);

        Chart.DrawHorizontalLine("ActiveEntry", setup.EntryPrice, Color.Cyan, 1, LineStyle.Dots);
        Chart.DrawHorizontalLine("ActiveStop", setup.StopPrice, Color.Red, 1, LineStyle.Dots);
        Chart.DrawHorizontalLine("ActiveTarget", setup.TargetPrice, Color.Lime, 1, LineStyle.Dots);
    }

    private void DrawDebugPanel(MtrSetup? setup)
    {
        var trend = _trendDetector.CurrentTrend;

        string trendInfo = $"Trend: {trend.Direction} | Str: {trend.Strength:F2} | AlwaysIn: {trend.AlwaysIn}";

        string bestBull = _tlManager.BestBullLine != null
            ? $"Bull TL: score={_tlManager.BestBullLine.Score:F0} touches={_tlManager.BestBullLine.TouchCount}"
            : "Bull TL: none";
        string bestBear = _tlManager.BestBearLine != null
            ? $"Bear TL: score={_tlManager.BestBearLine.Score:F0} touches={_tlManager.BestBearLine.TouchCount}"
            : "Bear TL: none";

        string mtrInfo = setup != null
            ? $"MTR: {setup.CurrentState} | {setup.Direction} | Reasons: {setup.ReasonCount}"
            : "MTR: Scanning";

        Chart.DrawStaticText("DebugPanel",
            $"{trendInfo}\n{bestBull} | {bestBear}\n{mtrInfo}",
            VerticalAlignment.Top, HorizontalAlignment.Left, Color.White);
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
