using System;
using RynthCore.Plugin.RynthDreamWeave;
using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// RynthDreamWeave's offline bonus tracker (Plugins\RynthCore.Plugin.RynthDreamWeave\DreamWeave\DreamWeaveXpBonus.cs,
// 2026-11-15): every line the server sends about it (worded as ACE-DreamWeave's source of 2026-11-02 writes them),
// every duration format, its own /offlinebonus's reply hidden while a typed one shows, the automatic read's timing,
// the local clock between readings, and the reading kept on disk.
internal static class DreamWeaveXpBonusTests
{
    private const int Broadcast = 0x00, System = 0x05, Tell = 0x03;
    private static readonly DateTime Utc = new(2026, 11, 15, 17, 0, 0, DateTimeKind.Utc);

    private const string Spent = "Offline experience bonus: +100% to your combat experience and Luminance for the next 48m 31s, currently being SPENT (draining while you fight).";
    private const string Banking = "Offline experience bonus: +100% to your combat experience and Luminance for the next 3h 10m, currently BANKING (idle for longer than the 5m 0s timeout, so it's growing again instead of draining).";
    private const string NoneBanked = "You have no offline experience bonus banked. Log this character out to bank bonus time while you play others.";
    private const string Disabled = "The offline experience bonus is not enabled on this server.";

    public static void Register(Runner r)
    {
        r.Add("dw xpbonus: every duration format is read, and written as the server writes it", Durations);
        r.Add("dw xpbonus: the /offlinebonus replies (spent, banking, active, other percent, none banked, not enabled)", StatusReplies);
        r.Add("dw xpbonus: the pushes (now active, banking again, exhausted, welcome back)", Pushes);
        r.Add("dw xpbonus: the [BANK] line and /bonus lines keep the state; two a minute apart can set it", RemainingOnly);
        r.Add("dw xpbonus: player chat and a quoted copy are not read", NotRead);
        r.Add("dw xpbonus: only the reply to its own request is hidden; typed and pushed lines show", OursAndTyped);
        r.Add("dw xpbonus: automatic read: off by default, 35 s after login, every 10 min, only when quiet", AutoTiming);
        r.Add("dw xpbonus: Check now goes at the next quiet moment, the automatic read on or off", CheckNow);
        r.Add("dw xpbonus: the clock counts down while active, up while banking (to the cap), stops at 0", Clock);
        r.Add("dw xpbonus: the tracker's line and how old the reading is", Lines);
        r.Add("dw xpbonus: the reading is saved and read back (a newer live one kept, broken data left out)", Saved);
        r.Add("dw xpbonus: the bank's and the boards' next command times, for the gap", NextTimes);
    }

    private static DreamWeaveXpBonus Logged(out long t)
    {
        var x = new DreamWeaveXpBonus();
        t = 1_000_000;
        x.Login(t);
        return x;
    }

    private static void Durations()
    {
        void Is(string text, double seconds, double res)
        {
            Check.True(DreamWeaveXpBonus.TryDuration(text, out double s, out double r), $"'{text}' is read");
            Check.Eq(s, seconds, $"'{text}' = {seconds} s");
            Check.Eq(r, res, $"'{text}' is shown to {res} s");
        }
        Is("3h 10m", 3 * 3600 + 600, 60);
        Is("37h 5m", 37 * 3600 + 300, 60);      // a long absence: total hours
        Is("24h 0m", 86_400, 60);
        Is("48m 31s", 48 * 60 + 31, 1);
        Is("1m 0s", 60, 1);
        Is("12s", 12, 1);
        Is("0s", 0, 1);
        Is("5m", 300, 60);
        Is("2h", 7200, 3600);
        foreach (string bad in new[] { "", "abc", "3h10m", "48m31s", "-5s", "3 h", "1d 2h", "3h 10m 5s" })
            Check.False(DreamWeaveXpBonus.TryDuration(bad, out _, out _), $"'{bad}' is not a duration");

        Check.Eq(DreamWeaveXpBonus.Format(2911), "48m 31s", "minutes and seconds");
        Check.Eq(DreamWeaveXpBonus.Format(11_400), "3h 10m", "hours and minutes");
        Check.Eq(DreamWeaveXpBonus.Format(11_459.9), "3h 10m", "seconds are dropped past an hour");
        Check.Eq(DreamWeaveXpBonus.Format(133_500), "37h 5m", "total hours");
        Check.Eq(DreamWeaveXpBonus.Format(12.7), "12s", "seconds, rounded down");
        Check.Eq(DreamWeaveXpBonus.Format(-3), "0s", "never negative");
        Check.Eq(DreamWeaveXpBonus.Format(60), "1m 0s", "a minute");
    }

