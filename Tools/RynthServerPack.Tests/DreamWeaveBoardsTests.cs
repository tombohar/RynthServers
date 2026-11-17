using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RynthCore.Plugin.RynthDreamWeave;
using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// RynthDreamWeave's leaderboards (Plugins\RynthCore.Plugin.RynthDreamWeave\DreamWeave\DreamWeaveBoards.cs,
// 2026-11-15): DreamWeave's /top replies read into boards, its own replies marked for hiding while a typed
// /top shows, the automatic refresh's timing, the boards kept on disk, and where you stand.
// The replies are shaped as a live client got them on 2026-11-15 (one chat message per board, lines split by
// '\n'); every name is made up.
internal static class DreamWeaveBoardsTests
{
    private const int System = 0x05, Broadcast = 0x00, Tell = 0x03;
    private static readonly DateTime Utc = new(2026, 11, 15, 17, 0, 0, DateTimeKind.Utc);

    private static readonly string[] Names =
    {
        "Aldric Venn", "Brisa", "Corvo Hale", "Dunmere", "Elsk", "Fenwick Ash", "Gorran", "Hessa Pell", "Ivo", "Jarrow Kett",
        "Kaelith", "Lunet", "Morrow Stane", "Nyssa", "Orrin Dale", "Pellam", "Quill", "Rhoswen", "Sable Mott", "Tamsin",
    };

    private static string LevelBoard()
    {
        var sb = new StringBuilder("=== Level Leaderboard (Top 20) ===\n");
        for (int i = 0; i < 20; i++) sb.Append($"{i + 1}. {Names[i]} - Level {498 - i * 5}\n");
        return sb.ToString();
    }

    private static string CapBoard() =>
        "=== Class Ability Points Leaderboard (Top 20) ===\n"
        + string.Concat(Enumerable.Range(0, 20).Select(i => $"{i + 1}. {Names[i]} - {45 - i / 2} earned\n"))
        + "...\n34. Wynfor (you) - 33 earned\n";

    public static void Register(Runner r)
    {
        r.Add("dw boards: a /top board is read (header, 20 rows, your row below the list)", ReadBoard);
        r.Add("dw boards: every board shape (bank account row, speed season, winners, arena, knot, empty, refusals)", Shapes);
        r.Add("dw boards: only the reply to its own request is hidden; a typed /top shows and still updates", OursAndTyped);
        r.Add("dw boards: player chat, a quote mid-message and a late split line are not read", NotRead);
        r.Add("dw boards: a board longer than RynthCore hands a plugin is read to its last whole line and marked cut", Cut);
        r.Add("dw boards: lines split over messages are read within a few seconds of the header", SplitLines);
        r.Add("dw boards: automatic refresh: off by default, one board every 35 s, one at a time, gives up after 6 s", AutoTiming);
        r.Add("dw boards: automatic refresh skips cut boards, recent boards, portal space and a typed /top's reply", AutoSkips);
        r.Add("dw boards: Read all boards asks for every board, one every 6 s", Sweep);
        r.Add("dw boards: the boards are saved and read back (broken or unknown data left out)", Saved);
        r.Add("dw boards: where you stand, and the command and board names", Standing);
    }

    private static DreamWeaveBoards Logged(out long t)
    {
        var b = new DreamWeaveBoards();
        t = 1_000_000;
        b.Login(t);
        return b;
    }

