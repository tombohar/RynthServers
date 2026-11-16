// DreamWeaveXp.cs - DreamWeave's level chart past retail's 275, for the Skills panel's
// "XP for next level".
//
// The retail client reads the next level's total from portal.dat's XpTable, which ends at level 275,
// so on DreamWeave (levels past 275 for enlightened characters) AC's own Character panel subtracts the
// player's total from the level-275 total and shows a NEGATIVE number (a level-344 character with
// 600,303,541,190 XP reads -409,077,230,943). DreamWeave's server extends the chart itself:
// ACE-DreamWeave Source/ACE.Server/Entity/EnlightenmentXpCurve.cs (public, github.com/Awful-Waffle-Rofl/
// ACE-DreamWeave, read 2026-11-07) grows each level past 275 by the ratio of the chart's last two steps,
// as an exact fraction, until the total would pass long.MaxValue / 2. This is that rule, re-implemented
// (no code copied), so the panel reads what the server will ask for.
//
// Pure: RynthServerPack.Tests checks it against the server's numbers.
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthDreamWeave;

internal static class DreamWeaveXp
{
    /// <summary>The server's ceiling for a chart total: half of long.MaxValue.</summary>
    public const ulong TotalCap = (ulong)(long.MaxValue / 2);

    private static readonly Lazy<ulong[]> Chart = new(() => Extend(RetailData.LevelTotals, TotalCap), true);

    /// <summary>Total XP to reach each level (index = level), retail's 0..275 then DreamWeave's tail.</summary>
    public static IReadOnlyList<ulong> Totals => Chart.Value;

    /// <summary>The highest level the chart reaches.</summary>
    public static int MaxLevel => Chart.Value.Length - 1;

    /// <summary>
    /// <paramref name="retail"/> followed by levels whose step grows by last step / step before it (an exact
    /// fraction; a flat or shrinking tail repeats its last step), never below the step before, until the next
    /// total would pass <paramref name="cap"/>.
    /// </summary>
    internal static ulong[] Extend(IReadOnlyList<ulong> retail, ulong cap)
    {
        var chart = new List<ulong>(retail);
        if (chart.Count < 3) return chart.ToArray();
        int last = chart.Count - 1;
        ulong step = chart[last] - chart[last - 1];
        ulong before = chart[last - 1] - chart[last - 2];
        ulong num = step, den = before;
        if (den == 0 || num <= den) num = den = 1;
        if (step == 0) return chart.ToArray();
        ulong total = chart[last];
        while (true)
        {
            ulong next = (ulong)((UInt128)step * num / den);
            if (next < step) next = step;
            if (next > cap - total) break;
            total += next;
            step = next;
            chart.Add(total);
        }
        return chart.ToArray();
    }

    /// <summary>
    /// The XP still needed for the next level, as the panel's "XP for next level" line: null at the
    /// chart's top (retail's panel says "Infinity!" there) or when the level is unknown. A total already
    /// past the next level's (the server levels the character on its next award) reads 0.
    /// </summary>
    public static long? ToNextLevel(int level, long totalXp) => ToNextLevel(Totals, level, totalXp);

    internal static long? ToNextLevel(IReadOnlyList<ulong> chart, int level, long totalXp)
    {
        if (level < 1 || totalXp < 0 || level + 1 >= chart.Count) return null;
        ulong next = chart[level + 1];
        ulong have = (ulong)totalXp;
        return have >= next ? 0 : (long)(next - have);
    }
}
