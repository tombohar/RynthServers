// AttributesTitles.cs - The Attributes and Titles pages of the DreamWeave Skills panel (2026-11-07, Tom:
// "build the attributes and titles tabs ... make it complete"): the rows they show, their footer text and
// the character's titles as the server sent them. Pure (no ImGui, no host): RynthServerPack.Tests covers it.
//
// Attributes: the six attributes and three vital maximums, in retail's order, with retail's icons and
// hover text (the Character panel's), base and buffed values and raise costs from the engine's training
// numbers (GetTrainingInfo, as the skills), and the vitals' current values ("250/300", as retail).
//
// Titles: the server sends the whole list once, at login (game event 0x0029 CharacterTitle: u32 1, u32 the
// displayed title, u32 count, count x u32 title ids) and one title at a time after (0x002B UpdateTitle: u32
// title, u32 set-as-displayed). The plugin keeps them per character in its settings, so a hot reload (which
// misses the login's list) still has them. Choosing one sends game action 0x002C through "/rc title <id>".
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RynthCore.Plugin.RynthDreamWeave;

/// <summary>One attribute (Kind 1, Stype 1..6) or vital maximum (Kind 2, Stype 1 health, 3 stamina, 5 mana).</summary>
internal sealed record AttributeLine(int Kind, uint Stype, string Name, uint Icon, int Base, int Buffed,
    long CostOne, long CostTen, bool Raisable, bool AtTop, uint Ranks, uint XpSpent, int Current = -1)
{
    public int BuffDelta => Buffed - Base;
    public bool IsVital => Kind == 2;

    /// <summary>The row's value: an attribute's buffed value, a vital's "current/maximum".</summary>
    public string ValueText => IsVital && Current >= 0
        ? Current.ToString(CultureInfo.InvariantCulture) + "/" + Buffed.ToString(CultureInfo.InvariantCulture)
        : Buffed.ToString(CultureInfo.InvariantCulture);
}

internal static class AttributesPage
{
    /// <summary>Retail's rows, in its order: (kind, stype, name, icon, hover text).</summary>
    public static readonly (int Kind, uint Stype, string Name, uint Icon, string Tip)[] Rows =
    {
        (1, 1, "Strength", 0x060002C8u, "Measures your character's muscular power."),
        (1, 2, "Endurance", 0x060002C4u, "Measures how healthy your character is."),
        (1, 4, "Coordination", 0x060002C9u, "Measures your character's reflexes"),
        (1, 3, "Quickness", 0x060002C6u, "Measures how fast your character is."),
        (1, 5, "Focus", 0x060002C5u, "Measures your character's mind and senses."),
        (1, 6, "Self", 0x060002C7u, "Measures your character's willpower."),
        (2, 1, "Health", 0x06004C3Bu, "(Endurance/2)\nIf you run out of health, you will die!"),
        (2, 3, "Stamina", 0x06004C3Cu, "(Endurance)\nAffects your actions and movement."),
        (2, 5, "Mana", 0x06004C3Du, "(Self)\nAffects how much magic you can cast."),
    };

    public static int RowIndex(int kind, uint stype) => Array.FindIndex(Rows, r => r.Kind == kind && r.Stype == stype);

    public static string Tip(AttributeLine a)
    {
        int i = RowIndex(a.Kind, a.Stype);
        return i >= 0 ? Rows[i].Tip : "";
    }

    /// <summary>The footer's title: "Strength: 250" and the buff part (" (+10)").</summary>
    public static (string Text, string Delta) FooterTitle(AttributeLine? a)
    {
        if (a == null) return ("Select an Attribute to Improve", "");
        int d = a.BuffDelta;
        return (a.Name + ": " + a.Buffed.ToString(CultureInfo.InvariantCulture), d > 0 ? $" (+{d})" : d < 0 ? $" ({d})" : "");
    }

