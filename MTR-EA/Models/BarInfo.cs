#nullable enable

using System;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots.Models;

/// <summary>
/// Wrapper around a single price bar with pre-computed analytical properties.
/// Encapsulates all bar-by-bar calculations used in Al Brooks price action analysis.
/// </summary>
public class BarInfo
{
    private const double DefaultDojiThreshold = 0.30;

    /// <summary>Bar index in the Bars collection.</summary>
    public int Index { get; init; }

    /// <summary>Bar open time.</summary>
    public DateTime OpenTime { get; init; }

    /// <summary>Open price.</summary>
    public double Open { get; init; }

    /// <summary>High price.</summary>
    public double High { get; init; }

    /// <summary>Low price.</summary>
    public double Low { get; init; }

    /// <summary>Close price.</summary>
    public double Close { get; init; }

    /// <summary>Total bar range (High - Low).</summary>
    public double Range { get; init; }

    /// <summary>Absolute body size |Close - Open|.</summary>
    public double Body { get; init; }

    /// <summary>Body as a fraction of range (0.0 to 1.0). 0 if range is zero.</summary>
    public double BodyPercent { get; init; }

    /// <summary>Upper tail/wick: High - Max(Open, Close).</summary>
    public double UpperTail { get; init; }

    /// <summary>Lower tail/wick: Min(Open, Close) - Low.</summary>
    public double LowerTail { get; init; }

    /// <summary>Upper tail as a fraction of range (0.0 to 1.0).</summary>
    public double UpperTailPercent { get; init; }

    /// <summary>Lower tail as a fraction of range (0.0 to 1.0).</summary>
    public double LowerTailPercent { get; init; }

    /// <summary>Midpoint of the bar: (High + Low) / 2.</summary>
    public double MidPoint { get; init; }

    /// <summary>True if Close > Open (bullish bar).</summary>
    public bool IsBull { get; init; }

    /// <summary>True if Close &lt; Open (bearish bar).</summary>
    public bool IsBear { get; init; }

    /// <summary>True if body is less than the doji threshold of the range.</summary>
    public bool IsDoji { get; init; }

    /// <summary>True if this bar engulfs the previous bar (High > prevHigh AND Low &lt; prevLow).</summary>
    public bool IsOutsideBar { get; init; }

    /// <summary>True if this bar is contained within the previous bar (High &lt; prevHigh AND Low > prevLow).</summary>
    public bool IsInsideBar { get; init; }

    /// <summary>
    /// Position of the Close within the bar's range: 0.0 = at the Low, 1.0 = at the High.
    /// Returns 0.5 for zero-range (doji) bars.
    /// </summary>
    public double ClosePosition { get; init; }

    /// <summary>
    /// Creates a <see cref="BarInfo"/> from a cTrader Bars collection at the specified index.
    /// </summary>
    /// <param name="bars">The cTrader Bars data series.</param>
    /// <param name="index">Bar index to analyze.</param>
    /// <param name="previous">Previous bar info, used to compute IsOutsideBar/IsInsideBar. Null for the first bar.</param>
    /// <param name="dojiThreshold">Body/Range threshold below which a bar is considered a doji (default 0.30).</param>
    /// <returns>A fully computed <see cref="BarInfo"/> instance.</returns>
    public static BarInfo FromBars(Bars bars, int index, BarInfo? previous = null, double dojiThreshold = DefaultDojiThreshold)
    {
        var open = bars.OpenPrices[index];
        var high = bars.HighPrices[index];
        var low = bars.LowPrices[index];
        var close = bars.ClosePrices[index];
        var openTime = bars.OpenTimes[index];

        var range = high - low;
        var body = Math.Abs(close - open);
        var bodyPercent = range > 0 ? body / range : 0.0;

        var upperTail = high - Math.Max(open, close);
        var lowerTail = Math.Min(open, close) - low;
        var upperTailPercent = range > 0 ? upperTail / range : 0.0;
        var lowerTailPercent = range > 0 ? lowerTail / range : 0.0;

        var midPoint = (high + low) / 2.0;
        var closePosition = range > 0 ? (close - low) / range : 0.5;

        var isBull = close > open;
        var isBear = close < open;
        var isDoji = bodyPercent < dojiThreshold;

        var isOutsideBar = previous != null && high > previous.High && low < previous.Low;
        var isInsideBar = previous != null && high < previous.High && low > previous.Low;

        return new BarInfo
        {
            Index = index,
            OpenTime = openTime,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            Range = range,
            Body = body,
            BodyPercent = bodyPercent,
            UpperTail = upperTail,
            LowerTail = lowerTail,
            UpperTailPercent = upperTailPercent,
            LowerTailPercent = lowerTailPercent,
            MidPoint = midPoint,
            IsBull = isBull,
            IsBear = isBear,
            IsDoji = isDoji,
            IsOutsideBar = isOutsideBar,
            IsInsideBar = isInsideBar,
            ClosePosition = closePosition
        };
    }

    public override string ToString() =>
        $"Bar[{Index}] {(IsBull ? "BULL" : IsBear ? "BEAR" : "DOJI")} O={Open:F5} H={High:F5} L={Low:F5} C={Close:F5} Body={BodyPercent:P0}";
}
