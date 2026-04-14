#nullable enable

using System;

namespace cAlgo.Robots.NAS100.Models;

public class DaySession
{
    public int TradesExecuted { get; private set; }
    public ReversalDirection? LastTradeDirection { get; private set; }
    public DateTime SessionDate { get; private set; }

    public bool CanTrade => TradesExecuted < 2;

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
