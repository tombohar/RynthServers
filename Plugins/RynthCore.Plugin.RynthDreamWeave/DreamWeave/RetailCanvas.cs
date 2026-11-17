// RetailCanvas.cs - RynthDreamWeave's retail drawing helpers, shared by the Character panel (SkillsPanel.cs)
// and the Leaderboards page (BoardsPage.cs) so both look like retail's own panels (2026-11-15: moved out of
// SkillsPanel unchanged, so the leaderboards use the same pictures, dat fonts, rows, scrollbar, red buttons
// and tooltip). Design pixels in, screen pixels out: Begin sets where the content's top-left is and how many
// screen pixels a design pixel is.
//
// The pictures are the retail client's own 0x06 textures (GetIconTexture kind UiSprite) and the text its
// dat fonts drawn glyph by glyph from their sheets (kind FontSheet, glyph tables in RetailData.g.cs). An
// engine without those kinds (before 2026-11-07) gets flat stand-ins and ImGui's font.
// AC's render thread only.
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDreamWeave;

internal sealed class RetailCanvas
{
    // ── Retail pictures both panels draw ────────────────────────────────
    public const uint ArtPage = 0x06004CC2u, ArtRule = 0x06004CC7u, ArtRowSelected = 0x06000F93u, ArtTitleRule = 0x06001420u,
        ArtTrack = 0x06004C5Fu, ArtThumbTop = 0x06004C60u, ArtThumbMid = 0x06004C63u, ArtThumbBottom = 0x06004C66u,
        ArtScrollUp = 0x06004C69u, ArtScrollDown = 0x06004C6Cu,
        // The upright rule between the Character panel's header and its level box (5 wide).
        ArtColumnRule = 0x06004CB8u;
    public const int RuleH = 7;
    /// <summary>The skill list's section banners (282x20): Specialized, Trained, Untrained, Unusable.</summary>
    public const uint ArtBannerSpecialized = 0x06000F90u, ArtBannerTrained = 0x06000F86u, ArtBannerUntrained = 0x06000F98u, ArtBannerOther = 0x06000F89u;
    /// <summary>The red button, a 3-slice: left 32, a stretched 15-wide middle, right 32; pressed -1, ghosted -2.</summary>
    public static readonly uint[] ArtRedButton = { 0x06004C96u, 0x06004C99u, 0x06004C9Cu };
    public const int ScrollW = 16, ScrollBtn = 16, ScrollCap = 3, RowH = 20;

    // ── Colours (ImGui ABGR) ────────────────────────────────────────────
    public const uint White = 0xFFFFFFFFu, Black = 0xFF000000u;
    public static readonly uint Body = Rgb(235, 230, 209);          // OpenAC's "parchment" for the footer prompt
    public static readonly uint TabOn = Rgb(204, 204, 204), TabOff = Rgb(127, 127, 127);
    public static readonly uint Gold = Rgb(255, 242, 127);           // the level number (the layout's 1, .949, .498)
    public static readonly uint Green = Rgb(0, 255, 0), Red = Rgb(255, 0, 0);

    public static uint Rgb(int r, int g, int b) => 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | (uint)r;

    private readonly RynthCoreHost _host;

    public RetailCanvas(RynthCoreHost host) => _host = host;

    /// <summary>The draw list being drawn into (the window's; the tooltip swaps in the foreground's).</summary>
    public ImDrawListPtr Dl;
    /// <summary>The content's top-left, screen pixels.</summary>
    public Vector2 Origin;
    /// <summary>Screen pixels per design pixel.</summary>
    public float K = 1f;

    /// <summary>Starts a frame: draws into <paramref name="dl"/> with design (0,0) at <paramref name="origin"/>.</summary>
    public void Begin(ImDrawListPtr dl, Vector2 origin, float k)
    {
        Dl = dl;
        Origin = origin;
        K = k;
    }

    public Vector2 P(float x, float y) => new(Origin.X + x * K, Origin.Y + y * K);
    public Vector2 S(float w, float h) => new(Math.Max(1, w * K), Math.Max(1, h * K));

    public bool Hit(int x, int y, int w, int h)
    {
        Vector2 m = ImGui.GetMousePos(), a = P(x, y), b = P(x + w, y + h);
        return m.X >= a.X && m.X < b.X && m.Y >= a.Y && m.Y < b.Y;
    }

    public IntPtr Tex(RynthIconKind kind, uint id) => id == 0 ? IntPtr.Zero : _host.GetIconTexture(kind, id);

    /// <summary>A retail picture stretched over the box (a flat stand-in until the engine has made it).</summary>
    public void Sprite(uint id, int x, int y, int w, int h, uint fallback)
    {
        if (w <= 0 || h <= 0) return;
        IntPtr t = Tex(RynthIconKind.UiSprite, id);
        if (t == IntPtr.Zero) { Dl.AddRectFilled(P(x, y), P(x + w, y + h), fallback); return; }
        Dl.AddImage(t, P(x, y), P(x + w, y + h));
    }