    private static void ReadBoard()
    {
        DreamWeaveBoards b = Logged(out long t);
        Check.Eq(b.Request("level", t), "/top level", "the level board's command");
        Check.True(b.Pending, "the request waits");
        Check.True(b.OnChatText(LevelBoard(), System, t + 300, Utc), "our reply: hidden");
        Check.False(b.Pending, "answered");
        BoardResult? r = b.Result("level");
        Check.NotNull(r, "read");
        Check.Eq(r!.Rows.Count, 20, "20 rows");
        Check.Eq(r.Rows[0].Rank, 1, "rank 1");
        Check.Eq(r.Rows[0].Name, "Aldric Venn", "a name with a space");
        Check.Eq(r.Rows[19].Value, "Level 403", "the last value");
        Check.Null(r.Below, "nothing below the list");
        Check.Eq(r.UpdatedUtc, Utc, "when");
        Check.False(r.Cut, "whole");

        b.Request("cap", t + 10_000);
        Check.True(b.OnChatText(CapBoard(), System, t + 10_200, Utc), "cap: hidden");
        r = b.Result("cap")!;
        Check.Eq(r.Rows.Count, 20, "cap: 20 rows, the '...' and your row aren't rows");
        Check.NotNull(r.Below, "your row below the list");
        Check.Eq(r.Below!.Rank, 34, "your rank");
        Check.Eq(r.Below.Name, "Wynfor", "your name without '(you)'");
        Check.Eq(r.Below.Value, "33 earned", "your value");
        Check.True(r.Below.Marked && !r.Below.Account, "marked as you");
    }