    public static ((string Label, string Value) One, (string Label, string Value) Two) FooterLines(SkillsSheet sheet, AttributeLine? a)
    {
        string credits = sheet.SkillCredits >= 0 ? SkillsSheet.Number(sheet.SkillCredits) : "";
        string unassigned = sheet.UnassignedXp >= 0 ? SkillsSheet.Xp(sheet.UnassignedXp) : "";
        if (a == null) return (("Skill Credits Available:", credits), ("Unassigned Experience:", unassigned));
        return (("Experience To Raise:", a.CostOne > 0 ? SkillsSheet.Xp(a.CostOne) : "Infinity!"), ("Unassigned Experience:", unassigned));
    }

    public static bool CanRaiseOne(SkillsSheet sheet, AttributeLine a)
        => a.Raisable && a.CostOne > 0 && sheet.UnassignedXp >= a.CostOne;

    public static bool CanRaiseTen(SkillsSheet sheet, AttributeLine a)
        => a.Raisable && a.CostTen > 0 && sheet.UnassignedXp >= a.CostTen;

    /// <summary>The way through the current rank (0..1) on the attribute or vital chart: the footer's blue bar.</summary>
    public static float RankProgress(AttributeLine a)
    {
        uint[] chart = a.IsVital ? RetailData.VitalXp : RetailData.AttributeXp;
        if (a.AtTop || a.Ranks + 1 >= chart.Length) return 0f;
        uint from = chart[a.Ranks], to = chart[a.Ranks + 1];
        if (to <= from || a.XpSpent <= from) return 0f;
        if (a.XpSpent >= to) return 1f;
        return (float)((double)(a.XpSpent - from) / (to - from));
    }
}

/// <summary>The character's titles: the one displayed and every one earned (ids, CharacterTitleId's numbers).</summary>
internal sealed record TitleList(uint Displayed, IReadOnlyList<uint> Earned)
{
    public static readonly TitleList Empty = new(0, Array.Empty<uint>());

    public bool Known => Earned.Count > 0;

    /// <summary>The earned titles' text, sorted as retail's list (by text, then id); unknown ids left out.</summary>
    public List<(uint Id, string Text)> Sorted()
        => Earned.Select(id => (Id: id, Text: SkillsLayout.Title((int)id)))
            .Where(t => t.Text.Length > 0)
            .OrderBy(t => t.Text, StringComparer.Ordinal).ThenBy(t => t.Id)
            .ToList();

    /// <summary>Game event 0x0029 CharacterTitle's body: u32 1, u32 displayed, u32 count, count x u32.</summary>
    public static bool TryParseTable(ReadOnlySpan<byte> body, out TitleList list)
    {
        list = Empty;
        if (body.Length < 12) return false;
        uint displayed = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(8));
        if (count > 10000 || body.Length < 12 + 4 * (long)count) return false;
        var ids = new List<uint>((int)count);
        for (int i = 0; i < count; i++) ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(12 + 4 * i)));
        list = new TitleList(displayed, ids);
        return true;
    }

    /// <summary>Game event 0x002B UpdateTitle's body (u32 title, u32 set as displayed) applied to this list.</summary>
    public TitleList WithUpdate(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8) return this;
        uint id = BinaryPrimitives.ReadUInt32LittleEndian(body);
        bool display = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4)) != 0;
        var earned = Earned.Contains(id) || id == 0 ? Earned : Earned.Append(id).ToList();
        return new TitleList(display ? id : Displayed, earned);
    }

    /// <summary>A copy with another displayed title (CharacterTitleId changed, e.g. after Set as Display Title).</summary>
    public TitleList WithDisplayed(uint id) => id == 0 || id == Displayed ? this : new TitleList(id, Earned);

    /// <summary>"433|1,7,433" for the plugin's settings.</summary>
    public string Save() => Displayed.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(",", Earned.Select(i => i.ToString(CultureInfo.InvariantCulture)));

    public static TitleList Load(string? saved)
    {
        if (string.IsNullOrEmpty(saved)) return Empty;
        string[] p = saved.Split('|');
        if (p.Length != 2 || !uint.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint shown)) return Empty;
        var ids = new List<uint>();
        foreach (string s in p[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint id)) ids.Add(id);
        return new TitleList(shown, ids);
    }
}
