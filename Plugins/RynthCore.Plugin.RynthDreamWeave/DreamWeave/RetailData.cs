// RetailData.cs - The shapes of the retail data RetailData.g.cs carries (generated from portal.dat by
// Tools/RetailDataGen/gen_retail_data.py): a skill table row and a dat font's glyph table.
// Pure (no ImGui, no host): RynthServerPack.Tests covers the lookups.
using System.Collections.Generic;

namespace RynthCore.Plugin.RynthDreamWeave;

/// <summary>
/// A SkillTable row: the skill's id, name, 0x06 icon, whether it is usable untrained (MinLevel 1), and the
/// hover tooltip: its formula line ("( Focus + Self ) / 4", empty for a skill with no attribute part) and
/// its description.
/// </summary>
internal sealed record RetailSkill(uint Id, string Name, uint Icon, bool UsableUntrained, string Formula, string Description);

/// <summary>One glyph of a dat font: where it sits on the sheet and how it advances.</summary>
internal readonly record struct RetailGlyph(int SheetX, int SheetY, int Width, int Height, int Before, int After, int Top)
{
    /// <summary>The pen's advance (retail: before + width + after).</summary>
    public int Advance => Before + Width + After;
}

/// <summary>
/// A retail dat font (a Font file, 0x40xxxxxx): its glyph sheets (0x06 A8 textures the engine hands over
/// as GetIconTexture kind FontSheet), line height and the glyph table for ASCII and Latin-1.
/// The outline sheet is set only for the faces the panel draws outlined (the level number).
/// </summary>
internal sealed class RetailFont
{
    private readonly Dictionary<char, RetailGlyph> _glyphs = new();

    public RetailFont(uint id, int lineHeight, int baseline, int borderX, int borderY,
        uint sheet, int sheetWidth, int sheetHeight, uint outlineSheet, int outlineWidth, int outlineHeight, short[] glyphs)
    {
        Id = id;
        LineHeight = lineHeight;
        Baseline = baseline;
        BorderX = borderX;
        BorderY = borderY;
        Sheet = sheet;
        SheetWidth = sheetWidth;
        SheetHeight = sheetHeight;
        OutlineSheet = outlineSheet;
        OutlineWidth = outlineWidth;
        OutlineHeight = outlineHeight;
        for (int i = 0; i + 7 < glyphs.Length; i += 8)
            _glyphs[(char)glyphs[i]] = new RetailGlyph(glyphs[i + 1], glyphs[i + 2], glyphs[i + 3], glyphs[i + 4],
                glyphs[i + 5], glyphs[i + 6], glyphs[i + 7]);
    }

    public uint Id { get; }
    public int LineHeight { get; }
    public int Baseline { get; }
    public int BorderX { get; }
    public int BorderY { get; }
    public uint Sheet { get; }
    public int SheetWidth { get; }
    public int SheetHeight { get; }
    public uint OutlineSheet { get; }
    public int OutlineWidth { get; }
    public int OutlineHeight { get; }

    public bool TryGlyph(char c, out RetailGlyph glyph) => _glyphs.TryGetValue(c, out glyph);

    /// <summary>Breaks <paramref name="text"/> at spaces into lines no wider than <paramref name="width"/> design px
    /// (a word wider than that gets a line of its own).</summary>
    public List<string> Wrap(string? text, int width)
    {
        var lines = new List<string>();
        foreach (string para in (text ?? "").Replace("\r", "").Split('\n'))
        {
            string line = "";
            foreach (string word in para.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(next) > width) { lines.Add(line); line = word; }
                else line = next;
            }
            if (line.Length > 0) lines.Add(line);
        }
        return lines;
    }

    /// <summary>The width of <paramref name="text"/> in design pixels (characters the font lacks are skipped, as retail).</summary>
    public int Measure(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int w = 0;
        foreach (char c in text)
            if (_glyphs.TryGetValue(c, out RetailGlyph g)) w += g.Advance;
        return w;
    }
}

internal static partial class RetailData
{
    private static Dictionary<uint, RetailSkill>? _byId;

    /// <summary>The skill table row for an id, or null.</summary>
    public static RetailSkill? Skill(uint id)
    {
        _byId ??= BuildIndex();
        return _byId.TryGetValue(id, out RetailSkill? s) ? s : null;
    }

    private static Dictionary<uint, RetailSkill> BuildIndex()
    {
        var map = new Dictionary<uint, RetailSkill>(Skills.Length);
        foreach (RetailSkill s in Skills) map[s.Id] = s;
        return map;
    }
}
