// SkillsSheet.cs - What the DreamWeave Skills panel shows: one immutable snapshot of the character,
// read on the plugin tick (SkillsReader) and drawn on the render thread (SkillsPanel).
//
// The numbers are the engine's (plugin API v79 GetTrainingInfo: level, total and unassigned XP, skill
// credits, and per skill its class, base and buffed values and the XP for +1 and +10, priced from the
// client's own tables), the header's words the player object's properties. The one number the client
// can't give past level 275 is the next level's total: that comes from DreamWeaveXp.
//
// Building the rows from that snapshot is pure (Build), so RynthServerPack.Tests covers the sections,
// their order and the footer's text.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RynthCore.Plugin.RynthDreamWeave;

/// <summary>A skill's training: untrained 1, trained 2, specialized 3 (the training API's Class).</summary>
internal enum SkillClass { None = 0, Untrained = 1, Trained = 2, Specialized = 3 }

/// <summary>One skill as the engine reported it.</summary>
internal sealed record SkillLine(uint Id, string Name, uint Icon, SkillClass Class, bool Usable,
    int Base, int Buffed, long CostOne, long CostTen, int TrainCredits, bool Raisable, bool Trainable, bool AtTop,
    uint Ranks = 0, uint XpSpent = 0)
{
    /// <summary>The buff part of the value (buffed - base).</summary>
    public int BuffDelta => Buffed - Base;
}

