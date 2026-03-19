#nullable enable

using cAlgo.API;
using cAlgo.Robots.Core;
using cAlgo.Robots.Models;
using cAlgo.Robots.Utils;

namespace cAlgo.Robots;

[Robot(AccessRights = AccessRights.None, AddIndicators = true)]
public class MTREA : Robot
{
    [Parameter("Swing Strength", DefaultValue = 3, MinValue = 1, MaxValue = 10)]
    public int SwingStrength { get; set; }

    [Parameter("Log Level (0=Debug, 4=Error)", DefaultValue = 1, MinValue = 0, MaxValue = 4)]
    public int LogLevelParam { get; set; }

    private SwingPointTracker _swingTracker = null!;
    private Logger _logger = null!;

    protected override void OnStart()
    {
        _logger = new Logger(this, (LogLevel)LogLevelParam);
        _swingTracker = new SwingPointTracker(SwingStrength);
        _logger.Info("MTR-EA started — Phase 1 (Foundation)");
    }

    protected override void OnBar()
    {
        // Need at least enough bars for swing detection
        if (Bars.Count < SwingStrength * 2 + 2)
            return;

        // Create BarInfo for the bar that just closed (second-to-last bar)
        var closedBarIndex = Bars.Count - 2;
        BarInfo? previousBar = closedBarIndex > 0
            ? BarInfo.FromBars(Bars, closedBarIndex - 1)
            : null;
        var currentBar = BarInfo.FromBars(Bars, closedBarIndex, previousBar);

        // Update swing points
        _swingTracker.Update(Bars, Bars.Count - 1);

        var lastHigh = _swingTracker.LastSwingHigh;
        var lastLow = _swingTracker.LastSwingLow;

        if (lastHigh != null)
            _logger.Debug($"Last SH: {lastHigh.Price:F5} at bar {lastHigh.BarIndex}");
        if (lastLow != null)
            _logger.Debug($"Last SL: {lastLow.Price:F5} at bar {lastLow.BarIndex}");

        // Log swing structure
        if (_swingTracker.HasHigherHighs() && _swingTracker.HasHigherLows())
            _logger.Debug("Swing structure: BULL (HH + HL)");
        else if (_swingTracker.HasLowerHighs() && _swingTracker.HasLowerLows())
            _logger.Debug("Swing structure: BEAR (LH + LL)");
    }

    protected override void OnStop()
    {
        _logger.Info("MTR-EA stopped");
    }
}