    private static void StatusReplies()
    {
        DreamWeaveXpBonus x = Logged(out long t);
        x.OnChatText(Spent, Broadcast, t, Utc);
        Check.Eq(x.State, XpBonusState.Active, "SPENT = active");
        Check.Eq(x.Seconds, 2911.0, "48m 31s");
        Check.Eq(x.Percent, 100, "+100%");
        Check.Eq(x.LastLine, XpBonusLine.Status, "a status line");

        x.OnChatText(Banking, Broadcast, t + 1000, Utc);
        Check.Eq(x.State, XpBonusState.Banking, "BANKING = idle");
        Check.Eq(x.Seconds, 11_400.0, "3h 10m");

        x.OnChatText("Offline experience bonus: +100% to your combat experience and Luminance for the next 20m 0s, ACTIVE (draining).", Broadcast, t + 2000, Utc);
        Check.Eq(x.State, XpBonusState.Active, "an ACTIVE wording is active too");
        Check.Eq(x.Seconds, 1200.0, "20m 0s");

        x.OnChatText("Offline experience bonus: +50% to your combat experience and Luminance for the next 5m, currently BANKING.", Broadcast, t + 3000, Utc);
        Check.Eq(x.Percent, 50, "another percent");
        Check.Eq(x.Seconds, 300.0, "a bare minutes duration");
        Check.Eq(x.Title, "XP bonus +50%", "titled with its percent");

        x.OnChatText("Offline experience bonus: +100% to your combat experience and Luminance for the next 9s, doing something new.", Broadcast, t + 4000, Utc);
        Check.Eq(x.State, XpBonusState.Banking, "an unknown state wording keeps the state");
        Check.Eq(x.Seconds, 9.0, "and still reads the time");

        x.OnChatText(NoneBanked, Broadcast, t + 5000, Utc);
        Check.Eq(x.State, XpBonusState.Empty, "none banked");
        Check.Eq(x.Seconds, 0.0, "0 s");
        Check.Eq(x.Percent, 100, "the percent stays");

        x.OnChatText(Disabled, Broadcast, t + 6000, Utc);
        Check.Eq(x.State, XpBonusState.Disabled, "not enabled");
        Check.Eq(x.Line(t + 6000), "Offline XP bonus: not enabled on this server", "the not-enabled line");

        // With a chat timestamp in front (as RynthCore's chat timestamps put it).
        x.OnChatText("[17:01:05] " + Spent, Broadcast, t + 7000, Utc);
        Check.Eq(x.State, XpBonusState.Active, "read after a timestamp");
    }

