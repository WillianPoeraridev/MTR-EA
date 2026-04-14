#nullable enable

using System;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;
using cAlgo.Robots.Core;
using cAlgo.Robots.Models;
using cAlgo.Robots.Utils;
using cAlgo.Robots.NAS100.Core;
using cAlgo.Robots.NAS100.Models;

namespace cAlgo.Robots;

[Robot(AccessRights = AccessRights.None, AddIndicators = true)]
public class NAS100OpeningEA : Robot
{
    // =========================================
    // PARAMETERS
    // =========================================

    [Parameter("Session Open Hour", DefaultValue = 10, MinValue = 0, MaxValue = 23)]
    public int SessionOpenHour { get; set; }

    [Parameter("Session Open Minute", DefaultValue = 30, MinValue = 0, MaxValue = 59)]
    public int SessionOpenMinute { get; set; }

    [Parameter("Session Window Minutes", DefaultValue = 60, MinValue = 5, MaxValue = 480)]
    public int SessionWindowMinutes { get; set; }

    [Parameter("Max Trades Per Day", DefaultValue = 2, MinValue = 1, MaxValue = 5)]
    public int MaxTradesPerDay { get; set; }

    [Parameter("Volume In Units", DefaultValue = 1.0, MinValue = 0.01)]
    public double VolumeInUnits { get; set; }

    [Parameter("Default SL Pips", DefaultValue = 35, MinValue = 5, MaxValue = 200)]
    public int DefaultSlPips { get; set; }

    [Parameter("Default TP Pips", DefaultValue = 75, MinValue = 5, MaxValue = 500)]
    public int DefaultTpPips { get; set; }

    [Parameter("Min R:R Ratio", DefaultValue = 2.1, MinValue = 1.0, MaxValue = 10.0)]
    public double MinRR { get; set; }

    [Parameter("ATR SL Multiplier", DefaultValue = 0.8, MinValue = 0.1, MaxValue = 5.0)]
    public double AtrSlMultiplier { get; set; }

    [Parameter("Micro Channel Lookback", DefaultValue = 4, MinValue = 2, MaxValue = 20)]
    public int MicroChannelLookback { get; set; }

    [Parameter("Swing Strength", DefaultValue = 5, MinValue = 2, MaxValue = 10)]
    public int SwingStrength { get; set; }

    [Parameter("Trend Lookback", DefaultValue = 50, MinValue = 20, MaxValue = 200)]
    public int TrendLookback { get; set; }

    [Parameter("Show Visual Debug", DefaultValue = true)]
    public bool ShowVisualDebug { get; set; }

    [Parameter("Log Level (0=Debug, 4=Error)", DefaultValue = 1, MinValue = 0, MaxValue = 4)]
    public int LogLevelParam { get; set; }

    [Parameter("UTC Offset Hours", DefaultValue = -3, MinValue = -12, MaxValue = 14)]
    public int UtcOffsetHours { get; set; }

    // =========================================
    // PRIVATE FIELDS
    // =========================================

    private ExponentialMovingAverage _ema20 = null!;
    private AverageTrueRange _atr = null!;

    private SwingPointTracker _swingTracker = null!;
    private TrendDetector _trendDetector = null!;
    private TrendLineManager _tlManager = null!;

    private WedgeDetector _wedgeDetector = null!;
    private MicroChannelDetector _microChannelDetector = null!;
    private HorizontalLevelDetector _horizontalDetector = null!;
    private LevelManager _levelManager = null!;
    private AtrRiskManager _riskManager = null!;

    private DaySession _daySession = null!;
    private Logger _logger = null!;

    private const int MaxTrendLines = 3;

    // =========================================
    // LIFECYCLE
    // =========================================

