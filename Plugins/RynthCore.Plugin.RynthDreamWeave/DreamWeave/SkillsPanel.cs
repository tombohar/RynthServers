// SkillsPanel.cs - The DreamWeave Skills panel: retail's Character panel on its Skills page, drawn 1:1,
// with "XP for next level" read from DreamWeave's chart past level 275 (DreamWeaveXp) instead of the
// client's, which ends at 275 and shows a negative number there.
//
// Everything is the retail client's own: the positions are LayoutDesc 0x2100002E's (the Character panel,
// page 0x1000022C), the pictures its 0x06 textures (GetIconTexture kind UiSprite) and the text its dat
// fonts (Palatino_Linotype_14/16/18, Chat_PalatinoLinotype_Large and Stonehenge_36 for the level),
// drawn glyph by glyph from their sheets (kind FontSheet) with the glyph tables in RetailData.g.cs.
// An engine without those kinds (before 2026-11-07) gets flat stand-ins and ImGui's font, same layout.
//
// Design pixels: the panel is 300 wide inside a 5-pixel gold frame (retail's 0x0600612A..D); the
// window is 310 at 100% and everything scales with its draw size (Ctrl+wheel over it).
//
// The tabs row is the drag handle; Attributes and Titles open AC's own Character and Character
// Information panels; the minimize button closes this one. A row click selects a skill for the footer,
// whose arrow raises it one rank (or trains it with credits) and "+10" ten ranks, as retail's do.
//
// AC's render thread. Reads SkillsReader's snapshot; reads and raises are queued for the tick.
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDreamWeave;

internal sealed class SkillsPanel
{
    // ── LayoutDesc 0x2100002E (design pixels) ───────────────────────────
    public const int PanelW = 300, Frame = 5;
    private const int TabW = 92, TabH = 25, CapW = 17, CloseX = 276, CloseW = 24;
    private const int HeaderH = 105, RuleH = 7, ListTop = 112, FooterH = 55, RowH = 20, BannerW = 280;
    private const int ScrollX = 281, ScrollW = 16, ScrollBtn = 16, ScrollCap = 3;

    // The pictures the Leaderboards page shares are RetailCanvas's (moved there 2026-11-15, unchanged).
    private const uint ArtEdgeTop = 0x0600612Au, ArtEdgeLeft = 0x0600612Bu, ArtEdgeBottom = 0x0600612Cu, ArtEdgeRight = 0x0600612Du,
        ArtCorner = 0x06006129u, ArtClose = 0x06001393u, ArtClosePressed = 0x06001394u,
        ArtHeaderRule = 0x06004CB8u, ArtPlate = 0x060011A6u, ArtXpFill = 0x060011A5u, ArtRankFill = 0x06000F8Au,
        ArtRule = RetailCanvas.ArtRule, ArtRowSelected = RetailCanvas.ArtRowSelected,
        ArtRaise = 0x06004CB6u, ArtRaiseGhost = 0x06004CB7u, ArtRaiseTen = 0x0600712Bu, ArtRaiseTenGhost = 0x0600712Cu;
    // Tab 3-slices: "Open" (the shown page) and "Closed" states.
    private static readonly uint[] TabOpen = { 0x06005D92u, 0x06005D94u, 0x06005D96u };
    private static readonly uint[] TabClosed = { 0x06005D93u, 0x06005D95u, 0x06005D97u };

    private static uint Banner(SkillSection s) => s switch
    {
        SkillSection.Specialized => RetailCanvas.ArtBannerSpecialized,
        SkillSection.Trained => RetailCanvas.ArtBannerTrained,
        SkillSection.Untrained => RetailCanvas.ArtBannerUntrained,
        _ => RetailCanvas.ArtBannerOther,
    };

    // ── Colours (ImGui ABGR): RetailCanvas's ────────────────────────────
    private const uint White = RetailCanvas.White, Black = RetailCanvas.Black;
    private static readonly uint Body = RetailCanvas.Body;
    private static readonly uint TabOn = RetailCanvas.TabOn, TabOff = RetailCanvas.TabOff;
    private static readonly uint Gold = RetailCanvas.Gold;
    private static readonly uint Green = RetailCanvas.Green, Red = RetailCanvas.Red;

    private static uint Rgb(int r, int g, int b) => RetailCanvas.Rgb(r, g, b);

