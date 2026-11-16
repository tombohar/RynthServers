// DreamWeaveSkills.cs - RynthDreamWeave's Character panel (2026-11-07), through the shared server plugin's
// hooks (Plugins/Shared/ServerPack/ServerPackPlugin.cs): retail's Character window, Attributes, Skills and
// Titles, drawn 1:1 (DreamWeave/SkillsPanel.cs), whose "XP for next level" keeps reading past level 275 on
// DreamWeave's own chart (DreamWeave/DreamWeaveXp.cs) where AC's panel goes negative.
//
// It is a window of its own on RynthCore's bar ("DreamWeave Skills", only on DreamWeave, frameless, drawn
// under AC's own UI like a native window), also opened with "/rserver skills". With "Open this instead of
// AC's Character window" (on by default; the panel's right-click menu, or /rserver skills replace on|off)
// AC's own Character window (toolbar button or key) opens this one instead. AC's Character Information
// window (the mini bar's backpack button) stays AC's own (2026-11-08), one of the main panels.
//
// Titles come from the server's game events 0x0029 (the list, at login) and 0x002B (one title), kept per
// character in the plugin's settings so a hot reload still has them; "Set as Display Title" sends
// "/rc title <id>" (the engine calls AC's own sender).
//
// Hooks used: ServerWindows, ServerCommandVerbs, ServerChatCommand, ServerMessageInterest, ServerMessage,
// ServerRender - none of the other DreamWeave features' (the bank check's), so they sit side by side.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using RynthCore.Plugin.RynthDreamWeave;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.ServerPack;

public sealed partial class ServerPackPlugin
{
    internal const string SkillsWindowId = "skills";
    internal const string SkillsWindowTitle = "DreamWeave Skills";
    private const string ReplaceAcSetting = "skillsReplaceAcCharacter";
    private const string OnePanelSetting = "skillsOnePanelAtATime";
    private const uint EventCharacterTitle = 0x0029, EventUpdateTitle = 0x002B;

    private readonly SkillsReader _skillsReader = new();
    private SkillsPanel? _skillsPanel;
    private int _replaceAc = -1;   // -1 not read yet (the settings are the pump's), 0 off, 1 on
    private int _onePanel = 1;     // one main panel at a time (OnePanelRule); read with _replaceAc
    private readonly OnePanelRule _onePanelRule = new();
    private bool _skillsStoreSet;

    partial void ServerWindows(List<ServerWindow> windows)
    {
        int w = RynthCore.Plugin.RynthDreamWeave.SkillsPanel.PanelW + 2 * RynthCore.Plugin.RynthDreamWeave.SkillsPanel.Frame;
        // fitHeight: the panel resizes itself from its bottom edge, as retail's Character window (SkillsPanel).
        // underUi: AC's own windows cover it, as they cover each other.
        // DrawScale: its size is its own (Ctrl+wheel, 30-250%), 100% = retail's pixels, not the text size.
        // The minimums are small on purpose: the window is in RynthCore's DPI-scaled units, and the panel (retail's
        // own pixels at 100%) fits it to itself; a 310 minimum held it 1.6x wide at 160% display scaling.
        windows.Add(new ServerWindow(SkillsWindowId, SkillsWindowTitle, "", w, 610, 120, 120, Frameless: true, DrawSkills,
            FitHeight: true, UnderUi: true, DrawScale: true));
    }

    private RynthCore.Plugin.RynthDreamWeave.SkillsPanel Panel()
    {
        if (!_skillsStoreSet)
        {
            _skillsStoreSet = true;
            // Pump thread (the reader's tick and the server messages): the plugin's settings, one key per character.
            _skillsReader.SetTitleStore(
                id => Settings.GetString(TitlesKey(id), ""),
                (id, saved) => { Settings.SetString(TitlesKey(id), saved); Settings.Save(); });
        }
        return _skillsPanel ??= new SkillsPanel(Host, _skillsReader, SkillsWindowTitle, RunOnTick, ChatLine,
            () => Volatile.Read(ref _replaceAc) != 0, SetReplaceAc);
    }

    private static string TitlesKey(uint characterId) => "titles." + characterId.ToString("X8", CultureInfo.InvariantCulture);

    private System.Numerics.Vector2 DrawSkills(bool popped) => Panel().Draw(popped);

    // ── The DreamWeave window's Character page (open it, its setting: no chat commands needed) ──

    /// <remarks>
    /// The bank check (DreamWeavePlugin.cs) implements ServerPages and calls this for the Character page.
    /// </remarks>
    private void AddCharacterPage(List<ServerPage> pages)
    {
        var page = new CharacterPage(
            active: () => Active,
            panelState: () => Host.HasSetPanelOpen ? Host.SetPanelOpen(SkillsWindowTitle, RynthPanelOpen.Ask) : -1,
            open: tab =>
            {
                Panel().ShowPage(tab);
                Host.SetPanelOpen(SkillsWindowTitle, RynthPanelOpen.Open);
            },
            close: () => Host.SetPanelOpen(SkillsWindowTitle, RynthPanelOpen.Close),
            replacesAc: () => Volatile.Read(ref _replaceAc) != 0,
            setReplacesAc: SetReplaceAc,
            onePanel: () => Volatile.Read(ref _onePanel) != 0,
            setOnePanel: SetOnePanel);
        pages.Add(page.Page());
    }

