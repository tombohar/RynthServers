using System;
using System.IO;
using RynthCore.Plugin.RynthDreamWeave;
using RynthCore.Plugin.ServerPack;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// RynthDreamWeave's bank check (Plugins\RynthCore.Plugin.RynthDreamWeave\DreamWeave, 2026-11-02): "/b"
// every minute after the first 20 s, "[BANK] Luminance: N" read into the "bankLuminance" profile value
// for Lum/hr, deposits taken out, its own replies marked for hiding while a typed /b shows, and the
// setting (on by default).
internal static class DreamWeaveTests
{
    // The /b reply as DreamWeave's ShowBankBalances sends it.
    private static readonly string[] Reply =
    {
        "[BANK] Your balances:",
        "[BANK] Pyreals (shared by every character on this account): 12,345,678",
        "[BANK] Luminance: 8,904,758",
        "[BANK] Legendary Keys: 3",
        "[BANK] Promissory Notes: 0",
        "[BANK] Offline Bonus Time: 1h 20m",
        "[BANK] Alternate Character Bonus +25% until Enl 2, Level 150",
        "[BANK] Type /bank for the full list of commands.",
    };

    public static void Register(Runner r)
    {
        r.Add("dreamweave: /b every minute while logged in, not in the first 20 s, not when turned off", Timing);
        r.Add("dreamweave: the /b reply's luminance is read, and every line of it is ours to hide", ReplyRead);
        r.Add("dreamweave: a /b the player types updates the value and its reply shows", TypedShows);
        r.Add("dreamweave: deposits are taken out of the published value", Deposits);
        r.Add("dreamweave: a pasted or quoted bank line is ignored", Pasted);
        r.Add("dreamweave: the setting is on by default and saved", Setting);
        r.Add("dreamweave: the profile value key is the shared bankLuminance", Key);
    }

    private static void Timing()
    {
        var b = new DreamWeaveBank();
        Check.Null(b.CheckDue(0), "nothing before login");
        long t = 1_000_000;
        b.Login(t);
        Check.Null(b.CheckDue(t + 19_000), "not in the first 20 s");
        Check.Eq(b.CheckDue(t + 20_000), "/b", "the first check 20 s after login");
        Check.Null(b.CheckDue(t + 50_000), "not again within the minute");
        Check.Eq(b.CheckDue(t + 80_000), "/b", "a minute later");
        b.OnChatLine("[BANK] Luminance: 10", t + 100_000);
        Check.Null(b.CheckDue(t + 140_000), "a reading starts the minute again");
        Check.Eq(b.CheckDue(t + 160_000), "/b", "a minute after the reading");
        b.CheckEveryMinute = false;
        Check.Null(b.CheckDue(t + 400_000), "off: no checks");
        b.CheckEveryMinute = true;
        Check.Eq(b.CheckDue(t + 400_000), "/b", "on again");
        b.Logout();
        Check.Null(b.CheckDue(t + 900_000), "nothing after logout");
        Check.True(DreamWeaveBank.IsBalanceCommand(" /B ") && DreamWeaveBank.IsBalanceCommand("/bank"), "/b and /bank are the balance command");
        Check.False(DreamWeaveBank.IsBalanceCommand("/b d") || DreamWeaveBank.IsBalanceCommand("/bank deposit"), "a deposit isn't");
    }

    private static void ReplyRead()
    {
        var b = new DreamWeaveBank();
        long t = 1_000_000;
        b.Login(t);
        Check.Null(b.Published, "no value before a reading");
        b.CheckDue(t + 20_000);
        b.NoteTypedCommand();                 // our own /b through the chat bar
        b.Tick();
        long now = t + 20_500;
        foreach (string line in Reply)
            Check.True(b.OnChatLine(line, now += 100), "ours: " + line);
        Check.Eq(b.Luminance, (long?)8_904_758, "luminance with commas");
        Check.Eq(b.Published, (long?)8_904_758, "published as read");
        Check.False(b.OnChatLine("You say, \"hello\"", now), "other chat isn't ours");
        Check.False(b.OnChatLine("[BANK] Luminance: 8,904,800", now + 30_000), "a reply long after the request isn't ours");
        Check.Eq(b.Luminance, (long?)8_904_800, "but it is read");
        Check.True(b.OnChatLine("[12:01:02] [BANK] Luminance: 8,904,900\r\n", now + 30_100) == false && b.Luminance == 8_904_900,
                   "a chat timestamp in front is fine");
        b.Logout();
        Check.Null(b.Published, "logout forgets the reading");
    }