    private readonly RynthCoreHost _host;
    // The drawing helpers (RetailCanvas.cs, shared with the Leaderboards page).
    private readonly RetailCanvas _c;
    private readonly SkillsReader _reader;
    private readonly Action<Action> _onTick;
    private readonly Action<string> _chat;
    private readonly string _title;

    private int _tab = 1;           // 0 Attributes, 1 Skills, 2 Titles
    private uint _selected;         // the skill selected on the Skills page
    private int _attrSelected = -1; // the row selected on the Attributes page (AttributesPage.Rows)
    private uint _titleSelected;    // the title selected on the Titles page
    private float _scroll;          // rows scrolled (fractional while the thumb is dragged)
    private float _attrScroll, _titleScroll;
    private readonly Func<bool> _replacesAc;
    private readonly Action<bool> _setReplacesAc;
    // Resizing from the bottom edge (retail's Character window: height only). Design px, frame included.
    private bool _resizing;
    private float _resizeStartH, _resizeStartY, _dragH;
    private float _wantH;           // the height asked for after a resize, until the window has it
    private long _wantSince;
    private ImDrawListPtr _dl;
    private Vector2 _origin;        // the content's top-left (inside the frame), screen pixels
    private float _k = 1f;          // screen pixels per design pixel

    public SkillsPanel(RynthCoreHost host, SkillsReader reader, string title, Action<Action> onTick, Action<string> chat,
        Func<bool> replacesAc, Action<bool> setReplacesAc)
    {
        _host = host;
        _c = new RetailCanvas(host);
        _reader = reader;
        _title = title;
        _onTick = onTick;
        _chat = chat;
        _replacesAc = replacesAc;
        _setReplacesAc = setReplacesAc;
    }

    /// <summary>Logout: nothing selected, back to the top.</summary>
    public void Reset()
    {
        _selected = 0;
        _attrSelected = -1;
        _titleSelected = 0;
        _scroll = _attrScroll = _titleScroll = 0;
    }

    /// <summary>Opens on a page (0 Attributes, 1 Skills, 2 Titles): AC's own Character window asked for (the replace option).</summary>
    public void ShowPage(int tab) => _tab = Math.Clamp(tab, 0, 2);

    /// <summary>The page shown (0 Attributes, 1 Skills, 2 Titles).</summary>
    public int CurrentPage => _tab;

    /// <summary>
    /// Draws the panel into the host's frameless window (its body, edge to edge) and returns the size it
    /// wants (the host fits the window to it). <paramref name="popped"/>: popped out (the engine draws no
    /// retail pictures there).
    /// </summary>
    public Vector2 Draw(bool popped)
    {
        if (_reader.ReadDue()) _onTick(() => _reader.Read(_host));
        SkillsSheet sheet = _reader.Sheet;

        Vector2 start = ImGui.GetCursorScreenPos();
        Vector2 avail = ImGui.GetContentRegionAvail();
        if (avail.X < 60 || avail.Y < 60) return new Vector2(0, Math.Max(60, avail.Y));
        // The scale is the window's own draw size (Ctrl+wheel on it, 30-250%; Settings > UI > Toolbar), 100% =
        // retail's own pixels until it's set: never the window's width, Windows' display scaling or the text
        // size, which every panel follows ("All panels"; Tom's is 160%). Retail's panel is a fixed 300 wide and
        // isn't scaled, and the window is fitted to the panel (2026-11-07: the window's width, the DPI-scaled UI
        // font, then the text size drew it 1.4-1.6x: "still massive").
        int pct = _host.PanelScale(_title);
        _k = pct > 0 ? Math.Clamp(pct / 100f, 0.3f, 2.5f) : 1f;
        _dl = ImGui.GetWindowDrawList();
        _origin = new Vector2(MathF.Round(start.X + Frame * _k), MathF.Round(start.Y + Frame * _k));
        _c.Begin(_dl, _origin, _k);

        // The height drawn: the window's, or while the bottom edge is dragged the dragged one (drawn past the
        // window's edge, the way retail's frame follows the mouse), and after the release the asked-for one
        // until the host has fitted the window to it ("fitHeight").
        float windowH = avail.Y / _k;
        if (_wantH > 0 && (MathF.Abs(windowH - _wantH) < 1f || Environment.TickCount64 - _wantSince > 2000)) _wantH = 0;
        float panelH = MathF.Floor(_resizing ? _dragH : _wantH > 0 ? _wantH : windowH);
        panelH = Math.Max(panelH, MinPanelH);
        float contentH = panelH - 2 * Frame;
        int pageH = (int)contentH - TabH;

        float panelW = (PanelW + 2 * Frame) * _k;
        _dl.PushClipRect(start, start + new Vector2(Math.Max(avail.X, panelW), Math.Max(avail.Y, panelH * _k)), false);
        try
        {
            DrawPanel(sheet, pageH, popped);
        }
        finally { _dl.PopClipRect(); }

        ImGui.SetCursorScreenPos(start);
        ImGui.Dummy(new Vector2(avail.X, avail.Y));
        return new Vector2(MathF.Ceiling(panelW), MathF.Ceiling(panelH * _k));
    }

