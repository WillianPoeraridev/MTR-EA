#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.Robots.NAS100.Models;
using cAlgo.Robots.Core;
using cAlgo.Robots.Models;

namespace cAlgo.Robots.NAS100.Core;

/// <summary>
/// Identifies horizontal S/R zones from swing highs (resistance) and swing lows (support).
/// Groups nearby swings within 0.3 * ATR, returns the 3 most-touched zones per type.
/// </summary>
public class HorizontalLevelDetector
{
    private readonly SwingPointTracker _swingTracker;
    private const int TopZones = 3;

    public HorizontalLevelDetector(SwingPointTracker swingTracker)
    {
        _swingTracker = swingTracker;
    }

    public List<OpeningLevel> Detect(int currentBarIndex, double atr)
    {
        var result = new List<OpeningLevel>();
        if (atr <= 0) return result;

        double groupTolerance = 0.3 * atr;

        var highs = _swingTracker.GetRecentHighs(20);
        var lows  = _swingTracker.GetRecentLows(20);

        // Build resistance zones from swing highs
        var resistanceZones = BuildZones(highs.Select(sp => sp.Price).ToList(), groupTolerance);
        foreach (var zone in resistanceZones.Take(TopZones))
        {
            result.Add(new OpeningLevel
            {
                LevelType = LevelType.HorizontalSR,
                ReversalDirection = ReversalDirection.Sell,
                TriggerPrice = zone.Price,
                BarIndexCreated = currentBarIndex,
                IsActive = true
            });
        }

        // Build support zones from swing lows
        var supportZones = BuildZones(lows.Select(sp => sp.Price).ToList(), groupTolerance);
        foreach (var zone in supportZones.Take(TopZones))
        {
            result.Add(new OpeningLevel
            {
                LevelType = LevelType.HorizontalSR,
                ReversalDirection = ReversalDirection.Buy,
                TriggerPrice = zone.Price,
                BarIndexCreated = currentBarIndex,
                IsActive = true
            });
        }

        return result;
    }

    private List<PriceZone> BuildZones(List<double> prices, double tolerance)
    {
        var zones = new List<PriceZone>();

        foreach (double price in prices)
        {
            var existing = zones.FirstOrDefault(z => Math.Abs(z.Price - price) <= tolerance);
            if (existing != null)
            {
                // Merge: update price to running average, increment touch count
                existing.Price = (existing.Price * existing.TouchCount + price) / (existing.TouchCount + 1);
                existing.TouchCount++;
            }
            else
            {
                zones.Add(new PriceZone { Price = price, TouchCount = 1 });
            }
        }

        // Sort by most-touched descending
        zones.Sort((a, b) => b.TouchCount.CompareTo(a.TouchCount));
        return zones;
    }

    private class PriceZone
    {
        public double Price { get; set; }
        public int TouchCount { get; set; }
    }
}