    // ── Opening it instead of AC's Character window ─────────────────────

    private void SetReplaceAc(bool on)
    {
        Volatile.Write(ref _replaceAc, on ? 1 : 0);
        RunOnTick(() => { Settings.SetBool(ReplaceAcSetting, on); Settings.Save(); });
    }

    /// <summary>AC's main panels, the ones that share retail's panel frame (not the Character window: that is this panel).
    /// The Character Information window (the mini bar's backpack button) stays AC's own and is one of them.</summary>
    private static readonly RynthAcPanel[] MainAcPanels =
    {
        RynthAcPanel.Book, RynthAcPanel.HelpfulSpells, RynthAcPanel.HarmfulSpells, RynthAcPanel.Inventory,
        RynthAcPanel.LinkStatus, RynthAcPanel.Options, RynthAcPanel.Social, RynthAcPanel.Spellbook,
        RynthAcPanel.Help, RynthAcPanel.Vitae, RynthAcPanel.Map, RynthAcPanel.Quests, RynthAcPanel.CharacterInfo,
    };

    private void SetOnePanel(bool on)
    {
        Volatile.Write(ref _onePanel, on ? 1 : 0);
        RunOnTick(() => { Settings.SetBool(OnePanelSetting, on); Settings.Save(); });
    }

    /// <summary>
    /// Every frame (AC's thread): one main panel at a time, as retail - this panel opening closes AC's open main
    /// panel (inventory, spellbook, social, options, map...), and one of those opening closes this panel.
    /// </summary>
    private void OnePanelAtATime()
    {
        if (Volatile.Read(ref _onePanel) == 0 || !Active || !Host.HasAcPanels || !Host.HasSetPanelOpen)
        {
            _onePanelRule.Reset();
            return;
        }
        int ours = Host.SetPanelOpen(SkillsWindowTitle, RynthPanelOpen.Ask);
        if (ours < 0) return;   // the window isn't there (older engine)
        uint ac = 0;
        RynthAcPanel[] main = MainAcPanels;
        for (int i = 0; i < main.Length; i++)
            if (Host.IsAcPanelOpen(main[i]) == true) ac |= 1u << i;
        OnePanelRule.Step step = _onePanelRule.Next(ours == 1, ac);
        for (int i = 0; i < main.Length; i++)
            if ((step.CloseAc & (1u << i)) != 0) Host.AcPanel(main[i], RynthAcPanelOp.Close);
        if (step.CloseOurs) Host.SetPanelOpen(SkillsWindowTitle, RynthPanelOpen.Close);
    }

    // AC's own Character windows claimed (engine 2026-11-07, "/rc panels replace"): the engine holds back AC's
    // open, so AC's window never shows, and runs these instead. Sent when the setting is read or changes; an
    // engine reload reloads the plugins too, so a new engine hears it again.
    private const string PressCharacter = "/rserver skills press character", PressTitles = "/rserver skills press titles";
    private int _claimed = -1;   // what was last sent: -1 nothing, 0 off, 1 on

    private bool _infoReleased;

    private void ClaimAcPanels(int replace)
    {
        bool on = replace == 1 && Active && Host.HasSetPanelOpen;
        int want = on ? 1 : 0;
        if (want == _claimed || (_claimed < 0 && !on) || !Host.HasInvokeChatParser) return;   // never claimed: nothing to drop
        _claimed = want;
        string id = ((int)RynthAcPanel.Character).ToString(CultureInfo.InvariantCulture);
        Host.InvokeChatParser($"/rc panels replace {id} {(on ? PressCharacter : "off")}");
        // AC's Character Information window (the mini bar's backpack button) is AC's own again (2026-11-08, Tom:
        // it "used to open up an information panel for the character, now it opens the titles panel"). It was
        // claimed as a "Titles window"; an engine still holding that claim from the build before lets it go
        // (quiet when it holds none).
        if (!_infoReleased)
        {
            _infoReleased = true;
            string info = ((int)RynthAcPanel.CharacterInfo).ToString(CultureInfo.InvariantCulture);
            Host.InvokeChatParser($"/rc panels replace {info} off");
        }
    }

