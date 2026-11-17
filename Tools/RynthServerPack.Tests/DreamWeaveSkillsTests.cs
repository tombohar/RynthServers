using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Engine.UI.Sections;
using RynthCore.Plugin.RynthDreamWeave;
using RynthCore.Plugin.ServerPack;
using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// RynthDreamWeave's Skills panel (2026-11-07): DreamWeave's level chart past 275 (the reason for the panel:
// AC's own panel reads a negative "XP for next level" there), the retail data it carries, the list's
// sections and footer, and the shared ServerWindows hook it is declared through.
internal static class DreamWeaveSkillsTests
{
    public static void Register(Runner r)
    {
        r.Add("dreamweave skills: the retail chart is portal.dat's, levels 0..275", RetailChart);
        r.Add("dreamweave skills: the chart past 275 follows the server's rule", ExtendedChart);
        r.Add("dreamweave skills: XP for next level past 275 (the reported level-344 character)", NextLevelPast275);
        r.Add("dreamweave skills: XP for next level at the edges", NextLevelEdges);
        r.Add("dreamweave skills: sections, order and what shows in each", Sections);
        r.Add("dreamweave skills: the footer reads as retail's", Footer);
        r.Add("dreamweave skills: raise arrows only when affordable", RaiseGates);
        r.Add("dreamweave skills: header words (rank title, character title)", Header);
        r.Add("dreamweave skills: the XP bar fills with the way through the level", LevelBar);
        r.Add("dreamweave skills: tooltips (formula, description, wrapped as retail)", Tooltips);
        r.Add("dreamweave skills: fonts and skill table", RetailTables);
        r.Add("hooks: server windows are validated and declared frameless only when the engine can", Windows);
        r.Add("dreamweave skills: the Attributes page (rows, values, footer, bar)", Attributes);
        r.Add("dreamweave skills: the Titles page (the server's list, updates, saving)", Titles);
        r.Add("dreamweave skills: one main panel at a time, as retail", OnePanel);
        r.Add("dreamweave skills: +10 presses made while a raise is on its way wait for its answer", HeldPresses);
        r.Add("dreamweave skills: +10 buys the last ranks when fewer than ten are left, as retail", TenToTheTop);
        r.Add("dreamweave skills: a read dropped at logout doesn't freeze the panel (R-20261114-190620-f744)", ReadGateLostRead);
    }

    private static void Attributes()
    {
        Check.Eq(string.Join(",", AttributesPage.Rows.Select(r => r.Name)), "Strength,Endurance,Coordination,Quickness,Focus,Self,Health,Stamina,Mana", "retail's order");
        var str = new AttributeLine(1, 1, "Strength", 0, 200, 250, 1000, 12000, true, false, 50, RetailData.AttributeXp[50] + (RetailData.AttributeXp[51] - RetailData.AttributeXp[50]) / 2);
        Check.Eq(str.ValueText, "250", "an attribute shows its buffed value");
        Check.Eq(AttributesPage.FooterTitle(str), ("Strength: 250", " (+50)"), "footer title");
        Check.Eq(AttributesPage.FooterTitle(null).Text, "Select an Attribute to Improve", "retail's prompt");
        var sheet = SkillsSheet.Empty with { UnassignedXp = 5000, SkillCredits = 2 };
        Check.Eq(AttributesPage.FooterLines(sheet, str).One, ("Experience To Raise:", "1,000"), "cost");
        Check.Eq(AttributesPage.FooterLines(sheet, null).One, ("Skill Credits Available:", "2"), "nothing selected");
        Check.True(AttributesPage.CanRaiseOne(sheet, str) && !AttributesPage.CanRaiseTen(sheet, str), "one affordable, ten not");
        Check.Near(AttributesPage.RankProgress(str), 0.5, 0.01, "half way through rank 50");
        var health = new AttributeLine(2, 1, "Health", 0, 300, 350, -1, -1, false, true, 196, 0, Current: 280);
        Check.Eq(health.ValueText, "280/350", "a vital shows current/maximum, as retail");
        Check.Eq(AttributesPage.FooterLines(sheet, health).One, ("Experience To Raise:", "Infinity!"), "at the top");
        Check.Eq(AttributesPage.RankProgress(health), 0f, "the last rank: empty");
        Check.True(AttributesPage.Tip(health).StartsWith("(Endurance/2)"), "health's hover text");
    }