    private static void Pushes()
    {
        DreamWeaveXpBonus x = Logged(out long t);
        x.OnChatText("Welcome back - you were away for 37h 5m. Your offline experience bonus bank has been increased to 24h 0m.", Broadcast, t, Utc);
        Check.Eq(x.LastLine, XpBonusLine.Welcome, "welcome back");
        Check.Eq(x.State, XpBonusState.Banking, "a fresh login is idle");
        Check.Eq(x.Seconds, 86_400.0, "the bank, not the time away");

        x.OnChatText("Your offline experience bonus is now active - 23h 59m remaining.", Broadcast, t + 60_000, Utc);
        Check.Eq(x.LastLine, XpBonusLine.Activated, "now active");
        Check.Eq(x.State, XpBonusState.Active, "active");
        Check.Eq(x.Seconds, 86_340.0, "23h 59m");

        x.OnChatText("Your offline experience bonus is now idle and banking again - 48m 31s stored.", Broadcast, t + 120_000, Utc);
        Check.Eq(x.LastLine, XpBonusLine.Banking, "banking again");
        Check.Eq(x.State, XpBonusState.Banking, "idle");
        Check.Eq(x.Seconds, 2911.0, "48m 31s");

        x.OnChatText("Your offline experience bonus is now idle and banking again - 0s stored.", Broadcast, t + 130_000, Utc);
        Check.Eq(x.State, XpBonusState.Banking, "banking from an empty bank");
        Check.Eq(x.Seconds, 0.0, "0s stored");
        Check.Eq(x.Remaining(t + 140_000), 10.0, "and it counts up from 0");

        x.OnChatText("Your offline experience bonus is now active - 12s remaining.", Broadcast, t + 150_000, Utc);
        x.OnChatText("Your offline experience bonus is exhausted.", Broadcast, t + 162_000, Utc);
        Check.Eq(x.LastLine, XpBonusLine.Exhausted, "exhausted");
        Check.Eq(x.State, XpBonusState.Empty, "nothing left");
        Check.Eq(x.Seconds, 0.0, "0 s");
    }

    private static void RemainingOnly()
    {
        DreamWeaveXpBonus x = Logged(out long t);
        x.OnChatText("[BANK] Offline Bonus Time: 48m 31s", System, t, Utc);
        Check.Eq(x.LastLine, XpBonusLine.Bank, "the [BANK] line");
        Check.Eq(x.State, XpBonusState.Unknown, "no state from one /b");
        Check.Eq(x.Seconds, 2911.0, "its time");
        Check.False(x.Running, "the clock doesn't run without a state");
        Check.Eq(x.Line(t + 30_000), "Double XP: 48m 31s banked", "the time, no state");

        // A minute later, a minute less: being spent.
        x.OnChatText("[BANK] Offline Bonus Time: 47m 31s", System, t + 60_000, Utc);
        Check.Eq(x.State, XpBonusState.Active, "two readings going down = active");
        x.OnChatText("[BANK] Offline Bonus Time: 48m 31s", System, t + 120_000, Utc);
        Check.Eq(x.State, XpBonusState.Banking, "going up = banking");
        Check.Eq(x.Seconds, 2911.0, "48m 31s");

        // A push in between starts the comparison again: the next /b doesn't undo it.
        x.OnChatText("Your offline experience bonus is now active - 48m 11s remaining.", Broadcast, t + 140_000, Utc);
        x.OnChatText("[BANK] Offline Bonus Time: 47m 31s", System, t + 180_000, Utc);
        Check.Eq(x.State, XpBonusState.Active, "the push's state holds");
        x.OnChatText("[BANK] Offline Bonus Time: 47m 21s", System, t + 190_000, Utc);
        Check.Eq(x.State, XpBonusState.Active, "readings 10 s apart don't set a state");

        x.OnChatText("[BANK] Offline Bonus Time: none", System, t + 200_000, Utc);
        Check.Eq(x.State, XpBonusState.Empty, "none = empty");
        x.OnChatText("[BANK] Offline Bonus Time: 15s", System, t + 215_000, Utc);
        Check.Eq(x.State, XpBonusState.Banking, "an empty bank that grows is banking");

        // /bonus: the percent and the time.
        DreamWeaveXpBonus y = Logged(out t);
        y.OnChatText("Your offline experience bonus is now active - 3h 10m remaining.", Broadcast, t, Utc);
        y.OnChatText("  Offline bonus: +100% (3h 9m remaining)", Broadcast, t + 30_000, Utc);
        Check.Eq(y.LastLine, XpBonusLine.Summary, "the /bonus line");
        Check.Eq(y.Percent, 100, "its percent");
        Check.Eq(y.State, XpBonusState.Active, "the state stays");
        Check.Near(y.Seconds, 11_370, 0.01, "3h 9m read inside the running clock keeps the clock's seconds (11400 - 30)");
        y.OnChatText("  Offline bonus: +0% (none banked)", Broadcast, t + 40_000, Utc);
        Check.Eq(y.State, XpBonusState.Empty, "none banked");
        Check.Eq(y.Percent, 100, "+0% doesn't replace the percent");
        y.OnChatText("  Offline bonus: +50.5% (12s remaining)", Broadcast, t + 50_000, Utc);
        Check.Eq(y.Percent, 51, "a decimal percent, rounded");

        // Another [BANK] line is not ours.
        y.OnChatText("[BANK] Luminance: 8,904,758", System, t + 60_000, Utc);
        Check.Eq(y.LastLine, XpBonusLine.Summary, "the luminance line is the bank's, not ours");
    }

