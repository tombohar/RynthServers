// DreamWeaveXpBonusHost.cs - RynthDreamWeave's offline bonus tracker ("double XP time", 2026-11-15), through the
// shared server plugin's hooks (Plugins/Shared/ServerPack/ServerPackPlugin.cs): the row at the top of the
// DreamWeave window's Character page (DreamWeave/XpBonusStrip.cs) and "/rserver xpbonus", fed by the server's own
// lines (DreamWeave/DreamWeaveXpBonus.cs reads them).
//
// Passive: every offline-bonus line the server sends is read and shows as usual - the login line and the status
// line 3 s after it, "now active", "now idle and banking again", "exhausted", /bonus, and the "[BANK] Offline Bonus
// Time" line of every /b (the bank check's own every-minute /b, whose reply the bank keeps out of chat, included).
// With "Read the offline bonus automatically" on (off by default) /offlinebonus also goes 35 s after login and then
// every ten minutes, never within 5 s of the bank's /b or the leaderboards' /top, and its reply is kept out of chat;
// a /offlinebonus the player types shows as usual and still updates the row. Check now (and "/rserver xpbonus
// check") asks once, at the next quiet moment, the automatic read on or off.
// The last reading is kept per character beside the plugin's settings
// (%APPDATA%\RynthCore\rynthdreamweave.xpbonus.<character id>.json), so the row isn't empty after a reload.
//
// The hooks it shares with the bank, the leaderboards, the Skills panel and the web tools are implemented once
// (DreamWeavePlugin.cs, DreamWeaveSkills.cs, DreamWeaveWebTools.cs) and call the XpBonus* methods here.
// Threads: the chat bar note runs on AC's thread (interlocked fields only); everything else on the pump, holding
// _xpBonus.Gate around the reader only (never around Send, Reply or file I/O).
using System;
using System.Collections.Generic;
using System.IO;
using RynthCore.Plugin.RynthDreamWeave;

namespace RynthCore.Plugin.ServerPack;

public sealed partial class ServerPackPlugin
{
    private readonly DreamWeaveXpBonus _xpBonus = new();
    private uint _xpBonusCharacter;
    private long _xpBonusSaveDueMs;
    private bool _xpBonusSaveFailed;
    // A typed "/rserver xpbonus check": its answer (or that none came) is said in chat.
    private bool _xpBonusAnnounce;
    // The last of RynthDreamWeave's own server commands (/b, /top, /offlinebonus), for the gap between them.
    private long _lastServerSendMs = long.MinValue / 2;
    private XpBonusStrip? _xpBonusStrip;
    private const long XpBonusSaveEveryMs = 30_000;

    /// <summary>The Character page's top row (DreamWeaveSkills.AddCharacterPage). Render thread.</summary>
    private void DrawXpBonusStrip()
    {
        _xpBonusStrip ??= new XpBonusStrip(_xpBonus, () => Host, () => Host.PanelScale(ServerPackModel.WindowTitle(Info)),
            RunOnTick, () => XpBonusCheck(typed: false), on => XpBonusSetAuto(on, typed: false));
        _xpBonusStrip.Draw();
    }

    /// <summary>One of RynthDreamWeave's own server commands just went out (the gap the offline bonus read keeps).</summary>
    private void NoteServerSend() => _lastServerSendMs = Environment.TickCount64;

    private void XpBonusInitialize()
    {
        lock (_xpBonus.Gate) _xpBonus.LoadSettings(Settings.GetBool);
    }

    private void XpBonusLogin()
    {
        _xpBonusCharacter = 0;
        _xpBonusAnnounce = false;
        lock (_xpBonus.Gate) _xpBonus.Login(Environment.TickCount64);
    }

    private void XpBonusLogout()
    {
        XpBonusSave();
        lock (_xpBonus.Gate) _xpBonus.Logout();
        _xpBonusCharacter = 0;
        _xpBonusAnnounce = false;
    }

    private void XpBonusShutdown() => XpBonusSave();

