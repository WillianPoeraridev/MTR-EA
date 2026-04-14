#nullable enable

using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.Robots.NAS100.Models;
using cAlgo.Robots.Core;

namespace cAlgo.Robots.NAS100.Core;

/// <summary>
/// Aggregates levels from WedgeDetector, MicroChannelDetector and HorizontalLevelDetector.
/// Expires levels older than MaxLevelAgeBars bars.
/// </summary>
public class LevelManager
{
    private const int MaxLevelAgeBars = 20;

    private readonly WedgeDetector _wedgeDetector;
    private readonly MicroChannelDetector _microChannelDetector;
    private readonly HorizontalLevelDetector _horizontalDetector;

    private readonly List<OpeningLevel> _levels = new();

    public LevelManager(
        WedgeDetector wedgeDetector,
        MicroChannelDetector microChannelDetector,
        HorizontalLevelDetector horizontalDetector)
    {
        _wedgeDetector = wedgeDetector;
        _microChannelDetector = microChannelDetector;
        _horizontalDetector = horizontalDetector;
    }

    /// <summary>
    /// Called on each closed bar. Refreshes the full level list from all detectors.
    /// Horizontal S/R levels are rebuilt each bar (they represent current swing state).
    /// Wedge and MicroChannel levels are added if fresh, not duplicated if already present.
    /// </summary>
    public void Update(Bars bars, int closedBarIndex, double atr,
        List<cAlgo.Robots.Models.TrendLineCandidate> activeBullLines,
        List<cAlgo.Robots.Models.TrendLineCandidate> activeBearLines)
    {
        // Expire old levels
        _levels.RemoveAll(l => closedBarIndex - l.BarIndexCreated > MaxLevelAgeBars);

        // Rebuild horizontal S/R every bar (reflects latest swings)
        _levels.RemoveAll(l => l.LevelType == LevelType.HorizontalSR);
        var srLevels = _horizontalDetector.Detect(closedBarIndex, atr);
        _levels.AddRange(srLevels);

        // Wedge: add if detected and no active wedge level already present
        if (!_levels.Any(l => l.LevelType == LevelType.Wedge && l.IsActive))
        {
            var wedge = _wedgeDetector.Detect(activeBullLines, activeBearLines, closedBarIndex);
            if (wedge != null)
                _levels.Add(wedge);
        }

        // MicroChannel: add if detected and no active MC level already present
        if (!_levels.Any(l => l.LevelType == LevelType.MicroChannel && l.IsActive))
        {
            var mc = _microChannelDetector.Detect(bars, closedBarIndex);
            if (mc != null)
                _levels.Add(mc);
        }
    }

    /// <summary>Returns only the currently active levels.</summary>
    public List<OpeningLevel> GetActiveLevels() =>
        _levels.Where(l => l.IsActive).ToList();
}
