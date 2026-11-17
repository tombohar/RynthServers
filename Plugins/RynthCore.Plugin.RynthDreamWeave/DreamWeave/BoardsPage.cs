// BoardsPage.cs - RynthDreamWeave's Leaderboards page (2026-11-15): a tab of the DreamWeave window on
// RynthCore's bar (and of the /rserver window), drawn the way the plugin's Character panel is (SkillsPanel.cs,
// through the same RetailCanvas helpers): retail's tiled page, Palatino dat fonts, the skill list's section
// banners and 20-pixel rows with the blue selected-row bar, the chain scrollbar, the rule over the footer, a
// footer with a Palatino 20 title and two label/value lines, retail's red buttons and its tooltip box.
//
//   left   every board, under three banners (Character, Proving Grounds, PvP Arena), with your rank on each:
//          the summary, and the board picker (click a row);
//   right  the board picked: its title and age, the rows (yours highlighted in gold), "..." and your row when
//          you are below the list, and what the server added (an empty board, a cut list, a refusal);
//   footer your place on that board, the automatic refresh, and the Refresh now, Read all boards and Auto
//          refresh buttons (right-click the page for the same setting as a checkbox, as on the Character panel).
// Render thread: draws holding the boards' lock, and queues what it sends for the tick.
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.Plugin.ServerPack;
using RynthCore.Plugin.Shared;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDreamWeave;

internal sealed class BoardsPage
{
    private const int RowH = RetailCanvas.RowH, RuleH = RetailCanvas.RuleH, ScrollW = RetailCanvas.ScrollW;
    private const int FooterH = 20 + 17 + 18 + 4 + 32 + 4;
    private const int TwoColumnsFrom = 500, LeftW = 200, Gap = 5;
    private const uint White = RetailCanvas.White;
    private static readonly uint Gold = RetailCanvas.Gold, Body = RetailCanvas.Body, Grey = RetailCanvas.TabOff;

    /// <summary>The left list's sections: a banner (its picture) and its boards.</summary>
    private static readonly (string Title, uint Art, string[] Keys)[] Sections =
    {
        ("Character", RetailCanvas.ArtBannerTrained, new[] { "level", "bank", "cap", "stamp", "thread", "knot", "title", "deaths" }),
        ("Proving Grounds", RetailCanvas.ArtBannerSpecialized, new[] { "dps", "defense", "wave", "speed", "winners" }),
        ("PvP Arena", RetailCanvas.ArtBannerUntrained, new[] { "1v1", "2v2", "tugak", "bg" }),
    };

    private readonly DreamWeaveBoards _b;
    private readonly Func<bool> _active;
    private readonly Func<RynthCoreHost> _host;
    private readonly Func<int> _scale;
    private readonly Action<Action> _runOnTick;
    private readonly Action<string> _refresh;
    private readonly Action _sweep;
    private readonly Action<bool> _setAuto;
    private RetailCanvas? _c;

    private string _selected = "level";
    private float _listScroll, _rowScroll;
    private string _scrolledFor = "";

    /// <param name="scale">The DreamWeave window's draw size in percent (Ctrl+wheel; 0 when unknown).</param>
    /// <param name="refresh">Asks for one board now (pump thread, queued with runOnTick).</param>
    /// <param name="sweep">Reads every board, one every few seconds (pump thread).</param>
    /// <param name="setAuto">Turns the automatic refresh on or off and saves it (pump thread).</param>
    public BoardsPage(DreamWeaveBoards boards, Func<bool> active, Func<RynthCoreHost> host, Func<int> scale,
        Action<Action> runOnTick, Action<string> refresh, Action sweep, Action<bool> setAuto)
    {
        _b = boards;
        _active = active;
        _host = host;
        _scale = scale;
        _runOnTick = runOnTick;
        _refresh = refresh;
        _sweep = sweep;
        _setAuto = setAuto;
    }

