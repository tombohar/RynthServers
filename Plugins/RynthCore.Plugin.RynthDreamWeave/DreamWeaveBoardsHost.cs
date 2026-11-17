// DreamWeaveBoardsHost.cs - RynthDreamWeave's Leaderboards (2026-11-15), through the shared server plugin's
// hooks (Plugins/Shared/ServerPack/ServerPackPlugin.cs): the Leaderboards page (DreamWeave/BoardsPage.cs) and
// "/rserver boards", fed by DreamWeave's own /top (DreamWeave/DreamWeaveBoards.cs reads the replies).
//
// With "Refresh leaderboards automatically" on (off by default) one board's /top goes every 35 s while the
// character is in the world and not in portal space, so each of the 17 boards is about ten minutes old at most;
// the replies to RynthDreamWeave's own requests are kept out of chat, a /top the player types shows as usual and
// still updates the page. One request at a time; a reply that doesn't come in 6 s is given up.
// The last reading of every board is kept per character beside the plugin's settings
// (%APPDATA%\RynthCore\rynthdreamweave.boards.<character id>.json), so the page isn't empty after a reload.
//
// The hooks it shares with the bank, the Skills panel and the web tools are implemented once (DreamWeavePlugin.cs,
// DreamWeaveSkills.cs, DreamWeaveWebTools.cs) and call the Boards* methods here.
// Threads: the chat bar note runs on AC's thread (interlocked counter and a queue only); everything else on the
// pump, holding _boards.Gate around the reader only (never around Send, Reply or file I/O).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using RynthCore.Plugin.RynthDreamWeave;

namespace RynthCore.Plugin.ServerPack;

public sealed partial class ServerPackPlugin
{
    private readonly DreamWeaveBoards _boards = new();
    // Our own "/top" sends not yet seen by the chat bar (0 or 1): tells them from a /top the player typed.
    private int _boardSelfSends;
    // /top lines the player typed (AC's thread), read on the pump.
    private readonly ConcurrentQueue<string> _typedTops = new();
    private uint _boardsCharacter;
    private long _boardsSaveDueMs;
    private bool _boardsSaveFailed;
    private const long BoardsSaveEveryMs = 30_000;

    private void AddBoardsPage(List<ServerPage> pages)
    {
        var page = new BoardsPage(_boards, () => Active, () => Host,
            () => Host.PanelScale(ServerPackModel.WindowTitle(Info)), RunOnTick,
            key => BoardsRefresh(key, typed: false), () => BoardsSweep(typed: false), on => BoardsSetAuto(on, typed: false));
        pages.Add(page.Page());
    }

    private void BoardsInitialize()
    {
        lock (_boards.Gate) _boards.LoadSettings(Settings.GetBool);
    }

    private void BoardsLogin()
    {
        while (_typedTops.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _boardSelfSends, 0);
        _boardsCharacter = 0;
        lock (_boards.Gate) _boards.Login(Environment.TickCount64);
    }

    private void BoardsLogout()
    {
        BoardsSave(force: true);
        lock (_boards.Gate) _boards.Logout();
        _boardsCharacter = 0;
    }

    private void BoardsShutdown() => BoardsSave(force: true);

    /// <summary>AC's thread: a line through the chat bar. Our own /top, or one the player typed?</summary>
    private void BoardsChatBar(string text)
    {
        if (DreamWeaveBoards.KeyOfCommand(text) == null) return;
        if (Interlocked.Decrement(ref _boardSelfSends) >= 0) return;
        Interlocked.Exchange(ref _boardSelfSends, 0);
        _typedTops.Enqueue(text);
    }

    /// <summary>An incoming chat line on DreamWeave: true when it answers RynthDreamWeave's own /top.</summary>
    private bool BoardsChatText(string text, int chatType)
    {
        long now = Environment.TickCount64;
        lock (_boards.Gate)
        {
            while (_typedTops.TryDequeue(out string? typed)) _boards.NoteTyped(typed, now);
            return _boards.OnChatText(text, chatType, now, DateTime.UtcNow);
        }
    }

