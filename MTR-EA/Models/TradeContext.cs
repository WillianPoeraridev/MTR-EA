#nullable enable

namespace cAlgo.Robots.Models;

/// <summary>
/// Complete snapshot of the market state used to make trading decisions.
/// Assembled fresh on each bar and passed to the decision engine.
/// </summary>
public class TradeContext
{
    /// <summary>Current trend assessment.</summary>
    public TrendState CurrentTrend { get; set; } = new();

    /// <summary>Active MTR setup, or null if none is in progress.</summary>
    public MtrSetup? ActiveSetup { get; set; }

    /// <summary>Current bid-ask spread in price units.</summary>
    public double CurrentSpread { get; set; }

    /// <summary>20-period Average True Range.</summary>
    public double Atr20 { get; set; }

    /// <summary>Current 20-period Exponential Moving Average value.</summary>
    public double Ema20 { get; set; }

    /// <summary>Whether the current time is within the allowed trading session window.</summary>
    public bool IsWithinSession { get; set; }

    /// <summary>True if Barb Wire is detected — overlapping bars near EMA, no-trade zone.</summary>
    public bool IsBarbWire { get; set; }

    /// <summary>Number of consecutive bars in the trend direction.</summary>
    public int ConsecutiveTrendBars { get; set; }

    /// <summary>Price of the most recent confirmed swing high.</summary>
    public double LastSwingHigh { get; set; }

    /// <summary>Price of the most recent confirmed swing low.</summary>
    public double LastSwingLow { get; set; }
}