    /// <summary>The smallest panel retail allows: header, three rows, footer (design px, frame included).</summary>
    private const int MinPanelH = TabH + ListTop + 3 * RowH + RuleH + 3 + FooterH + 2 * Frame;

    private void DrawPanel(SkillsSheet sheet, int pageH, bool popped)
    {
        Page(pageH);
        Tabs();
        _c.ClearHover();
        if (_tab == 2) TitlesPage(sheet, pageH);
        else
        {
            Header(sheet);
            if (_tab == 0) AttributesPageDraw(sheet, pageH);
            else
            {
                List<SkillRow> rows = SkillsLayout.Rows(sheet.Skills);
                SkillLine? selected = Find(sheet, _selected);
                if (selected == null) _selected = 0;
                SkillList(sheet, rows, pageH);
                Footer(sheet, selected, pageH);
            }
            if (!sheet.HasNumbers)
            {
                string wait = _reader.Message.Length > 0 ? _reader.Message : "Reading your character...";
                Text(RetailData.Pal16, wait, 0, TabH + ListTop, BannerW, RowH * 2, White, center: true);
            }
        }
        UpdateTooltip();
        FrameArt(TabH + pageH);
        FrameHandles(TabH + pageH, popped);
        OptionsMenu();
        Tooltip();
    }

    // ── The Attributes page (the header and footer are the Skills page's) ──

    private void AttributesPageDraw(SkillsSheet sheet, int pageH)
    {
        IReadOnlyList<AttributeLine> rows = sheet.AttributeRows;
        int top = TabH + ListTop;
        int listH = pageH - ListTop - FooterH - RuleH - 3;
        int visible = Math.Max(1, listH / RowH);
        float maxScroll = Math.Max(0, rows.Count - visible);

        ImGui.SetCursorScreenPos(P(0, top));
        ImGui.InvisibleButton("##dw_attr_list", S(BannerW, listH));
        bool listHovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        if (listHovered && !ImGui.GetIO().KeyCtrl) _attrScroll -= ImGui.GetIO().MouseWheel * 3;
        _attrScroll = Math.Clamp(_attrScroll, 0, maxScroll);
        int first = (int)MathF.Round(_attrScroll);

        _dl.PushClipRect(P(0, top), P(BannerW, top + listH), true);
        bool onRow = false;
        try
        {
            for (int i = first, y = top; i < rows.Count && y < top + listH; i++, y += RowH)
            {
                AttributeLine a = rows[i];
                int row = AttributesPage.RowIndex(a.Kind, a.Stype);
                if (row == _attrSelected) SpriteCropped(ArtRowSelected, 0, y, BannerW, RowH, 282, 20, 0x60206080u);
                Icon(a.Icon, 0, y, 20);
                Text(RetailData.Pal18, a.Name, 25, y, 150, RowH, White);
                uint color = a.Buffed > a.Base ? Green : a.Buffed < a.Base ? Red : White;
                Text(RetailData.Pal18, a.ValueText, 125, y, 149, RowH, color, right: true);
                if (clicked && Hit(0, y, BannerW, RowH)) { _attrSelected = row; onRow = true; }
                if (listHovered && Hit(0, y, BannerW, RowH)) Hover(1000u + (uint)row, AttributesPage.Tip(a));
            }
        }
        finally { _dl.PopClipRect(); }
        if (clicked && !onRow) _attrSelected = -1;   // below the rows: nothing selected, as on the Skills page
        if (rows.Count > visible) ScrollBar("attr", ref _attrScroll, top, listH, visible, rows.Count, maxScroll);

        AttributeLine? sel = null;
        foreach (AttributeLine a in rows) if (AttributesPage.RowIndex(a.Kind, a.Stype) == _attrSelected) sel = a;
        (string title, string delta) = AttributesPage.FooterTitle(sel);
        var (one, two) = AttributesPage.FooterLines(sheet, sel);
        FooterBox(pageH, title, delta, sel?.BuffDelta ?? 0, one, two, sel != null,
            sel != null ? AttributesPage.RankProgress(sel) : 0,
            sel != null && AttributesPage.CanRaiseOne(sheet, sel), sel != null && AttributesPage.CanRaiseTen(sheet, sel), showTen: true,
            sel == null ? null : () => Press(new RaiseTarget(sel.Kind, sel.Stype, Ten: false)),
            sel == null ? null : () => Press(new RaiseTarget(sel.Kind, sel.Stype, Ten: true)));
    }