    private static void NotRead()
    {
        DreamWeaveXpBonus x = Logged(out long t);
        Check.False(x.OnChatText(Spent, Tell, t, Utc), "a tell isn't read");
        Check.False(x.HasReading, "nothing read from a tell");
        x.OnChatText("Brisa says, \"" + Spent + "\"", Broadcast, t, Utc);
        Check.False(x.HasReading, "a quoted copy isn't read (anchored)");
        x.OnChatText("I think: [BANK] Offline Bonus Time: 24h 0m", Broadcast, t, Utc);
        Check.False(x.HasReading, "a [BANK] line mid-text isn't read");
        x.OnChatText("Your offline experience bonus is now active - soon remaining.", Broadcast, t, Utc);
        Check.False(x.HasReading, "a line without a duration isn't read");
        Check.False(x.OnChatText(null, Broadcast, t, Utc) || x.OnChatText("", Broadcast, t, Utc), "empty text");
    }

    private static void OursAndTyped()
    {
        DreamWeaveXpBonus x = Logged(out long t);
        Check.Eq(x.Request(t), "/offlinebonus", "the command");
        x.NoteChatBar();   // our own send through the chat bar
        Check.True(x.Waiting(t + 100), "waiting for the reply");
        Check.False(x.OnChatText("Your offline experience bonus is now active - 48m 31s remaining.", Broadcast, t + 200, Utc), "a push in the window still shows");
        Check.True(x.OnChatText(Spent, Broadcast, t + 300, Utc), "our reply: hidden");
        Check.False(x.Waiting(t + 400), "answered");
        Check.False(x.OnChatText(Spent, Broadcast, t + 500, Utc), "the same line again (the server's own, or typed): shows");

        // The player types /offlinebonus while ours waits: the reply is theirs.
        x.Request(t + 10_000);
        x.NoteChatBar();   // ours
        x.NoteChatBar();   // theirs
        Check.False(x.OnChatText(Banking, Broadcast, t + 10_300, Utc), "a typed /offlinebonus's reply shows");
        Check.Eq(x.State, XpBonusState.Banking, "and still updates the row");

        // None banked and not enabled are replies too.
        x.Request(t + 20_000);
        x.NoteChatBar();
        Check.True(x.OnChatText(NoneBanked, Broadcast, t + 20_200, Utc), "none banked: hidden");
        x.Request(t + 30_000);
        x.NoteChatBar();
        Check.True(x.OnChatText(Disabled, Broadcast, t + 30_200, Utc), "not enabled: hidden");

        // No reply in time: given up once, and a late reply shows.
        x.Request(t + 40_000);
        x.NoteChatBar();
        Check.False(x.Tick(t + 41_000), "still within the window");
        Check.True(x.Tick(t + 46_000), "given up");
        Check.False(x.Tick(t + 47_000), "given up once");
        Check.Eq(x.LastMiss, "No reply to /offlinebonus.", "said why");
        Check.False(x.OnChatText(Spent, Broadcast, t + 47_500, Utc), "a late reply shows");
        Check.Eq(x.LastMiss, "", "a reading clears the miss");

        // A typed /offlinebonus with no own request is just typed.
        x.NoteChatBar();
        Check.False(x.OnChatText(Spent, Broadcast, t + 50_000, Utc), "shows");

        // Not sent: nothing waits.
        x.Request(t + 60_000);
        x.NotSent();
        Check.False(x.Waiting(t + 60_100), "not sent: not waiting");
        Check.False(x.OnChatText(Spent, Broadcast, t + 60_200, Utc), "and a reply shows");

        Check.True(DreamWeaveXpBonus.IsCommand("/offlinebonus") && DreamWeaveXpBonus.IsCommand(" /OfflineBonus ") && DreamWeaveXpBonus.IsCommand("/offlinebonus now"), "the command, any case, words after it");
        Check.False(DreamWeaveXpBonus.IsCommand("/offlinebonusx") || DreamWeaveXpBonus.IsCommand("/bonus") || DreamWeaveXpBonus.IsCommand("offlinebonus") || DreamWeaveXpBonus.IsCommand(null), "not the command");
    }