    /// <summary>The top-left <paramref name="w"/> x <paramref name="h"/> of a <paramref name="texW"/> x <paramref name="texH"/> picture, unscaled.</summary>
    public void SpriteCropped(uint id, int x, int y, int w, int h, int texW, int texH, uint fallback)
    {
        IntPtr t = Tex(RynthIconKind.UiSprite, id);
        if (t == IntPtr.Zero) { Dl.AddRectFilled(P(x, y), P(x + w, y + h), fallback); return; }
        Dl.AddImage(t, P(x, y), P(x + w, y + h), Vector2.Zero, new Vector2(Math.Min(1f, w / (float)texW), Math.Min(1f, h / (float)texH)));
    }

    public void Icon(uint id, int x, int y, int size)
    {
        IntPtr t = Tex(RynthIconKind.Icon, id);
        if (t != IntPtr.Zero) Dl.AddImage(t, P(x, y), P(x + size, y + size));
    }

    /// <summary>A retail button: its picture, and true when clicked.</summary>
    public bool Button(string imguiId, uint art, int x, int y, int w, int h)
    {
        ImGui.SetCursorScreenPos(P(x, y));
        bool clicked = ImGui.InvisibleButton(imguiId, S(w, h));
        Sprite(art, x, y, w, h, 0xFF3C8A3Cu);
        return clicked;
    }

    /// <summary>
    /// Retail's red button (the Titles page's "Set as Display Title"): the 3-slice art, ghosted when not
    /// <paramref name="enabled"/>, pressed while held, its label in Palatino 18. True when clicked while enabled.
    /// </summary>
    public bool RedButton(string imguiId, string label, int x, int y, int w, bool enabled)
    {
        w = Math.Max(w, 64);
        ImGui.SetCursorScreenPos(P(x, y));
        bool pressed = ImGui.InvisibleButton(imguiId, S(w, 32));
        bool held = ImGui.IsItemActive();
        uint d = !enabled ? 2u : held ? 1u : 0u;
        Sprite(ArtRedButton[0] - d, x, y, 32, 32, 0xFF20208Cu);
        Sprite(ArtRedButton[1] - d, x + 32, y, w - 64, 32, 0xFF20208Cu);
        Sprite(ArtRedButton[2] - d, x + w - 32, y, 32, 32, 0xFF20208Cu);
        Text(RetailData.Pal18, label, x, y, w, 32, enabled ? White : TabOff, center: true);
        return pressed && enabled;
    }

    /// <summary>The page background: a 48x48 black at alpha 175, tiled, so the world shows through as retail.</summary>
    public void PageBackground(int x0, int y0, int w, int h)
    {
        IntPtr t = Tex(RynthIconKind.UiSprite, ArtPage);
        for (int y = 0; y < h; y += 48)
            for (int x = 0; x < w; x += 48)
            {
                int tw = Math.Min(48, w - x), th = Math.Min(48, h - y);
                if (t != IntPtr.Zero)
                    Dl.AddImage(t, P(x0 + x, y0 + y), P(x0 + x + tw, y0 + y + th), Vector2.Zero, new Vector2(tw / 48f, th / 48f));
                else
                    Dl.AddRectFilled(P(x0 + x, y0 + y), P(x0 + x + tw, y0 + y + th), 0xAF000000u);
            }
    }