    private static void Shapes()
    {
        DreamWeaveBoards b = Logged(out long t);
        b.OnChatText("=== Banked Pyreals Leaderboard (Top 20) ===\n1. Brisa - 12,007,997,013 pyreals\n2. Gorran - 9,123,456 pyreals\n...\n27. Lunet (your account) - 747,436,009 pyreals\n", System, t, Utc);
        BoardResult bank = b.Result("bank")!;
        Check.Eq(bank.Rows[0].Value, "12,007,997,013 pyreals", "bank value");
        Check.True(bank.Below!.Account && bank.Below.Marked, "the account row");
        Check.Eq(b.StandingText("bank"), "#27 (below the list) (your account)", "the account standing");

        b.Request("speed", t + 1_000);
        Check.True(b.OnChatText("=== The Black Book - Cells of the Black Book Leaderboard (Top 20) ===\n1. Corvo Hale - 3:55.55\n2. Elsk - 4:11.89\n", System, t + 1_200, Utc),
            "speed: any title, when /top speed was asked");
        Check.Eq(b.Result("speed")!.Title, "The Black Book - Cells of the Black Book", "the season title");
        Check.Eq(b.Result("speed")!.Rows[1].Value, "4:11.89", "a time");
        Check.False(b.OnChatText("=== Mystery Board Leaderboard (Top 20) ===\n1. Ivo - 3\n", System, t + 60_000, Utc), "an unknown title without /top speed isn't read");
        Check.Eq(b.Result("speed")!.Rows.Count, 2, "and changes nothing");

        b.Request("speed", t + 70_000);
        b.OnChatText("=== Season 4 - Crypt Leaderboard (Top 20) ===\nThat season has ended. No speed trial season is currently running.\nNo scores have been recorded yet.\n", System, t + 70_100, Utc);
        Check.Eq(b.Result("speed")!.Rows.Count, 0, "an ended season with no runs");
        Check.True(b.Result("speed")!.Note.Contains("That season has ended") && b.Result("speed")!.Note.Contains("No scores"), "both notes");

        b.Request("winners", t + 80_000);
        Check.True(b.OnChatText("=== Speed Trial Winners (Last 20 Seasons) ===\n1. The Black Book - Cells of the Black Book - Kaelith - 3:55.55\n2. The Unsung Choir - Lost Coastal Archive - unclaimed\n3. Test Season - The Orange Room - unclaimed\n", System, t + 80_100, Utc), "winners: hidden");
        BoardResult w = b.Result("winners")!;
        Check.Eq(w.Rows.Count, 3, "three seasons");
        Check.Eq(w.Rows[0].Detail, "The Black Book - Cells of the Black Book", "the season");
        Check.Eq(w.Rows[0].Name, "Kaelith", "its winner");
        Check.Eq(w.Rows[0].Value, "3:55.55", "the time");
        Check.Eq(w.Rows[1].Name, "unclaimed", "an unclaimed season");
        Check.Eq(w.Rows[1].Detail, "The Unsung Choir - Lost Coastal Archive", "its season");

        b.Request("2v2", t + 90_000);
        Check.True(b.OnChatText("[Arena] Top 10 - 2v2:\r\n1. Dunmere (War Mage) 1540 (2-0)\r\n2. Fenwick Ash (Heavy Weapon) 1500 (1-1)\r\n3. Pellam 1460 (0-2)", Broadcast, t + 90_100, Utc), "arena: hidden");
        BoardResult a = b.Result("2v2")!;
        Check.Eq(a.Rows.Count, 3, "arena rows");
        Check.Eq(a.Rows[1].Name, "Fenwick Ash", "arena name");
        Check.Eq(a.Rows[1].Value, "1500", "rating");
        Check.Eq(a.Rows[1].Detail, "Heavy Weapon, 1-1", "template and record");
        Check.Eq(a.Rows[2].Detail, "0-2", "no template");
        b.OnChatText("[Arena] Top 10 - Tugak Brawl:\n1. Nyssa (Finesse Weapon) 1500 (1-0)", Broadcast, t + 95_000, Utc);
        Check.Eq(b.Result("tugak")!.Rows.Count, 1, "tugak by its label");
        b.Request("bg", t + 100_000);
        Check.True(b.OnChatText("[Arena] No one is ranked in Battleground yet.", Broadcast, t + 100_100, Utc), "an empty ladder: ours");
        Check.Eq(b.Result("bg")!.Note, "No one is ranked yet.", "empty ladder note");

        b.Request("knot", t + 110_000);
        b.OnChatText("=== Knot Clears Leaderboard (Top 20) ===\n1. Quill - Level 436 - Drift Olthoi Matron - 2:40.91\n2. Tamsin - Level 297\n", System, t + 110_100, Utc);
        Check.Eq(b.Result("knot")!.Rows[0].Value, "Level 436 - Drift Olthoi Matron - 2:40.91", "knot: the whole value");
        Check.Eq(b.Result("knot")!.Rows[1].Value, "Level 297", "a clear without a time");

        b.Request("thread", t + 120_000);
        b.OnChatText("=== Thread-Guide Leaderboard (Top 20) ===\n1. Rhoswen - rung 375 (1,132 clears)\n2. Ivo - no rung (4 clears)\n", System, t + 120_100, Utc);
        Check.Eq(b.Result("thread")!.Rows[1].Value, "no rung (4 clears)", "thread values");

        b.Request("stamp", t + 130_000);
        Check.True(b.OnChatText("Quest stamps are not currently enabled on this server.", System, t + 130_100, Utc), "a refusal answers our request");
        Check.Eq(b.Result("stamp")!.Note, "Quest stamps are not currently enabled on this server.", "kept as the board's note");
        b.Request("1v1", t + 140_000);
        Check.True(b.OnChatText("[Arena] Arena ratings are unavailable right now.", Broadcast, t + 140_100, Utc), "arena refusal");
        Check.False(b.OnChatText("Bank balances are unavailable right now, try again shortly.", System, t + 400_000, Utc), "a refusal with no /top out isn't read");

        b.Request("deaths", t + 150_000);
        b.OnChatText("=== Deaths Leaderboard (Top 20) ===\nNo scores have been recorded yet.\nYou are exempt from the leaderboards, so your own scores are not listed.\n", System, t + 150_100, Utc);
        Check.True(b.Result("deaths")!.Note.StartsWith("No scores") && b.Result("deaths")!.Note.Contains("exempt"), "empty and exempt");
        foreach (BoardDef d in DreamWeaveBoards.Defs) Check.NotNull(DreamWeaveBoards.Def(d.Key), "def " + d.Key);
        Check.Eq(DreamWeaveBoards.Defs.Length, 17, "17 boards");
    }