    private static void AutoTiming()
    {
        var x = new DreamWeaveXpBonus();
        x.LoadSettings((_, def) => def);
        Check.False(x.AutoRead, "off by default");
        x.LoadSettings((key, def) => key == DreamWeaveXpBonus.SettingAutoRead ? true : def);
        Check.True(x.AutoRead, "the setting turns it on");

        long t = 1_000_000;
        x.Login(t);
        Check.Eq(x.NextReadMs, t + 35_000, "the first read 35 s after login");
        Check.Null(x.Due(t + 34_000, true, true), "not before");
        Check.Null(x.Due(t + 35_000, true, false), "not while another command is near");
        Check.Null(x.Due(t + 35_500, false, true), "not in portal space");
        Check.Eq(x.Due(t + 36_000, true, true), "/offlinebonus", "then it goes");
        Check.Eq(x.NextReadMs, t + 36_000 + 600_000, "the next in ten minutes");
        Check.Null(x.Due(t + 37_000, true, true), "one at a time");

        // A full reading of any kind (the login line, a typed one) starts the ten minutes again.
        x.OnChatText(Spent, Broadcast, t + 300_000, Utc);
        Check.Eq(x.NextReadMs, t + 300_000 + 600_000, "a reading pushes the next read out");
        x.OnChatText("[BANK] Offline Bonus Time: 48m 0s", System, t + 310_000, Utc);
        Check.Eq(x.NextReadMs, t + 300_000 + 600_000, "a /b reading (no state) doesn't");

        x.SetAutoRead(false, t + 320_000);
        Check.Eq(x.NextReadMs, long.MaxValue, "off: nothing coming");
        Check.Null(x.Due(t + 2_000_000, true, true), "off: nothing sent");
        x.SetAutoRead(true, t + 400_000);
        Check.Eq(x.NextReadMs, t + 400_000 + DreamWeaveXpBonus.GapMs, "turned on: soon");

        x.Logout();
        Check.Eq(x.NextReadMs, long.MaxValue, "logged out: nothing coming");
        Check.False(x.HasReading, "logged out: the reading is gone");
    }

    private static void CheckNow()
    {
        DreamWeaveXpBonus x = Logged(out long t);
        Check.False(x.AutoRead, "automatic read off");
        x.CheckSoon(t);
        Check.True(x.Waiting(t), "a Check now waits to go");
        Check.Eq(x.NextReadMs, t, "now");
        Check.Null(x.Due(t, true, false), "not while another command is near");
        Check.Eq(x.Due(t + 2_000, true, true), "/offlinebonus", "at the next quiet moment");
        Check.True(x.Waiting(t + 2_100), "then waits for the reply");
        Check.Eq(x.NextReadMs, long.MaxValue, "and nothing more comes with the automatic read off");
        x.NoteChatBar();
        Check.True(x.OnChatText(Spent, Broadcast, t + 2_300, Utc), "its reply is hidden");
    }