    // ── The Titles page (0x10000539) ────────────────────────────────────

    private const uint ArtTitleRule = RetailCanvas.ArtTitleRule;
    private const int TitleRowH = 24;

    private void TitlesPage(SkillsSheet sheet, int pageH)
    {
        int y = TabH;
        TitleList book = sheet.TitleBook;
        Text(RetailData.Pal18, "Current Display Title:", 8, y + 20, 270, 18, White);
        string current = book.Displayed != 0 ? SkillsLayout.Title((int)book.Displayed) : "";
        Text(RetailData.Pal18, current.Length > 0 ? current : "None", 8, y + 40, 270, 18, Gold, center: true);
        if (Hit(8, y + 40, 270, 18))
            Hover(2u, "This is the title that all other players will see when they examine your character.");
        Sprite(ArtTitleRule, 0, y + 60, PanelW, 9, 0xFF54788Fu);
        Text(RetailData.Pal18, "All Available Titles:", 8, y + 70, 270, 18, White);

        // The button anchors to the bottom (retail: 200x32 at the page's foot), the rule 10 above it.
        int buttonY = y + pageH - 37;
        int ruleY = buttonY - 10;
        int top = y + 90, listH = Math.Max(TitleRowH, ruleY - 4 - top);
        List<(uint Id, string Text)> titles = book.Sorted();
        int visible = Math.Max(1, listH / TitleRowH);
        float maxScroll = Math.Max(0, titles.Count - visible);

        ImGui.SetCursorScreenPos(P(8, top));
        ImGui.InvisibleButton("##dw_title_list", S(270, listH));
        bool listHovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        if (listHovered && !ImGui.GetIO().KeyCtrl) _titleScroll -= ImGui.GetIO().MouseWheel * 3;
        _titleScroll = Math.Clamp(_titleScroll, 0, maxScroll);
        int first = (int)MathF.Round(_titleScroll);

        _dl.PushClipRect(P(8, top), P(278, top + listH), true);
        try
        {
            if (!book.Known)
                Text(RetailData.Pal16, "Your titles show here after your next login.", 8, top, 270, TitleRowH * 2, White, center: true);
            for (int i = first, ry = top; i < titles.Count && ry < top + listH; i++, ry += TitleRowH)
            {
                (uint id, string text) = titles[i];
                if (id == _titleSelected) SpriteCropped(ArtRowSelected, 8, ry, 270, TitleRowH, 282, 20, 0x60206080u);
                Text(RetailData.Pal16, text, 12, ry, 262, TitleRowH, id == book.Displayed ? Gold : White);
                if (clicked && Hit(8, ry, 270, TitleRowH)) _titleSelected = id;
            }
        }
        finally { _dl.PopClipRect(); }
        if (titles.Count > visible) ScrollBar("title", ref _titleScroll, top, listH, visible, titles.Count, maxScroll);

        Sprite(ArtTitleRule, 0, ruleY, PanelW, 9, 0xFF54788Fu);
        // "Set as Display Title": ghosted until another title than the displayed one is selected.
        bool can = _titleSelected != 0 && _titleSelected != book.Displayed;
        if (_c.RedButton("##dw_set_title", "Set as Display Title", 53, buttonY, 200, can))
        {
            uint id = _titleSelected;
            _onTick(() => _reader.SetDisplayTitle(_host, id, _chat));
        }
    }

    // ── Options (right-click the frame or the tabs) ─────────────────────

