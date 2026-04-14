#nullable enable

using System;

namespace cAlgo.Robots.NAS100.Models;

public class DaySession
{
    private readonly int _maxTrades;

    public int TradesExecuted { get; private set; }
    public ReversalDirection? LastTradeDirection { get; private set; }
    public DateTime SessionDate { get; private set; }

    public DaySession(int maxTrades) { _maxTrades = maxTrades; }

    public bool CanTrade => TradesExecuted < _maxTrades;

    public void RegisterTrade(ReversalDirection dir)
    {
        TradesExecuted++;
        LastTradeDirection = dir;
    }

    public void Reset(DateTime newDate)
    {
        TradesExecuted = 0;
        LastTradeDirection = null;
        SessionDate = newDate;
    }
}
