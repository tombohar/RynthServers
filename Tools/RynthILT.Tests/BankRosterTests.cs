using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// The Banking tab's every-character table (Hub/IltBankRoster.cs): SilentKelpie's Banking tab, in
// RynthInventory's window until 2026-10-21 (its tests came with it, adapted: the roster is RynthILT's own
// characters now, without Global Inventory's "not read yet" rows) and RynthILT's own tab since.
internal static class BankRosterTests
{
    public static void Register(Runner r)
    {
        r.Add("bank roster: you first, then by name; the totals add every read balance", Roster);
        r.Add("bank roster: the Banking tab lists every character RynthILT has run on this server", FromSavedCharacters);
    }

    private static IltBankState Bank(long py, long lum, long lk, long mk, long ec, long wec, long pc, DateTime updated) => new()
    {
        Pyreals = py, Luminance = lum, LegendaryKeys = lk, MythicalKeys = mk,
        EnlightenedCoins = ec, WeaklyEnlightenedCoins = wec, PrestigeCoins = pc, LastUpdated = updated,
    };

    private static void Roster()
    {
        DateTime t = new(2026, 10, 3, 11, 0, 0, DateTimeKind.Local);
        var rows = new List<IltBankExport.Row>
        {
            new("Willow", false, Bank(1, 1, 0, 0, 0, 0, 0, t.AddDays(-1))),
            new("Buffy", true, Bank(100, 2000, 1, 0, 3, 0, 0, t)),
            new("Ghost", false, Bank(50, 0, 2, 1, 0, 4, 5, t.AddDays(-2))),
            new("buffy", false, Bank(999, 999, 9, 9, 9, 9, 9, t.AddDays(-9))),   // a stale saved copy of you
            new("Never", false, Bank(7, 7, 7, 7, 7, 7, 7, DateTime.MinValue)),    // never read: not in the totals
        };
        IltBankRosterRow[] roster = IltBankRoster.Build(rows);
        Check.Eq(string.Join(",", roster.Select(r => r.Name)), "Buffy,Ghost,Never,Willow", "you first, then by name; your saved copy doesn't replace you");
        Check.True(roster[0].Current && roster[0].Pyreals == 100, "your live balance");
        IltBankRosterRow total = IltBankRoster.Totals(roster);
        Check.True(total.Name == "Total" && total.Pyreals == 151 && total.Luminance == 2001 && total.LegendaryKeys == 3 && total.MythicalKeys == 1
                   && total.EnlightenedCoins == 3 && total.WeaklyEnlightenedCoins == 4 && total.PrestigeCoins == 5,
                   "totals add every balance read (a never-read one isn't counted)");
        Check.Eq(string.Join(",", IltBankRoster.Others(roster)), "Ghost,Never,Willow", "the transfer form's picker: your other characters");
        Check.Eq(IltBankRoster.Build(new List<IltBankExport.Row>()).Length, 0, "nothing: an empty roster");
        Check.Eq(IltBankRoster.Age(DateTime.MinValue, t), "-", "never read");
        Check.Eq(IltBankRoster.Age(t.AddMinutes(-5), t), "5m ago", "minutes");
        Check.Eq(IltBankRoster.Age(t.AddHours(-3), t), "3h ago", "hours");
        Check.Eq(IltBankRoster.Age(t.AddDays(-2), t), "2d ago", "days");
    }

    private static void FromSavedCharacters()
    {
        FakeHost.Reset();
        FakeAiHub.Reset();
        FakeHost.WorldName = "InfiniteLeaftide";
        var host = FakeHost.Create(0x50000D41);
        string world = Path.Combine(Program.TempRoot, "roster-world");
        if (Directory.Exists(world)) Directory.Delete(world, recursive: true);
        // Two other characters RynthILT has run here (their saved hub.json), one that never banked.
        foreach ((string name, long py, bool banked) in new[] { ("Ghost", 50L, true), ("Willow", 7L, true), ("Nobank", 0L, false) })
        {
            string dir = Path.Combine(world, name);
            Directory.CreateDirectory(dir);
            var state = new IltHubState();
            state.Bank.Pyreals = py;
            if (banked) state.Bank.LastUpdated = DateTime.Now.AddHours(-2);
            File.WriteAllText(Path.Combine(dir, IltHubStore.StateFile), JsonSerializer.Serialize(state, IltHubJsonContext.Default.IltHubState));
        }
        string me = Path.Combine(world, "Buffy");
        Directory.CreateDirectory(me);
        var hub = new IltHubController(host, me, new IltSettings(), new RynthAiClient(host));
        hub.OnLoginComplete("Buffy", forceFromMigration: false);
        hub.State.Bank.Pyreals = 100;
        hub.State.Bank.LastUpdated = DateTime.Now;

        hub.Tick();
        Check.Eq(hub.Banking.Roster.Length, 0, "nothing is read while the table isn't on screen");
        hub.Banking.MarkRosterWatched();
        hub.Tick();
        IltBankRosterRow[] roster = hub.Banking.Roster;
        Check.Eq(string.Join(",", roster.Select(r => r.Name)), "Buffy,Ghost,Willow", "you and the characters with a saved balance");
        Check.True(roster[0].Current && roster[0].Pyreals == 100 && roster[1].Pyreals == 50, "live and saved balances");
        Check.Eq(IltBankRoster.Totals(roster).Pyreals, 157L, "the total");
    }
}
