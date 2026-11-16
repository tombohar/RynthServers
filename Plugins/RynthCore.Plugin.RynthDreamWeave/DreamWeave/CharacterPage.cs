// CharacterPage.cs - RynthDreamWeave's "Character" page (2026-11-07, Tom: "add this to the actual Dreamweave
// plugin UI. The settings and the ability to open the new skills panel ... to not have to use chat
// commands"): a tab of the DreamWeave window on RynthCore's bar (and of the /rserver window) that opens and
// closes the Character panel (SkillsPanel), on any of its pages, and holds its setting.
using System;
using ImGuiNET;
using RynthCore.Plugin.ServerPack;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthDreamWeave;

/// <summary>The Character page. Render thread; what it changes is saved on the tick.</summary>
internal sealed class CharacterPage
{
    private readonly Func<bool> _active;
    private readonly Func<int> _panelState;        // 1 open, 0 closed, -1 this RynthCore can't host it
    private readonly Action<int> _open;            // opens the panel on a page (0 Attributes, 1 Skills, 2 Titles)
    private readonly Action _close;
    private readonly Func<bool> _replacesAc;
    private readonly Action<bool> _setReplacesAc;
    private readonly Func<bool> _onePanel;
    private readonly Action<bool> _setOnePanel;

    public CharacterPage(Func<bool> active, Func<int> panelState, Action<int> open, Action close,
        Func<bool> replacesAc, Action<bool> setReplacesAc, Func<bool> onePanel, Action<bool> setOnePanel)
    {
        _active = active;
        _panelState = panelState;
        _open = open;
        _close = close;
        _replacesAc = replacesAc;
        _setReplacesAc = setReplacesAc;
        _onePanel = onePanel;
        _setOnePanel = setOnePanel;
    }

    public ServerPage Page() => new("character", "Character",
        "The retail Character window, whose XP for next level keeps reading past level 275 (RynthDreamWeave).", Draw);

    private void Draw()
    {
        if (!_active())
        {
            SafeImGui.TextWrapped("This page is for DreamWeave. RynthCore doesn't see this world as DreamWeave "
                                  + "(check RynthCore Options > Diagnostics, or /rserver info).");
            return;
        }
        SafeImGui.TextWrapped("AC's Character window (Attributes, Skills and Titles), drawn as retail draws it, with "
                              + "\"XP for next level\" read from DreamWeave's own chart past level 275, where AC's "
                              + "own window shows a negative number.");
        ImGui.Spacing();

        int state = _panelState();
        if (state < 0)
        {
            SafeImGui.TextWrapped("This RynthCore can't show it: it needs plugin windows that draw their own frame "
                                  + "(plugin API v90). Update RynthCore.");
            return;
        }

        if (state == 1)
        {
            if (ImGui.Button("Close the Character panel##dw_char_close")) _close();
        }
        else if (ImGui.Button("Open the Character panel##dw_char_open")) _open(1);
        Tooltip("Also on RynthCore's bar (\"DreamWeave Skills\"), and with /rserver skills.");

        SafeImGui.TextDisabled("Open it on:");
        ImGui.SameLine();
        if (ImGui.SmallButton("Attributes##dw_char_attr")) _open(0);
        ImGui.SameLine();
        if (ImGui.SmallButton("Skills##dw_char_skills")) _open(1);
        ImGui.SameLine();
        if (ImGui.SmallButton("Titles##dw_char_titles")) _open(2);

        ImGui.Spacing();
        bool replace = _replacesAc();
        if (ImGui.Checkbox("Open it instead of AC's Character window##dw_char_replace", ref replace)) _setReplacesAc(replace);
        Tooltip("On: AC's toolbar button and key for the Character window open (and close) this panel instead. "
                + "AC's Character Information window (the backpack button) stays AC's own.\nOff: AC's own windows "
                + "open as usual; open this one from here or from RynthCore's bar.");

        bool one = _onePanel();
        if (ImGui.Checkbox("One panel at a time, as retail##dw_char_onepanel", ref one)) _setOnePanel(one);
        Tooltip("On: opening it puts away AC's open panel (inventory, spellbook, social, options, map...), and "
                + "opening one of those puts it away, as retail's panels share one frame.\nOff: it stays open beside them.");

        ImGui.Spacing();
        SafeImGui.TextDisabled("Move it by its gold frame, resize it from its bottom edge, Ctrl+wheel over it for a "
                               + "bigger or smaller panel. Right-click its tabs for the same setting.");
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(text);
    }
}