    /// <summary>AC's thread: a line through the chat bar. Our own /offlinebonus, or one the player typed?</summary>
    private void XpBonusChatBar(string text)
    {
        if (DreamWeaveXpBonus.IsCommand(text)) _xpBonus.NoteChatBar();
    }

    /// <summary>An incoming chat line on DreamWeave: true when it answers RynthDreamWeave's own /offlinebonus.</summary>
    private bool XpBonusChatText(string text, int chatType)
    {
        bool ours;
        string line;
        lock (_xpBonus.Gate)
        {
            ours = _xpBonus.OnChatText(text, chatType, Environment.TickCount64, DateTime.UtcNow);
            line = _xpBonus.Line(Environment.TickCount64);
        }
        if (ours && _xpBonusAnnounce)
        {
            _xpBonusAnnounce = false;
            Reply.Ok(line + ".");
        }
        return ours;
    }

    /// <summary>
    /// The pump's tick, after the bank's and the boards': the automatic (or asked-for) read when one is due and none
    /// of RynthDreamWeave's other commands went or is going within DreamWeaveXpBonus.GapMs.
    /// </summary>
    private void XpBonusTick()
    {
        long now = Environment.TickCount64;
        uint player = Host.HasGetPlayerId ? Host.GetPlayerId() : 0;
        if (player != 0 && player != _xpBonusCharacter) XpBonusLoadFor(player);
        bool canSend = player != 0 && Host.HasInvokeChatParser && !(Host.HasIsPortaling && Host.IsPortaling());
        long next;
        lock (_xpBonus.Gate) next = _xpBonus.NextReadMs;
        bool quiet = next <= now && XpBonusQuiet(now);   // the other gates are taken one at a time, never nested
        string? command;
        bool missed;
        lock (_xpBonus.Gate)
        {
            missed = _xpBonus.Tick(now);
            command = _xpBonus.Due(now, canSend, quiet);
        }
        if (missed && _xpBonusAnnounce)
        {
            _xpBonusAnnounce = false;
            Reply.Fail($"No reply to {DreamWeaveXpBonus.Command}.");
        }
        if (command != null)
        {
            if (Send(command)) NoteServerSend();
            else lock (_xpBonus.Gate) _xpBonus.NotSent();
        }
        if (now >= _xpBonusSaveDueMs) XpBonusSave();
    }

    /// <summary>No /b or /top went in the last few seconds, none is due in the next few, and no board reply is awaited.</summary>
    private bool XpBonusQuiet(long now)
    {
        const long gap = DreamWeaveXpBonus.GapMs;
        if (now - _lastServerSendMs < gap) return false;
        long bankNext;
        lock (_bank.Gate) bankNext = _bank.NextCheckMs;
        if (bankNext - now < gap && bankNext - now > -gap) return false;
        lock (_boards.Gate)
        {
            if (_boards.Pending || _boards.SweepLeft > 0) return false;
            long boardsNext = _boards.NextAutoMs;
            if (boardsNext - now < gap && boardsNext - now > -gap) return false;
        }
        return true;
    }

    /// <summary>The reading saved for this character (it doesn't replace one read since login).</summary>
    private void XpBonusLoadFor(uint player)
    {
        _xpBonusCharacter = player;
        string? json = null;
        try
        {
            string path = DreamWeaveXpBonus.SavePath(Settings.FilePath, player);
            if (File.Exists(path)) json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            LogTagged("The saved offline bonus wasn't read: " + ex.GetType().Name);
        }
        lock (_xpBonus.Gate) _xpBonus.LoadSaved(json);
    }

    /// <summary>Writes the reading when it changed (looked at every 30 s, and at logout and shutdown). Pump thread.</summary>
    private void XpBonusSave()
    {
        _xpBonusSaveDueMs = Environment.TickCount64 + XpBonusSaveEveryMs;
        uint player = _xpBonusCharacter;
        if (player == 0) return;
        string json;
        lock (_xpBonus.Gate)
        {
            if (!_xpBonus.Dirty || !_xpBonus.HasReading) return;
            json = _xpBonus.SaveJson();
        }
        try
        {
            string path = DreamWeaveXpBonus.SavePath(Settings.FilePath, player);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
            _xpBonusSaveFailed = false;
        }
        catch (Exception ex)
        {
            if (!_xpBonusSaveFailed) LogTagged("The offline bonus wasn't saved: " + ex.GetType().Name + ": " + ex.Message);
            _xpBonusSaveFailed = true;
        }
    }