    private static void Titles()
    {
        // Game event 0x0029: u32 1, u32 displayed (433), u32 count (3), ids 1, 433, 7.
        byte[] table = new byte[12 + 12];
        void U32(int at, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(at), v);
        U32(0, 1); U32(4, 433); U32(8, 3); U32(12, 1); U32(16, 433); U32(20, 7);
        Check.True(TitleList.TryParseTable(table, out TitleList list), "parses");
        Check.Eq(list.Displayed, 433U, "displayed");
        Check.Eq(list.Earned.Count, 3, "three earned");
        Check.Eq(list.Sorted()[0].Text, "Adventurer", "sorted by text (retail's list)");
        Check.False(TitleList.TryParseTable(table.AsSpan(0, 18), out _), "a short list is refused");

        byte[] update = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(update, 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(update.AsSpan(4), 1);
        TitleList after = list.WithUpdate(update);
        Check.True(after.Earned.Contains(2U) && after.Displayed == 2, "0x002B adds a title and can make it the displayed one");
        Check.Eq(list.WithDisplayed(1).Displayed, 1U, "CharacterTitleId moves the displayed one");

        string saved = after.Save();
        TitleList back = TitleList.Load(saved);
        Check.Eq(back.Save(), saved, "saved and loaded per character");
        Check.False(TitleList.Load("").Known, "nothing saved: not known yet");
        Check.False(TitleList.Load("garbage").Known, "a broken save is nothing");
    }

    private static void RetailChart()
    {
        Check.Eq(RetailData.LevelTotals.Length, 276, "levels 0..275");
        Check.Eq(RetailData.LevelTotals[2], 1000UL, "level 2");
        Check.Eq(RetailData.LevelTotals[275], 191_226_310_247UL, "level 275 (AC's panel subtracts from this past 275)");
        Check.Eq(RetailData.LevelCredits[275], (byte)1, "a credit at 275");
    }

    private static void ExtendedChart()
    {
        IReadOnlyList<ulong> t = DreamWeaveXp.Totals;
        for (int i = 0; i <= 275; i++) Check.Eq(t[i], RetailData.LevelTotals[i], "retail levels kept, " + i);
        // The step past 275 grows by 3,390,451,400 / 3,342,189,670 (the chart's last two steps).
        ulong s275 = t[275] - t[274], s274 = t[274] - t[273];
        Check.Eq(t[276] - t[275], (ulong)((UInt128)s275 * s275 / s274), "the first synthesized step");
        for (int i = 277; i < t.Count; i++) Check.True(t[i] - t[i - 1] >= t[i - 1] - t[i - 2], "steps never shrink, " + i);
        Check.True(t[^1] <= DreamWeaveXp.TotalCap, "the top stays under long.MaxValue / 2");
        Check.True(DreamWeaveXp.MaxLevel > 1000, "a thousand-odd levels: " + DreamWeaveXp.MaxLevel);
        // A flat or shrinking chart repeats its last step; too short a chart is left alone.
        Check.Eq(string.Join(",", DreamWeaveXp.Extend(new ulong[] { 0, 10, 20 }, 50)), "0,10,20,30,40,50", "flat tail");
        Check.Eq(string.Join(",", DreamWeaveXp.Extend(new ulong[] { 0, 10 }, 50)), "0,10", "too short");
    }

    private static void NextLevelPast275()
    {
        // The report's character: level 344, 600,303,541,190 total XP. AC's panel read -409,077,230,943.
        const long total = 600_303_541_190;
        IReadOnlyList<ulong> t = DreamWeaveXp.Totals;
        Check.True(t[344] <= (ulong)total && (ulong)total < t[345], "the total is level 344 on DreamWeave's chart, as the server says");
        Check.Eq(t[344], 593_567_252_692UL, "level 344");
        Check.Eq(t[345], 602_816_527_434UL, "level 345");
        Check.Eq(DreamWeaveXp.ToNextLevel(344, total), 2_512_986_244L, "XP for next level");
        var sheet = SkillsSheet.Empty with { Level = 344, TotalXp = total };
        Check.Eq(sheet.NextLevelText, "2,512,986,244", "as the panel prints it");
        Check.Eq((long)RetailData.LevelTotals[275] - total, -409_077_230_943L, "what AC's own panel showed (for the record)");
    }

    private static void NextLevelEdges()
    {
        Check.Eq(DreamWeaveXp.ToNextLevel(1, 0), 1000L, "level 1");
        Check.Eq(DreamWeaveXp.ToNextLevel(275, (long)RetailData.LevelTotals[275]), (long)(DreamWeaveXp.Totals[276] - DreamWeaveXp.Totals[275]), "at 275 it goes on");
        Check.Eq(DreamWeaveXp.ToNextLevel(10, long.MaxValue / 4), 0L, "already past the next level: 0, not negative");
        Check.Null(DreamWeaveXp.ToNextLevel(DreamWeaveXp.MaxLevel, 0), "the chart's top");
        Check.Eq((SkillsSheet.Empty with { Level = DreamWeaveXp.MaxLevel, TotalXp = 1 }).NextLevelText, "Infinity!", "retail's word at the top");
        Check.Null(DreamWeaveXp.ToNextLevel(0, 5), "no level");
        Check.Eq(SkillsSheet.Empty.NextLevelText, "", "nothing read yet");
    }

    private static SkillLine Skill(uint id, SkillClass c, bool usable = true, int b = 100, int buffed = 100,
        long one = 1000, long ten = 12000, int credits = 0, bool raisable = true, bool trainable = false)
        => new(id, RetailData.Skill(id)?.Name ?? "?", RetailData.Skill(id)?.Icon ?? 0, c, usable, b, buffed, one, ten, credits, raisable, trainable, false);

    private static void Sections()
    {
        var skills = new List<SkillLine>
        {
            Skill(46, SkillClass.Specialized), Skill(38, SkillClass.Specialized),   // Finesse Weapons, Alchemy
            Skill(14, SkillClass.Trained),                                           // Arcane Lore
            Skill(40, SkillClass.Untrained, usable: true),                           // Salvaging
            Skill(33, SkillClass.Untrained, usable: false),                          // Life Magic
        };
        List<SkillRow> rows = SkillsLayout.Rows(skills);
        string text = string.Join(" | ", rows.Select(r => r.IsBanner ? "[" + SkillsLayout.Title(r.Section) + "]" : r.Skill!.Name));
        Check.Eq(text, "[Specialized Skills] | Alchemy | Finesse Weapons | [Trained Skills] | Arcane Lore | [Untrained Skills] | Salvaging | [Unusable Skills] | Life Magic",
            "sections in retail's order, names sorted");
        Check.Eq(SkillsLayout.Rows(new[] { Skill(14, SkillClass.Trained) }).Count, 2, "empty sections are left out");
    }

    private static void Footer()
    {
        var sheet = SkillsSheet.Empty with { UnassignedXp = 275_586_157_349, SkillCredits = 3 };
        var (none1, none2) = SkillsLayout.FooterLines(sheet, null);
        Check.Eq(SkillsLayout.FooterTitle(null).Text, "Select a Skill to Improve", "nothing selected");
        Check.Eq(none1, ("Skill Credits Available:", "3"), "credits");
        Check.Eq(none2, ("Unassigned Experience:", "275,586,157,349"), "unassigned");

        // The reported footer: "Salvaging: 303 (+45)", "Experience To Raise: Infinity!" at the top rank.
        SkillLine salvaging = Skill(40, SkillClass.Specialized, b: 258, buffed: 303, one: -1, ten: -1, raisable: false);
        Check.Eq(SkillsLayout.FooterTitle(salvaging), ("Salvaging: 303", " (+45)"), "title and buff part");
        var (s1, s2) = SkillsLayout.FooterLines(sheet, salvaging);
        Check.Eq(s1, ("Experience To Raise:", "Infinity!"), "maxed");
        Check.Eq(s2, ("Unassigned Experience:", "275,586,157,349"), "unassigned");
        Check.Eq(SkillsLayout.FooterTitle(Skill(40, SkillClass.Trained, b: 300, buffed: 290)).Delta, " (-10)", "a debuff");

        SkillLine untrained = Skill(23, SkillClass.Untrained, usable: false, credits: 6, trainable: true);
        Check.Eq(SkillsLayout.FooterTitle(untrained), ("Lockpick", ""), "untrained: the name alone");
        var (u1, u2) = SkillsLayout.FooterLines(sheet, untrained);
        Check.Eq(u1, ("Skill Credits To Raise:", "6"), "credits to train");
        Check.Eq(u2, ("Skill Credits Available:", "3"), "credits on hand");
    }

    private static void RaiseGates()
    {
        var rich = SkillsSheet.Empty with { UnassignedXp = 50_000, SkillCredits = 10 };
        Check.True(SkillsLayout.CanRaiseOne(rich, Skill(14, SkillClass.Trained, one: 1000)), "affordable");
        Check.True(SkillsLayout.CanRaiseTen(rich, Skill(14, SkillClass.Trained, ten: 12000)), "ten affordable");
        Check.False(SkillsLayout.CanRaiseTen(rich, Skill(14, SkillClass.Trained, ten: 90_000)), "ten too dear");
        Check.False(SkillsLayout.CanRaiseOne(rich, Skill(14, SkillClass.Trained, one: -1, raisable: false)), "at the top");
        Check.True(SkillsLayout.CanRaiseOne(rich with { Busy = true }, Skill(14, SkillClass.Trained)), "lit while an earlier raise is on its way (the press is held)");
        Check.True(SkillsLayout.CanRaiseTen(rich with { Busy = true }, Skill(14, SkillClass.Trained, ten: 12000)), "+10 too");
        Check.True(SkillsLayout.CanRaiseOne(rich, Skill(23, SkillClass.Untrained, credits: 6, trainable: true)), "train with credits");
        Check.False(SkillsLayout.CanRaiseOne(rich with { SkillCredits = 5 }, Skill(23, SkillClass.Untrained, credits: 6, trainable: true)), "not enough credits");
        Check.False(SkillsLayout.CanRaiseTen(rich, Skill(23, SkillClass.Untrained, credits: 6, trainable: true)), "no +10 for untrained");
    }

    private static void Header()
    {
        Check.Eq(SkillsLayout.Identity(1, 1, "Obviously Bored"), "Male Aluvian Obviously Bored", "the reported line");
        Check.Eq(SkillsLayout.Identity(2, 2, ""), "Female Gharu'ndim", "no title");
        Check.Eq(SkillsLayout.Identity(0, 0, null), "", "nothing known");
        Check.Eq(SkillsLayout.PkStatus(0x02), "Non-Player Killer", "NPK");
        Check.Eq(SkillsLayout.PkStatus(0x04), "Player Killer", "PK");
        Check.Eq(SkillsLayout.PkStatus(0x40), "Player Killer Lite", "PK lite");

        // The reported retail header: "Yeoman Drakkon" / "Male Aluvian Obviously Bored".
        Check.Eq(SkillsLayout.NameLine("Drakkon", 1, 1, 1), "Yeoman Drakkon", "Aluvian rank 1");
        Check.Eq(SkillsLayout.NameLine("Drakkon", 1, 1, 0), "Drakkon", "no allegiance rank");
        Check.Eq(SkillsLayout.NameLine("Lucy", 2, 4, 9), "Queen Lucy", "a Viamontian queen");
        Check.Eq(SkillsLayout.NameLine("Kor", 1, 6, 3), "Optio Kor", "Gearknight titles are the same for both");
        Check.Eq(SkillsLayout.Title(433), "Obviously Bored", "CharacterTitleId 433");
        Check.Eq(SkillsLayout.Title(1), "Adventurer", "title 1");
        Check.Eq(SkillsLayout.Title(0), "", "no title");
        Check.True(RetailData.Titles.Count > 800, "the title table: " + RetailData.Titles.Count);
    }

    private static void LevelBar()
    {
        IReadOnlyList<ulong> t = DreamWeaveXp.Totals;
        Check.Eq(SkillsLayout.LevelProgress(345, (long)t[345]), 0f, "just levelled");
        Check.Near(SkillsLayout.LevelProgress(345, (long)(t[345] + (t[346] - t[345]) / 4)), 0.25, 1e-6, "a quarter of the way");
        // The reported level-345 character (605,149,503,805 XP) is about a quarter through 345.
        float p = SkillsLayout.LevelProgress(345, 605_149_503_805);
        Check.True(p > 0.2f && p < 0.3f, "the reported character: " + p);
        Check.Eq(SkillsLayout.LevelProgress(DreamWeaveXp.MaxLevel, 1), 1f, "the top");
        Check.Eq(SkillsLayout.LevelProgress(0, 5), 0f, "unknown");

        // The footer's blue bar: a skill's way through its rank (it fills from use).
        uint[] tr = RetailData.TrainedSkillXp, sp = RetailData.SpecializedSkillXp;
        Check.Eq(tr.Length, 209, "trained ranks 0..208");
        Check.Eq(sp.Length, 227, "specialized ranks 0..226");
        Check.Eq(sp[^1], 4_100_490_438U, "the specialized cap");
        SkillLine Spec(uint ranks, uint xp, bool top = false) => new(38, "Alchemy", 0, SkillClass.Specialized, true, 1, 1, 1, 1, 0, !top, false, top, ranks, xp);
        Check.Near(SkillsLayout.RankProgress(Spec(100, sp[100] + (sp[101] - sp[100]) / 2)), 0.5, 1e-3, "half way through rank 100");
        Check.Eq(SkillsLayout.RankProgress(Spec(100, sp[100])), 0f, "just ranked up");
        Check.Eq(SkillsLayout.RankProgress(Spec(226, sp[226], top: true)), 0f, "the last rank: empty, as retail's maxed Salvaging");
        Check.Eq(SkillsLayout.RankProgress(new SkillLine(23, "Lockpick", 0, SkillClass.Untrained, false, 0, 0, -1, -1, 6, false, true, false, 0, 0)), 0f, "untrained");
        Check.Near(SkillsLayout.RankProgress(new SkillLine(14, "Arcane Lore", 0, SkillClass.Trained, true, 1, 1, 1, 1, 0, true, false, false, 10, tr[10] + (tr[11] - tr[10]) / 4)), 0.25, 0.01, "trained uses its own chart");
    }

    private static void Tooltips()
    {
        RetailSkill ie = RetailData.Skill(32)!;
        Check.Eq(ie.Formula, "( Focus + Self ) / 4", "Item Enchantment's formula, as retail's tooltip writes it");
        Check.Eq(ie.Description, "Allows you to cast spells that affect an item's properties.", "its description");
        Check.Eq(RetailData.Skill(24)!.Formula, "( Quickness )", "one attribute, no divisor");
        Check.Eq(RetailData.Skill(19)!.Formula, "", "no attribute part: no formula line");
        // Retail breaks that description after "item's" (the reported tooltip).
        Check.Eq(string.Join("|", RetailData.Pal14.Wrap(ie.Description, 230)), "Allows you to cast spells that affect an item's|properties.", "wrapped as retail");
        Check.Eq(RetailData.Pal14.Wrap("", 230).Count, 0, "nothing to wrap");
    }

    private static void RetailTables()
    {
        Check.Eq(RetailData.Skills.Length, 38, "the 38 skills of end-of-retail's table");
        Check.Eq(RetailData.Skill(40)?.Name, "Salvaging", "by id");
        Check.True(RetailData.Skill(40)!.UsableUntrained && !RetailData.Skill(33)!.UsableUntrained, "usable untrained");
        Check.True(RetailData.Skills.All(s => (s.Icon >> 24) == 0x06), "icons are 0x06 textures");
        foreach (RetailFont f in new[] { RetailData.Pal14, RetailData.Pal16, RetailData.Pal18, RetailData.Pal20, RetailData.Stone36 })
        {
            Check.True(f.TryGlyph('0', out RetailGlyph zero) && zero.Width > 0, $"0x{f.Id:X8} has digits");
            Check.True(f.Sheet >> 24 == 0x06 && f.SheetWidth <= 2048, $"0x{f.Id:X8} sheet fits the engine's font-sheet kind");
        }
        Check.True(RetailData.Pal16.Measure("Alchemy") > RetailData.Pal16.Measure("Jump"), "widths add up");
        Check.Eq(RetailData.Pal16.Measure(""), 0, "empty");
        Check.True(RetailData.Stone36.OutlineSheet != 0 && RetailData.Pal16.OutlineSheet == 0, "only the level number is outlined");
    }

    private static void Windows()
    {
        Func<bool, System.Numerics.Vector2> draw = _ => default;
        var notes = new List<string>();
        var ok = ServerPackWindows.Validate(new List<ServerWindow>
        {
            new("skills", "DreamWeave Skills", "", 310, 610, 120, 120, true, draw, FitHeight: true, UnderUi: true, DrawScale: true),
            new("skills", "Again", "", 1, 1, 1, 1, false, draw),
            new("server", "Taken", "", 1, 1, 1, 1, false, draw),
            new("Bad", "Bad", "", 1, 1, 1, 1, false, draw),
            new("blank", " ", "", 1, 1, 1, 1, false, draw),
        }, notes.Add);
        Check.Eq(ok.Count, 1, "one good window");
        Check.Eq(notes.Count, 4, "four notes: " + string.Join("; ", notes));
        Check.Eq(ServerPackWindows.IdOf("window:skills"), "skills", "surface to id");
        Check.Null(ServerPackWindows.IdOf("window:server"), "the server window is the shared code's");
        Check.Null(ServerPackWindows.IdOf("section:server-dreamweave"), "not a window");

        var info = new ServerPackInfo("dreamweave", "DreamWeave", new[] { "play.acdreamweave.com" }, new[] { "DreamWeave" });
        foreach (bool frameless in new[] { true, false })
        {
            string json = ServerPackModel.BuildSectionsJson(info, "RynthDreamWeave", null, ok, frameless);
            SectionManifest? m = SectionManifestParser.Parse("RynthDreamWeave", json, out string error);
            Check.NotNull(m, "parses: " + error);
            SectionWindowDecl? skills = m!.Windows.FirstOrDefault(w => w.Id == "skills");
            if (!frameless) { Check.Null(skills, "an engine without frameless windows doesn't get it"); continue; }
            Check.NotNull(skills, "declared");
            Check.Eq(skills!.Surface, "window:skills", "surface");
            Check.Eq(skills.Server, "dreamweave", "DreamWeave only");
            Check.True(skills.Frameless && skills.Bar, "frameless, on the bar");
            Check.True(skills.FitHeight, "sized by the panel (retail's bottom-edge resize)");
            Check.True(skills.UnderUi, "drawn under AC's own UI (\"layer\": \"underUi\")");
            Check.True(skills.DrawScale, "its own draw size, 100% = retail's pixels (\"scale\": \"draw\"), not the text size");
            Check.True(m.Windows.Any(w => w.Id == ServerPackModel.WindowId), "the server window is still there");
        }
    }

    private static void OnePanel()
    {
        const uint Inventory = 1u << 3, Spellbook = 1u << 7, Map = 1u << 10;
        var rule = new OnePanelRule();
        Check.Eq(rule.Next(false, Inventory), default(OnePanelRule.Step), "the first frame only learns");
        Check.Eq(rule.Next(false, Inventory), default(OnePanelRule.Step), "nothing changed");
        // The Character panel opens with the inventory up: the inventory goes.
        Check.Eq(rule.Next(true, Inventory), new OnePanelRule.Step(Inventory, false), "opening it closes AC's open panel");
        // AC still reads the inventory open for a moment: not an opening.
        Check.Eq(rule.Next(true, Inventory), default(OnePanelRule.Step), "a panel on its way out isn't an opening");
        Check.Eq(rule.Next(true, 0), default(OnePanelRule.Step), "the inventory closed");
        // The spellbook opens while it's up: it goes.
        Check.Eq(rule.Next(true, Spellbook), new OnePanelRule.Step(0, true), "an AC panel opening closes it");
        Check.Eq(rule.Next(true, Spellbook), default(OnePanelRule.Step), "asked once, not every frame while it closes");
        Check.Eq(rule.Next(false, Spellbook), default(OnePanelRule.Step), "closed");
        // AC switching between its own panels with it closed: none of its business.
        Check.Eq(rule.Next(false, Map), default(OnePanelRule.Step), "AC's own switch");
        // Opened again with two up (an engine without retail's own rule): both go.
        Check.Eq(rule.Next(true, Map | Spellbook), new OnePanelRule.Step(Map | Spellbook, false), "closes every open one");
        rule.Reset();
        Check.Eq(rule.Next(true, Map), default(OnePanelRule.Step), "after a reset the first frame only learns again");
    }

    // 2026-11-14, report R-20261114-190620-f744: a level-497 character's panel still read level 495 and that
    // level's XP for next level. The panel's one-read-in-flight flag was set when a read was queued for the
    // tick and cleared when it ran; the shared plugin drops the tick's queue at logout, so a read queued in
    // the last frames before a logout left the flag set and no read was ever queued again.
    private static void ReadGateLostRead()
    {
        var gate = new ReadGate();
        Check.True(gate.Due(10_000, everyFrame: false), "the first frame reads");
        Check.True(gate.Waiting, "one queued");
        Check.False(gate.Due(10_000 + ReadGate.EveryMs, everyFrame: false), "not a second while the first waits");
        Check.False(gate.Due(10_001, everyFrame: true), "not even every-frame while one waits");
        gate.Done();
        Check.False(gate.Due(10_100, everyFrame: false), "four a second");
        Check.True(gate.Due(10_000 + ReadGate.EveryMs, everyFrame: false), "the next after 250 ms");
        gate.Done();
        Check.True(gate.Due(10_251, everyFrame: true), "a raise on its way: the very next frame");
        gate.Done();

        // The reported freeze: a read queued, then its queue dropped (logout), so Done never comes.
        Check.True(gate.Due(20_000, everyFrame: false), "queued just before the logout");
        for (long t = 20_000; t < 20_000 + ReadGate.LostAfterMs; t += 16)
            Check.False(gate.Due(t, everyFrame: false), "still waiting for it at " + t);
        Check.True(gate.Due(20_000 + ReadGate.LostAfterMs, everyFrame: false), "taken as lost and queued again");
        gate.Done();
        Check.True(gate.Due(20_000 + ReadGate.LostAfterMs + ReadGate.EveryMs, everyFrame: false), "and reading on after it");

        // Logout resets it (SkillsReader.Clear): the next login's first frame reads, whatever was waiting.
        Check.True(gate.Waiting, "a read waiting at logout");
        gate.Reset();
        Check.False(gate.Waiting, "forgotten");
        Check.True(gate.Due(30_000, everyFrame: false), "the next login reads at once");
        gate.Done();

        // After a raise or a title the panel asks for a read at once.
        Check.True(gate.Due(40_000, everyFrame: false), "a read");
        gate.Done();
        gate.AskNow();
        Check.True(gate.Due(40_001, everyFrame: false), "AskNow: the next frame, not 250 ms later");
        gate.Done();
        // A clock reading of 0 still marks a read as waiting.
        var zero = new ReadGate();
        Check.True(zero.Due(0, everyFrame: false), "read at clock 0");
        Check.True(zero.Waiting, "and it waits");
    }

    private static void HeldPresses()
    {
        var held = new HeldRaises();
        var ten = new RaiseTarget(RaiseTarget.Skill, 14, Ten: true);
        Check.False(held.Next(0, busy: false, out _), "nothing pressed");
        held.Press(ten, 0);
        Check.True(held.Next(0, busy: false, out RaiseTarget t) && t == ten, "a press goes at once when nothing is on its way");
        held.Sent();
        // Spamming while the first is on its way: each waits, none is lost.
        held.Press(ten, 100); held.Press(ten, 150); held.Press(ten, 200);
        Check.Eq(held.Count, 3, "three held");
        Check.False(held.Next(250, busy: true, out _), "not while the last raise hasn't landed");
        Check.True(held.Next(300, busy: false, out _), "its answer is in: the next goes");
        held.Sent();
        Check.Eq(held.Count, 2, "one per answer");
        // Another button or row is a new decision.
        var one = new RaiseTarget(RaiseTarget.Skill, 14, Ten: false);
        held.Press(one, 400);
        Check.Eq(held.Count, 1, "another target replaces what waited");
        held.Drop();
        Check.Eq(held.Count, 0, "refused: all forgotten");
        // Train never waits twice: once trained the same arrow raises, which wasn't asked for.
        var train = new RaiseTarget(RaiseTarget.Train, 23, Ten: false);
        held.Press(train, 500); held.Press(train, 520);
        Check.Eq(held.Count, 1, "one train");
        held.Drop();
        for (int i = 0; i < 50; i++) held.Press(ten, 600);
        Check.Eq(held.Count, HeldRaises.Max, "capped");
        Check.False(held.Next(600 + HeldRaises.ForgetMs + 1, busy: false, out _), "forgotten when no answer came");
        Check.Eq(held.Count, 0, "and dropped");

        // The Attributes tab (2026-11-08: the fix has to reach it too): +10 on Strength and on a vital are
        // held like a skill's, one per answer, not capped at one like train.
        var str = new RaiseTarget(RaiseTarget.Attribute, 1, Ten: true);
        held.Press(str, 10_000);
        Check.True(held.Next(10_000, busy: false, out t) && t == str, "an attribute +10 goes at once");
        held.Sent();
        for (int i = 0; i < 4; i++) held.Press(str, 10_050 + i);
        Check.Eq(held.Count, 4, "attribute +10 spam: four held");
        Check.False(held.Next(10_100, busy: true, out _), "attribute: not while the last raise is on its way");
        Check.True(held.Next(10_200, busy: false, out t) && t == str, "attribute: the next goes when it lands");
        held.Sent();
        Check.Eq(held.Count, 3, "attribute: one per answer");
        // A vital with the same id is another target (Kind 2), so it replaces what waited.
        var vital = new RaiseTarget(RaiseTarget.Vital, 1, Ten: true);
        held.Press(vital, 10_300);
        Check.Eq(held.Count, 1, "a vital is not the attribute with the same id");
        held.Press(vital, 10_310); held.Press(vital, 10_320);
        Check.Eq(held.Count, 3, "vital +10 spam is held too");
        held.Drop();
    }

    // A #rynth-issues report, 2026-11-11 ("Can't max skills"): a trained skill untrained and trained again
    // came back at rank 0; twenty +10s took it to 200 of 208 and +10 went dark (the engine prices +10 only
    // with ten ranks left), so it could no longer be maxed by tens. Retail's +10 (GetCostToRaise10) buys
    // the ranks left when fewer than ten are. The report's footer read "Experience To Raise: 180,507,736",
    // which is the trained chart's step from 200 to 201.
    private static void TenToTheTop()
    {
        uint[] tr = RetailData.TrainedSkillXp, sp = RetailData.SpecializedSkillXp;
        Check.Eq(tr.Length - 1, 208, "trained skills top out at 208 ranks");
        Check.Eq(sp.Length - 1, 226, "specialized at 226");
        Check.Eq(tr[208], 4_203_819_496u, "the trained top is portal.dat's (the engine prices from the same table)");
        Check.Eq(sp[226], 4_100_490_438u, "the specialized top too");
        Check.Eq((long)tr[201] - tr[200], 180_507_736L, "the report's cost to raise: trained rank 200");

        // The reported skill: trained, rank 200, the engine's +10 is -1.
        var rich = SkillsSheet.Empty with { UnassignedXp = 194_045_060_050 };
        SkillLine at200 = Skill(18, SkillClass.Trained, b: 277, buffed: 353, one: 180_507_736, ten: -1) with { Ranks = 200, XpSpent = tr[200] };
        Check.Eq(SkillsLayout.Ten(at200), (8u, (long)tr[208] - tr[200]), "+10 at 200 of 208 buys the last 8");
        Check.True(SkillsLayout.CanRaiseTen(rich, at200), "and is lit with the XP for them");
        Check.False(SkillsLayout.CanRaiseTen(rich with { UnassignedXp = (long)tr[208] - tr[200] - 1 }, at200), "dark one XP short");
        // XP from use part way into the rank counts, as the engine's own price does.
        SkillLine partWay = at200 with { XpSpent = tr[200] + 1000 };
        Check.Eq(SkillsLayout.Ten(partWay).Cost, (long)tr[208] - tr[200] - 1000, "XP already in the rank is not paid twice");
        // At the top: nothing.
        SkillLine top = at200 with { Ranks = 208, XpSpent = tr[208], CostOne = -1, Raisable = false, AtTop = true };
        Check.Eq(SkillsLayout.Ten(top), (0u, -1L), "at the top +10 buys nothing");
        Check.False(SkillsLayout.CanRaiseTen(rich, top), "and is dark");
        // Ten or more left: the engine's price, as before.
        SkillLine low = Skill(18, SkillClass.Trained, ten: 12_000) with { Ranks = 50, XpSpent = tr[50] };
        Check.Eq(SkillsLayout.Ten(low), (10u, 12_000L), "ten left: the engine's +10");
        Check.Eq(SkillsLayout.TenStep(tr, 50, tr[50], -1), (0u, -1L), "ten left but no engine price: don't guess");
        Check.Eq(SkillsLayout.Ten(Skill(23, SkillClass.Untrained, ten: -1, credits: 6, trainable: true) with { Ranks = 200 }), (0u, -1L), "untrained: no chart, no +10");

        // From rank 0 by tens to the very top, trained and specialized (the engine's +10 is -1 under ten left).
        foreach ((uint[] chart, SkillClass c, int presses) in new[] { (tr, SkillClass.Trained, 21), (sp, SkillClass.Specialized, 23) })
        {
            SkillLine s = Skill(18, c) with { Ranks = 0, XpSpent = 0 };
            int n = 0;
            long paid = 0;
            while (n < 100)
            {
                long engineTen = s.Ranks + 10 < chart.Length ? (long)chart[s.Ranks + 10] - s.XpSpent : -1;
                (uint ranks, long cost) = SkillsLayout.Ten(s with { CostTen = engineTen });
                if (ranks == 0) break;
                n++;
                paid += cost;
                s = s with { Ranks = s.Ranks + ranks, XpSpent = chart[s.Ranks + ranks] };
            }
            Check.Eq((int)s.Ranks, chart.Length - 1, c + ": +10 reaches the top");
            Check.Eq(n, presses, c + ": in " + presses + " presses");
            Check.Eq(paid, (long)chart[^1], c + ": for exactly the chart's total");
        }

        // Attributes and vitals: retail's gmAttributeUI does the same.
        uint[] at = RetailData.AttributeXp, vt = RetailData.VitalXp;
        int atTop = at.Length - 1, vtTop = vt.Length - 1;
        var str = new AttributeLine(1, 1, "Strength", 0, 280, 280, (long)at[atTop - 4] - at[atTop - 5], -1, true, false, (uint)(atTop - 5), at[atTop - 5]);
        Check.Eq(AttributesPage.Ten(str), (5u, (long)at[atTop] - at[atTop - 5]), "an attribute 5 from the top: +10 buys 5");
        Check.True(AttributesPage.CanRaiseTen(rich, str), "lit");
        var mana = new AttributeLine(2, 5, "Mana", 0, 400, 400, (long)vt[vtTop] - vt[vtTop - 1], -1, true, false, (uint)(vtTop - 1), vt[vtTop - 1]);
        Check.Eq(AttributesPage.Ten(mana), (1u, (long)vt[vtTop] - vt[vtTop - 1]), "a vital one from the top: +10 buys 1");
        var maxed = mana with { Ranks = (uint)vtTop, XpSpent = vt[vtTop], CostOne = -1, Raisable = false, AtTop = true };
        Check.False(AttributesPage.CanRaiseTen(rich, maxed), "a vital at the top: dark");
    }
}