/// <summary>The character as the panel shows it.</summary>
internal sealed record SkillsSheet(
    string Name, string Identity, string PkStatus,
    int Level, long TotalXp, long UnassignedXp, int SkillCredits,
    long? LuminanceAvailable, long? LuminanceMax,
    IReadOnlyList<SkillLine> Skills, bool Busy, uint Version,
    IReadOnlyList<AttributeLine>? Attributes = null, TitleList? Titles = null)
{
    public static readonly SkillsSheet Empty = new("", "", "", 0, -1, -1, -1, null, null, Array.Empty<SkillLine>(), false, 0);

    /// <summary>The Attributes page's rows (retail's order); empty until read.</summary>
    public IReadOnlyList<AttributeLine> AttributeRows => Attributes ?? Array.Empty<AttributeLine>();

    /// <summary>The Titles page's list; empty until the server's list has been seen once.</summary>
    public TitleList TitleBook => Titles ?? TitleList.Empty;

    public bool HasNumbers => Skills.Count > 0;

    /// <summary>"XP for next level": DreamWeave's chart past 275, retail's below it.</summary>
    public string NextLevelText
    {
        get
        {
            if (TotalXp < 0 || Level <= 0) return "";
            long? need = DreamWeaveXp.ToNextLevel(Level, TotalXp);
            return need is long n ? Xp(n) : "Infinity!";
        }
    }

    public static string Xp(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A row of the skills list: a section banner, or a skill.</summary>
internal readonly record struct SkillRow(SkillSection Section, SkillLine? Skill)
{
    public bool IsBanner => Skill == null;
}

/// <summary>The page's four sections, in order, with their banner art (LayoutDesc 0x2100002E's).</summary>
internal enum SkillSection { Specialized, Trained, Untrained, Unusable }

internal static class SkillsLayout
{
    /// <summary>
    /// The list as retail draws it: Specialized, Trained, Untrained (usable untrained) and Unusable
    /// Skills, each a banner then its skills by name; a section with no skills is left out.
    /// </summary>
    public static List<SkillRow> Rows(IReadOnlyList<SkillLine> skills)
    {
        var rows = new List<SkillRow>(skills.Count + 4);
        foreach (SkillSection section in new[] { SkillSection.Specialized, SkillSection.Trained, SkillSection.Untrained, SkillSection.Unusable })
        {
            var members = new List<SkillLine>();
            foreach (SkillLine s in skills)
                if (SectionOf(s) == section) members.Add(s);
            if (members.Count == 0) continue;
            members.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            rows.Add(new SkillRow(section, null));
            foreach (SkillLine s in members) rows.Add(new SkillRow(section, s));
        }
        return rows;
    }

    public static SkillSection? SectionOf(SkillLine s) => s.Class switch
    {
        SkillClass.Specialized => SkillSection.Specialized,
        SkillClass.Trained => SkillSection.Trained,
        SkillClass.Untrained => s.Usable ? SkillSection.Untrained : SkillSection.Unusable,
        _ => null,
    };

    public static string Title(SkillSection section) => section switch
    {
        SkillSection.Specialized => "Specialized Skills",
        SkillSection.Trained => "Trained Skills",
        SkillSection.Untrained => "Untrained Skills",
        _ => "Unusable Skills",
    };

    /// <summary>The footer's title: "Salvaging: 303" then the buff part ("(+45)") drawn green or red.</summary>
    public static (string Text, string Delta) FooterTitle(SkillLine? s)
    {
        if (s == null) return ("Select a Skill to Improve", "");
        if (s.Class < SkillClass.Trained) return (s.Name, "");
        string text = s.Name + ": " + SkillsSheet.Number(s.Buffed);
        int d = s.BuffDelta;
        return (text, d > 0 ? $" (+{d})" : d < 0 ? $" ({d})" : "");
    }

    /// <summary>The footer's two label/value lines (retail's words).</summary>
    public static ((string Label, string Value) One, (string Label, string Value) Two) FooterLines(SkillsSheet sheet, SkillLine? s)
    {
        string credits = sheet.SkillCredits >= 0 ? SkillsSheet.Number(sheet.SkillCredits) : "";
        string unassigned = sheet.UnassignedXp >= 0 ? SkillsSheet.Xp(sheet.UnassignedXp) : "";
        if (s == null)
            return (("Skill Credits Available:", credits), ("Unassigned Experience:", unassigned));
        if (s.Class < SkillClass.Trained)
            return (("Skill Credits To Raise:", s.TrainCredits > 0 ? SkillsSheet.Number(s.TrainCredits) : "Infinity!"),
                    ("Skill Credits Available:", credits));
        return (("Experience To Raise:", s.CostOne > 0 ? SkillsSheet.Xp(s.CostOne) : "Infinity!"),
                ("Unassigned Experience:", unassigned));
    }

    /// <summary>
    /// Whether the raise arrow (+1, or train for an untrained skill) can be pressed now. Not ghosted while an
    /// earlier raise is on its way: that press is held and sent when it lands (HeldRaises).
    /// </summary>
    public static bool CanRaiseOne(SkillsSheet sheet, SkillLine s)
    {
        if (s.Class >= SkillClass.Trained) return s.Raisable && s.CostOne > 0 && sheet.UnassignedXp >= s.CostOne;
        return s.Trainable && s.TrainCredits > 0 && sheet.SkillCredits >= s.TrainCredits;
    }

    /// <summary>Whether "+10" can be pressed now (trained or specialized, a rank left and the step affordable).</summary>
    public static bool CanRaiseTen(SkillsSheet sheet, SkillLine s)
        => s.Class >= SkillClass.Trained && s.Raisable && Ten(s) is { Ranks: > 0 } t && sheet.UnassignedXp >= t.Cost;

    /// <summary>What "+10" buys for a trained or specialized skill (<see cref="TenStep"/> on its chart).</summary>
    public static (uint Ranks, long Cost) Ten(SkillLine s) => TenStep(s.Class switch
    {
        SkillClass.Specialized => RetailData.SpecializedSkillXp,
        SkillClass.Trained => RetailData.TrainedSkillXp,
        _ => Array.Empty<uint>(),
    }, s.Ranks, s.XpSpent, s.CostTen);

    /// <summary>
    /// Retail's "+10" (gmSkillUI / gmAttributeUI GetCostToRaise10, acclient 0x0049B130 / 0x0049CEE0): ten
    /// ranks, or every rank left when fewer than ten are, for chart[ranks + n] - spent. The engine's +10
    /// (<paramref name="costTen"/>) is -1 when fewer than ten are left, so the panel ghosted +10 for the last
    /// nine ranks and a stat raised by tens stuck there (a #rynth-issues report, 2026-11-11, "Can't max
    /// skills": a skill untrained and trained again comes back at rank 0, and twenty +10s leave a trained one
    /// at 200 of 208). The engine's cost is used whenever it has one; the last ranks are priced from the same
    /// portal chart, and the engine checks that price against its own before anything is sent. (0, -1): none.
    /// </summary>
    public static (uint Ranks, long Cost) TenStep(uint[] chart, uint ranks, uint spent, long costTen)
    {
        if (costTen > 0) return (10, costTen);
        if (chart.Length == 0 || ranks + 1L >= chart.Length) return (0, -1);   // at the top (or no chart)
        uint left = (uint)(chart.Length - 1 - ranks);
        if (left >= 10) return (0, -1);   // ten are left yet the engine priced none: don't guess
        long cost = (long)chart[ranks + left] - spent;
        return cost > 0 ? (left, cost) : (0, -1);
    }

    /// <summary>
    /// Retail's header line one: the allegiance rank's title for the heritage and sex, then the name
    /// ("Yeoman Drakkon"); the name alone with no rank (0) or no title for the heritage.
    /// </summary>
    public static string NameLine(string name, int gender, int heritage, int rank)
    {
        string? title = RankTitle(gender, heritage, rank);
        return string.IsNullOrEmpty(title) || string.IsNullOrEmpty(name) ? name : title + " " + name;
    }

    // Allegiance rank titles 1..10 by heritage (male; female where it differs). Game text, as the client shows it.
    private static readonly string[] AluvianM = { "Yeoman", "Baronet", "Baron", "Reeve", "Thane", "Ealdor", "Duke", "Aetheling", "King", "High King" };
    private static readonly string[] AluvianF = { "Yeoman", "Baronet", "Baroness", "Reeve", "Thane", "Ealdor", "Duchess", "Aetheling", "Queen", "High Queen" };
    private static readonly string[] GharuM = { "Sayyid", "Shayk", "Maulan", "Mu'allim", "Naquib", "Qadi", "Mushir", "Amir", "Malik", "Sultan" };
    private static readonly string[] GharuF = { "Sayyida", "Shayka", "Maulana", "Mu'allima", "Naquiba", "Qadiya", "Mushira", "Amira", "Malika", "Sultana" };
    private static readonly string[] ShoM = { "Jinin", "Jo-chueh", "Nan-chueh", "Shi-chueh", "Ta-chueh", "Kun-chueh", "Kou", "Taikou", "Ou", "Koutei" };
    private static readonly string[] ShoF = { "Jinin", "Jo-chueh", "Nan-chueh", "Shi-chueh", "Ta-chueh", "Kun-chueh", "Kou", "Taikou", "Jo-ou", "Koutei" };
    private static readonly string[] ViaM = { "Squire", "Banner", "Baron", "Viscount", "Count", "Marquis", "Duke", "Grand Duke", "King", "High King" };
    private static readonly string[] ViaF = { "Dame", "Banner", "Baroness", "Viscountess", "Countess", "Marquise", "Duchess", "Grand Duchess", "Queen", "High Queen" };
    private static readonly string[] ShadowM = { "Tenebrous", "Shade", "Squire", "Knight", "Void Knight", "Void Lord", "Duke", "Archduke", "Highborn", "King" };
    private static readonly string[] ShadowF = { "Tenebrous", "Shade", "Squire", "Knight", "Void Knight", "Void Lady", "Duchess", "Archduchess", "Highborn", "Queen" };
    private static readonly string[] Tumerok = { "Xutua", "Tuona", "Ona", "Nuona", "Turea", "Rea", "Nurea", "Kauh", "Sutah", "Tah" };
    private static readonly string[] Gearknight = { "Tribunus", "Praefectus", "Optio", "Centurion", "Principes", "Legatus", "Consul", "Dux", "Secondus", "Primus" };
    private static readonly string[] Lugian = { "Laigus", "Raigus", "Amploth", "Arintoth", "Obeloth", "Lithos", "Kantos", "Gigas", "Extas", "Tiatus" };
    private static readonly string[] EmpyreanM = { "Ensign", "Corporal", "Lieutenant", "Commander", "Captain", "Commodore", "Admiral", "Warlord", "Ipharsin", "Aulin" };
    private static readonly string[] EmpyreanF = { "Ensign", "Corporal", "Lieutenant", "Commander", "Captain", "Commodore", "Admiral", "Warlord", "Ipharsia", "Aulia" };
    private static readonly string[] UndeadM = { "Neophyte", "Acolyte", "Adept", "Esquire", "Squire", "Knight", "Count", "Viscount", "Highness", "Annointed" };
    private static readonly string[] UndeadF = { "Neophyte", "Acolyte", "Adept", "Esquire", "Squire", "Knight", "Countess", "Viscountess", "Highness", "Annointed" };

    /// <summary>The allegiance rank's title (rank 1..10) for a sex (1 male, 2 female) and heritage, or null.</summary>
    public static string? RankTitle(int gender, int heritage, int rank)
    {
        if (rank is < 1 or > 10 || gender is not (1 or 2)) return null;
        bool f = gender == 2;
        string[]? t = heritage switch
        {
            1 => f ? AluvianF : AluvianM,
            2 => f ? GharuF : GharuM,
            3 => f ? ShoF : ShoM,
            4 => f ? ViaF : ViaM,
            5 or 10 => f ? ShadowF : ShadowM,
            6 => Gearknight,
            7 => Tumerok,
            8 => Lugian,
            9 => f ? EmpyreanF : EmpyreanM,
            11 => f ? UndeadF : UndeadM,
            _ => null,
        };
        return t?[rank - 1];
    }

    /// <summary>The character title for CharacterTitleId (string table 0x2300000E), or "".</summary>
    public static string Title(int titleId) => RetailData.Titles.TryGetValue(titleId, out string? t) ? t : "";

    /// <summary>
    /// How far through the current level the character is (0..1), for the red fill of the "XP for next
    /// level" bar: DreamWeave's chart past 275. 0 when unknown, 1 at the chart's top.
    /// </summary>
    public static float LevelProgress(int level, long totalXp)
    {
        IReadOnlyList<ulong> chart = DreamWeaveXp.Totals;
        if (level < 1 || totalXp < 0 || level >= chart.Count) return 0f;
        if (level + 1 >= chart.Count) return 1f;
        ulong from = chart[level], to = chart[level + 1];
        if (to <= from) return 1f;
        ulong have = (ulong)totalXp;
        if (have <= from) return 0f;
        if (have >= to) return 1f;
        return (float)((double)(have - from) / (to - from));
    }

    /// <summary>
    /// How far a trained or specialized skill is through its current rank (0..1), for the blue fill of the
    /// footer's "Experience To Raise" bar. Skills earn XP from use (the server's proficiency points: a
    /// successful use adds a little to the skill, up to a full share every 15 minutes) and go up a rank by
    /// themselves when it fills, until the last rank. 0 untrained, at the last rank, or unknown.
    /// </summary>
    public static float RankProgress(SkillLine s)
    {
        uint[] chart = s.Class switch
        {
            SkillClass.Specialized => RetailData.SpecializedSkillXp,
            SkillClass.Trained => RetailData.TrainedSkillXp,
            _ => System.Array.Empty<uint>(),
        };
        if (s.Ranks + 1 >= chart.Length || s.AtTop) return 0f;
        uint from = chart[s.Ranks], to = chart[s.Ranks + 1];
        if (to <= from || s.XpSpent <= from) return 0f;
        if (s.XpSpent >= to) return 1f;
        return (float)((double)(s.XpSpent - from) / (to - from));
    }

    /// <summary>Retail's header line two: "Male Aluvian Obviously Bored" (sex, heritage, title).</summary>
    public static string Identity(int gender, int heritage, string? title)
    {
        var parts = new List<string>(3);
        string? g = gender switch { 1 => "Male", 2 => "Female", _ => null };
        string? h = heritage switch
        {
            1 => "Aluvian", 2 => "Gharu'ndim", 3 => "Sho", 4 => "Viamontian", 5 => "Umbraen", 6 => "Gearknight",
            7 => "Tumerok", 8 => "Lugian", 9 => "Empyrean", 10 => "Penumbraen", 11 => "Undead", 12 or 13 => "Olthoi",
            _ => null,
        };
        if (g != null) parts.Add(g);
        if (h != null) parts.Add(h);
        if (!string.IsNullOrWhiteSpace(title)) parts.Add(title.Trim());
        return string.Join(" ", parts);
    }

    /// <summary>Retail's header line three, from PlayerKillerStatus (0x04 PK, 0x40 PK Lite).</summary>
    public static string PkStatus(int status)
        => (status & 0x04) != 0 ? "Player Killer" : (status & 0x40) != 0 ? "Player Killer Lite" : "Non-Player Killer";
}
