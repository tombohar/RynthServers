// XpBonusStrip.cs - RynthDreamWeave's offline bonus tracker (2026-11-15): the row at the top of the DreamWeave
// window's Character page, drawn the way the Leaderboards page and the Character panel are (RetailCanvas): retail's
// tiled page, a section banner, the bonus in Palatino 16 (green while it is being spent), how old the server's
// reading is in Palatino 14, and retail's red Check now and Auto read buttons; right-click it for the setting as a
// checkbox, as on the Leaderboards page. Hovering a line shows retail's tooltip box.
// Render thread: draws holding the reader's lock, and queues what it sends for the tick.
using System;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDreamWeave;

internal sealed class XpBonusStrip
{
    private const int H = 20 + 20 + 18 + 4 + 32 + 4;
    private static readonly uint Grey = RetailCanvas.TabOff;

    private readonly DreamWeaveXpBonus _x;
    private readonly Func<RynthCoreHost> _host;
    private readonly Func<int> _scale;
    private readonly Action<Action> _runOnTick;
    private readonly Action _check;
    private readonly Action<bool> _setAuto;
    private RetailCanvas? _c;

    /// <param name="scale">The DreamWeave window's draw size in percent (Ctrl+wheel; 0 when unknown).</param>
    /// <param name="check">Check now (pump thread, queued with runOnTick).</param>
    /// <param name="setAuto">Turns the automatic read on or off and saves it (pump thread).</param>
    public XpBonusStrip(DreamWeaveXpBonus reader, Func<RynthCoreHost> host, Func<int> scale, Action<Action> runOnTick,
        Action check, Action<bool> setAuto)
    {
        _x = reader;
        _host = host;
        _scale = scale;
        _runOnTick = runOnTick;
        _check = check;
        _setAuto = setAuto;
    }

    public void Draw()
    {
        _c ??= new RetailCanvas(_host());
        RetailCanvas c = _c;
        Vector2 start = ImGui.GetCursorScreenPos();
        Vector2 avail = ImGui.GetContentRegionAvail();
        int pct = _scale();
        float k = pct > 0 ? Math.Clamp(pct / 100f, 0.3f, 2.5f) : 1f;
        int w = Math.Max(280, (int)(avail.X / k));
        c.Begin(ImGui.GetWindowDrawList(), new Vector2(MathF.Round(start.X), MathF.Round(start.Y)), k);
        c.ClearHover();
        long now = Environment.TickCount64;
        bool auto;
        lock (_x.Gate)
        {
            auto = _x.AutoRead;
            c.PageBackground(0, 0, w, H);
            c.SpriteCropped(RetailCanvas.ArtBannerSpecialized, 0, 0, w, 20, 282, 20, 0xFF3A1E2Au);
            c.Text(RetailData.Pal18, "Double XP (offline bonus)", 4, 0, w - 8, 20, RetailCanvas.White);

            string line = _x.Line(now);
            uint color = _x.IsActive(now) ? RetailCanvas.Green
                : _x.HasReading && (_x.State is XpBonusState.Banking or XpBonusState.Unknown) && _x.Remaining(now) > 0 ? RetailCanvas.White
                : Grey;
            c.Text(RetailData.Pal16, RetailCanvas.Fit(RetailData.Pal16, line, w - 12), 6, 20, w - 12, 20, color);
            if (c.Hit(0, 20, w, 20))
                c.Hover(1u, "DreamWeave's offline bonus: while any time is left, your combat experience and luminance get "
                            + $"{(_x.Percent is int p ? "+" + p + "%" : "the bonus (+100% by default)")}. It banks 1:1 while this "
                            + "character is logged out, or online but idle (no combat XP for 5 minutes), up to 24 hours, and "
                            + "drains 1:1 while you fight. /offlinebonus asks the server.");

            string waiting = _x.Waiting(now) ? "Asking the server..." : _x.Detail(now, DateTime.UtcNow);
            c.Text(RetailData.Pal14, RetailCanvas.Fit(RetailData.Pal14, waiting, w - 12), 6, 40, w - 12, 18, RetailCanvas.Body);
            if (c.Hit(0, 40, w, 18))
                c.Hover(2u, "Between readings the row counts down while the bonus is spent and up while it banks. The server's "
                            + "own lines keep it right: the login lines, \"now active\", \"now idle and banking again\", "
                            + "\"exhausted\", /bonus and the [BANK] line of every /b (the bank check sends one a minute).");

            int by = 20 + 20 + 18 + 4;
            int bw = (w - 3 * 6) / 2;
            bool busy = _x.Waiting(now);
            if (c.RedButton("##dw_xpbonus_check", "Check now", 6, by, bw, !busy)) _runOnTick(_check);
            if (c.Hit(6, by, bw, 32)) c.Hover(3u, $"Sends {DreamWeaveXpBonus.Command} at the next quiet moment and reads the reply (kept out of chat).");
            string autoLabel = bw < 150 ? (auto ? "Auto: On" : "Auto: Off") : (auto ? "Auto read: On" : "Auto read: Off");
            if (c.RedButton("##dw_xpbonus_auto", autoLabel, 12 + bw, by, bw, true)) _runOnTick(() => _setAuto(!auto));
            if (c.Hit(12 + bw, by, bw, 32))
                c.Hover(4u, $"Read the offline bonus automatically: {DreamWeaveXpBonus.Command} 35 seconds after login, then every "
                            + "ten minutes, kept out of chat and never within a few seconds of the bank's /b or the leaderboards' /top. "
                            + "Off by default: the server's own lines and the bank check's /b keep the row current without it.");
        }
        c.UpdateTooltip();
        OptionsMenu(w, auto);
        c.Tooltip();
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(w * k, H * k));
    }

    /// <summary>Right-click the row: its setting as a checkbox (the Leaderboards page's options menu).</summary>
    private void OptionsMenu(int w, bool auto)
    {
        RetailCanvas c = _c!;
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && c.Hit(0, 0, w, H) && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            ImGui.OpenPopup("##dw_xpbonus_options");
        if (!ImGui.BeginPopup("##dw_xpbonus_options")) return;
        try
        {
            bool on = auto;
            if (ImGui.Checkbox("Read the offline bonus automatically", ref on)) _runOnTick(() => _setAuto(on));
            if (ImGui.IsItemHovered())
                SafeImGui.SetTooltip($"{DreamWeaveXpBonus.Command} after login and every ten minutes; its replies stay out of chat.");
        }
        finally { ImGui.EndPopup(); }
    }
}
