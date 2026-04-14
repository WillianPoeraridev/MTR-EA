#nullable enable

using System;
using cAlgo.API;
using cAlgo.Robots.NAS100.Models;

namespace cAlgo.Robots.NAS100.Core;

/// <summary>
/// Detects micro channel patterns and their breaks.
/// Bull Micro Channel: each bar's Low >= prior bar's Low (staircase up).
/// Bear Micro Channel: each bar's High <= prior bar's High (staircase down).
/// A break of the channel generates a reversal level.
/// </summary>
public class MicroChannelDetector
{
    private readonly int _lookback;
    private const int FreshnessLimit = 3;

    public MicroChannelDetector(int lookback = 4)
    {
        _lookback = Math.Max(2, lookback);
    }

    public OpeningLevel? Detect(Bars bars, int closedBarIndex)
    {
        // Need enough bars
        if (closedBarIndex < _lookback + FreshnessLimit)
            return null;

        // Scan the last FreshnessLimit bars for a recent break
        for (int breakBar = closedBarIndex; breakBar >= closedBarIndex - FreshnessLimit + 1; breakBar--)
        {
            // Check for Bull Micro Channel break at breakBar
            // breakBar closes below prior bar's low → buy reversal
            if (IsBullChannelBreak(bars, breakBar))
            {
                return new OpeningLevel
                {
                    LevelType = LevelType.MicroChannel,
                    ReversalDirection = ReversalDirection.Buy,
                    TriggerPrice = bars.LowPrices[breakBar],
                    BarIndexCreated = closedBarIndex,
                    IsActive = true
                };
            }

            // Check for Bear Micro Channel break at breakBar
            // breakBar closes above prior bar's high → sell reversal
            if (IsBearChannelBreak(bars, breakBar))
            {
                return new OpeningLevel
                {
                    LevelType = LevelType.MicroChannel,
                    ReversalDirection = ReversalDirection.Sell,
                    TriggerPrice = bars.HighPrices[breakBar],
                    BarIndexCreated = closedBarIndex,
                    IsActive = true
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true if the _lookback bars before breakBar formed a Bull Micro Channel
    /// (each Low >= prior Low) and breakBar itself closed below the prior bar's Low.
    /// </summary>
    private bool IsBullChannelBreak(Bars bars, int breakBar)
    {
        if (breakBar < _lookback) return false;

        // Verify the breakBar closes below prior low
        if (bars.ClosePrices[breakBar] >= bars.LowPrices[breakBar - 1])
            return false;

        // Verify the preceding _lookback bars were a bull micro channel
        for (int i = breakBar - _lookback + 1; i <= breakBar - 1; i++)
        {
            if (bars.LowPrices[i] < bars.LowPrices[i - 1])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns true if the _lookback bars before breakBar formed a Bear Micro Channel
    /// (each High <= prior High) and breakBar itself closed above the prior bar's High.
    /// </summary>
    private bool IsBearChannelBreak(Bars bars, int breakBar)
    {
        if (breakBar < _lookback) return false;

        // Verify the breakBar closes above prior high
        if (bars.ClosePrices[breakBar] <= bars.HighPrices[breakBar - 1])
            return false;

        // Verify the preceding _lookback bars were a bear micro channel
        for (int i = breakBar - _lookback + 1; i <= breakBar - 1; i++)
        {
            if (bars.HighPrices[i] > bars.HighPrices[i - 1])
                return false;
        }

        return true;
    }
}
