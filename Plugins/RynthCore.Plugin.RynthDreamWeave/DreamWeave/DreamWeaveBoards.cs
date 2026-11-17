// DreamWeaveBoards.cs - DreamWeave's leaderboards (2026-11-15), read from the replies of the server's own
// /top command, for the Leaderboards page (BoardsPage.cs). The formats come from DreamWeave's source
// (ACE-DreamWeave, Command/Handlers/PlayerCommands.cs HandleTop + SendLeaderboard + SendSpeedLeaderboard +
// SendSpeedWinners, PvpArenaCommands.HandleArenaTop) and were checked against a live client on 2026-11-15.
// Each board is ONE chat message whose lines are separated by '\n':
//   /top <board>          "=== <Title> Leaderboard (Top 20) ===" then "N. Name - value" (20 at most),
//                         and when you are below the cut "..." then "N. Name (you) - value"
//                         ("(your account)" on the per-account bank board). An empty board says
//                         "No scores have been recorded yet."; an exempt (staff) requester gets
//                         "You are exempt from the leaderboards, ...".
//   /top speed            the same, titled with the season ("=== Season - Dungeon Leaderboard (Top 20) ==="),
//                         plus "That season has ended. ..." between rotations.
//   /top speed winners    "=== Speed Trial Winners (Last 20 Seasons) ===" then "N. Season - Dungeon - Name - m:ss.cc"
//                         or "N. Season - Dungeon - unclaimed".
//   /top 1v1|2v2|tugak|bg "[Arena] Top 10 - <mode>:" then "N. Name (template) rating (W-L)" (Broadcast, no "(you)" row);
//                         "[Arena] No one is ranked in <mode> yet." when empty.
//   refusals              one line: "Bank balances are unavailable right now, ...", "... are not currently enabled on
//                         this server.", "No speed trial season has been run yet.", "[Arena] Arena ratings are unavailable ...".
// RynthCore hands a plugin at most the first 1024 characters of a chat message, and can't keep a longer one
// out of the chat window (Compatibility/ChatCallbackHooks.cs MaxIncomingChatChars). Such a board is read up to
// its last whole line and marked Cut; the automatic refresh leaves it out, so it doesn't print every ten minutes.
//
// A reply is RynthDreamWeave's own (hidden) only when it answers the board RynthDreamWeave asked for, within
// ReplyTimeoutMs; a /top the player types shows as usual and still updates the page. One request at a time.
// The header must start the message, and a player's chat types are never read, so a pasted copy can't add rows.
// Pure (no host calls): RynthServerPack.Tests covers it. Threads: everything here runs under Gate.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthDreamWeave;

internal enum BoardKind { Standard, SpeedWinners, Arena }

/// <summary>One of DreamWeave's boards: its key, the page's label, the command, and how the server titles it.</summary>
internal sealed record BoardDef(string Key, string Label, string Command, BoardKind Kind, string ServerTitle, string Help);

/// <summary>A row of a board. Arena: Value is the rating, Detail "template, W-L". Speed winners: Detail is the season.</summary>
internal sealed class BoardRow
{
    public int Rank;
    public string Name = "";
    public string Value = "";
    public string Detail = "";
    /// <summary>The server marked it "(you)" or "(your account)".</summary>
    public bool Marked;
    /// <summary>"(your account)": the bank board's row for this account (another character's name).</summary>
    public bool Account;
}

/// <summary>The last reading of one board.</summary>
internal sealed class BoardResult
{
    public string Key = "";
    /// <summary>The title the server gave it (the season's name on the speed board).</summary>
    public string Title = "";
    public readonly List<BoardRow> Rows = new();
    /// <summary>Your row below the cut ("..." then "N. Name (you) - value"), else null.</summary>
    public BoardRow? Below;
    /// <summary>What the server added: an empty board, the season ended, a refusal.</summary>
    public string Note = "";
    public DateTime UpdatedUtc = DateTime.MinValue;
    /// <summary>Environment.TickCount64 of the reading; 0 for one loaded from disk.</summary>
    public long UpdatedMs;
    /// <summary>The message ran past what RynthCore hands a plugin: the rows after the cut are missing.</summary>
    public bool Cut;
}

