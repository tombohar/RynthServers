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

    private const uint ArtEdgeTop = 0x0600612Au, ArtEdgeLeft = 0x0600612Bu, ArtEdgeBottom = 0x0600612Cu, ArtEdgeRight = 0x0600612Du,
        ArtCorner = 0x06006129u, ArtPage = 0x06004CC2u, ArtClose = 0x06001393u, ArtClosePressed = 0x06001394u,
        ArtHeaderRule = 0x06004CB8u, ArtPlate = 0x060011A6u, ArtXpFill = 0x060011A5u, ArtRankFill = 0x06000F8Au, ArtRule = 0x06004CC7u, ArtRowSelected = 0x06000F93u,
        ArtTrack = 0x06004C5Fu, ArtThumbTop = 0x06004C60u, ArtThumbMid = 0x06004C63u, ArtThumbBottom = 0x06004C66u,
        ArtScrollUp = 0x06004C69u, ArtScrollDown = 0x06004C6Cu,
        ArtRaise = 0x06004CB6u, ArtRaiseGhost = 0x06004CB7u, ArtRaiseTen = 0x0600712Bu, ArtRaiseTenGhost = 0x0600712Cu;
    // Tab 3-slices: "Open" (the shown page) and "Closed" states.
    private static readonly uint[] TabOpen = { 0x06005D92u, 0x06005D94u, 0x06005D96u };
    private static readonly uint[] TabClosed = { 0x06005D93u, 0x06005D95u, 0x06005D97u };

    private static uint Banner(SkillSection s) => s switch
    {
        SkillSection.Specialized => 0x06000F90u,
        SkillSection.Trained => 0x06000F86u,
        SkillSection.Untrained => 0x06000F98u,
        _ => 0x06000F89u,
    };

    // ── Colours (ImGui ABGR) ────────────────────────────────────────────
    private const uint White = 0xFFFFFFFFu, Black = 0xFF000000u;
    private static readonly uint Body = Rgb(235, 230, 209);          // OpenAC's "parchment" for the footer prompt
    private static readonly uint TabOn = Rgb(204, 204, 204), TabOff = Rgb(127, 127, 127);
    private static readonly uint Gold = Rgb(255, 242, 127);           // the level number (the layout's 1, .949, .498)
    private static readonly uint Green = Rgb(0, 255, 0), Red = Rgb(255, 0, 0);

    private static uint Rgb(int r, int g, int b) => 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | (uint)r;

    private readonly RynthCoreHost _host;
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
    private bool _thumbDrag;
    private float _thumbGrab;
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
        _tipKey = 0;
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

    private const uint ArtTitleRule = 0x06001420u;
    // The red button, a 3-slice: left 32, a stretched 15-wide middle, right 32; pressed -1, ghosted -2.
    private static readonly uint[] ArtRedButton = { 0x06004C96u, 0x06004C99u, 0x06004C9Cu };
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
        ImGui.SetCursorScreenPos(P(53, buttonY));
        bool pressed = ImGui.InvisibleButton("##dw_set_title", S(200, 32));
        bool held = ImGui.IsItemActive();
        uint d = !can ? 2u : held ? 1u : 0u;
        Sprite(ArtRedButton[0] - d, 53, buttonY, 32, 32, 0xFF20208Cu);
        Sprite(ArtRedButton[1] - d, 85, buttonY, 136, 32, 0xFF20208Cu);
        Sprite(ArtRedButton[2] - d, 221, buttonY, 32, 32, 0xFF20208Cu);
        Text(RetailData.Pal18, "Set as Display Title", 53, buttonY, 200, 32, can ? White : TabOff, center: true);
        if (pressed && can)
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
        IntPtr t = Tex(RynthIconKind.UiSprite, ArtPage);
        int fullH = TabH + pageH;
        for (int y = 0; y < fullH; y += 48)
            for (int x = 0; x < PanelW; x += 48)
            {
                int w = Math.Min(48, PanelW - x), h = Math.Min(48, fullH - y);
                if (t != IntPtr.Zero)
                    _dl.AddImage(t, P(x, y), P(x + w, y + h), Vector2.Zero, new Vector2(w / 48f, h / 48f));
                else
                    _dl.AddRectFilled(P(x, y), P(x + w, y + h), 0xAF000000u);
            }
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

    // ── The tooltip (drawn last, over everything) ───────────────────────
    // Hovering a row a moment shows retail's tooltip: a skill's formula and description, an attribute's
    // or vital's text, the displayed title's explanation.

    private const long TooltipDelayMs = 400;
    private const int TooltipWrap = 230;   // design px: retail breaks the description at about this width
    private static readonly uint TooltipBorder = Rgb(196, 164, 92), TooltipBack = 0xE0000000u;
    private uint _hoverId, _tipKey;
    private long _hoverSince;
    private string _tipCandidate = "";
    private string? _tipText;

    /// <summary>This frame, the mouse is on something with a tooltip (<paramref name="key"/> tells one row from another).</summary>
    private void Hover(uint key, string text)
    {
        _tipKey = key;
        _tipCandidate = text;
    }

    private void UpdateTooltip()
    {
        long now = Environment.TickCount64;
        if (_tipKey != _hoverId) { _hoverId = _tipKey; _hoverSince = now; }
        _tipText = _tipKey != 0 && now - _hoverSince >= TooltipDelayMs && _tipCandidate.Length > 0 ? _tipCandidate : null;
    }

    private void Tooltip()
    {
        if (_tipText == null) return;
        RetailFont f = RetailData.Pal14;
        List<string> lines = f.Wrap(_tipText, TooltipWrap);
        if (lines.Count == 0) return;
        int w = 0;
        foreach (string l in lines) w = Math.Max(w, f.Measure(l));
        int boxW = w + 8, boxH = lines.Count * f.LineHeight + 6;

        // Below and right of the cursor, kept on screen. Design px from the panel's origin.
        Vector2 mouse = ImGui.GetMousePos(), screen = ImGui.GetIO().DisplaySize;
        float x = (mouse.X - _origin.X) / _k + 14, y = (mouse.Y - _origin.Y) / _k + 16;
        float maxX = (screen.X - _origin.X) / _k - boxW - 2, maxY = (screen.Y - _origin.Y) / _k - boxH - 2;
        int bx = (int)MathF.Round(Math.Max(-_origin.X / _k, Math.Min(x, maxX)));
        int by = (int)MathF.Round(Math.Max(-_origin.Y / _k, Math.Min(y, maxY)));

        ImDrawListPtr saved = _dl;
        _dl = ImGui.GetForegroundDrawList();
        try
        {
            _dl.AddRectFilled(P(bx, by), P(bx + boxW, by + boxH), TooltipBack);
            _dl.AddRect(P(bx, by), P(bx + boxW, by + boxH), TooltipBorder, 0, ImDrawFlags.None, Math.Max(1f, _k));
            for (int i = 0; i < lines.Count; i++)
                Text(f, lines[i], bx + 4, by + 3 + i * f.LineHeight, boxW - 8, f.LineHeight, White);
        }
        finally { _dl = saved; }
    }



    private void ScrollBar(string id, ref float scroll, int top, int h, int visible, int total, float maxScroll)
    {
        // The track: a chain of 16x32 links, tiled.
        IntPtr track = Tex(RynthIconKind.UiSprite, ArtTrack);
        for (int y = top + ScrollBtn; y < top + h - ScrollBtn; y += 32)
        {
            int th = Math.Min(32, top + h - ScrollBtn - y);
            if (track != IntPtr.Zero) _dl.AddImage(track, P(ScrollX, y), P(ScrollX + ScrollW, y + th), Vector2.Zero, new Vector2(1, th / 32f));
            else _dl.AddRectFilled(P(ScrollX + 6, y), P(ScrollX + 10, y + th), 0xFF3C6A8Au);
        }
        // The arrows.
        if (Button("##dw_up_" + id, ArtScrollUp, ScrollX, top, ScrollW, ScrollBtn)) scroll = Math.Max(0, scroll - 1);
        if (Button("##dw_dn_" + id, ArtScrollDown, ScrollX, top + h - ScrollBtn, ScrollW, ScrollBtn)) scroll = Math.Min(maxScroll, scroll + 1);

        // The thumb: caps and a stretched blue middle, sized to the share of the list that shows.
        int trackTop = top + ScrollBtn, trackH = h - 2 * ScrollBtn;
        if (trackH <= 8) return;
        int thumbH = Math.Clamp((int)(trackH * visible / (float)total), 16, trackH);
        int thumbY = trackTop + (int)((trackH - thumbH) * (maxScroll > 0 ? scroll / maxScroll : 0));
        ImGui.SetCursorScreenPos(P(ScrollX, trackTop));
        ImGui.InvisibleButton("##dw_track_" + id, S(ScrollW, trackH));
        float mouseY = (ImGui.GetMousePos().Y - _origin.Y) / _k;
        if (ImGui.IsItemActivated())
        {
            if (mouseY >= thumbY && mouseY < thumbY + thumbH) { _thumbDrag = true; _thumbGrab = mouseY - thumbY; }
            else scroll = Math.Clamp(scroll + (mouseY < thumbY ? -visible : visible), 0, maxScroll);
        }
        if (_thumbDrag && ImGui.IsItemActive() && trackH > thumbH)
            scroll = Math.Clamp((mouseY - _thumbGrab - trackTop) / (trackH - thumbH) * maxScroll, 0, maxScroll);
        if (!ImGui.IsItemActive()) _thumbDrag = false;
        Sprite(ArtThumbTop, ScrollX, thumbY, ScrollW, ScrollCap, 0xFF8C5A1Eu);
        Sprite(ArtThumbMid, ScrollX, thumbY + ScrollCap, ScrollW, thumbH - 2 * ScrollCap, 0xFF8C3A10u);
        Sprite(ArtThumbBottom, ScrollX, thumbY + thumbH - ScrollCap, ScrollW, ScrollCap, 0xFF8C5A1Eu);
    }

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
    // ── Drawing helpers (design pixels in, screen pixels out) ───────────

    private Vector2 P(float x, float y) => new(_origin.X + x * _k, _origin.Y + y * _k);
    private Vector2 S(float w, float h) => new(Math.Max(1, w * _k), Math.Max(1, h * _k));

    private bool Hit(int x, int y, int w, int h)
    {
        Vector2 m = ImGui.GetMousePos(), a = P(x, y), b = P(x + w, y + h);
        return m.X >= a.X && m.X < b.X && m.Y >= a.Y && m.Y < b.Y;
    }

    private IntPtr Tex(RynthIconKind kind, uint id) => id == 0 ? IntPtr.Zero : _host.GetIconTexture(kind, id);

    /// <summary>A retail picture stretched over the box (a flat stand-in until the engine has made it).</summary>
    private void Sprite(uint id, int x, int y, int w, int h, uint fallback)
    {
        if (w <= 0 || h <= 0) return;
        IntPtr t = Tex(RynthIconKind.UiSprite, id);
        if (t == IntPtr.Zero) { _dl.AddRectFilled(P(x, y), P(x + w, y + h), fallback); return; }
        _dl.AddImage(t, P(x, y), P(x + w, y + h));
    }

    /// <summary>The top-left <paramref name="w"/> x <paramref name="h"/> of a <paramref name="texW"/> x <paramref name="texH"/> picture, unscaled.</summary>
    private void SpriteCropped(uint id, int x, int y, int w, int h, int texW, int texH, uint fallback)
    {
        IntPtr t = Tex(RynthIconKind.UiSprite, id);
        if (t == IntPtr.Zero) { _dl.AddRectFilled(P(x, y), P(x + w, y + h), fallback); return; }
        _dl.AddImage(t, P(x, y), P(x + w, y + h), Vector2.Zero, new Vector2(Math.Min(1f, w / (float)texW), Math.Min(1f, h / (float)texH)));
    }

    private void Icon(uint id, int x, int y, int size)
    {
        IntPtr t = Tex(RynthIconKind.Icon, id);
        if (t != IntPtr.Zero) _dl.AddImage(t, P(x, y), P(x + size, y + size));
    }

    /// <summary>A retail button: its picture, and true when clicked.</summary>
    private bool Button(string imguiId, uint art, int x, int y, int w, int h)
    {
        ImGui.SetCursorScreenPos(P(x, y));
        bool clicked = ImGui.InvisibleButton(imguiId, S(w, h));
        Sprite(art, x, y, w, h, 0xFF3C8A3Cu);
        return clicked;
    }

    /// <summary>
    /// Text in a retail dat font, left-aligned (or <paramref name="right"/> / <paramref name="center"/>)
    /// in the box and centred down it, glyph by glyph from the font's sheet; <paramref name="outline"/>
    /// draws the font's outline sheet in black under it (the level number). ImGui's font stands in on an
    /// engine without font sheets.
    /// </summary>
    private void Text(RetailFont font, string? text, int x, int y, int w, int h, uint color,
        bool right = false, bool center = false, bool outline = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        int width = font.Measure(text);
        float left = right ? x + w - width : center ? x + (w - width) / 2f : x;
        float top = y + (h - font.LineHeight) / 2f;
        IntPtr sheet = Tex(RynthIconKind.FontSheet, font.Sheet);
        if (sheet == IntPtr.Zero)
        {
            Vector2 sz = ImGui.CalcTextSize(text);
            float fx = right ? P(x + w, 0).X - sz.X : center ? P(x + w / 2f, 0).X - sz.X / 2 : P(x, 0).X;
            _dl.AddText(new Vector2(fx, P(0, y + h / 2f).Y - sz.Y / 2), color, text);
            return;
        }
        left = MathF.Round(left);
        top = MathF.Round(top);
        if (outline && font.OutlineSheet != 0)
        {
            IntPtr back = Tex(RynthIconKind.FontSheet, font.OutlineSheet);
            if (back != IntPtr.Zero) Glyphs(font, text, left, top, back, font.OutlineWidth, font.OutlineHeight, Black, font.BorderX, font.BorderY);
        }
        Glyphs(font, text, left, top, sheet, font.SheetWidth, font.SheetHeight, color, 0, 0);
    }

    private void Glyphs(RetailFont font, string text, float x, float y, IntPtr sheet, int sheetW, int sheetH, uint color, int bx, int by)
    {
        float pen = x;
        foreach (char c in text)
        {
            if (!font.TryGlyph(c, out RetailGlyph g)) continue;
            if (g.Width > 0 && g.Height > 0)
            {
                float gx = pen + g.Before - bx, gy = y + g.Top - by;
                int sx = g.SheetX - bx, sy = g.SheetY - by, gw = g.Width + 2 * bx, gh = g.Height + 2 * by;
                if (sx < 0) { gx -= sx; gw += sx; sx = 0; }
                if (sy < 0) { gy -= sy; gh += sy; sy = 0; }
                if (gw > 0 && gh > 0)
                    _dl.AddImage(sheet, P(gx, gy), P(gx + gw, gy + gh),
                        new Vector2(sx / (float)sheetW, sy / (float)sheetH),
                        new Vector2((sx + gw) / (float)sheetW, (sy + gh) / (float)sheetH), color);
            }
            pen += g.Advance;
        }
    }

    private static SkillLine? Find(SkillsSheet sheet, uint id)
    {
        if (id == 0) return null;
        foreach (SkillLine s in sheet.Skills) if (s.Id == id) return s;
        return null;
    }
}