    public ServerPage Page() => new("boards", "Leaderboards",
        "DreamWeave's leaderboards (/top), with your rank on every board (RynthDreamWeave).", Draw);

    private void Draw()
    {
        if (!_active())
        {
            SafeImGui.TextWrapped("This page is for DreamWeave. RynthCore doesn't see this world as DreamWeave "
                                  + "(check RynthCore Options > Diagnostics, or /rserver info).");
            return;
        }
        _c ??= new RetailCanvas(_host());
        Vector2 start = ImGui.GetCursorScreenPos();
        Vector2 avail = ImGui.GetContentRegionAvail();
        int pct = _scale();
        float k = pct > 0 ? Math.Clamp(pct / 100f, 0.3f, 2.5f) : 1f;
        int w = Math.Max(280, (int)(avail.X / k));
        int h = avail.Y / k >= 360 ? (int)(avail.Y / k) : 460;
        _c.Begin(ImGui.GetWindowDrawList(), new Vector2(MathF.Round(start.X), MathF.Round(start.Y)), k);
        _c.ClearHover();
        lock (_b.Gate)
        {
            if (DreamWeaveBoards.Def(_selected) == null) _selected = "level";
            _c.PageBackground(0, 0, w, h);
            int ruleY = h - FooterH - RuleH - 3;
            if (w >= TwoColumnsFrom)
            {
                BoardList(0, 0, LeftW, ruleY - 2);
                _c.Sprite(RetailCanvas.ArtColumnRule, LeftW, 0, Gap, ruleY, 0xFF54788Fu);
                Board(LeftW + Gap, 0, w - LeftW - Gap, ruleY - 2);
            }
            else
            {
                int listH = Math.Min(8 * RowH, (ruleY - 2) / 2);
                BoardList(0, 0, w, listH);
                _c.Sprite(RetailCanvas.ArtRule, 0, listH, w, RuleH, 0xFF54788Fu);
                Board(0, listH + RuleH + 2, w, ruleY - 2 - (listH + RuleH + 2));
            }
            Footer(w, h, ruleY);
        }
        _c.UpdateTooltip();
        OptionsMenu(w, h);
        _c.Tooltip();
        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(w * k, h * k));
    }

    // ── Every board and your rank on it (the board picker) ──────────────

    private void BoardList(int x0, int top, int width, int height)
    {
        RetailCanvas c = _c!;
        var rows = new List<string?>();   // null = a section banner (by order), else a board key
        var banners = new List<int>();
        foreach (var s in Sections)
        {
            banners.Add(rows.Count);
            rows.Add(null);
            foreach (string key in s.Keys) rows.Add(key);
        }
        int visible = Math.Max(1, height / RowH);
        bool scroll = rows.Count > visible;
        int rowW = scroll ? width - ScrollW - 1 : width;
        float maxScroll = Math.Max(0, rows.Count - visible);

        ImGui.SetCursorScreenPos(c.P(x0, top));
        ImGui.InvisibleButton("##dw_boards_list", c.S(rowW, height));
        bool hovered = ImGui.IsItemHovered(), clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        if (hovered && !ImGui.GetIO().KeyCtrl) _listScroll -= ImGui.GetIO().MouseWheel * 3;
        _listScroll = Math.Clamp(_listScroll, 0, maxScroll);
        int first = (int)MathF.Round(_listScroll);

        c.Dl.PushClipRect(c.P(x0, top), c.P(x0 + rowW, top + height), true);
        try
        {
            for (int i = first, y = top; i < rows.Count && y < top + height; i++, y += RowH)
            {
                string? key = rows[i];
                if (key == null)
                {
                    var s = Sections[banners.IndexOf(i)];
                    c.SpriteCropped(s.Art, x0, y, rowW, RowH, 282, 20, 0xFF3A1E2Au);
                    c.Text(RetailData.Pal18, s.Title, x0 + 4, y, rowW - 8, RowH, White);
                    continue;
                }
                BoardDef d = DreamWeaveBoards.Def(key)!;
                if (key == _selected) c.SpriteCropped(RetailCanvas.ArtRowSelected, x0, y, rowW, RowH, 282, 20, 0x60206080u);
                BoardStanding st = _b.Standing(key);
                string rank = !st.Read ? "" : st.Rank == 0 ? "-" : "#" + st.Rank;
                uint rankColor = !st.Read || st.Rank == 0 ? Grey : st.OutsideList ? White : Gold;
                int rankW = Math.Max(RetailData.Pal16.Measure(rank), 30);
                c.Text(RetailData.Pal16, RetailCanvas.Fit(RetailData.Pal16, d.Label, rowW - rankW - 14), x0 + 6, y, rowW - rankW - 10, RowH,
                    _b.Result(key) != null ? White : Grey);
                c.Text(RetailData.Pal16, rank, x0 + rowW - rankW - 4, y, rankW, RowH, rankColor, right: true);
                if (clicked && c.Hit(x0, y, rowW, RowH)) Select(key);
                if (hovered && c.Hit(x0, y, rowW, RowH))
                    c.Hover(100u + (uint)i, $"{d.Help}.\nYou: {_b.StandingText(key)}. {AgeText(key)}.\n{d.Command}");
            }
        }
        finally { c.Dl.PopClipRect(); }
        if (scroll) c.ScrollBar("boards_list", x0 + width - ScrollW, ref _listScroll, top, height, visible, rows.Count, maxScroll);
    }

    private void Select(string key)
    {
        _selected = key;
        _rowScroll = 0;
    }

    // ── The board picked ────────────────────────────────────────────────

    private void Board(int x0, int top, int width, int height)
    {
        RetailCanvas c = _c!;
        BoardDef d = DreamWeaveBoards.Def(_selected)!;
        BoardResult? r = _b.Result(_selected);
        string title = d.Key == "speed" && r != null && r.Title.Length > 0 ? r.Title : d.Label;
        c.Text(RetailData.Pal18, RetailCanvas.Fit(RetailData.Pal18, title, width - 8), x0, top, width, 20, White, center: true);
        string sub = _b.PendingKey == _selected ? "Waiting for the reply..."
            : _b.LastMiss.Length > 0 && r == null ? _b.LastMiss
            : r == null ? "Not read yet: press Refresh now." : AgeText(_selected);
        c.Text(RetailData.Pal14, sub, x0, top + 20, width, 16, Body, center: true);
        c.Sprite(RetailCanvas.ArtRule, x0, top + 37, width, RuleH, 0xFF54788Fu);

        int listTop = top + 37 + RuleH + 2, listH = height - (listTop - top);
        if (listH < RowH) return;
        var lines = new List<(BoardRow? Row, string Text)>();   // a row, or a line of text ("..." or a note)
        if (r != null)
        {
            foreach (BoardRow row in r.Rows) lines.Add((row, ""));
            if (r.Below != null) { lines.Add((null, "...")); lines.Add((r.Below, "")); }
            if (r.Note.Length > 0)
                foreach (string l in RetailData.Pal14.Wrap(r.Note, width - ScrollW - 12)) lines.Add((null, l));
        }
        if (_scrolledFor != _selected) { _scrolledFor = _selected; _rowScroll = 0; }
        int visible = Math.Max(1, listH / RowH);
        bool scroll = lines.Count > visible;
        int rowW = scroll ? width - ScrollW - 1 : width;
        float maxScroll = Math.Max(0, lines.Count - visible);

        ImGui.SetCursorScreenPos(c.P(x0, listTop));
        ImGui.InvisibleButton("##dw_board_rows", c.S(rowW, listH));
        bool hovered = ImGui.IsItemHovered();
        if (hovered && !ImGui.GetIO().KeyCtrl) _rowScroll -= ImGui.GetIO().MouseWheel * 3;
        _rowScroll = Math.Clamp(_rowScroll, 0, maxScroll);
        int first = (int)MathF.Round(_rowScroll);

        c.Dl.PushClipRect(c.P(x0, listTop), c.P(x0 + rowW, listTop + listH), true);
        try
        {
            if (r == null || (r.Rows.Count == 0 && r.Below == null && r.Note.Length == 0))
                c.Text(RetailData.Pal16, r == null ? "Nothing read yet." : "Nobody is on this board yet.", x0, listTop, rowW, RowH * 2, White, center: true);
            for (int i = first, y = listTop; i < lines.Count && y < listTop + listH; i++, y += RowH)
            {
                (BoardRow? row, string text) = lines[i];
                if (row == null)
                {
                    if (text == "...") c.Text(RetailData.Pal16, text, x0, y, rowW, RowH, Grey, center: true);
                    else c.Text(RetailData.Pal14, text, x0 + 6, y, rowW - 12, RowH, Body);
                    continue;
                }
                bool you = _b.IsYou(row);
                if (you) c.SpriteCropped(RetailCanvas.ArtRowSelected, x0, y, rowW, RowH, 282, 20, 0x60206080u);
                (string name, string value, string tip) = Cells(d, row);
                uint color = you ? Gold : White;
                int valueW = Math.Min(RetailData.Pal16.Measure(value), (rowW - 40) / 2);
                c.Text(RetailData.Pal16, row.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture), x0, y, 30, RowH, color, right: true);
                c.Text(RetailData.Pal16, RetailCanvas.Fit(RetailData.Pal16, name, rowW - 40 - valueW - 10), x0 + 38, y, rowW - 48 - valueW, RowH, color);
                c.Text(RetailData.Pal16, RetailCanvas.Fit(RetailData.Pal16, value, valueW), x0 + rowW - valueW - 4, y, valueW, RowH, color, right: true);
                if (hovered && c.Hit(x0, y, rowW, RowH)) c.Hover(1000u + (uint)i, tip);
            }
        }
        finally { c.Dl.PopClipRect(); }
        if (scroll) c.ScrollBar("board_rows", x0 + width - ScrollW, ref _rowScroll, listTop, listH, visible, lines.Count, maxScroll);
    }

    /// <summary>A row's name and value columns, and its tooltip (the whole line).</summary>
    private static (string Name, string Value, string Tip) Cells(BoardDef d, BoardRow row)
    {
        string you = row.Account ? " (your account)" : row.Marked ? " (you)" : "";
        return d.Kind switch
        {
            BoardKind.Arena => (row.Name, row.Value, $"{row.Rank}. {row.Name}: rating {row.Value} ({row.Detail})"),
            BoardKind.SpeedWinners => (row.Detail, row.Name, row.Value.Length > 0
                ? $"{row.Detail}: won by {row.Name} in {row.Value}" : $"{row.Detail}: unclaimed"),
            _ => (row.Name + you, row.Value, $"{row.Rank}. {row.Name}{you}: {row.Value}"),
        };
    }

    private string AgeText(string key)
    {
        BoardResult? r = _b.Result(key);
        if (r == null) return "Not read yet";
        string age = DreamWeaveBoards.Age(r.UpdatedUtc, DateTime.UtcNow);
        age = char.ToUpperInvariant(age[0]) + age[1..];
        return r.Cut ? age + " (cut short; left out of the automatic refresh)" : age;
    }

    // ── The footer: your place, the automatic refresh, the buttons ─────

    private void Footer(int w, int h, int ruleY)
    {
        RetailCanvas c = _c!;
        c.Sprite(RetailCanvas.ArtRule, 0, ruleY, w, RuleH, 0xFF54788Fu);
        int y = h - FooterH;
        BoardDef d = DreamWeaveBoards.Def(_selected)!;
        BoardStanding st = _b.Standing(_selected);
        string place = _b.StandingText(_selected);
        c.Text(RetailData.Pal20, $"{d.Label}: {place}", 0, y, w, 20, st.Read && st.Rank > 0 ? White : Body, center: true);
        c.Text(RetailData.Pal16, "Your place", 5, y + 20, 140, 17, White);
        string value = !st.Read ? "Press Refresh now" : st.Rank == 0 ? "-" : st.Value;
        c.Text(RetailData.Pal16, RetailCanvas.Fit(RetailData.Pal16, value, w - 155), 145, y + 20, w - 150, 17, White, right: true);
        c.Text(RetailData.Pal16, "Refresh automatically", 5, y + 37, 160, 18, White);
        string auto = !_b.AutoRefresh ? "Off"
            : _b.SweepLeft > 0 ? $"On (reading all: {_b.SweepLeft} left)" : "On: every board about every 10 min";
        if (!_b.AutoRefresh && _b.SweepLeft > 0) auto = $"Off (reading all: {_b.SweepLeft} left)";
        c.Text(RetailData.Pal16, RetailCanvas.Fit(RetailData.Pal16, auto, w - 175), 165, y + 37, w - 170, 18, White, right: true);
        if (c.Hit(0, y + 37, w, 18))
            c.Hover(3u, "On: RynthDreamWeave sends one board's /top every 35 seconds while you are in the world, so each "
                        + "board is about ten minutes old at most, and keeps its replies out of chat. A /top you type "
                        + "always shows. A board too long for RynthCore to hide is left out. Off by default.");

        int by = y + 20 + 17 + 18 + 4;
        int bw = (w - 4 * 6) / 3;
        bool shortLabels = bw < 150;
        bool waiting = _b.Pending;
        if (c.RedButton("##dw_boards_refresh", shortLabels ? "Refresh" : "Refresh now", 6, by, bw, !waiting))
        {
            string key = _selected;
            _runOnTick(() => _refresh(key));
        }
        if (c.Hit(6, by, bw, 32)) c.Hover(4u, $"Sends {d.Command} now and reads the reply (kept out of chat).");
        if (c.RedButton("##dw_boards_all", shortLabels ? "Read all" : "Read all boards", 12 + bw, by, bw, _b.SweepLeft == 0))
            _runOnTick(_sweep);
        if (c.Hit(12 + bw, by, bw, 32))
            c.Hover(5u, $"Reads every board once, one every {DreamWeaveBoards.SweepIntervalMs / 1000} seconds (about two minutes), replies kept out of chat.");
        bool on = _b.AutoRefresh;
        string autoLabel = shortLabels ? (on ? "Auto: On" : "Auto: Off") : (on ? "Auto refresh: On" : "Auto refresh: Off");
        if (c.RedButton("##dw_boards_auto", autoLabel, 18 + 2 * bw, by, bw, true)) _runOnTick(() => _setAuto(!on));
        if (c.Hit(18 + 2 * bw, by, bw, 32)) c.Hover(6u, "Refresh leaderboards automatically: one board every 35 seconds, each about every ten minutes.");
    }

    /// <summary>Right-click the page: its setting as a checkbox (the Character panel's options menu).</summary>
    private void OptionsMenu(int w, int h)
    {
        RetailCanvas c = _c!;
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && c.Hit(0, 0, w, h) && ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows))
            ImGui.OpenPopup("##dw_boards_options");
        if (!ImGui.BeginPopup("##dw_boards_options")) return;
        try
        {
            bool on;
            lock (_b.Gate) on = _b.AutoRefresh;
            if (ImGui.Checkbox("Refresh leaderboards automatically", ref on)) _runOnTick(() => _setAuto(on));
            if (ImGui.IsItemHovered())
                SafeImGui.SetTooltip("One board's /top every 35 seconds while in the world (each board about every ten minutes); its replies stay out of chat.");
        }
        finally { ImGui.EndPopup(); }
    }
}
