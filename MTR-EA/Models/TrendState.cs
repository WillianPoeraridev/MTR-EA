namespace cAlgo.Robots.Models;

/// <summary>
/// Overall trend direction detected by the system.
/// </summary>
public enum TrendDirection
{
    /// <summary>Bullish trend — higher highs and higher lows.</summary>
    Bull,

    /// <summary>Bearish trend — lower highs and lower lows.</summary>
    Bear,

    /// <summary>No clear trend — trading range.</summary>
    Range
}

/// <summary>
/// Al Brooks "Always In" direction concept.
/// Indicates the side a trader should be on if forced to hold a position at all times.
/// </summary>
public enum AlwaysInDirection
{
    /// <summary>Always-in long.</summary>
    Long,

    /// <summary>Always-in short.</summary>
    Short,

    /// <summary>No clear always-in direction.</summary>
    None
}

/// <summary>
/// Captures the current trend state including direction, strength,
/// and Al Brooks-specific indicators like Always In and Two-Hour Move.
/// </summary>
public class TrendState
{
    /// <summary>Current trend direction.</summary>
    public TrendDirection Direction { get; set; } = TrendDirection.Range;

    /// <summary>Trend strength from 0.0 (weak) to 1.0 (strong), based on Brooks' Signs of Strength.</summary>
    public double Strength { get; set; }

    /// <summary>Number of bars since the current trend was first detected.</summary>
    public int BarsSinceTrendStart { get; set; }

    /// <summary>Count of aligned swing points (HH+HL for bull, LH+LL for bear).</summary>
    public int SwingCount { get; set; }

    /// <summary>Two-Hour Move: market has been 2+ hours without touching the EMA.</summary>
    public bool IsTwoHourMove { get; set; }

    /// <summary>Brooks' Always In direction — Long, Short, or None.</summary>
    public AlwaysInDirection AlwaysIn { get; set; } = AlwaysInDirection.None;

    public override string ToString() =>
        $"Trend: {Direction} (strength={Strength:F2}, swings={SwingCount}, AI={AlwaysIn})";
}