    private static void TypedShows()
    {
        var b = new DreamWeaveBank();
        long t = 1_000_000;
        b.Login(t);
        // The player types /b with no request of ours out: read, and shown.
        b.NoteTypedCommand();
        b.Tick();
        Check.False(b.OnChatLine("[BANK] Your balances:", t + 5_000), "typed: the heading shows");
        Check.False(b.OnChatLine("[BANK] Luminance: 1,000", t + 5_100), "typed: the luminance shows");
        Check.Eq(b.Published, (long?)1_000, "typed: the value updates");

        // Our check goes out, then the player types /b too: the reply is theirs and shows.
        b.CheckDue(t + 80_000);
        b.NoteTypedCommand();                 // ours, through the chat bar
        b.NoteTypedCommand();                 // the player's
        b.Tick();
        Check.False(b.OnChatLine("[BANK] Luminance: 1,500", t + 80_300), "both asked: the reply shows");
        Check.Eq(b.Published, (long?)1_500, "and is read");

        // Our check, and the chat bar never sees our send: still ours.
        b.CheckDue(t + 200_000);
        b.Tick();
        Check.True(b.OnChatLine("[BANK] Luminance: 1,600", t + 200_300), "ours without the chat bar echo");
        // A failed send leaves nothing waiting.
        b.Request(t + 300_000);
        b.NotSent();
        Check.False(b.OnChatLine("[BANK] Luminance: 1,700", t + 300_100), "a failed send: the reply isn't ours");
    }

    private static void Deposits()
    {
        var b = new DreamWeaveBank();
        long t = 1_000_000;
        b.Login(t);
        b.OnChatLine("[BANK] Luminance: 100,000", t);
        b.OnChatLine("[BANK] Luminance: 150,000", t + 60_000);
        Check.Eq(b.Published, (long?)150_000, "earned: the value rises");
        Check.False(b.OnChatLine("[BANK] Deposited 20,000 luminance. Balance: 170,000", t + 70_000), "a deposit line shows");
        Check.Eq(b.Luminance, (long?)170_000, "the deposit's balance is a reading");
        Check.Eq(b.Published, (long?)150_000, "the deposit isn't earned");
        b.OnChatLine("[BANK] Luminance: 180,000", t + 130_000);
        Check.Eq(b.Published, (long?)160_000, "earned after it counts");
        b.OnChatLine("[BANK] Deposited all. Pyreals: 1,000 | Luminance: 230,000 | Keys: 1 | Promissory Notes: 0", t + 140_000);
        Check.Eq(b.MovedIn, 70_000L, "deposit all: what the bank gained since the reading moved in");
        Check.Eq(b.Published, (long?)160_000, "deposit all isn't earned");
        b.OnChatLine("[BANK] Luminance: 30,000", t + 200_000);
        Check.Eq(b.Published, (long?)0, "spending from the bank is a drop (readers don't count drops), never below 0");
        b.OnChatLine("[BANK] Luminance: 35,000", t + 260_000);
        Check.Eq(b.Published, (long?)5_000, "and what comes after it counts");
    }

    private static void Pasted()
    {
        var b = new DreamWeaveBank();
        b.Login(0);
        b.Request(1_000);
        Check.False(b.OnChatLine("Someone tells you, \"[BANK] Luminance: 999,999,999\"", 1_100), "a tell");
        Check.False(b.OnChatLine("[General] Someone says, \"[BANK] Luminance: 999\"", 1_200), "a channel");
        b.OnChatLine("[BANK] Luminance: lots", 1_300);
        Check.Null(b.Luminance, "nothing read");
    }

    private static void Setting()
    {
        string dir = Path.Combine(Path.GetTempPath(), "RynthServerPack.Tests", "dreamweave-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = new ServerPackSettings(Path.Combine(dir, "rynthdreamweave.json"));
            s.Load();
            var b = new DreamWeaveBank();
            b.LoadSettings(s);
            Check.True(b.CheckEveryMinute, "on by default");
            b.CheckEveryMinute = false;
            b.SaveSettings(s);
            var s2 = new ServerPackSettings(s.FilePath);
            s2.Load();
            var b2 = new DreamWeaveBank();
            b2.LoadSettings(s2);
            Check.False(b2.CheckEveryMinute, "off is saved");
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void Key()
    {
        Check.Eq(DreamWeaveBank.LuminanceKey, RynthServerProfile.ValueBankLuminance, "the shared key");
        Check.True(ServerProfileValues.IsValidKey(DreamWeaveBank.LuminanceKey), "a valid profile value key");
        var values = new ServerProfileValues();
        values.Set(DreamWeaveBank.LuminanceKey, 8_904_758);
        string json = ServerPackProfile.BuildJson(new ServerPackInfo("dreamweave", "DreamWeave", new[] { "play.example.test" }, new[] { "DreamWeave" }), "RynthDreamWeave", "0.1.0", true,
                                                  "DreamWeave", values.Snapshot(out _));
        Check.Eq(ServerProfileView.Value(json, RynthServerProfile.ValueBankLuminance, -1), 8_904_758.0, "readers get it from the profile");
    }
}
