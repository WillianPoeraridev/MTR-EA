#nullable enable

using System;

namespace cAlgo.Robots.NAS100.Models;

public enum LevelType
{
    Wedge,
    MicroChannel,
    HorizontalSR
}

public enum ReversalDirection
{
    Buy,
    Sell
}

public class OpeningLevel
{
    public LevelType LevelType { get; set; }
    public ReversalDirection ReversalDirection { get; set; }

    /// <summary>Exact price where the order is triggered.</summary>
    public double TriggerPrice { get; set; }

    public double StopPrice { get; set; }
    public double TargetPrice { get; set; }
    public bool IsActive { get; set; } = true;
    public int BarIndexCreated { get; set; }

    /// <summary>
    /// Returns true when price has touched (or passed) the trigger level.
    /// Sell: price rose to TriggerPrice → currentPrice >= TriggerPrice - tolerance
    /// Buy:  price fell to TriggerPrice → currentPrice <= TriggerPrice + tolerance
    /// </summary>
    public bool IsTouched(double currentPrice, double tolerance)
    {
        if (ReversalDirection == ReversalDirection.Sell)
            return currentPrice >= TriggerPrice - tolerance;

        return currentPrice <= TriggerPrice + tolerance;
    }
}