    private static void OursAndTyped()
    {
        DreamWeaveBoards b = Logged(out long t);
        // Typed by the player: shows, and updates.
        b.NoteTyped("/top level", t);
        Check.False(b.OnChatText(LevelBoard(), System, t + 300, Utc), "typed: shown");
        Check.Eq(b.Result("level")!.Rows.Count, 20, "typed: read");

        // Ours for level, the player types /top level too: theirs, shown.
        b.Request("level", t + 10_000);
        b.NoteTyped("/top level", t + 10_050);
        Check.False(b.Pending, "the typed one takes it over");
        Check.False(b.OnChatText(LevelBoard(), System, t + 10_300, Utc), "both asked: shown");

        // Ours for cap, the player types /top deaths: the cap reply is still ours, deaths shows.
        b.Request("cap", t + 20_000);
        b.NoteTyped("/top deaths", t + 20_010);
        Check.False(b.OnChatText("=== Deaths Leaderboard (Top 20) ===\n1. Ivo - 3 deaths\n", System, t + 20_200, Utc), "their board shows");
        Check.True(b.Pending, "ours still waits");
        Check.True(b.OnChatText(CapBoard(), System, t + 20_300, Utc), "ours hidden");

        // Late: not ours any more.
        b.Request("title", t + 30_000);
        Check.False(b.OnChatText("=== Titles Leaderboard (Top 20) ===\n1. Brisa - 203 titles\n", System, t + 30_000 + DreamWeaveBoards.ReplyTimeoutMs + 1, Utc), "a late reply shows");
        Check.Eq(b.Result("title")!.Rows[0].Value, "203 titles", "and is read");
        Check.True(b.LastMiss.Length > 0, "the miss is noted");

        // A failed send leaves nothing waiting.
        b.Request("level", t + 40_000);
        b.NotSent();
        Check.False(b.Pending, "not sent: nothing waits");
        Check.False(b.OnChatText(LevelBoard(), System, t + 40_100, Utc), "and the reply isn't ours");
    }

    private static void NotRead()
    {
        DreamWeaveBoards b = Logged(out long t);
        b.Request("level", t);
        Check.False(b.OnChatText(LevelBoard(), Tell, t + 100, Utc), "a tell isn't read");
        Check.Null(b.Result("level"), "nothing read");
        Check.False(b.OnChatText("Ivo says, \"=== Level Leaderboard (Top 20) ===\"\n1. Ivo - Level 999\n", System, t + 200, Utc), "a quote isn't a header");
        Check.Null(b.Result("level"), "still nothing");
        Check.False(b.OnChatText("1. Ivo - Level 999", System, t + 300, Utc), "a row with no header isn't read");
        b.OnChatText(LevelBoard(), System, t + 400, Utc);
        Check.False(b.OnChatText("21. Ivo - Level 1", System, t + 400 + DreamWeaveBoards.BlockMs + 1, Utc), "a row long after the header isn't read");
        Check.Eq(b.Result("level")!.Rows.Count, 20, "rows unchanged");
        Check.False(b.OnChatText("", System, t, Utc) || b.OnChatText(null, System, t, Utc), "empty");
    }

    private static string LongKnot()
    {
        var sb = new StringBuilder("=== Knot Clears Leaderboard (Top 20) ===\n");
        for (int i = 0; i < 20; i++) sb.Append($"{i + 1}. {Names[i]} - Level {436 - i * 7} - Drift Olthoi Matron - {2 + i % 7}:{10 + i * 2}.{50 + i}\n");
        sb.Append("...\n41. Wynfor (you) - Level 120 - Drift Olthoi Matron - 9:00.00\n");
        return sb.ToString();
    }