    private static void Clock()
    {
        DreamWeaveXpBonus x = Logged(out long t);
        x.OnChatText(Spent, Broadcast, t, Utc);
        Check.True(x.Running, "active: running");
        Check.Eq(x.Remaining(t + 31_000), 2880.0, "31 s less");
        Check.Eq(x.Remaining(t + 2_911_000), 0.0, "to 0");
        Check.Eq(x.Remaining(t + 9_000_000), 0.0, "and no further");
        Check.False(x.IsActive(t + 2_911_000), "spent: not active any more");
        Check.Eq(x.Line(t + 3_000_000), "Double XP +100%: None banked", "and none banked");

        x.OnChatText(Banking, Broadcast, t, Utc);
        Check.Eq(x.Remaining(t + 600_000), 12_000.0, "banking: ten minutes more");
        Check.Eq(x.Remaining(t + 100_000_000), 86_400.0, "up to the 24 h cap");
        Check.Eq(x.Line(t + 100_000_000), "Double XP +100%: 24h 0m banked (idle, full)", "full");

        x.OnChatText("Welcome back - you were away for 30h 0m. Your offline experience bonus bank has been increased to 30h 0m.", Broadcast, t, Utc);
        Check.Eq(x.CapSeconds, 108_000.0, "a bank above 24 h raises the cap (the server's is set higher)");

        x.OnChatText(NoneBanked, Broadcast, t, Utc);
        Check.False(x.Running, "empty: not running");
        Check.Eq(x.Remaining(t + 600_000), 0.0, "stays 0");

        // A reading shown to the minute replaces the clock only when the clock is outside it.
        DreamWeaveXpBonus y = Logged(out t);
        y.OnChatText(Banking, Broadcast, t, Utc);   // 3h 10m 0s, banking
        y.OnChatText("[BANK] Offline Bonus Time: 3h 10m", System, t + 45_000, Utc);
        Check.Near(y.Seconds, 11_445, 0.01, "3h 10m 45s is inside '3h 10m': the clock's seconds kept");
        y.OnChatText("[BANK] Offline Bonus Time: 3h 0m", System, t + 50_000, Utc);
        Check.Eq(y.Seconds, 10_800.0, "outside it: the server's value");
    }

    private static void Lines()
    {
        DreamWeaveXpBonus x = Logged(out long t);
        Check.Eq(x.Line(t), "Double XP: not read yet", "before a reading");
        Check.Eq(x.Detail(t, Utc), "Not read yet: press Check now.", "what to do");
        x.OnChatText(Spent, Broadcast, t, Utc);
        Check.Eq(x.Line(t), "Double XP +100%: 48m 31s left (active)", "active");
        Check.True(x.IsActive(t), "coloured");
        Check.Eq(x.Detail(t, Utc), "Server read just now; counting down", "fresh, counting down");
        Check.Eq(x.Detail(t + 180_000, Utc.AddMinutes(3)), "Server read 3 min ago; counting down", "three minutes old");
        Check.Eq(x.Line(t + 31_000), "Double XP +100%: 48m 0s left (active)", "counted down");

        x.OnChatText(Banking, Broadcast, t, Utc);
        Check.Eq(x.Line(t), "Double XP +100%: 3h 10m banked (idle)", "banking");
        Check.False(x.IsActive(t), "not coloured");
        Check.Eq(x.Detail(t, Utc.AddHours(2)), "Server read 2 h ago; counting up", "counting up");

        DreamWeaveXpBonus y = Logged(out t);
        y.OnChatText("Your offline experience bonus is now active - 1m 0s remaining.", Broadcast, t, Utc);
        Check.Eq(y.Line(t), "Double XP: 1m 0s left (active)", "no percent known yet");
        y.OnChatText("[BANK] Offline Bonus Time: none", System, t, Utc);
        Check.Eq(y.Line(t), "Double XP: None banked", "none banked");

        Check.Eq(DreamWeaveXpBonus.Age(Utc, Utc.AddSeconds(59)), "just now", "under a minute");
        Check.Eq(DreamWeaveXpBonus.Age(Utc, Utc.AddMinutes(59)), "59 min ago", "minutes");
        Check.Eq(DreamWeaveXpBonus.Age(Utc, Utc.AddHours(5)), "5 h ago", "hours");
        Check.Eq(DreamWeaveXpBonus.Age(Utc, Utc.AddDays(3)), "3 d ago", "days");
    }

