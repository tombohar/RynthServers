// The DreamWeave card's website buttons (Class Abilities, Market, Suit Builder). Render thread, inside
// the card hook only: this file draws and nothing else. It calls no host function, sends nothing and
// opens nothing; a click is handed to the caller, which queues the work for the tick (RunOnTick).
// It never switches the ImGui context (RenderEmbedded and OnRender own it), and it draws no
// Begin/End or Push/Pop pairs, so an exception part-way can't leave ImGui's stacks unbalanced.
using System;
using ImGuiNET;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthDreamWeave;

internal static class WebButtons
{
    /// <summary>Shown on the card, and answered to a verb, when RynthCore doesn't see the world as DreamWeave.</summary>
    public const string NotDreamWeave = "The web tools are DreamWeave's; RynthCore doesn't see this world as DreamWeave.";

    /// <summary>
    /// One small button per destination on one row, each with its tooltip; while a request is pending,
    /// the status line instead. The launcher's state is copied out under its lock first, then drawn.
    /// </summary>
    public static void Draw(WebLauncher l, bool active, Action<WebDestination> click)
    {
        if (!active)
        {
            SafeImGui.TextWrapped(NotDreamWeave);
            return;
        }

        bool pending;
        string status;
        lock (l.Gate)
        {
            pending = l.Pending != null;
            status = l.Status;
        }

        if (pending)
        {
            SafeImGui.TextDisabled(status);
            return;
        }

        WebDestination? clicked = null;
        bool first = true;
        foreach (WebDestination d in DreamWeaveWeb.Destinations)
        {
            if (!first) ImGui.SameLine();
            first = false;
            if (ImGui.SmallButton(d.Label + "##dw_web_" + d.Id)) clicked = d;
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(d.Tooltip);
        }
        if (status.Length > 0) SafeImGui.TextWrapped(status);
        if (clicked != null) click(clicked);
    }
}