    private void BoardsTick()
    {
        long now = Environment.TickCount64;
        uint player = Host.HasGetPlayerId ? Host.GetPlayerId() : 0;
        if (player != 0 && player != _boardsCharacter) BoardsLoadFor(player);
        bool canSend = player != 0 && Host.HasInvokeChatParser && !(Host.HasIsPortaling && Host.IsPortaling());
        string? command;
        lock (_boards.Gate)
        {
            while (_typedTops.TryDequeue(out string? typed)) _boards.NoteTyped(typed, now);
            if (!_boards.Pending) Interlocked.Exchange(ref _boardSelfSends, 0);
            command = _boards.Due(now, canSend);
        }
        if (command != null) SendBoardCommand(command);
        if (now >= _boardsSaveDueMs) BoardsSave(force: false);
    }

    /// <summary>Sends a /top the boards asked for (marked as ours first); false when it didn't go.</summary>
    private bool SendBoardCommand(string command)
    {
        Interlocked.Exchange(ref _boardSelfSends, 1);
        if (Send(command)) { NoteServerSend(); return true; }   // DreamWeaveXpBonusHost.cs keeps clear of it
        Interlocked.Exchange(ref _boardSelfSends, 0);
        lock (_boards.Gate) _boards.NotSent();
        return false;
    }

    /// <summary>The boards saved for this character, and its name (to find your row inside a list).</summary>
    private void BoardsLoadFor(uint player)
    {
        _boardsCharacter = player;
        string name = Host.HasGetObjectName && Host.TryGetObjectName(player, out string n) ? n.Trim() : "";
        string? json = null;
        try
        {
            string path = DreamWeaveBoards.SavePath(Settings.FilePath, player);
            if (File.Exists(path)) json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            LogTagged("The saved leaderboards weren't read: " + ex.GetType().Name);
        }
        lock (_boards.Gate)
        {
            _boards.YourName = name;
            _boards.LoadSaved(json);
        }
    }

    /// <summary>Writes the boards read since the last save (at most every 30 s, and at logout). Pump thread.</summary>
    private void BoardsSave(bool force)
    {
        _boardsSaveDueMs = Environment.TickCount64 + BoardsSaveEveryMs;
        uint player = _boardsCharacter;
        if (player == 0) return;
        string json;
        lock (_boards.Gate)
        {
            if (!_boards.Dirty || (!force && _boards.Pending)) return;
            json = _boards.SaveJson();
        }
        try
        {
            string path = DreamWeaveBoards.SavePath(Settings.FilePath, player);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            _boardsSaveFailed = false;
        }
        catch (Exception ex)
        {
            if (!_boardsSaveFailed) LogTagged("The leaderboards weren't saved: " + ex.GetType().Name + ": " + ex.Message);
            _boardsSaveFailed = true;
        }
    }

    // ── What the page's buttons and "/rserver boards" do (pump thread) ──

    private void BoardsRefresh(string key, bool typed)
    {
        BoardDef? d = DreamWeaveBoards.Def(key);
        if (d == null) return;
        if (!Active) { Reply.Fail("The leaderboards are DreamWeave's; RynthCore doesn't see this world as DreamWeave."); return; }
        string? command;
        lock (_boards.Gate)
        {
            if (_boards.Pending) command = null;
            else command = _boards.Request(key, Environment.TickCount64);
        }
        if (command == null) { Reply.Fail("Still waiting for the last leaderboard reply; try again in a few seconds."); return; }
        if (!SendBoardCommand(command)) { Reply.Fail($"Couldn't send {command} (not in the world)."); return; }
        if (typed) Reply.Ok($"Reading the {d.Label} board ({command}).");
    }

