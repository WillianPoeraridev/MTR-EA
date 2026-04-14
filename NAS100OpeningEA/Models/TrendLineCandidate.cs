#nullable enable

using cAlgo.Robots.Models;

namespace cAlgo.Robots.Models;

/// <summary>
/// Direction of a trendline candidate.
/// </summary>
public enum TrendLineDirection
{
    /// <summary>Bull trendline connecting swing lows (support).</summary>
    Bull,

    /// <summary>Bear trendline connecting swing highs (resistance).</summary>
    Bear
}

/// <summary>
/// A candidate trendline connecting two swing points, scored by quality criteria.
/// Higher-scoring lines represent trendlines that "more traders see" — more touches,
/// more respect events, longer duration.
/// </summary>
public class TrendLineCandidate
{
    /// <summary>First anchor swing point (older).</summary>
    public SwingPoint Point1 { get; set; } = null!;

    /// <summary>Second anchor swing point (newer).</summary>
    public SwingPoint Point2 { get; set; } = null!;

    /// <summary>Bull (support) or Bear (resistance).</summary>
    public TrendLineDirection Direction { get; set; }

    /// <summary>Price change per bar (positive = ascending).</summary>
    public double Slope { get; set; }

    /// <summary>Composite quality score (0–100).</summary>
    public double Score { get; set; }

    /// <summary>How many swing points touch or nearly touch this line.</summary>
    public int TouchCount { get; set; }

    /// <summary>How many times price reversed near this line (respect events).</summary>
    public int RespectCount { get; set; }

    /// <summary>Bars between Point1 and Point2.</summary>
    public int DurationBars { get; set; }

    /// <summary>Bars since the last touch or respect event.</summary>
    public int BarsSinceLastTouch { get; set; }

    /// <summary>False if expired (no touch in too many bars).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>True if this line has been broken by a close beyond it.</summary>
    public bool IsBroken { get; set; }

    /// <summary>Bar index where the line was broken.</summary>
    public int BrokenAtBarIndex { get; set; }

    /// <summary>How far beyond the line the breaking close was.</summary>
    public double BreakDistance { get; set; }

    /// <summary>Extrapolates the trendline price at the given bar index.</summary>
    public double GetPriceAt(int barIndex)
    {
        int barsDiff = barIndex - Point1.BarIndex;
        return Point1.Price + Slope * barsDiff;
    }
}
