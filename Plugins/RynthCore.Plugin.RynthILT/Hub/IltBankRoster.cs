// IltBankRoster.cs - Every character's bank on this server, for the Banking tab's "Characters" table
// and the transfer form's recipient picker. SilentKelpie's (RynthInventory 0.1.1's Banking tab, RynthSuite
// PR 12, ported by hand 2026-10-19); it read RynthILT's bank export from RynthInventory's window and
// moved into RynthILT's own Banking tab on 2026-10-21 (one server's features live in its server plugin).
// The characters are the ones RynthILT keeps balances for on this server: the logged-in one from its
// live state, every other one from its saved hub.json (IltBankExport.ReadSiblings), so every account
// played on this PC with RynthILT loaded. (RynthInventory's version also listed characters Global
// Inventory knew without a balance yet, as "not read yet" rows; those carry no balance and are left out.)
// Pure: RynthILT.Tests covers it.
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>One character's balances as the Banking tab shows them (a copy: the render thread reads it).</summary>
internal sealed record IltBankRosterRow(string Name, bool Current, long Pyreals, long Luminance, long LegendaryKeys, long MythicalKeys,
                                        long EnlightenedCoins, long WeaklyEnlightenedCoins, long PrestigeCoins, DateTime Updated)
{
    public static IltBankRosterRow From(string name, bool current, IltBankState b) =>
        new(name, current, b.Pyreals, b.Luminance, b.LegendaryKeys, b.MythicalKeys, b.EnlightenedCoins, b.WeaklyEnlightenedCoins,
            b.PrestigeCoins, b.LastUpdated);
}

internal static class IltBankRoster
{
    /// <summary>
    /// The roster: the logged-in character first, then by name; a name listed twice keeps the current
    /// character's row (its live state beats its saved file).
    /// </summary>
    public static IltBankRosterRow[] Build(IReadOnlyList<IltBankExport.Row> rows)
    {
        var byName = new Dictionary<string, IltBankRosterRow>(StringComparer.OrdinalIgnoreCase);
        foreach (IltBankExport.Row r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Name)) continue;
            if (byName.TryGetValue(r.Name, out IltBankRosterRow? have) && have.Current && !r.Current) continue;
            byName[r.Name] = IltBankRosterRow.From(r.Name, r.Current, r.Bank);
        }
        var list = new List<IltBankRosterRow>(byName.Values);
        list.Sort((a, b) => a.Current != b.Current ? (a.Current ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list.ToArray();
    }

    /// <summary>The sums over every character with a balance read (one row of the same shape).</summary>
    public static IltBankRosterRow Totals(IReadOnlyList<IltBankRosterRow> roster)
    {
        long py = 0, lum = 0, lk = 0, mk = 0, ec = 0, wec = 0, pc = 0;
        foreach (IltBankRosterRow r in roster)
        {
            if (r.Updated == DateTime.MinValue) continue;
            py += r.Pyreals; lum += r.Luminance; lk += r.LegendaryKeys; mk += r.MythicalKeys;
            ec += r.EnlightenedCoins; wec += r.WeaklyEnlightenedCoins; pc += r.PrestigeCoins;
        }
        return new IltBankRosterRow("Total", false, py, lum, lk, mk, ec, wec, pc, DateTime.MinValue);
    }

    /// <summary>The other characters, by name: the transfer form's recipient picker.</summary>
    public static List<string> Others(IReadOnlyList<IltBankRosterRow> roster)
    {
        var names = new List<string>();
        foreach (IltBankRosterRow r in roster) if (!r.Current) names.Add(r.Name);
        return names;
    }

    /// <summary>"3m ago", "2h ago", "4d ago": how old a saved balance is; "-" when never read.</summary>
    public static string Age(DateTime updatedLocal, DateTime nowLocal)
    {
        if (updatedLocal == DateTime.MinValue) return "-";
        TimeSpan d = nowLocal - updatedLocal;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours}h ago";
        return $"{(int)d.TotalDays}d ago";
    }
}