    protected override void OnStart()
    {
        _logger = new Logger(this, (LogLevel)LogLevelParam);

        _ema20 = Indicators.ExponentialMovingAverage(Bars.ClosePrices, 20);
        _atr   = Indicators.AverageTrueRange(14, MovingAverageType.Exponential);

        _swingTracker      = new SwingPointTracker(SwingStrength);
        _trendDetector     = new TrendDetector(_swingTracker, TrendLookback);
        _tlManager         = new TrendLineManager(_swingTracker, _logger, topN: 3, tlbMinBreakAtr: 0.5, recalcIntervalBars: 6);

        _wedgeDetector        = new WedgeDetector();
        _microChannelDetector = new MicroChannelDetector(MicroChannelLookback);
        _horizontalDetector   = new HorizontalLevelDetector(_swingTracker);
        _levelManager         = new LevelManager(_wedgeDetector, _microChannelDetector, _horizontalDetector);

        _riskManager = new AtrRiskManager(DefaultSlPips, DefaultTpPips, MinRR, AtrSlMultiplier, Symbol.PipSize);

        _daySession = new DaySession(MaxTradesPerDay);
        _daySession.Reset(Server.Time.Date);

        _logger.Info("NAS100OpeningEA started");
    }

    protected override void OnBar()
    {
        if (Bars.Count < 30) return;

        // Day rollover
        if (Server.Time.Date != _daySession.SessionDate)
            _daySession.Reset(Server.Time.Date);

        int closedBarIndex = Bars.Count - 2;
        double atr = _atr.Result[closedBarIndex];
        double ema = _ema20.Result[closedBarIndex];

        _swingTracker.Update(Bars, Bars.Count - 1, atr);
        _trendDetector.Update(Bars, closedBarIndex, ema, atr);
        _tlManager.Update(Bars, closedBarIndex, ema, atr);

        _levelManager.Update(Bars, closedBarIndex, atr,
            _tlManager.ActiveBullLines,
            _tlManager.ActiveBearLines);

        if (ShowVisualDebug)
        {
            DrawTrendLines();
            DrawActiveLevels();
            DrawDebugPanel();
        }
    }

    protected override void OnTick()
    {
        if (!_daySession.CanTrade) return;
        if (!IsInOpeningWindow()) return;

        // Always use the last CLOSED bar's ATR — never the forming bar
        int lastClosed = Bars.Count - 2;
        if (lastClosed < 1) return;

        double atr       = _atr.Result[lastClosed];
        double tolerance = atr * 0.05;

        var levels = _levelManager.GetActiveLevels();

        foreach (var level in levels)
        {
            if (!level.IsActive) continue;

            double touchPrice = level.ReversalDirection == ReversalDirection.Buy
                ? Symbol.Ask
                : Symbol.Bid;

            if (!level.IsTouched(touchPrice, tolerance)) continue;

            var (stopPrice, targetPrice) = _riskManager.Calculate(
                level.ReversalDirection, touchPrice, _atr.Result[lastClosed]);

            var tradeType = level.ReversalDirection == ReversalDirection.Buy
                ? TradeType.Buy
                : TradeType.Sell;

            double slPips = Math.Abs(touchPrice - stopPrice) / Symbol.PipSize;
            double tpPips = Math.Abs(touchPrice - targetPrice) / Symbol.PipSize;

            ExecuteMarketOrder(tradeType, SymbolName, VolumeInUnits, "NAS-Open", slPips, tpPips);

            level.IsActive = false;
            _daySession.RegisterTrade(level.ReversalDirection);

            _logger.Trade($"Order: {tradeType} @ {touchPrice:F2} | SL={stopPrice:F2} TP={targetPrice:F2} | Level={level.LevelType}");

            break;
        }
    }

    protected override void OnStop()
    {
        _logger.Info("NAS100OpeningEA stopped");
    }

    // =========================================
    // HELPERS
    // =========================================

    private bool IsInOpeningWindow()
    {
        var localOpen = new TimeSpan(SessionOpenHour, SessionOpenMinute, 0);
        var utcOpen   = localOpen.Add(TimeSpan.FromHours(-UtcOffsetHours));
        var utcClose  = utcOpen.Add(TimeSpan.FromMinutes(SessionWindowMinutes));
        var now       = Server.Time.TimeOfDay;
        return now >= utcOpen && now <= utcClose;
    }