/// <summary>Where you stand on a board.</summary>
internal readonly record struct BoardStanding(bool Read, int Rank, string Value, bool OutsideList, bool AccountRow);

internal sealed class DreamWeaveBoards
{
    public const string SettingAutoRefresh = "autoRefreshLeaderboards";
    /// <summary>A reply this long after the request isn't ours any more (and the next request may go).</summary>
    public const long ReplyTimeoutMs = 6_000;
    /// <summary>Lines in a later message still belong to a header for this long (an engine that splits lines).</summary>
    public const long BlockMs = 4_000;
    /// <summary>Automatic refresh: one board this often, so each of the 17 is read about every ten minutes.</summary>
    public const long AutoIntervalMs = 35_000;
    /// <summary>After login, before the first automatic request (the welcome and the bank's first /b go first).</summary>
    public const long FirstAutoDelayMs = 45_000;
    /// <summary>"Read all boards": one board this often.</summary>
    public const long SweepIntervalMs = 6_000;
    /// <summary>RynthCore hands a plugin at most this many characters of a chat message.</summary>
    public const int EngineMaxChars = 1024;

    public static readonly BoardDef[] Defs =
    {
        new("level", "Level", "/top level", BoardKind.Standard, "Level", "Highest character level"),
        new("bank", "Bank", "/top bank", BoardKind.Standard, "Banked Pyreals", "Most pyreals banked (one entry per account)"),
        new("cap", "Class ability points", "/top cap", BoardKind.Standard, "Class Ability Points", "Most class ability points earned, lifetime"),
        new("stamp", "Quest stamps", "/top stamp", BoardKind.Standard, "Quest Stamps", "Most quest stamps earned by a single character"),
        new("thread", "Thread-Guide", "/top thread", BoardKind.Standard, "Thread-Guide", "Highest Thread-Guide rung won, ties broken by lifetime Thread clears"),
        new("knot", "Knot clears", "/top knot", BoardKind.Standard, "Knot Clears", "Best Knot clear: highest Knot level, then fastest time"),
        new("title", "Titles", "/top title", BoardKind.Standard, "Titles", "Most character titles earned"),
        new("deaths", "Deaths", "/top deaths", BoardKind.Standard, "Deaths", "Most deaths"),
        new("dps", "DPS", "/top dps", BoardKind.Standard, "DPS Challenge", "Attack arena: highest damage dealt in the 60 s trial"),
        new("defense", "Defense", "/top defense", BoardKind.Standard, "Defense Challenge", "Defense arena: longest survival against the Squall"),
        new("wave", "Wave", "/top wave", BoardKind.Standard, "Wave Challenge", "Wave gauntlet: highest wave reached"),
        new("speed", "Speed trial", "/top speed", BoardKind.Standard, "", "Speed trial: fastest run of the current season's dungeon"),
        new("winners", "Speed trial winners", "/top speed winners", BoardKind.SpeedWinners, "Speed Trial Winners", "Speed trial: the winner of each of the 20 most recent seasons"),
        new("1v1", "Arena 1v1", "/top 1v1", BoardKind.Arena, "1v1", "PvP arena ladder, ranked by rating"),
        new("2v2", "Arena 2v2", "/top 2v2", BoardKind.Arena, "2v2", "PvP arena ladder, ranked by rating"),
        new("tugak", "Tugak Brawl", "/top tugak", BoardKind.Arena, "Tugak Brawl", "PvP free-for-all ladder, ranked by rating"),
        new("bg", "Battleground", "/top bg", BoardKind.Arena, "Battleground", "PvP battleground ladder, ranked by rating"),
    };