    private static void Cut()
    {
        DreamWeaveBoards b = Logged(out long t);
        string full = LongKnot();
        Check.True(full.Length > DreamWeaveBoards.EngineMaxChars, "the fixture is longer than the engine hands over");
        string seen = full[..DreamWeaveBoards.EngineMaxChars];
        b.Request("knot", t);
        Check.True(b.OnChatText(seen, System, t + 200, Utc), "ours (the engine prints it anyway)");
        BoardResult r = b.Result("knot")!;
        Check.True(r.Cut, "marked cut");
        int whole = seen.Split('\n').Length - 1;   // complete lines, the header included
        Check.Eq(r.Rows.Count, whole - 1, "every whole row, not the cut one");
        foreach (BoardRow row in r.Rows) Check.True(row.Value.Contains(':') && row.Value.Split(" - ").Length == 3, "a whole row: " + row.Value);
        Check.Null(r.Below, "your row was past the cut");
        Check.True(r.Note.Contains("cut short"), "the note says so");
        Check.Eq(b.StandingText("knot"), "not in the part read", "standing on a cut board");
    }

    private static void SplitLines()
    {
        DreamWeaveBoards b = Logged(out long t);
        b.Request("level", t);
        Check.True(b.OnChatText("=== Level Leaderboard (Top 20) ===", System, t + 100, Utc), "header alone: ours");
        Check.True(b.OnChatText("1. Brisa - Level 498", System, t + 150, Utc), "a row in its own message: ours");
        Check.True(b.OnChatText("2. Ivo - Level 490", System, t + 3_000, Utc), "within a few seconds of the last line");
        Check.True(b.OnChatText("...", System, t + 3_100, Utc), "the dots");
        Check.True(b.OnChatText("40. Wynfor (you) - Level 300", System, t + 3_200, Utc), "your row");
        Check.Eq(b.Result("level")!.Rows.Count, 2, "two rows");
        Check.Eq(b.Result("level")!.Below!.Rank, 40, "and yours");
        Check.False(b.OnChatText("3. Ivo - Level 1", System, t + 3_200 + DreamWeaveBoards.BlockMs + 1, Utc), "too late");
    }

    private static void AutoTiming()
    {
        var b = new DreamWeaveBoards();
        b.LoadSettings((_, fallback) => fallback);
        Check.False(b.AutoRefresh, "off by default");
        long t = 1_000_000;
        b.Login(t);
        Check.Null(b.Due(t + DreamWeaveBoards.FirstAutoDelayMs + 1, true), "off: nothing");
        b.AutoRefresh = true;
        Check.Null(b.Due(t + 1_000, true), "not right after login");
        string? first = b.Due(t + DreamWeaveBoards.FirstAutoDelayMs, true);
        Check.Eq(first, "/top level", "the first board after the delay");
        long now = t + DreamWeaveBoards.FirstAutoDelayMs;
        Check.True(b.Pending, "it waits for its reply");
        b.OnChatText(LevelBoard(), System, now + 300, Utc);
        Check.Null(b.Due(now + 1_000, true), "not before the interval");
        Check.Eq(b.Due(now + DreamWeaveBoards.AutoIntervalMs, true), "/top bank", "the next board in turn");
        now += DreamWeaveBoards.AutoIntervalMs;
        // No reply: given up after 6 s, the next one goes at its time.
        b.Expire(now + DreamWeaveBoards.ReplyTimeoutMs);
        Check.False(b.Pending, "given up");
        Check.Eq(b.LastMiss, "No reply to /top bank.", "the miss");
        Check.Eq(b.Due(now + DreamWeaveBoards.AutoIntervalMs, true), "/top cap", "the next");
        // Every board once per round: 17 x 35 s is about ten minutes.
        Check.True(DreamWeaveBoards.Defs.Length * DreamWeaveBoards.AutoIntervalMs is >= 9 * 60_000 and <= 11 * 60_000, "a round is about ten minutes");
        b.Logout();
        Check.Null(b.Due(now + 10 * 60_000, true), "nothing after logout");
        // Turned on later: the first board soon.
        var c = new DreamWeaveBoards();
        c.Login(t);
        c.SetAutoRefresh(true, t + 1_000);
        Check.Eq(c.Due(t + 6_000, true), "/top level", "on: a board within seconds");
    }