    /// <summary>
    /// AC's Character button or key (<paramref name="page"/> 1), or its Character Titles window (2): as retail's
    /// button, it toggles this panel; Titles turns an open panel to its Titles page first. AC's thread.
    /// </summary>
    private void PressAcButton(int page)
    {
        int ours = Host.SetPanelOpen(SkillsWindowTitle, RynthPanelOpen.Ask);
        if (ours < 0) return;   // the window isn't there (older engine)
        RynthCore.Plugin.RynthDreamWeave.SkillsPanel panel = Panel();
        if (ours == 1 && !(page == 2 && panel.CurrentPage != 2))
        {
            Host.SetPanelOpen(SkillsWindowTitle, RynthPanelOpen.Close);
            return;
        }
        if (page == 2) panel.ShowPage(2);
        Host.SetPanelOpen(SkillsWindowTitle, RynthPanelOpen.Open);
    }

    /// <summary>Every frame (AC's thread): AC's Character or Character Titles window opened -> this panel instead;
    /// one main panel at a time.</summary>
    partial void ServerRender()
    {
        int replace = Volatile.Read(ref _replaceAc);
        if (replace < 0)
        {
            Volatile.Write(ref _replaceAc, 2);   // asked for once
            RunOnTick(() =>
            {
                Volatile.Write(ref _onePanel, Settings.GetBool(OnePanelSetting, true) ? 1 : 0);
                Volatile.Write(ref _replaceAc, Settings.GetBool(ReplaceAcSetting, true) ? 1 : 0);
            });
            return;
        }
        if (replace == 2) return;
        ClaimAcPanels(replace);
        OnePanelAtATime();
        // An engine without the claim (before 2026-11-07): AC's window opened, close it and open this one. It
        // shows for a moment (AC's open state is read at most 5 times a second).
        if (replace != 1 || !Active || !Host.HasAcPanels || !Host.HasSetPanelOpen) return;
        if (Host.IsAcPanelOpen(RynthAcPanel.Character) != true) return;
        // AC's close is queued: one swap per press.
        long now = Environment.TickCount64;
        bool fresh = now >= _replaceHoldUntil;
        Host.AcPanel(RynthAcPanel.Character, RynthAcPanelOp.Close);
        if (!fresh) return;
        _replaceHoldUntil = now + 700;
        PressAcButton(1);
    }

    private long _replaceHoldUntil;

    // ── Titles from the server ──────────────────────────────────────────

    partial void ServerMessageInterest(List<uint> gameEvents)
    {
        gameEvents.Add(EventCharacterTitle);
        gameEvents.Add(EventUpdateTitle);
    }

    partial void ServerMessage(uint opcode, ReadOnlySpan<byte> body)
    {
        var msg = new RynthCore.PluginSdk.Net.ServerMessage(opcode, body);
        if (!msg.IsGameEvent || (msg.EventType != EventCharacterTitle && msg.EventType != EventUpdateTitle)) return;
        Panel();   // the title store is wired
        _skillsReader.OnTitleEvent(msg.EventObjectId, msg.EventType, msg.EventBody);
    }

    // ── /rserver skills ─────────────────────────────────────────────────

    partial void ServerCommandVerbs(List<string> verbs)
    {
        verbs.Add("skills");
        WebCommandVerbs(verbs);   // DreamWeaveWebTools.cs: the website verbs
    }

    partial void ServerChatCommand(string[] parts, ref bool handled)
    {
        WebChatCommand(parts, ref handled);   // DreamWeaveWebTools.cs: a website verb
        if (handled) return;
        if (parts.Length < 2 || !parts[1].Equals("skills", StringComparison.OrdinalIgnoreCase)) return;
        handled = true;
        // "press character|titles": what AC's own Character button or Titles window does now (the engine runs it
        // in their place, ClaimAcPanels). Quiet: it's a button, not a command.
        if (parts.Length >= 4 && parts[2].Equals("press", StringComparison.OrdinalIgnoreCase))
        {
            if (Active && Host.HasSetPanelOpen)
                PressAcButton(parts[3].Equals("titles", StringComparison.OrdinalIgnoreCase) ? 2 : 1);
            return;
        }
        if (parts.Length >= 4 && parts[2].Equals("replace", StringComparison.OrdinalIgnoreCase))
        {
            bool on = parts[3].Equals("on", StringComparison.OrdinalIgnoreCase);
            if (!on && !parts[3].Equals("off", StringComparison.OrdinalIgnoreCase)) { Reply.Fail("Usage: /rserver skills replace on|off"); return; }
            SetReplaceAc(on);
            Reply.Ok(on ? "AC's Character window now opens the DreamWeave panel." : "AC's Character window opens as usual.");
            return;
        }
        if (!Host.HasSetPanelOpen || Host.SetPanelOpen(SkillsWindowTitle, RynthPanelOpen.Ask) < 0)
        {
            Reply.Fail(Active
                ? "The Skills panel needs a RynthCore with plugin windows that draw their own frame (plugin API v90)."
                : "The Skills panel is for DreamWeave only.");
            return;
        }
        bool open = Host.SetPanelOpen(SkillsWindowTitle, RynthPanelOpen.Toggle) == 1;
        Reply.Ok(open ? "Skills panel open." : "Skills panel closed.");
    }
}