    /// <summary>
    /// Text in a retail dat font, left-aligned (or <paramref name="right"/> / <paramref name="center"/>)
    /// in the box and centred down it, glyph by glyph from the font's sheet; <paramref name="outline"/>
    /// draws the font's outline sheet in black under it (the level number). ImGui's font stands in on an
    /// engine without font sheets.
    /// </summary>
    public void Text(RetailFont font, string? text, int x, int y, int w, int h, uint color,
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
            Dl.AddText(new Vector2(fx, P(0, y + h / 2f).Y - sz.Y / 2), color, text);
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

    /// <summary><paramref name="text"/> cut to fit <paramref name="width"/> design px in <paramref name="font"/>, "..." added.</summary>
    public static string Fit(RetailFont font, string? text, int width)
    {
        string t = text ?? "";
        if (font.Measure(t) <= width) return t;
        int dots = font.Measure("...");
        while (t.Length > 0 && font.Measure(t) + dots > width) t = t[..^1];
        return t.TrimEnd() + "...";
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
                    Dl.AddImage(sheet, P(gx, gy), P(gx + gw, gy + gh),
                        new Vector2(sx / (float)sheetW, sy / (float)sheetH),
                        new Vector2((sx + gw) / (float)sheetW, (sy + gh) / (float)sheetH), color);
            }
            pen += g.Advance;
        }
    }

    // ── The scrollbar (the Character panel's 0x1000023E) ─────────────────

    private bool _thumbDrag;
    private float _thumbGrab;

    /// <summary>Retail's chain-link scrollbar at design x <paramref name="x"/>, beside a list of <paramref name="total"/> rows.</summary>
    public void ScrollBar(string id, int x, ref float scroll, int top, int h, int visible, int total, float maxScroll)
    {
        // The track: a chain of 16x32 links, tiled.
        IntPtr track = Tex(RynthIconKind.UiSprite, ArtTrack);
        for (int y = top + ScrollBtn; y < top + h - ScrollBtn; y += 32)
        {
            int th = Math.Min(32, top + h - ScrollBtn - y);
            if (track != IntPtr.Zero) Dl.AddImage(track, P(x, y), P(x + ScrollW, y + th), Vector2.Zero, new Vector2(1, th / 32f));
            else Dl.AddRectFilled(P(x + 6, y), P(x + 10, y + th), 0xFF3C6A8Au);
        }
        // The arrows.
        if (Button("##dw_up_" + id, ArtScrollUp, x, top, ScrollW, ScrollBtn)) scroll = Math.Max(0, scroll - 1);
        if (Button("##dw_dn_" + id, ArtScrollDown, x, top + h - ScrollBtn, ScrollW, ScrollBtn)) scroll = Math.Min(maxScroll, scroll + 1);

        // The thumb: caps and a stretched blue middle, sized to the share of the list that shows.
        int trackTop = top + ScrollBtn, trackH = h - 2 * ScrollBtn;
        if (trackH <= 8) return;
        int thumbH = Math.Clamp((int)(trackH * visible / (float)total), 16, trackH);
        int thumbY = trackTop + (int)((trackH - thumbH) * (maxScroll > 0 ? scroll / maxScroll : 0));
        ImGui.SetCursorScreenPos(P(x, trackTop));
        ImGui.InvisibleButton("##dw_track_" + id, S(ScrollW, trackH));
        float mouseY = (ImGui.GetMousePos().Y - Origin.Y) / K;
        if (ImGui.IsItemActivated())
        {
            if (mouseY >= thumbY && mouseY < thumbY + thumbH) { _thumbDrag = true; _thumbGrab = mouseY - thumbY; }
            else scroll = Math.Clamp(scroll + (mouseY < thumbY ? -visible : visible), 0, maxScroll);
        }
        if (_thumbDrag && ImGui.IsItemActive() && trackH > thumbH)
            scroll = Math.Clamp((mouseY - _thumbGrab - trackTop) / (trackH - thumbH) * maxScroll, 0, maxScroll);
        if (!ImGui.IsItemActive()) _thumbDrag = false;
        Sprite(ArtThumbTop, x, thumbY, ScrollW, ScrollCap, 0xFF8C5A1Eu);
        Sprite(ArtThumbMid, x, thumbY + ScrollCap, ScrollW, thumbH - 2 * ScrollCap, 0xFF8C3A10u);
        Sprite(ArtThumbBottom, x, thumbY + thumbH - ScrollCap, ScrollW, ScrollCap, 0xFF8C5A1Eu);
    }

    // ── The tooltip (drawn last, over everything) ───────────────────────
    // Hovering something a moment shows retail's tooltip box: Palatino 14 on black in a gold line.

    private const long TooltipDelayMs = 400;
    private const int TooltipWrap = 230;   // design px: retail breaks the description at about this width
    private static readonly uint TooltipBorder = Rgb(196, 164, 92), TooltipBack = 0xE0000000u;
    private uint _hoverId, _tipKey;
    private long _hoverSince;
    private string _tipCandidate = "";
    private string? _tipText;

    /// <summary>Starts a frame's hover tracking (nothing hovered yet).</summary>
    public void ClearHover() => _tipKey = 0;

    /// <summary>This frame, the mouse is on something with a tooltip (<paramref name="key"/> tells one row from another).</summary>
    public void Hover(uint key, string text)
    {
        _tipKey = key;
        _tipCandidate = text;
    }

    public void UpdateTooltip()
    {
        long now = Environment.TickCount64;
        if (_tipKey != _hoverId) { _hoverId = _tipKey; _hoverSince = now; }
        _tipText = _tipKey != 0 && now - _hoverSince >= TooltipDelayMs && _tipCandidate.Length > 0 ? _tipCandidate : null;
    }

    public void Tooltip()
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
        float x = (mouse.X - Origin.X) / K + 14, y = (mouse.Y - Origin.Y) / K + 16;
        float maxX = (screen.X - Origin.X) / K - boxW - 2, maxY = (screen.Y - Origin.Y) / K - boxH - 2;
        int bx = (int)MathF.Round(Math.Max(-Origin.X / K, Math.Min(x, maxX)));
        int by = (int)MathF.Round(Math.Max(-Origin.Y / K, Math.Min(y, maxY)));

        ImDrawListPtr saved = Dl;
        Dl = ImGui.GetForegroundDrawList();
        try
        {
            Dl.AddRectFilled(P(bx, by), P(bx + boxW, by + boxH), TooltipBack);
            Dl.AddRect(P(bx, by), P(bx + boxW, by + boxH), TooltipBorder, 0, ImDrawFlags.None, Math.Max(1f, K));
            for (int i = 0; i < lines.Count; i++)
                Text(f, lines[i], bx + 4, by + 3 + i * f.LineHeight, boxW - 8, f.LineHeight, White);
        }
        finally { Dl = saved; }
    }
}
