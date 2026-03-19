using System;
using System.Collections.Generic;

namespace cAlgo.Robots.Models;

/// <summary>
/// Direction of the MTR reversal setup.
/// </summary>
public enum MtrDirection
{
    /// <summary>Reversing from bear to bull.</summary>
    BullReversal,

    /// <summary>Reversing from bull to bear.</summary>
    BearReversal
}

/// <summary>
/// State machine states for the MTR setup lifecycle.
/// </summary>
public enum MtrState
{
    /// <summary>Scanning for a trend to reverse.</summary>
    Scanning,

    /// <summary>Strong trend detected — watching for exhaustion.</summary>
    Trending,

    /// <summary>Trend Line Break occurred — key trigger.</summary>
    TlbActive,

    /// <summary>Testing the old extreme (overshoot, undershoot, or double).</summary>
    Testing,

    /// <summary>Setup triggered — waiting for entry confirmation.</summary>
    Triggered,

    /// <summary>Position is open.</summary>
    InTrade,

    /// <summary>Cooldown period after exit.</summary>
    Cooldown
}

/// <summary>
/// How the market tests the old trend extreme after TLB.
/// </summary>
public enum TestType
{
    /// <summary>Test falls short of the old extreme.</summary>
    Undershoot,

    /// <summary>Test exceeds the old extreme (but reverses).</summary>
    Overshoot,

    /// <summary>Test reaches approximately the same price as the old extreme.</summary>
    DoubleExact,

    /// <summary>No test detected yet.</summary>
    None
}

/// <summary>
/// Represents a complete Major Trend Reversal setup as defined by Al Brooks.
/// Tracks the full lifecycle from detection through entry, including quality scoring.
/// </summary>
public class MtrSetup
{
    /// <summary>Whether this is a bull or bear reversal.</summary>
    public MtrDirection Direction { get; set; }

    /// <summary>Current state in the MTR lifecycle state machine.</summary>
    public MtrState CurrentState { get; set; } = MtrState.Scanning;

    /// <summary>Bar index where the Trend Line Break occurred.</summary>
    public int TlbBarIndex { get; set; }

    /// <summary>Strength of the TLB from 0.0 to 1.0, based on Brooks' criteria.</summary>
    public double TlbStrength { get; set; }

    /// <summary>How the market tested the old extreme.</summary>
    public TestType TestType { get; set; } = TestType.None;

    /// <summary>Bar index where the test of the old extreme occurred.</summary>
    public int TestBarIndex { get; set; }

    /// <summary>Price of the old trend extreme being tested.</summary>
    public double OldExtremePrice { get; set; }

    /// <summary>Price reached during the test of the old extreme.</summary>
    public double TestPrice { get; set; }

    /// <summary>Bar index of the signal bar (entry trigger).</summary>
    public int SignalBarIndex { get; set; }

    /// <summary>Quality score of the signal bar from 0 to 100.</summary>
    public double SignalBarScore { get; set; }

    /// <summary>Number of reasons to enter the trade (Brooks: minimum 2).</summary>
    public int ReasonCount { get; set; }

    /// <summary>Textual list of detected reasons supporting the trade.</summary>
    public List<string> Reasons { get; set; } = new();

    /// <summary>Calculated entry price.</summary>
    public double EntryPrice { get; set; }

    /// <summary>Calculated stop loss price.</summary>
    public double StopPrice { get; set; }

    /// <summary>Calculated take profit price.</summary>
    public double TargetPrice { get; set; }

    /// <summary>Timestamp when the setup was first detected.</summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>Number of bars since the last state transition.</summary>
    public int BarsSinceStateChange { get; set; }

    /// <summary>True if this is a second entry (H2/L2) — more reliable per Brooks.</summary>
    public bool IsSecondEntry { get; set; }

    /// <summary>How many entry attempts have failed (max 2 before abandoning).</summary>
    public int FailedAttempts { get; set; }

    public override string ToString() =>
        $"MTR {Direction} [{CurrentState}] — reasons={ReasonCount}, score={SignalBarScore:F0}";
}