    private static void Saved()
    {
        DreamWeaveXpBonus x = Logged(out long t);
        x.OnChatText(Spent, Broadcast, t, Utc);
        Check.True(x.Dirty, "a reading is to be saved");
        string json = x.SaveJson();
        Check.False(x.Dirty, "saved");

        DreamWeaveXpBonus y = Logged(out t);
        y.LoadSaved(json);
        Check.Eq(y.State, XpBonusState.Active, "the state");
        Check.Eq(y.Seconds, 2911.0, "the time");
        Check.Eq(y.Percent, 100, "the percent");
        Check.Eq(y.ReadUtc, Utc, "when");
        Check.Eq(y.ReadMs, 0L, "loaded from disk");
        Check.False(y.Running, "the clock doesn't run on a saved reading");
        Check.Eq(y.Remaining(t + 600_000), 2911.0, "it stays as read");
        Check.Eq(y.Line(t), "Double XP +100%: 48m 31s left (active)", "shown as read");
        Check.Eq(y.Detail(t, Utc.AddHours(3)), "Saved reading, 3 h ago; not counting until the next one.", "and said to be old");
        Check.False(y.Dirty, "nothing new to save");

        // The login lines can come before the character is known: a newer live reading stays.
        DreamWeaveXpBonus z = Logged(out t);
        z.OnChatText("Welcome back - you were away for 9h 2m. Your offline experience bonus bank has been increased to 9h 2m.", Broadcast, t, Utc.AddHours(9));
        z.LoadSaved(json);
        Check.Eq(z.Seconds, 9.0 * 3600 + 120, "the live reading stays");
        Check.Eq(z.State, XpBonusState.Banking, "and its state");
        Check.Eq(z.Percent, 100, "the saved percent fills in the one the push didn't say");

        DreamWeaveXpBonus w = Logged(out t);
        foreach (string bad in new[] { "", "{", "[]", "{\"v\":1}", "{\"state\":\"active\",\"seconds\":5,\"updated\":\"never\"}" })
        {
            w.LoadSaved(bad);
            Check.False(w.HasReading, $"broken '{bad}': left out");
        }
        w.LoadSaved("{\"v\":1,\"state\":\"sideways\",\"seconds\":60,\"updated\":\"2026-11-15T17:00:00.0000000Z\"}");
        Check.Eq(w.State, XpBonusState.Unknown, "an unknown state reads as unknown");
        Check.Eq(w.Seconds, 60.0, "with its time");

        Check.True(DreamWeaveXpBonus.SavePath(@"C:\x\RynthCore\rynthdreamweave.json", 0x5000ABCD)
            .EndsWith(@"RynthCore\rynthdreamweave.xpbonus.5000ABCD.json", StringComparison.Ordinal), "saved beside the settings, per character");
    }

    private static void NextTimes()
    {
        var bank = new DreamWeaveBank();
        long t = 1_000_000;
        bank.Login(t);
        Check.Eq(bank.NextCheckMs, t + DreamWeaveBank.FirstCheckDelayMs, "the bank's first /b");
        bank.CheckEveryMinute = false;
        Check.Eq(bank.NextCheckMs, long.MaxValue, "bank check off: none");

        var boards = new DreamWeaveBoards();
        boards.Login(t);
        Check.Eq(boards.NextAutoMs, long.MaxValue, "boards' automatic refresh off: none");
        boards.SetAutoRefresh(true, t);
        Check.Eq(boards.NextAutoMs, t + 5_000, "on: its next /top");
        Check.True(DreamWeaveXpBonus.FirstReadDelayMs - DreamWeaveBank.FirstCheckDelayMs >= DreamWeaveXpBonus.GapMs
                   && DreamWeaveBoards.FirstAutoDelayMs - DreamWeaveXpBonus.FirstReadDelayMs >= DreamWeaveXpBonus.GapMs,
            "the first read after login sits between the bank's first /b and the boards' first /top");
    }
}
