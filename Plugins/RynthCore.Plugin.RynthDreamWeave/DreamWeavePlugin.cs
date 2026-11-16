// DreamWeavePlugin.cs - RynthDreamWeave's own features, through the shared server plugin's hooks
// (Plugins/Shared/ServerPack/ServerPackPlugin.cs): the every-minute bank check that gives Lum/hr its
// luminance on DreamWeave (DreamWeave/DreamWeaveBank.cs, 2026-11-02), published as the profile value
// "bankLuminance" for the session stats, and its Bank page (DreamWeave/DreamWeavePages.cs).
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
        AddCharacterPage(pages);   // DreamWeaveSkills.cs: the Character panel's page
    }

    partial void ServerInitialize()
    {
        lock (_bank.Gate) _bank.LoadSettings(Settings);
    }

    private void SaveDreamWeaveSettings()
    {
        lock (_bank.Gate) _bank.SaveSettings(Settings);
    }

    partial void ServerLoginComplete()
    {
        WebLoginComplete();
        lock (_bank.Gate) _bank.Login(Environment.TickCount64);
    }

    partial void ServerLogout()
    {
        WebLogout();
        lock (_bank.Gate) _bank.Logout();
    }

    partial void ServerShutdown()
    {
        WebShutdown();
        SaveDreamWeaveSettings();
    }

    partial void ServerChatText(string text, int chatType, ref bool eat)
    {
        WebChatText(text, chatType, ref eat);
        if (!Active) return;
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
    }

    partial void ServerTick(bool active)
    {
        WebTick();
        if (!active) return;
        string? check;
        lock (_bank.Gate)
        {
            _bank.Tick();
            check = _bank.CheckDue(Environment.TickCount64);
        }
        if (check != null && !Send(check)) lock (_bank.Gate) _bank.NotSent();
    }

    /// <summary>The page's Check now (on the tick).</summary>
    private void CheckBankNow()
    {
        if (!Active) return;
        string command;
        lock (_bank.Gate) command = _bank.Request(Environment.TickCount64);
        if (!Send(command))
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
        WebInfoLines(line);
    }
}