    // ── What the row's buttons and "/rserver xpbonus" do (pump thread) ──

    private const string XpBonusNotDreamWeave = "The offline bonus is DreamWeave's; RynthCore doesn't see this world as DreamWeave.";

    private void XpBonusCheck(bool typed)
    {
        if (!Active) { Reply.Fail(XpBonusNotDreamWeave); return; }
        if (!Host.HasInvokeChatParser) { Reply.Fail($"This RynthCore can't send {DreamWeaveXpBonus.Command} for a plugin."); return; }
        long now = Environment.TickCount64;
        lock (_xpBonus.Gate)
        {
            if (_xpBonus.Waiting(now))
            {
                if (typed) Reply.Fail("Still waiting for the last offline bonus reply; try again in a few seconds.");
                return;
            }
            _xpBonus.CheckSoon(now);
        }
        if (!typed) return;
        _xpBonusAnnounce = true;
        Reply.Ok($"Asking DreamWeave ({DreamWeaveXpBonus.Command}, at the next quiet moment); the answer follows here, not the server's line.");
    }

    private void XpBonusSetAuto(bool on, bool typed)
    {
        lock (_xpBonus.Gate) _xpBonus.SetAutoRead(on, Environment.TickCount64);
        if (Settings.SetBool(DreamWeaveXpBonus.SettingAutoRead, on)) Settings.Save();
        if (typed)
            Reply.Ok(on ? $"The offline bonus is read automatically: {DreamWeaveXpBonus.Command} after login and every ten minutes, its reply kept out of chat."
                        : "The offline bonus is no longer read automatically (the server's own lines and every /b still update it).");
    }

    private void XpBonusCommandVerbs(List<string> verbs) => verbs.Add("xpbonus");

    private const string XpBonusUsage = "/rserver xpbonus [check | auto on|off]: DreamWeave's offline bonus (double XP) time, also on the window's Character tab";

    private void XpBonusChatCommand(string[] parts, ref bool handled)
    {
        if (parts.Length < 2 || !parts[1].Equals("xpbonus", StringComparison.OrdinalIgnoreCase)) return;
        handled = true;
        string verb = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        string arg = parts.Length > 3 ? parts[3].ToLowerInvariant() : "";
        switch (verb)
        {
            case "":
            case "status":
            {
                if (!Active) { Reply.Fail(XpBonusNotDreamWeave); return; }
                string line, detail;
                bool auto;
                lock (_xpBonus.Gate)
                {
                    long now = Environment.TickCount64;
                    line = _xpBonus.Line(now);
                    detail = _xpBonus.Detail(now, DateTime.UtcNow);
                    auto = _xpBonus.AutoRead;
                }
                Reply.Ok($"{line}. {detail}. Automatic read {(auto ? "on" : "off")}.");
                return;
            }
            case "check":
            case "refresh":
                XpBonusCheck(typed: true);
                return;
            case "auto":
                if (arg == "on") XpBonusSetAuto(true, typed: true);
                else if (arg == "off") XpBonusSetAuto(false, typed: true);
                else Reply.Fail("Usage: /rserver xpbonus auto on|off");
                return;
        }
        Reply.Fail("Usage: " + XpBonusUsage);
    }

    private void XpBonusInfoLines(Action<string> line)
    {
        lock (_xpBonus.Gate)
        {
            long now = Environment.TickCount64;
            line($"{_xpBonus.Line(now)} ({_xpBonus.Detail(now, DateTime.UtcNow)}); automatic read {(_xpBonus.AutoRead ? "on" : "off")} (/rserver xpbonus).");
        }
    }
}