    private static void AutoSkips()
    {
        DreamWeaveBoards b = Logged(out long t);
        b.AutoRefresh = true;
        long now = t + DreamWeaveBoards.FirstAutoDelayMs;
        Check.Null(b.Due(now, false), "not outside the world or in portal space");
        // The player just typed /top bank: wait for its reply first.
        b.NoteTyped("/top bank", now);
        Check.Null(b.Due(now + 100, true), "a typed /top's reply may still come");
        // A cut board (knot) and one read just now (bank, the typed one) are skipped.
        b.OnChatText("=== Banked Pyreals Leaderboard (Top 20) ===\n1. Brisa - 5 pyreals\n", System, now + 200, Utc);
        b.Request("knot", now + 300);
        b.OnChatText(LongKnot()[..DreamWeaveBoards.EngineMaxChars], System, now + 400, Utc);
        var asked = new List<string>();
        long at = now + 1_000;
        for (int i = 0; i < 40 && asked.Count < 15; i++, at += DreamWeaveBoards.AutoIntervalMs)
        {
            string? cmd = b.Due(at, true);
            if (cmd == null) continue;
            asked.Add(cmd);
            b.NotSent();   // nothing waits
        }
        Check.False(asked.Contains("/top knot"), "the cut board is left out");
        Check.False(asked.Take(10).Contains("/top bank"), "a board read just now waits for the next round");
        Check.True(asked.Contains("/top level") && asked.Contains("/top bg"), "the others go");
    }

    private static void Sweep()
    {
        DreamWeaveBoards b = Logged(out long t);
        b.Request("level", t);
        b.SweepAll();
        Check.Eq(b.SweepLeft, 17, "every board queued");
        Check.Null(b.Due(t + 100, true), "one request at a time: not while one waits");
        b.OnChatText(LevelBoard(), System, t + 200, Utc);
        var sent = new List<string>();
        long now = t;
        for (int i = 0; i < 200 && b.SweepLeft > 0; i++, now += 1_000)
        {
            string? cmd = b.Due(now, true);
            if (cmd == null) continue;
            sent.Add(cmd);
            b.OnChatText("Quest stamps are not currently enabled on this server.", System, now + 100, Utc);   // any answer
        }
        Check.Eq(sent.Count, 17, "17 sent");
        Check.Eq(sent.Distinct().Count(), 17, "each once");
        Check.True(now - t >= 16 * DreamWeaveBoards.SweepIntervalMs, "paced");
        Check.True(sent.Contains("/top speed winners"), "speed winners");
    }

    private static void Saved()
    {
        DreamWeaveBoards b = Logged(out long t);
        b.OnChatText(CapBoard(), System, t, Utc);
        b.OnChatText("[Arena] Top 10 - 1v1:\n1. Dunmere (War Mage) 1600 (11-0)", Broadcast, t + 10, Utc);
        b.Request("knot", t + 20);
        b.OnChatText(LongKnot()[..DreamWeaveBoards.EngineMaxChars], System, t + 30, Utc);
        Check.True(b.Dirty, "read: to save");
        string json = b.SaveJson();
        Check.False(b.Dirty, "saved");
        var c = new DreamWeaveBoards();
        c.LoadSaved(json);
        Check.Eq(c.ReadCount, 3, "three boards back");
        BoardResult cap = c.Result("cap")!;
        Check.Eq(cap.Rows.Count, 20, "rows back");
        Check.Eq(cap.Rows[3].Name, Names[3], "a name");
        Check.Eq(cap.Below!.Rank, 34, "your row back");
        Check.True(cap.Below.Marked, "still yours");
        Check.Eq(cap.UpdatedUtc, Utc, "when");
        Check.Eq(cap.UpdatedMs, 0L, "from disk: due for the automatic refresh");
        Check.Eq(c.Result("1v1")!.Rows[0].Detail, "War Mage, 11-0", "arena detail");
        Check.True(c.Result("knot")!.Cut, "cut kept, so it stays out of the automatic refresh");
        Check.Eq(DreamWeaveBoards.FromJson("{ broken").Count, 0, "broken: nothing");
        Check.Eq(DreamWeaveBoards.FromJson(null).Count, 0, "missing: nothing");
        Check.Eq(DreamWeaveBoards.FromJson("{\"v\":1,\"boards\":[{\"key\":\"fishing\",\"rows\":[]},{\"key\":\"level\",\"rows\":[{\"r\":1,\"n\":\"Ivo\",\"v\":\"Level 9\"}]}]}").Count, 1,
            "an unknown board is left out");
        string path = DreamWeaveBoards.SavePath(@"C:\x\RynthCore\rynthdreamweave.json", 0x50000180);
        Check.Eq(path, @"C:\x\RynthCore\rynthdreamweave.boards.50000180.json", "beside the settings, per character");
    }