    private static readonly Regex Header = new(@"^=== (.+) Leaderboard \(Top \d+\) ===$");
    private static readonly Regex WinnersHeader = new(@"^=== Speed Trial Winners \(Last \d+ Seasons\) ===$");
    private static readonly Regex ArenaHeader = new(@"^\[Arena\] Top \d+ - (.+):$");
    private static readonly Regex ArenaEmpty = new(@"^\[Arena\] No one is ranked in (.+) yet\.$");
    private static readonly Regex Row = new(@"^(\d+)\. (.+?)(?: \((you|your account)\))? - (.+)$");
    private static readonly Regex ArenaRow = new(@"^(\d+)\. (.+?)(?: \(([^()]+)\))? (-?\d+) \((\d+)-(\d+)\)$");
    private static readonly Regex Refusal = new(@"^(?:Bank balances are unavailable right now|Class abilities are not currently enabled|Quest stamps are not currently enabled"
                                                + @"|Threads are not currently enabled|No speed trial season has been run yet|\[Arena\] Arena ratings are unavailable"
                                                + @"|Usage: /top <1v1\|2v2\|tugak\|bg>)");

    /// <summary>Held for everything here (pump and render thread).</summary>
    public readonly object Gate = new();

    /// <summary>Refresh the boards automatically (one every <see cref="AutoIntervalMs"/>). Off by default.</summary>
    public bool AutoRefresh;

    /// <summary>The character's name, to find your row inside the list (set at login).</summary>
    public string YourName = "";

    /// <summary>Bumped on every change (the page's Refresh row and the save watch it).</summary>
    public int Revision { get; private set; }

    /// <summary>The boards read since the last save.</summary>
    public bool Dirty { get; private set; }

    /// <summary>What the last request came to, for the page ("" before one).</summary>
    public string LastMiss { get; private set; } = "";

    private readonly Dictionary<string, BoardResult> _results = new(StringComparer.Ordinal);

    private string? _pendingKey;      // RynthDreamWeave's own request, waiting for its reply
    private long _pendingMs;
    private string? _expectKey;       // the last /top anyone sent (a typed one too), for a reply's unnamed parts
    private long _expectMs;
    private BoardResult? _block;      // the board whose rows are being read
    private bool _blockOurs;
    private long _blockMs;
    private int _rowsSeen;            // rows read into _block (a header with none stays as it was if cut)
    private bool _belowNext;          // a "..." line: the next row is your row below the cut

    private long _nextAutoMs = long.MaxValue;
    private int _rotation;
    private readonly Queue<string> _sweep = new();
    private long _nextSweepMs;

    public static BoardDef? Def(string key)
    {
        foreach (BoardDef d in Defs) if (d.Key == key) return d;
        return null;
    }

    /// <summary>The last reading of a board, or null.</summary>
    public BoardResult? Result(string key) => _results.TryGetValue(key, out BoardResult? r) ? r : null;

    public int ReadCount => _results.Count;

    /// <summary>RynthDreamWeave's own request is waiting for its reply.</summary>
    public bool Pending => _pendingKey != null;

    public string? PendingKey => _pendingKey;

    /// <summary>"Read all boards" still has boards to ask for.</summary>
    public int SweepLeft => _sweep.Count;

    /// <summary>When the automatic refresh's next /top goes (Environment.TickCount64); long.MaxValue when it is off.
    /// The offline bonus read keeps clear of it (DreamWeaveXpBonus.GapMs).</summary>
    public long NextAutoMs => AutoRefresh ? _nextAutoMs : long.MaxValue;

    // ── Settings and the saved boards ─────────────────────────────────────

    public void LoadSettings(Func<string, bool, bool> getBool) => AutoRefresh = getBool(SettingAutoRefresh, false);

    /// <summary>The boards saved for a character (the page isn't empty after a reload). Replaces what is held.</summary>
    public void LoadSaved(string? json)
    {
        _results.Clear();
        foreach (BoardResult r in FromJson(json)) _results[r.Key] = r;
        Dirty = false;
        Revision++;
    }

