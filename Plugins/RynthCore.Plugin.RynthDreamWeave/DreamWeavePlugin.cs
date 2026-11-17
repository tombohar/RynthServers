// DreamWeavePlugin.cs - RynthDreamWeave's own features, through the shared server plugin's hooks
// (Plugins/Shared/ServerPack/ServerPackPlugin.cs): the every-minute bank check that gives Lum/hr its
// luminance on DreamWeave (DreamWeave/DreamWeaveBank.cs, 2026-11-02), published as the profile value
// "bankLuminance" for the session stats, and its Bank page (DreamWeave/DreamWeavePages.cs).
// The leaderboards (DreamWeaveBoardsHost.cs, 2026-11-15) share these hooks too: each calls its Boards* method;
// so does the offline bonus tracker (DreamWeaveXpBonusHost.cs, 2026-11-15, its XpBonus* methods), which reads the
// bank check's [BANK] lines as well and keeps its own /offlinebonus clear of the bank's /b and the boards' /top.
// The website buttons and verbs (DreamWeaveWebTools.cs) share these hooks: each one below hands off to
// its Web* method first, so a launch-code line is read whatever the world is and the bank's own gates
// stay as they were.
using System;
using RynthCore.Plugin.RynthDreamWeave;

namespace RynthCore.Plugin.ServerPack;

public sealed partial class ServerPackPlugin
{
    private readonly DreamWeaveBank _bank = new();

    partial void ServerPages(System.Collections.Generic.List<ServerPage> pages)
    {
        var bankPages = new DreamWeavePages(_bank, () => Active, RunOnTick, CheckBankNow, SaveDreamWeaveSettings);
        pages.AddRange(bankPages.Pages());
        AddBoardsPage(pages);      // DreamWeaveBoardsHost.cs: the Leaderboards page
        AddCharacterPage(pages);   // DreamWeaveSkills.cs: the Character panel's page
    }

    partial void ServerInitialize()
    {
        lock (_bank.Gate) _bank.LoadSettings(Settings);
        BoardsInitialize();
        XpBonusInitialize();
    }

    private void SaveDreamWeaveSettings()
    {
        lock (_bank.Gate) _bank.SaveSettings(Settings);
    }

    partial void ServerLoginComplete()
    {
        WebLoginComplete();
        lock (_bank.Gate) _bank.Login(Environment.TickCount64);
        BoardsLogin();
        XpBonusLogin();
    }

    partial void ServerLogout()
    {
        WebLogout();
        lock (_bank.Gate) _bank.Logout();
        SkillsLogout();
        BoardsLogout();
        XpBonusLogout();
    }

    partial void ServerShutdown()
    {
        WebShutdown();
        SaveDreamWeaveSettings();
        BoardsShutdown();
        XpBonusShutdown();
    }

    partial void ServerChatText(string text, int chatType, ref bool eat)
    {
        WebChatText(text, chatType, ref eat);
        if (!Active) return;
        if (BoardsChatText(text, chatType)) eat = true;
        // Before the bank: it reads the [BANK] Offline Bonus Time line too (the bank may hide it; it is read anyway).
        if (XpBonusChatText(text, chatType)) eat = true;
        bool ours;
        long? value;
        lock (_bank.Gate)
        {
            ours = _bank.OnChatLine(text, Environment.TickCount64);
            value = _bank.Published;
        }
        if (value is long v) SetProfileValue(DreamWeaveBank.LuminanceKey, v);
        if (ours) eat = true;
    }

    partial void ServerChatBar(string text)
    {
        // The player's own /b: its reply shows (DreamWeaveBank tells it from RynthDreamWeave's).
        if (DreamWeaveBank.IsBalanceCommand(text)) _bank.NoteTypedCommand();
        BoardsChatBar(text);
        XpBonusChatBar(text);
    }

    partial void ServerTick(bool active)
    {
        WebTick();
        if (!active) return;
        BoardsTick();
        string? check;
        lock (_bank.Gate)
        {
            _bank.Tick();
            check = _bank.CheckDue(Environment.TickCount64);
        }
        if (check != null)
        {
            if (Send(check)) NoteServerSend();
            else lock (_bank.Gate) _bank.NotSent();
        }
        XpBonusTick();   // after the bank and the boards: it goes only when neither just went or is about to
    }

    /// <summary>The page's Check now (on the tick).</summary>
    private void CheckBankNow()
    {
        if (!Active) return;
        string command;
        lock (_bank.Gate) command = _bank.Request(Environment.TickCount64);
        if (Send(command)) NoteServerSend();
        else
        {
            lock (_bank.Gate) _bank.NotSent();
            ChatFail($"[{RynthPluginInfo.Name}] Couldn't send {command}.");
        }
    }

    partial void ServerInfoLines(Action<string> line)
    {
        lock (_bank.Gate)
        {
            string lum = _bank.Luminance is long l ? l.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : "not read yet";
            line($"Bank check every minute {(_bank.CheckEveryMinute ? "on" : "off")}; banked luminance {lum}.");
        }
        BoardsInfoLines(line);
        XpBonusInfoLines(line);
        WebInfoLines(line);
    }
}