    private static void Standing()
    {
        DreamWeaveBoards b = Logged(out long t);
        Check.Eq(b.StandingText("level"), "not read yet", "not read");
        b.OnChatText(LevelBoard(), System, t, Utc);
        Check.Eq(b.StandingText("level"), "not on it", "not on it");
        b.YourName = "hessa pell";
        Check.Eq(b.StandingText("level"), "#8", "by your name in the list (any case)");
        Check.Eq(b.Standing("level").Value, "Level 463", "your value");
        b.OnChatText(CapBoard(), System, t + 10, Utc);
        Check.Eq(b.StandingText("cap"), "#8", "in the list beats the server's below row only when listed");
        b.YourName = "Wynfor";
        Check.Eq(b.StandingText("cap"), "#34 (below the list)", "below the list");
        b.OnChatText("[Arena] Top 10 - 1v1:\n1. Wynfor (War Mage) 1600 (11-0)", Broadcast, t + 20, Utc);
        Check.Eq(b.Standing("1v1").Value, "rating 1600 (War Mage, 11-0)", "arena standing");

        Check.Eq(DreamWeaveBoards.KeyOfCommand("/top"), "", "the list");
        Check.Eq(DreamWeaveBoards.KeyOfCommand(" /TOP Speed Winners "), "winners", "speed winners");
        Check.Eq(DreamWeaveBoards.KeyOfCommand("/top speed"), "speed", "speed");
        Check.Eq(DreamWeaveBoards.KeyOfCommand("/top stamps"), "stamp", "stamps");
        Check.Eq(DreamWeaveBoards.KeyOfCommand("/top 2v2 20"), "2v2", "a count after an arena board");
        Check.Null(DreamWeaveBoards.KeyOfCommand("/top fishing"), "unknown");
        Check.Null(DreamWeaveBoards.KeyOfCommand("/topple"), "not /top");
        Check.Null(DreamWeaveBoards.KeyOfCommand("/b"), "not /top");
        Check.Eq(DreamWeaveBoards.KeyOfName("speed winners"), "winners", "name: speed winners");
        Check.Eq(DreamWeaveBoards.KeyOfName("Class ability points"), "cap", "name: a label");
        Check.Null(DreamWeaveBoards.KeyOfName(""), "name: empty");

        Check.Eq(DreamWeaveBoards.Age(Utc, Utc.AddSeconds(30)), "updated just now", "age now");
        Check.Eq(DreamWeaveBoards.Age(Utc, Utc.AddMinutes(9.5)), "updated 9 min ago", "age min");
        Check.Eq(DreamWeaveBoards.Age(Utc, Utc.AddHours(3)), "updated 3 h ago", "age h");
        Check.Eq(DreamWeaveBoards.Age(DateTime.MinValue, Utc), "not read yet", "age never");
    }
}