    private void OptionsMenu()
    {
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && Hit(-Frame, -Frame, PanelW + 2 * Frame, TabH + Frame))
            ImGui.OpenPopup("##dw_skills_options");
        if (!ImGui.BeginPopup("##dw_skills_options")) return;
        try
        {
            bool replace = _replacesAc();
            if (ImGui.Checkbox("Open this instead of AC's Character window", ref replace)) _setReplacesAc(replace);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("AC's toolbar button and key for the Character window open this panel instead.");
        }
        finally { ImGui.EndPopup(); }
    }

    /// <summary>
    /// Retail's window handling: the gold frame moves the window (an 8 px band round the edge, as AC's
    /// window border), and the bottom edge and its corners resize it, height only, as AC's Character window.
    /// The cursor shows which.
    /// </summary>
    private void FrameHandles(int contentH, bool popped)
    {
        const int Band = 8;              // retail's border band: the frame and 3 px inside it
        int inside = Band - Frame;

        // Resize first: the bottom edge (and its corners) belong to the resize, as in retail.
        ImGui.SetCursorScreenPos(P(-Frame, contentH - inside));
        ImGui.InvisibleButton("##dw_resize", S(PanelW + 2 * Frame, Band));
        bool resizeHot = ImGui.IsItemHovered() || ImGui.IsItemActive();
        if (!popped)
        {
            float mouseY = ImGui.GetMousePos().Y / _k;
            if (ImGui.IsItemActivated())
            {
                _resizing = true;
                _resizeStartH = contentH + 2 * Frame;
                _resizeStartY = mouseY;
                _dragH = _resizeStartH;
            }
            if (_resizing && ImGui.IsItemActive())
            {
                float screenH = ImGui.GetIO().DisplaySize.Y / _k;
                _dragH = Math.Clamp(_resizeStartH + (mouseY - _resizeStartY), MinPanelH, Math.Max(MinPanelH, screenH));
            }
            if (_resizing && !ImGui.IsItemActive())
            {
                _resizing = false;
                _wantH = MathF.Floor(_dragH);
                _wantSince = Environment.TickCount64;
            }
        }
        if (resizeHot && !popped) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);

        // Move: the top band and the two sides.
        bool moveHot = false;
        void MoveBand(string id, int x, int y, int w, int h)
        {
            ImGui.SetCursorScreenPos(P(x, y));
            ImGui.InvisibleButton(id, S(w, h));
            _host.PanelDragWithLastItem();
            moveHot |= ImGui.IsItemHovered() || ImGui.IsItemActive();
        }
        MoveBand("##dw_move_top", -Frame, -Frame, PanelW + 2 * Frame, Frame);
        MoveBand("##dw_move_left", -Frame, 0, Band, contentH - inside);
        MoveBand("##dw_move_right", PanelW - inside, 0, Band, contentH - inside);
        if (moveHot && !resizeHot) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
    }

    // ── Frame, page, tabs ───────────────────────────────────────────────

    private void FrameArt(int contentH)
    {
        int w = PanelW;
        Sprite(ArtEdgeTop, -Frame, -Frame, w + 2 * Frame, Frame, 0xFF54788Fu);
        Sprite(ArtEdgeBottom, -Frame, contentH, w + 2 * Frame, Frame, 0xFF54788Fu);
        Sprite(ArtEdgeLeft, -Frame, -Frame, Frame, contentH + 2 * Frame, 0xFF54788Fu);
        Sprite(ArtEdgeRight, w, -Frame, Frame, contentH + 2 * Frame, 0xFF54788Fu);
        Sprite(ArtCorner, -Frame, -Frame, Frame, Frame, 0xFF6B95B1u);
        Sprite(ArtCorner, w, -Frame, Frame, Frame, 0xFF6B95B1u);
        Sprite(ArtCorner, -Frame, contentH, Frame, Frame, 0xFF6B95B1u);
        Sprite(ArtCorner, w, contentH, Frame, Frame, 0xFF6B95B1u);
    }

    private void Page(int pageH)
    {
        // The page background is a 48x48 black at alpha 175, tiled: the world shows through, as retail.
        // It runs under the tab row too (retail's slanted tab corners show it, not the world).
        _c.PageBackground(0, 0, PanelW, TabH + pageH);
    }

    private void Tabs()
    {
        string[] names = { "Attributes", "Skills", "Titles" };
        for (int i = 0; i < 3; i++)
        {
            bool open = i == _tab;
            uint[] art = open ? TabOpen : TabClosed;
            int x0 = i * TabW;
            // Left cap, a stretched middle, then the right cap: the layout puts it at 83 in a 92-wide tab,
            // so only its first 9 columns show (the slant), as retail.
            Sprite(art[0], x0, 0, CapW, TabH, open ? 0xFF161E24u : Black);
            Sprite(art[1], x0 + CapW, 0, TabW - 2 * CapW + 8, TabH, open ? 0xFF161E24u : Black);
            SpriteCropped(art[2], x0 + TabW - 9, 0, 9, TabH, 17, 25, open ? 0xFF161E24u : Black);
            Text(RetailData.Pal16, names[i], x0, 0, TabW, TabH, open ? TabOn : TabOff, center: true);
            // Tabs only switch the page, as retail's (the window moves by its frame).
            ImGui.SetCursorScreenPos(P(x0, 0));
            if (ImGui.InvisibleButton("##dw_tab" + i, S(TabW, TabH))) _tab = i;
        }

        ImGui.SetCursorScreenPos(P(CloseX, 0));
        bool closed = ImGui.InvisibleButton("##dw_close", S(CloseW, TabH));
        bool held = ImGui.IsItemActive();
        Sprite(held ? ArtClosePressed : ArtClose, CloseX, 0, CloseW, TabH, 0xFF20282Eu);
        if (closed) _host.SetPanelOpen(_title, RynthPanelOpen.Close);
    }

    // ── The header (0x10000230, shared with the Attributes page) ─────────

    private void Header(SkillsSheet s)
    {
        int y = TabH;
        Text(RetailData.Pal18, s.Name, 0, y, 230, 20, White, center: true);
        Text(RetailData.Pal14, s.Identity, 0, y + 20, 230, 15, White, center: true);
        Text(RetailData.Pal14, s.PkStatus, 0, y + 35, 230, 15, White, center: true);
        if (s.LuminanceMax is long max)
        {
            Text(RetailData.Pal16, "Luminance:", 0, y + 52, 110, 18, White);
            Text(RetailData.Pal16, $"{SkillsSheet.Xp(s.LuminanceAvailable ?? 0)} / {SkillsSheet.Xp(max)}", 110, y + 52, 120, 18, White, right: true);
        }
        Text(RetailData.Pal16, "Total Experience (XP):", 0, y + 70, 130, 18, White);
        Text(RetailData.Pal16, s.TotalXp >= 0 ? SkillsSheet.Xp(s.TotalXp) : "", 130, y + 70, 100, 18, White, right: true);
        Sprite(ArtPlate, 0, y + 88, 230, 17, 0xFF1E2A4Au);
        // The plate's red inset (the layout's child at (0,1) 230x15) is a bar: it fills with the way
        // through the current level, on DreamWeave's chart past 275.
        float progress = SkillsLayout.LevelProgress(s.Level, s.TotalXp);
        if (progress > 0)
        {
            IntPtr fill = Tex(RynthIconKind.UiSprite, ArtXpFill);
            float w = 230 * progress;
            if (fill != IntPtr.Zero) _dl.AddImage(fill, P(0, y + 89), P(w, y + 104), Vector2.Zero, new Vector2(progress, 1));
            else _dl.AddRectFilled(P(0, y + 89), P(w, y + 104), Rgb(160, 30, 10));
        }
        Text(RetailData.Pal16, "XP for next level:", 0, y + 88, 130, 17, White);
        Text(RetailData.Pal16, s.NextLevelText, 130, y + 88, 100, 17, White, right: true);
        Sprite(ArtHeaderRule, 230, y, 5, HeaderH, 0xFF54788Fu);
        Text(RetailData.Pal16, "Character", 235, y + 1, 65, 17, White, center: true);
        Text(RetailData.Pal16, "Level", 235, y + 18, 65, 17, White, center: true);
        if (s.Level > 0)
            Text(RetailData.Stone36, SkillsSheet.Number(s.Level), 235, y + 35, 65, 50, Gold, center: true, outline: true);
        Sprite(ArtRule, 0, y + HeaderH, PanelW, RuleH, 0xFF54788Fu);
    }

    // ── The list (0x1000023D) and its scrollbar (0x1000023E) ────────────

    private void SkillList(SkillsSheet sheet, List<SkillRow> rows, int pageH)
    {
        int top = TabH + ListTop;
        int listH = pageH - ListTop - FooterH - RuleH - 3;
        int visible = Math.Max(1, listH / RowH);
        float maxScroll = Math.Max(0, rows.Count - visible);

        // Wheel over the list scrolls it (Ctrl+wheel is the host's draw size).
        ImGui.SetCursorScreenPos(P(0, top));
        ImGui.InvisibleButton("##dw_list", S(BannerW, listH));
        bool listHovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        if (listHovered && !ImGui.GetIO().KeyCtrl) _scroll -= ImGui.GetIO().MouseWheel * 3;
        _scroll = Math.Clamp(_scroll, 0, maxScroll);
        int first = (int)MathF.Round(_scroll);

        Vector2 clipMin = P(0, top), clipMax = P(BannerW, top + listH);
        _dl.PushClipRect(clipMin, clipMax, true);
        bool onRow = false;
        try
        {
            for (int i = first, y = top; i < rows.Count && y < top + listH; i++, y += RowH)
            {
                SkillRow row = rows[i];
                if (row.IsBanner)
                {
                    SpriteCropped(Banner(row.Section), 0, y, BannerW, RowH, 282, 20, 0xFF3A1E2Au);
                    Text(RetailData.Pal18, SkillsLayout.Title(row.Section), 4, y, BannerW - 8, RowH, White);
                    continue;
                }
                SkillLine s = row.Skill!;
                if (s.Id == _selected) SpriteCropped(ArtRowSelected, 0, y, BannerW, RowH, 282, 20, 0x60206080u);
                Icon(s.Icon, 0, y, 20);
                // The list's rows are Palatino 18 (retail's skill rows), the header and footer 16.
                Text(RetailData.Pal18, s.Name, 25, y, 150, RowH, White);
                uint color = s.Buffed > s.Base ? Green : s.Buffed < s.Base ? Red : White;
                Text(RetailData.Pal18, SkillsSheet.Number(s.Buffed), 175, y, 99, RowH, color, right: true);
                if (clicked && Hit(0, y, BannerW, RowH)) { _selected = s.Id; onRow = true; }
                if (listHovered && Hit(0, y, BannerW, RowH) && RetailData.Skill(s.Id) is RetailSkill info)
                    Hover(s.Id, info.Formula.Length > 0 ? info.Formula + "\n" + info.Description : info.Description);
            }
        }
        finally
        {
            _dl.PopClipRect();
        }
        // A click on a section header or below the rows selects nothing, as retail's: the footer shows the
        // skill credits and unassigned XP available (Tom 2026-11-08).
        if (clicked && !onRow) _selected = 0;

        if (rows.Count > visible) ScrollBar("skills", ref _scroll, top, listH, visible, rows.Count, maxScroll);
    }

    // ── The tooltip (drawn last, over everything) and the scrollbar: RetailCanvas's ──
    // Hovering a row a moment shows retail's tooltip: a skill's formula and description, an attribute's
    // or vital's text, the displayed title's explanation.

    private void Hover(uint key, string text) => _c.Hover(key, text);
    private void UpdateTooltip() => _c.UpdateTooltip();
    private void Tooltip() => _c.Tooltip();

    private void ScrollBar(string id, ref float scroll, int top, int h, int visible, int total, float maxScroll)
        => _c.ScrollBar(id, ScrollX, ref scroll, top, h, visible, total, maxScroll);

    // ── The footer (0x10000240 with nothing selected, 0x10000247 with a row), shared by both pages ──

    private void Footer(SkillsSheet sheet, SkillLine? s, int pageH)
    {
        (string title, string delta) = SkillsLayout.FooterTitle(s);
        var (one, two) = SkillsLayout.FooterLines(sheet, s);
        // The arrow raises one rank (trains an untrained skill); +10 shows for trained and specialized.
        FooterBox(pageH, title, delta, s?.BuffDelta ?? 0, one, two, s != null,
            s != null ? SkillsLayout.RankProgress(s) : 0,
            s != null && SkillsLayout.CanRaiseOne(sheet, s), s != null && SkillsLayout.CanRaiseTen(sheet, s),
            showTen: s != null && s.Class >= SkillClass.Trained,
            s == null ? null : () => Press(new RaiseTarget(s.Class >= SkillClass.Trained ? RaiseTarget.Skill : RaiseTarget.Train, s.Id, Ten: false)),
            s == null ? null : () => Press(new RaiseTarget(RaiseTarget.Skill, s.Id, Ten: true)));
    }

    /// <summary>A raise arrow or +10 click: sent on the tick, or held until the last raise lands (HeldRaises).</summary>
    private void Press(RaiseTarget t) => _onTick(() => _reader.Press(_host, t, _chat));

    /// <summary>
    /// The footer: the selected row's title ("Salvaging: 303 (+45)", the buff part green or red), then two
    /// label/value lines; with a row selected the first sits on the plate whose blue inset fills with the way
    /// through the rank, and the raise arrow and +10 stand at the right. With nothing selected, the prompt.
    /// </summary>
    private void FooterBox(int pageH, string title, string delta, int buffDelta,
        (string Label, string Value) one, (string Label, string Value) two, bool selected, float rank,
        bool canOne, bool canTen, bool showTen, Action? raiseOne, Action? raiseTen)
    {
        int ruleY = TabH + pageH - FooterH - RuleH - 3;
        int y = TabH + pageH - FooterH;
        Sprite(ArtRule, 0, ruleY, PanelW, RuleH, 0xFF54788Fu);
        if (!selected)
        {
            Text(RetailData.Pal20, title, 0, y, PanelW, 20, Body, center: true);
            Text(RetailData.Pal16, one.Label, 5, y + 20, 195, 17, White);
            Text(RetailData.Pal16, one.Value, 200, y + 20, 95, 17, White, right: true);
            Text(RetailData.Pal16, two.Label, 5, y + 37, 195, 18, White);
            Text(RetailData.Pal16, two.Value, 200, y + 37, 95, 18, White, right: true);
            return;
        }

        int w = RetailData.Pal20.Measure(title) + RetailData.Pal20.Measure(delta);
        int x = (PanelW - w) / 2;
        Text(RetailData.Pal20, title, x, y, PanelW, 20, White);
        if (delta.Length > 0)
            Text(RetailData.Pal20, delta, x + RetailData.Pal20.Measure(title), y, PanelW, 20, buffDelta > 0 ? Green : Red);
        Sprite(ArtPlate, 5, y + 20, 240, 17, 0xFF1E2A4Au);
        // The plate's blue inset (the layout's child at (0,1) 240x15) is a bar: the way through the rank
        // (a skill's fills by itself from use).
        if (rank > 0)
        {
            IntPtr fill = Tex(RynthIconKind.UiSprite, ArtRankFill);
            float fw = 240 * rank;
            if (fill != IntPtr.Zero) _dl.AddImage(fill, P(5, y + 21), P(5 + fw, y + 36), Vector2.Zero, new Vector2(rank, 1));
            else _dl.AddRectFilled(P(5, y + 21), P(5 + fw, y + 36), Rgb(21, 66, 160));
        }
        Text(RetailData.Pal16, one.Label, 5, y + 20, 145, 17, White);
        Text(RetailData.Pal16, one.Value, 150, y + 20, 95, 17, White, right: true);
        Text(RetailData.Pal16, two.Label, 5, y + 37, 145, 18, White);
        Text(RetailData.Pal16, two.Value, 150, y + 37, 95, 18, White, right: true);

        if (Button("##dw_raise", canOne ? ArtRaise : ArtRaiseGhost, 260, y + 26, 30, 26) && canOne) raiseOne?.Invoke();
        if (showTen && Button("##dw_raise10", canTen ? ArtRaiseTen : ArtRaiseTenGhost, 260, y, 30, 26) && canTen) raiseTen?.Invoke();
    }
    // ── Drawing helpers (design pixels in, screen pixels out): RetailCanvas's ──

    private Vector2 P(float x, float y) => _c.P(x, y);
    private Vector2 S(float w, float h) => _c.S(w, h);
    private bool Hit(int x, int y, int w, int h) => _c.Hit(x, y, w, h);
    private IntPtr Tex(RynthIconKind kind, uint id) => _c.Tex(kind, id);
    private void Sprite(uint id, int x, int y, int w, int h, uint fallback) => _c.Sprite(id, x, y, w, h, fallback);
    private void SpriteCropped(uint id, int x, int y, int w, int h, int texW, int texH, uint fallback)
        => _c.SpriteCropped(id, x, y, w, h, texW, texH, fallback);
    private void Icon(uint id, int x, int y, int size) => _c.Icon(id, x, y, size);
    private bool Button(string imguiId, uint art, int x, int y, int w, int h) => _c.Button(imguiId, art, x, y, w, h);
    private void Text(RetailFont font, string? text, int x, int y, int w, int h, uint color,
        bool right = false, bool center = false, bool outline = false)
        => _c.Text(font, text, x, y, w, h, color, right, center, outline);

    private static SkillLine? Find(SkillsSheet sheet, uint id)
    {
        if (id == 0) return null;
        foreach (SkillLine s in sheet.Skills) if (s.Id == id) return s;
        return null;
    }
}
