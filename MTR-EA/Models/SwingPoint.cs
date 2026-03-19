using System;

namespace cAlgo.Robots.Models;

/// <summary>
/// Type of swing point detected on the chart.
/// </summary>
public enum SwingType
{
    /// <summary>Swing high — local price maximum.</summary>
    High,

    /// <summary>Swing low — local price minimum.</summary>
    Low
}

/// <summary>
/// Represents a confirmed swing high or swing low on the price chart.
/// Used by <see cref="Core.SwingPointTracker"/> to track market structure.
/// </summary>
public class SwingPoint
{
    /// <summary>Index of the bar in the Bars collection.</summary>
    public int BarIndex { get; init; }

    /// <summary>Timestamp of the bar.</summary>
    public DateTime Time { get; init; }

    /// <summary>Price level — High for swing highs, Low for swing lows.</summary>
    public double Price { get; init; }

    /// <summary>Whether this is a swing high or swing low.</summary>
    public SwingType Type { get; init; }

    /// <summary>Number of bars before and after required for confirmation (default 3).</summary>
    public int Strength { get; init; } = 3;

    /// <summary>True only after N subsequent bars have confirmed this swing point.</summary>
    public bool IsConfirmed { get; set; }

    public override string ToString() =>
        $"{Type} @ {Price:F5} [bar {BarIndex}, {(IsConfirmed ? "confirmed" : "pending")}]";
}