    private void BoardsSweep(bool typed)
    {
        if (!Active) { Reply.Fail("The leaderboards are DreamWeave's; RynthCore doesn't see this world as DreamWeave."); return; }
        lock (_boards.Gate) _boards.SweepAll();
        if (typed)
            Reply.Ok($"Reading all {DreamWeaveBoards.Defs.Length} boards, one every {DreamWeaveBoards.SweepIntervalMs / 1000} seconds; their replies stay out of chat.");
    }

    private void BoardsSetAuto(bool on, bool typed)
    {
        lock (_boards.Gate) _boards.SetAutoRefresh(on, Environment.TickCount64);
        if (Settings.SetBool(DreamWeaveBoards.SettingAutoRefresh, on)) Settings.Save();
        if (typed)
            Reply.Ok(on ? "Leaderboards refresh automatically: one board every 35 seconds, each about every ten minutes."
                        : "Leaderboards no longer refresh automatically.");
    }

    private void BoardsCommandVerbs(List<string> verbs) => verbs.Add("boards");

    private const string BoardsUsage = "/rserver boards [<board> | refresh <board>|all | auto on|off]: DreamWeave's leaderboards (/top), also the window's Leaderboards tab";

    private void BoardsChatCommand(string[] parts, ref bool handled)
    {
        if (parts.Length < 2 || !parts[1].Equals("boards", StringComparison.OrdinalIgnoreCase)) return;
        handled = true;
        string verb = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        string rest = parts.Length > 3 ? string.Join(' ', parts.Skip(3)) : "";
        switch (verb)
        {
            case "":
            {
                int read;
                bool auto;
                lock (_boards.Gate) { read = _boards.ReadCount; auto = _boards.AutoRefresh; }
                Reply.Ok($"Leaderboards: {read} of {DreamWeaveBoards.Defs.Length} boards read; automatic refresh {(auto ? "on" : "off")}. "
                         + "The DreamWeave window's Leaderboards tab shows them.");
                return;
            }
            case "auto":
                if (rest.Equals("on", StringComparison.OrdinalIgnoreCase)) BoardsSetAuto(true, typed: true);
                else if (rest.Equals("off", StringComparison.OrdinalIgnoreCase)) BoardsSetAuto(false, typed: true);
                else Reply.Fail("Usage: /rserver boards auto on|off");
                return;
            case "refresh":
                if (rest.Equals("all", StringComparison.OrdinalIgnoreCase)) { BoardsSweep(typed: true); return; }
                if (DreamWeaveBoards.KeyOfName(rest) is string key) { BoardsRefresh(key, typed: true); return; }
                Reply.Fail(rest.Length == 0 ? "Usage: /rserver boards refresh <board>|all" : UnknownBoard(rest));
                return;
        }
        string name = string.Join(' ', parts.Skip(2));
        if (DreamWeaveBoards.KeyOfName(name) is not string k) { Reply.Fail(UnknownBoard(name)); return; }
        BoardDef d = DreamWeaveBoards.Def(k)!;
        string line;
        lock (_boards.Gate)
        {
            BoardResult? r = _boards.Result(k);
            BoardStanding st = _boards.Standing(k);
            line = r == null
                ? $"{d.Label}: not read yet. /rserver boards refresh {k} reads it."
                : $"{d.Label}: {_boards.StandingText(k)}{(st.Rank > 0 && st.Value.Length > 0 ? " (" + st.Value + ")" : "")}, "
                  + DreamWeaveBoards.Age(r.UpdatedUtc, DateTime.UtcNow) + ".";
        }
        Reply.Ok(line);
    }

    private static string UnknownBoard(string name)
        => $"No board called '{name}'. Boards: {string.Join(", ", DreamWeaveBoards.Defs.Select(d => d.Key == "winners" ? "speed winners" : d.Key))}.";

    private void BoardsInfoLines(Action<string> line)
    {
        lock (_boards.Gate)
            line($"Leaderboards: {_boards.ReadCount} of {DreamWeaveBoards.Defs.Length} read; automatic refresh {(_boards.AutoRefresh ? "on" : "off")} (/rserver boards).");
    }
}