    // =========================================
    // VISUAL DEBUG
    // =========================================

    private void DrawTrendLines()
    {
        Color[] bullColors = { Color.Lime, Color.Green, Color.DarkGreen };
        for (int i = 0; i < _tlManager.ActiveBullLines.Count && i < MaxTrendLines; i++)
        {
            var tl = _tlManager.ActiveBullLines[i];
            Chart.DrawTrendLine(
                $"BullTL_{i}",
                Bars.OpenTimes[tl.Point1.BarIndex], tl.Point1.Price,
                Bars.OpenTimes[tl.Point2.BarIndex], tl.Point2.Price,
                bullColors[i], i == 0 ? 2 : 1,
                i == 0 ? LineStyle.Solid : LineStyle.Dots);
            var obj = Chart.FindObject($"BullTL_{i}") as ChartTrendLine;
            if (obj != null) obj.ExtendToInfinity = true;
        }

        Color[] bearColors = { Color.Red, Color.DarkRed, Color.Maroon };
        for (int i = 0; i < _tlManager.ActiveBearLines.Count && i < MaxTrendLines; i++)
        {
            var tl = _tlManager.ActiveBearLines[i];
            Chart.DrawTrendLine(
                $"BearTL_{i}",
                Bars.OpenTimes[tl.Point1.BarIndex], tl.Point1.Price,
                Bars.OpenTimes[tl.Point2.BarIndex], tl.Point2.Price,
                bearColors[i], i == 0 ? 2 : 1,
                i == 0 ? LineStyle.Solid : LineStyle.Dots);
            var obj = Chart.FindObject($"BearTL_{i}") as ChartTrendLine;
            if (obj != null) obj.ExtendToInfinity = true;
        }

        for (int i = _tlManager.ActiveBullLines.Count; i < MaxTrendLines; i++)
            Chart.RemoveObject($"BullTL_{i}");
        for (int i = _tlManager.ActiveBearLines.Count; i < MaxTrendLines; i++)
            Chart.RemoveObject($"BearTL_{i}");
    }

    private void DrawActiveLevels()
    {
        // Clear previous level objects
        var levels = _levelManager.GetActiveLevels();

        // Remove stale level lines (simple approach: prefix all with "Level_")
        for (int i = 0; i < 30; i++)
            Chart.RemoveObject($"Level_{i}");

        int idx = 0;
        foreach (var level in levels)
        {
            Color color = level.LevelType switch
            {
                LevelType.Wedge        => Color.Yellow,
                LevelType.MicroChannel => Color.Cyan,
                LevelType.HorizontalSR => Color.White,
                _                      => Color.Gray
            };

            string label = $"{level.LevelType} {level.ReversalDirection}";

            Chart.DrawHorizontalLine($"Level_{idx}", level.TriggerPrice, color, 1, LineStyle.Dots);
            Chart.DrawText($"LevelLbl_{idx}", label, Bars.OpenTimes[Bars.Count - 2],
                level.TriggerPrice, color);

            idx++;
        }
    }

    private void DrawDebugPanel()
    {
        var trend  = _trendDetector.CurrentTrend;
        var levels = _levelManager.GetActiveLevels();
        bool inWindow = IsInOpeningWindow();

        string text =
            $"Trend: {trend.Direction} | Strength: {trend.Strength:F2}\n" +
            $"Active Levels: {levels.Count}\n" +
            $"Trades Today: {_daySession.TradesExecuted} / {MaxTradesPerDay}\n" +
            $"Window: {(inWindow ? "OPEN" : "closed")} ({SessionOpenHour:D2}:{SessionOpenMinute:D2} +{SessionWindowMinutes}min)";

        Chart.DrawStaticText("NAS_DebugPanel", text,
            VerticalAlignment.Top, HorizontalAlignment.Left, Color.White);
    }
}