    public string SaveJson()
    {
        Dirty = false;
        return ToJson(_results.Values);
    }

    // ── Login, logout, requests ───────────────────────────────────────────

    /// <summary>A character is in the world (or the plugin reloaded in game).</summary>
    public void Login(long nowMs)
    {
        ClearRequests();
        _nextAutoMs = nowMs + FirstAutoDelayMs;
    }

    public void Logout()
    {
        ClearRequests();
        _nextAutoMs = long.MaxValue;
        _results.Clear();
        YourName = "";
        LastMiss = "";
        Dirty = false;
        Revision++;
    }

    private void ClearRequests()
    {
        _pendingKey = _expectKey = null;
        _block = null;
        _sweep.Clear();
        _nextSweepMs = 0;
    }

    /// <summary>
    /// Marks a request for <paramref name="key"/> as RynthDreamWeave's own and returns its command, or null for an
    /// unknown board. The caller sends it (and calls <see cref="NotSent"/> when it didn't go).
    /// </summary>
    public string? Request(string key, long nowMs)
    {
        BoardDef? d = Def(key);
        if (d == null) return null;
        _pendingKey = _expectKey = key;
        _pendingMs = _expectMs = nowMs;
        LastMiss = "";
        return d.Command;
    }

    /// <summary>The request didn't go out (not in the world).</summary>
    public void NotSent()
    {
        _pendingKey = _expectKey = null;
    }

    /// <summary>Queues every board for "Read all boards" (one every <see cref="SweepIntervalMs"/>).</summary>
    public void SweepAll()
    {
        _sweep.Clear();
        foreach (BoardDef d in Defs) _sweep.Enqueue(d.Key);
    }

    /// <summary>The board a typed "/top ..." asks for ("winners" for "/top speed winners", "" for the list), else null.</summary>
    public static string? KeyOfCommand(string? text)
    {
        string[] w = (text ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0 || w[0] != "/top") return null;
        if (w.Length == 1) return "";
        return w[1] switch
        {
            "speed" => w.Length > 2 && w[2] == "winners" ? "winners" : "speed",
            "stamps" => "stamp",
            "threads" => "thread",
            "titles" => "title",
            "knots" => "knot",
            "ffa" or "brawl" => "tugak",
            "battleground" => "bg",
            var k => Def(k) != null ? k : null,
        };
    }

    /// <summary>A board named by a player ("level", "speed winners", "Arena 1v1", ...), else null.</summary>
    public static string? KeyOfName(string? text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        string? k = KeyOfCommand("/top " + t);
        if (!string.IsNullOrEmpty(k)) return k;
        foreach (BoardDef d in Defs)
            if (d.Label.Equals(t, StringComparison.OrdinalIgnoreCase) || d.Key.Equals(t, StringComparison.OrdinalIgnoreCase)) return d.Key;
        return null;
    }

    /// <summary>
    /// The player typed a /top (AC's chat bar; the pump calls this). Its reply is theirs and shows, and still
    /// updates the page; RynthDreamWeave's own request for the same board stops being its own.
    /// </summary>
    public void NoteTyped(string text, long nowMs)
    {
        string? key = KeyOfCommand(text);
        if (key == null) return;
        _expectKey = key.Length > 0 ? key : null;
        _expectMs = nowMs;
        if (_pendingKey != null && _pendingKey == key) _pendingKey = null;
    }

    /// <summary>
    /// The pump's tick: what to send now (a "Read all boards" step, or the automatic refresh's next board), else
    /// null. Never while a request waits, or while a typed /top's reply may still come. <paramref name="canSend"/>
    /// is false outside the world and in portal space.
    /// </summary>
    public string? Due(long nowMs, bool canSend)
    {
        Expire(nowMs);
        if (!canSend || _pendingKey != null) return null;
        if (_expectKey != null && nowMs - _expectMs < ReplyTimeoutMs) return null;
        if (_sweep.Count > 0)
        {
            if (nowMs < _nextSweepMs) return null;
            _nextSweepMs = nowMs + SweepIntervalMs;
            return Request(_sweep.Dequeue(), nowMs);
        }
        if (!AutoRefresh || nowMs < _nextAutoMs) return null;
        _nextAutoMs = nowMs + AutoIntervalMs;
        for (int i = 0; i < Defs.Length; i++)
        {
            int at = (_rotation + i) % Defs.Length;
            if (SkipAuto(Defs[at].Key, nowMs)) continue;
            _rotation = at + 1;
            return Request(Defs[at].Key, nowMs);
        }
        return null;
    }

    /// <summary>The automatic refresh leaves out a board too long to hide, and one read in the last half round.</summary>
    private bool SkipAuto(string key, long nowMs)
    {
        BoardResult? r = Result(key);
        if (r == null) return false;
        if (r.Cut) return true;
        return r.UpdatedMs != 0 && nowMs - r.UpdatedMs < AutoIntervalMs * Defs.Length / 2;
    }

    /// <summary>A request that got no reply in time is given up (the next may go).</summary>
    public void Expire(long nowMs)
    {
        if (_pendingKey == null || nowMs - _pendingMs < ReplyTimeoutMs) return;
        LastMiss = $"No reply to {Def(_pendingKey)?.Command ?? "/top"}.";
        _pendingKey = null;
        Revision++;
    }

    /// <summary>Turns the automatic refresh on or off; turned on, the first board goes soon.</summary>
    public void SetAutoRefresh(bool on, long nowMs)
    {
        AutoRefresh = on;
        if (on && _nextAutoMs != long.MaxValue && _nextAutoMs > nowMs + 5_000) _nextAutoMs = nowMs + 5_000;
    }

    // ── Reading the replies ───────────────────────────────────────────────

    /// <summary>
    /// An incoming chat message on DreamWeave. True when it should be kept out of the chat window: it answers
    /// RynthDreamWeave's own request. Any board reply (a typed one too) updates the boards.
    /// </summary>
    public bool OnChatText(string? text, int chatType, long nowMs, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(text) || ChatSource.IsPlayerAuthored(chatType)) return false;
        Expire(nowMs);
        bool cut = text.Length >= EngineMaxChars;
        string[] lines = text.Split('\n');
        int count = lines.Length;
        if (cut && count > 1) count--;   // the last line is incomplete
        bool any = false, ours = false;
        BoardResult? headed = null;
        for (int i = 0; i < count; i++)
        {
            string line = lines[i].TrimEnd('\r', ' ', '\t');
            if (i == 0) line = line.TrimStart();
            if (line.Length == 0) continue;
            if (i == 0 && ReadFirstLine(line, nowMs, utcNow, ref ours, out headed))
            {
                any = true;
                continue;
            }
            if (i == 0) { if (!ReadBlockLine(line, nowMs, ref ours)) return false; any = true; continue; }
            if (_block == null) break;
            if (ReadBlockLine(line, nowMs, ref ours)) any = true;
        }
        if (cut && headed != null && ReferenceEquals(headed, _block))
        {
            headed.Cut = true;
            if (headed.Below == null && headed.Note.Length == 0) headed.Note = "The list was cut short: it is longer than RynthCore hands a plugin.";
        }
        if (any) { Revision++; }
        return any && ours;
    }

    /// <summary>A header or a one-line answer. False when the line isn't one (it may continue a board).</summary>
    private bool ReadFirstLine(string line, long nowMs, DateTime utcNow, ref bool ours, out BoardResult? headed)
    {
        headed = null;
        Match m;
        string? key = null;
        string title = "";
        if (WinnersHeader.IsMatch(line)) { key = "winners"; title = "Speed Trial Winners"; }
        else if ((m = Header.Match(line)).Success)
        {
            title = m.Groups[1].Value;
            key = KeyOfTitle(title, BoardKind.Standard);
            // Any other title is the speed trial's season ("Season - Dungeon"), when /top speed was just asked.
            if (key == null && Expecting("speed", nowMs)) key = "speed";
        }
        else if ((m = ArenaHeader.Match(line)).Success)
        {
            title = m.Groups[1].Value;
            key = KeyOfTitle(title, BoardKind.Arena);
        }
        else if ((m = ArenaEmpty.Match(line)).Success)
        {
            string? k = KeyOfTitle(m.Groups[1].Value, BoardKind.Arena);
            if (k == null) return false;
            BoardResult empty = Start(k, m.Groups[1].Value, nowMs, utcNow, ref ours);
            empty.Note = "No one is ranked yet.";
            _block = null;
            return true;
        }
        else if (Refusal.IsMatch(line))
        {
            // A one-line refusal answers whichever /top was just sent; the rows read before stay.
            string? k = _pendingKey ?? (_expectKey != null && nowMs - _expectMs < ReplyTimeoutMs ? _expectKey : null);
            if (k == null) return false;
            ours = Answer(k, nowMs) || ours;
            BoardResult r = Get(k);
            r.Note = line;
            Dirty = true;
            _block = null;
            return true;
        }

        if (key == null) return false;
        headed = Start(key, title, nowMs, utcNow, ref ours);
        return true;
    }

    private BoardResult Start(string key, string title, long nowMs, DateTime utcNow, ref bool ours)
    {
        bool mine = Answer(key, nowMs);
        ours = mine || ours;
        var r = new BoardResult { Key = key, Title = title, UpdatedUtc = utcNow, UpdatedMs = nowMs };
        _results[key] = r;
        _block = r;
        _blockOurs = mine;
        _blockMs = nowMs;
        _rowsSeen = 0;
        _belowNext = false;
        Dirty = true;
        return r;
    }

    /// <summary>The reply for <paramref name="key"/> came: true when it answers RynthDreamWeave's own request.</summary>
    private bool Answer(string key, long nowMs)
    {
        bool mine = _pendingKey == key && nowMs - _pendingMs < ReplyTimeoutMs;
        if (mine) _pendingKey = null;
        if (_expectKey == key) _expectKey = null;
        return mine;
    }

    private bool Expecting(string key, long nowMs)
        => (_pendingKey == key && nowMs - _pendingMs < ReplyTimeoutMs) || (_expectKey == key && nowMs - _expectMs < ReplyTimeoutMs);

    private BoardResult Get(string key)
    {
        if (!_results.TryGetValue(key, out BoardResult? r)) _results[key] = r = new BoardResult { Key = key };
        return r;
    }

    /// <summary>A line of the board being read. False when there is none, or the line isn't part of one.</summary>
    private bool ReadBlockLine(string line, long nowMs, ref bool ours)
    {
        BoardResult? b = _block;
        if (b == null || nowMs - _blockMs > BlockMs) { _block = null; return false; }
        BoardDef? d = Def(b.Key);
        if (d == null) return false;
        Match m;
        if (line == "...") { _belowNext = true; return Took(ref ours, nowMs); }
        if (d.Kind == BoardKind.Arena)
        {
            if (!(m = ArenaRow.Match(line)).Success) return false;
            string detail = m.Groups[3].Success ? $"{m.Groups[3].Value}, {m.Groups[5].Value}-{m.Groups[6].Value}" : $"{m.Groups[5].Value}-{m.Groups[6].Value}";
            Add(b, new BoardRow { Rank = Int(m.Groups[1].Value), Name = m.Groups[2].Value, Value = m.Groups[4].Value, Detail = detail });
            return Took(ref ours, nowMs);
        }
        if (line.StartsWith("No scores have been recorded yet", StringComparison.Ordinal)
            || line.StartsWith("You are exempt from the leaderboards", StringComparison.Ordinal)
            || line.StartsWith("That season has ended", StringComparison.Ordinal))
        {
            b.Note = b.Note.Length > 0 ? b.Note + " " + line : line;
            return Took(ref ours, nowMs);
        }
        if (!(m = Row.Match(line)).Success) return false;
        if (d.Kind == BoardKind.SpeedWinners)
        {
            Add(b, Winner(Int(m.Groups[1].Value), line.Substring(m.Groups[1].Value.Length + 2)));
            return Took(ref ours, nowMs);
        }
        var row = new BoardRow
        {
            Rank = Int(m.Groups[1].Value), Name = m.Groups[2].Value, Value = m.Groups[4].Value,
            Marked = m.Groups[3].Success, Account = m.Groups[3].Value == "your account",
        };
        if (_belowNext || row.Marked) { b.Below = row; _belowNext = false; }
        else Add(b, row);
        return Took(ref ours, nowMs);
    }

    private void Add(BoardResult b, BoardRow row)
    {
        b.Rows.Add(row);
        _rowsSeen++;
    }

    private bool Took(ref bool ours, long nowMs)
    {
        ours = _blockOurs || ours;
        _blockMs = nowMs;
        Dirty = true;
        return true;
    }

    /// <summary>"Season - Dungeon - Name - 3:55.55" or "Season - Dungeon - unclaimed": the season's winner.</summary>
    private static BoardRow Winner(int rank, string rest)
    {
        var row = new BoardRow { Rank = rank };
        if (rest.EndsWith(" - unclaimed", StringComparison.Ordinal))
        {
            row.Detail = rest[..^" - unclaimed".Length];
            row.Name = "unclaimed";
            return row;
        }
        int t = rest.LastIndexOf(" - ", StringComparison.Ordinal);
        int n = t > 0 ? rest.LastIndexOf(" - ", t - 1, StringComparison.Ordinal) : -1;
        if (t < 0 || n < 0) { row.Detail = rest; return row; }
        row.Detail = rest[..n];
        row.Name = rest[(n + 3)..t];
        row.Value = rest[(t + 3)..];
        return row;
    }

    private static string? KeyOfTitle(string title, BoardKind kind)
    {
        foreach (BoardDef d in Defs)
            if (d.Kind == kind && d.ServerTitle.Length > 0 && d.ServerTitle.Equals(title, StringComparison.Ordinal)) return d.Key;
        return null;
    }

    private static int Int(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : 0;

    // ── Where you stand ───────────────────────────────────────────────────

    /// <summary>True for your row: marked "(you)"/"(your account)" by the server, or your name inside the list.</summary>
    public bool IsYou(BoardRow row)
        => row.Marked || (YourName.Length > 0 && row.Name.Equals(YourName, StringComparison.OrdinalIgnoreCase));

    public BoardStanding Standing(string key)
    {
        BoardResult? r = Result(key);
        if (r == null) return new BoardStanding(false, 0, "", false, false);
        foreach (BoardRow row in r.Rows)
            if (IsYou(row)) return new BoardStanding(true, row.Rank, StandingValue(key, row), false, row.Account);
        if (r.Below != null) return new BoardStanding(true, r.Below.Rank, r.Below.Value, true, r.Below.Account);
        return new BoardStanding(true, 0, "", false, false);
    }

    private static string StandingValue(string key, BoardRow row) => Def(key)?.Kind switch
    {
        BoardKind.Arena => $"rating {row.Value} ({row.Detail})",
        BoardKind.SpeedWinners => $"{row.Detail} - {row.Value}",
        _ => row.Value,
    };

    /// <summary>"#5", "#104 (below the list)", "not on it", "not read yet".</summary>
    public string StandingText(string key)
    {
        BoardStanding s = Standing(key);
        if (!s.Read) return "not read yet";
        if (s.Rank == 0) return Result(key)?.Cut == true ? "not in the part read" : "not on it";
        return "#" + s.Rank.ToString(CultureInfo.InvariantCulture) + (s.OutsideList ? " (below the list)" : "") + (s.AccountRow ? " (your account)" : "");
    }

    /// <summary>"updated 3 min ago" for a reading, from <paramref name="utcNow"/>.</summary>
    public static string Age(DateTime updatedUtc, DateTime utcNow)
    {
        if (updatedUtc == DateTime.MinValue) return "not read yet";
        TimeSpan a = utcNow - updatedUtc;
        if (a < TimeSpan.FromMinutes(1)) return "updated just now";
        if (a < TimeSpan.FromHours(1)) return $"updated {(int)a.TotalMinutes} min ago";
        if (a < TimeSpan.FromDays(1)) return $"updated {(int)a.TotalHours} h ago";
        return $"updated {(int)a.TotalDays} d ago";
    }

    // ── The saved boards (hand-written JSON: plugin types never go through the serializer) ──

    public static string SavePath(string settingsFile, uint characterId)
        => Path.Combine(Path.GetDirectoryName(settingsFile) ?? "", "rynthdreamweave.boards." + characterId.ToString("X8", CultureInfo.InvariantCulture) + ".json");

    public static string ToJson(IEnumerable<BoardResult> results)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteStartArray("boards");
            foreach (BoardResult r in results)
            {
                w.WriteStartObject();
                w.WriteString("key", r.Key);
                w.WriteString("title", r.Title);
                w.WriteString("note", r.Note);
                w.WriteString("updated", r.UpdatedUtc == DateTime.MinValue ? "" : r.UpdatedUtc.ToString("o", CultureInfo.InvariantCulture));
                w.WriteBoolean("cut", r.Cut);
                w.WriteStartArray("rows");
                foreach (BoardRow row in r.Rows) WriteRow(w, row);
                w.WriteEndArray();
                if (r.Below != null) { w.WritePropertyName("below"); WriteRow(w, r.Below); }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteRow(Utf8JsonWriter w, BoardRow row)
    {
        w.WriteStartObject();
        w.WriteNumber("r", row.Rank);
        w.WriteString("n", row.Name);
        w.WriteString("v", row.Value);
        if (row.Detail.Length > 0) w.WriteString("d", row.Detail);
        if (row.Marked) w.WriteBoolean("you", true);
        if (row.Account) w.WriteBoolean("acct", true);
        w.WriteEndObject();
    }

    /// <summary>The boards in a saved file; a missing or broken one (or an unknown board) is left out. Never throws.</summary>
    public static List<BoardResult> FromJson(string? json)
    {
        var list = new List<BoardResult>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("boards", out JsonElement boards) || boards.ValueKind != JsonValueKind.Array) return list;
            foreach (JsonElement b in boards.EnumerateArray())
            {
                string key = Str(b, "key");
                if (Def(key) == null || list.Exists(x => x.Key == key)) continue;
                var r = new BoardResult { Key = key, Title = Str(b, "title"), Note = Str(b, "note") };
                if (DateTime.TryParse(Str(b, "updated"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime u)) r.UpdatedUtc = u.ToUniversalTime();
                r.Cut = b.TryGetProperty("cut", out JsonElement c) && c.ValueKind == JsonValueKind.True;
                if (b.TryGetProperty("rows", out JsonElement rows) && rows.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement row in rows.EnumerateArray()) r.Rows.Add(ReadRow(row));
                if (b.TryGetProperty("below", out JsonElement below) && below.ValueKind == JsonValueKind.Object) r.Below = ReadRow(below);
                list.Add(r);
            }
        }
        catch (JsonException)
        {
            list.Clear();
        }
        return list;
    }

    private static BoardRow ReadRow(JsonElement e) => new()
    {
        Rank = e.TryGetProperty("r", out JsonElement r) && r.TryGetInt32(out int rank) ? rank : 0,
        Name = Str(e, "n"),
        Value = Str(e, "v"),
        Detail = Str(e, "d"),
        Marked = e.TryGetProperty("you", out JsonElement y) && y.ValueKind == JsonValueKind.True,
        Account = e.TryGetProperty("acct", out JsonElement a) && a.ValueKind == JsonValueKind.True,
    };

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
