using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthILT;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// Infinite Leaftide's Dynamic Stamps (Hub/IltDynamicStampList.cs, Hub/IltDynamicStamps.cs): SilentKelpie's
// (RynthSuite PR 12), rows of RynthOracle's Quests list until 2026-10-21 and RynthILT's own page since.
// The list and its matching rules (the tests that came with it from RynthOracle.Tests), the page's
// filters, where its flags come from, and what its buttons send (RynthNav, RynthAi's /ub give).
internal static class StampTests
{
    public static void Register(Runner r)
    {
        r.Add("stamps: the shipped list (243 stamps in 17 areas) and how a stamp is matched", ShippedList);
        r.Add("stamps: the loader skips bad and repeated rows", Loader);
        r.Add("stamps: the page's filters (area, missing / had, search)", Filters);
        r.Add("stamps: flags come from any /myquests reply; only RynthILT's own reply is hidden", Flags);
        r.Add("stamps: off ILT the page is dormant (no flags read)", Dormant);
        r.Add("stamps: Go / Route / Arrow ask RynthNav; Chat prints the stamp", Nav);
        r.Add("stamps: Give Parchment needs RynthAi, sends /ub give 1, then reads the flags again", Give);
        r.Add("stamps: a Parchment in the packs is found while the page is on screen", Parchment);
    }

    private const uint Player = 0x50000D31;
    private static readonly long Yesterday = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();

    private static IltHubController NewHub(string world, string folder, bool withAi = false)
    {
        FakeHost.Reset();
        FakeAiHub.Reset();
        FakeHost.WorldName = world;
        FakeHost.PluginCommandCalls = true;
        FakeHost.ContentsCalls = true;
        FakeHost.Containers[0x7000A001] = Player;
        FakeHost.Names[0x7000A001] = "Pyreal";
        var host = FakeHost.Create(Player);
        var ai = new RynthAiClient(host);
        if (withAi) ai.UseTableForTests(FakeAiHub.Table);
        string dir = Path.Combine(Program.TempRoot, "stamps-" + folder);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var hub = new IltHubController(host, dir, new IltSettings(), ai);
        hub.OnLoginComplete("Stamper", forceFromMigration: false);
        return hub;
    }

    private static void ShippedList()
    {
        var all = DynamicStampList.All;
        Check.Eq(all.Count, 243, "243 stamps from the ExtraDyn sheet");
        Check.True(DynamicStampList.Areas.Count == 17 && DynamicStampList.Areas[0] == "Facility Hub" && DynamicStampList.Areas[^1] == "Candeth Keep",
                   "areas: " + string.Join(",", DynamicStampList.Areas));
        Check.True(all.All(s => s.Stamp.StartsWith("dynamic_") && s.Stamp == s.Stamp.ToLowerInvariant()), "every stamp is a lower-case dynamic_ flag");
        Check.Eq(all.Select(s => s.Stamp).Distinct().Count(), all.Count, "no stamp listed twice");
        Check.True(all.All(s => s.Label.Length > 0), "every row names an NPC or a place");

        DynamicStamp felscuda = all.First(s => s.Npc == "Felscuda");
        Check.True(felscuda.Stamp == "dynamic_1_&9jiytb!" && felscuda.Place == "Folthid Cellar" && felscuda.Reward == "Healing Rabbit Pie"
                   && felscuda.Note == "8.8S, 53.1E", $"Felscuda row: {felscuda.Place} / {felscuda.Reward} / {felscuda.Note}");

        // /myquests keys flags as \S+ lower case, so the stamps' ! & # ^ $ survive.
        var f = new QuestFlagStore();
        long t = 900_000;
        f.OnChatLine($"Dynamic_1_&9jiyTB! - 1 solves ({Yesterday})\"Dynamic\" 1 0", t);
        f.OnChatLine($"Dynamic_1_ANND! - 1 solves ({Yesterday})\"Dynamic\" 1 0", t + 1);        // the sheet lost the closing !
        f.OnChatLine($"Dynamic_1_ZZZnewNPC! - 1 solves ({Yesterday})\"Dynamic\" 1 0", t + 2);   // not on the sheet
        f.OnChatLine($"rtwcompleted_1013 - 1 solves ({Yesterday})\"RTW\" -1 72000", t + 3);
        f.Tick(t + 5_000);
        Check.True(felscuda.Have(f), "Felscuda's stamp read from /myquests");
        DynamicStamp annd = all.First(s => s.Stamp == "dynamic_1_annd");
        Check.True(annd.Have(f) && annd.MatchedFlag(f) == "dynamic_1_annd!", "a stamp the sheet lost its ! on still matches");
        Check.False(all.First(s => s.Npc == "Raxanza Folthid").Have(f), "an NPC not done is missing");
        List<string> extra = DynamicStampList.Unlisted(f);
        Check.True(extra.Count == 1 && extra[0] == "dynamic_1_zzznewnpc!", "unlisted: only the stamp the sheet doesn't have: " + string.Join(",", extra));
        DynamicStamp shortStamp = all.First(s => s.Stamp == "dynamic_1_s");
        var sf = new QuestFlagStore();
        sf.OnChatLine($"Dynamic_1_SomethingElse! - 1 solves ({Yesterday})\"Dynamic\" 1 0", t);
        sf.Tick(t + 5_000);
        Check.False(shortStamp.Have(sf), "a cut-short stem under 14 characters isn't matched by prefix");

        // RynthNav targets, best first (RynthNav takes the first on its map).
        Check.Eq(string.Join(" | ", felscuda.NavCandidates()), "8.8S, 53.1E | Folthid Cellar | Felscuda | Facility Hub", "Felscuda nav");
        var gerent = new DynamicStamp { Area = "Graveyard", Place = "Gerent's Compound at 87.2S, 17.5W", Npc = "Gerent" };
        Check.Eq(string.Join(" | ", gerent.NavCandidates()), "87.2S, 17.5W | Gerent's Compound | Gerent | Graveyard", "coordinates inside the place, 'at' dropped");
        var lost = new DynamicStamp { Area = "Odd Stamps", Place = "Lost Wish Cottages (Arwic) 42.6n 62.4e", Npc = "Bob" };
        Check.Eq(string.Join(" | ", lost.NavCandidates()), "42.6N, 62.4E | Lost Wish Cottages (Arwic) | Lost Wish Cottages | Arwic",
                 "lower-case n/e, a bracketed town, no grouping area, a 3-letter NPC left out");
        var mhoire = new DynamicStamp { Area = "Looking Around", Place = "Mhoire Castle - Great Hall" };
        Check.Eq(string.Join(" | ", mhoire.NavCandidates()), "Mhoire Castle - Great Hall | Mhoire Castle | Great Hall", "a place's parts");
        Check.True(all.Count(s => s.NavCandidates().Count > 0) >= 200, "most stamps have somewhere to go: " + all.Count(s => s.NavCandidates().Count > 0));
    }

    private static void Loader()
    {
        var stamps = DynamicStampList.Parse(new StringReader(
            "Area,Place,Npc,Reward,Stamp,Note\n"
            + "\"A\",\"P\",\"Npc1\",\"R\",\"Dynamic_1_Abc!\",\"8.8S, 53.1E\"\n"
            + "\"A\",\"P\",\"Dup\",\"R\",\"dynamic_1_abc!\",\"\"\n"           // the same stamp again: skipped
            + "\"A\",\"P\",\"NotDyn\",\"R\",\"something_else\",\"\"\n"       // not a dynamic_ flag: skipped
            + "\"A\",\"P\",\"Short\"\n"                                       // no stamp column: skipped
            + "\n"
            + "\"B\",\"\",\"\",\"\",\"Dynamic_2_x\",\"\"\n"));
        Check.True(stamps.Count == 2 && stamps[0].Stamp == "dynamic_1_abc!" && stamps[1].Stamp == "dynamic_2_x",
                   "bad and repeated rows skipped, the rest kept: " + string.Join(",", stamps.Select(s => s.Stamp)));
        Check.True(stamps[0].Npc == "Npc1" && stamps[0].Note == "8.8S, 53.1E" && stamps[1].Label == "", "fields read, a missing NPC and place leave an empty label");
        Check.Eq(DynamicStampList.Parse(new StringReader("")).Count, 0, "an empty file is an empty list");
    }

    private static void Filters()
    {
        var f = new QuestFlagStore();
        f.OnChatLine($"Dynamic_1_&9jiyTB! - 1 solves ({Yesterday})\"Dynamic\" 1 0", 0);
        f.Tick(5_000);
        List<DynamicStampText.Row> rows = DynamicStampText.Rows(DynamicStampList.All, f);
        Check.Eq(rows.Count, 243, "a row per listed stamp");
        Check.Eq(rows.Count(r => r.Have), 1, "one had");
        Check.Eq(DynamicStampText.Filter(rows, "", null, DynamicStampText.Show.All).Count, 243, "no filter: everything");
        Check.Eq(DynamicStampText.Filter(rows, "", null, DynamicStampText.Show.Have).Single().Stamp.Npc, "Felscuda", "Had: Felscuda's");
        Check.Eq(DynamicStampText.Filter(rows, "", null, DynamicStampText.Show.Missing).Count, 242, "Missing: the rest");
        string area = DynamicStampList.Areas[0];
        int inArea = DynamicStampList.All.Count(s => s.Area == area);
        Check.Eq(DynamicStampText.Filter(rows, null, area, DynamicStampText.Show.All).Count, inArea, "the area picker narrows to " + area);
        Check.Eq(DynamicStampText.Filter(rows, "felscuda rabbit", null, DynamicStampText.Show.All).Single().Stamp.Npc, "Felscuda",
                 "every search word, in the NPC and the reward, any case");
        Check.Eq(DynamicStampText.Filter(rows, "felscuda nothinglikethis", null, DynamicStampText.Show.All).Count, 0, "a word nowhere: nothing");
    }

    private static void Flags()
    {
        IltHubController hub = NewHub("InfiniteLeaftide", "flags");
        IltDynamicStamps stamps = hub.DynamicStamps;
        long now = IltHubContext.NowMs;

        // A reply another plugin (or the player) asked for: read, and shown.
        bool eaten = hub.OnChat($"Dynamic_1_&9jiyTB! - 1 solves ({Yesterday})\"Dynamic\" 1 0");
        Check.False(eaten, "a reply RynthILT didn't ask for shows in chat");
        stamps.Tick(now + 5_000);
        Check.True(stamps.Flags.TryGet("dynamic_1_&9jiytb!", out _), "the flag is read from it");

        // Its own request: the reply is hidden.
        hub.Tick();
        stamps.RequestFlags();
        Check.True(FakeHost.ChatCommands.Contains("/myquests"), "Read quest flags sends /myquests");
        stamps.NoteTypedMyQuests();   // the engine's dispatcher hands it back through the chat bar
        Check.True(stamps.Flags.AwaitingOurs, "waiting for its own reply");
        Check.True(hub.OnChat($"Dynamic_1_ANND! - 1 solves ({Yesterday})\"Dynamic\" 1 0"), "its own reply is hidden");
        stamps.Tick(IltHubContext.NowMs + 5_000);
        Check.False(stamps.Flags.AwaitingOurs, "done waiting once the reply went quiet");
        Check.True(stamps.Flags.TryGet("dynamic_1_annd!", out _), "and read");

        // The page's snapshot follows the flags while it is on screen.
        stamps.MarkWatched();
        stamps.Tick(IltHubContext.NowMs);
        IltDynamicStamps.Snapshot s = stamps.Current;
        Check.True(s.Built && s.Rows.Count == 243, "the snapshot lists every stamp");
        Check.Eq(s.Have, 1, "one listed stamp had (the other read reply replaced the first)");
        Check.True(s.LastReadUtc != DateTime.MinValue, "when the flags were read");

        hub.OnLogout();
        Check.False(stamps.Current.Built || stamps.Flags.Flags.Count > 0, "logout forgets the flags and the snapshot");
    }

    private static void Dormant()
    {
        IltHubController hub = NewHub("Aeshnidae", "dormant");
        hub.OnChat($"Dynamic_1_&9jiyTB! - 1 solves ({Yesterday})\"Dynamic\" 1 0");
        hub.DynamicStamps.Tick(IltHubContext.NowMs + 5_000);
        Check.Eq(hub.DynamicStamps.Flags.Flags.Count, 0, "off ILT no chat reaches the page");
        Check.False(hub.Options.IsIltLikeWorld, "(not an ILT world)");
    }

    private static void Nav()
    {
        IltHubController hub = NewHub("InfiniteLeaftide", "nav");
        IltDynamicStamps stamps = hub.DynamicStamps;
        DynamicStamp felscuda = DynamicStampList.All.First(s => s.Npc == "Felscuda");
        Check.Eq(DynamicStampText.NavLine("route", felscuda), "route 8.8S, 53.1E || Folthid Cellar || Felscuda || Facility Hub", "the RynthNav line");
        Check.Eq(DynamicStampText.NavLine("go", new DynamicStamp { Stamp = "dynamic_x" }), "", "nowhere to go: no line");

        stamps.Nav("route", felscuda);
        stamps.Nav("go", felscuda);
        stamps.Nav("arrow", felscuda);
        Check.Eq(FakeHost.PluginCommands.Count, 3, "three RynthNav commands");
        Check.True(FakeHost.PluginCommands.All(c => c.Plugin == "RynthNav" && c.Action == "rnav"), "to RynthNav's rnav");
        Check.True(FakeHost.PluginCommands[1].Value.StartsWith("go 8.8S, 53.1E || "), "go with every candidate");
        Check.True(stamps.Status.Contains("arrow"), "the status says what happened: " + stamps.Status);
        FakeHost.PluginCommandDelivered = false;
        stamps.Nav("go", felscuda);
        Check.True(stamps.Status.Contains("RynthNav isn't loaded"), "without RynthNav: said so");

        FakeHost.Chat.Clear();
        stamps.Print(felscuda);
        Check.True(FakeHost.Chat.Count >= 2 && FakeHost.Chat[0].Contains("Felscuda") && FakeHost.Chat[0].Contains("missing")
                   && FakeHost.Chat[1].Contains("Folthid Cellar") && FakeHost.Chat[1].Contains("Healing Rabbit Pie"),
                   "Chat prints the stamp, its status, place and reward: " + string.Join(" | ", FakeHost.Chat));
    }

    private static void Give()
    {
        // Without RynthAi: the reason, nothing sent.
        Check.True(DynamicStampText.GiveUnavailableReason(false, false)!.Contains("Needs RynthAi"), "no RynthAi: says it needs it");
        Check.True(DynamicStampText.GiveUnavailableReason(true, true)!.Contains("Unrecognized /ub command"), "rejected: says why");
        Check.Null(DynamicStampText.GiveUnavailableReason(true, false), "RynthAi there: Give works");
        Check.Eq(DynamicStampText.GiveCommand("Parchment\nof /quit", "Felscuda"), "/ub give 1 Parchmentof /quit to Felscuda",
                 "control characters are stripped from the command");
        Check.True(DynamicStampText.IsGiveRejection("[RynthAi] Unrecognized /ub command.") && !DynamicStampText.IsGiveRejection("[RynthAi] ok"),
                   "RynthAi's rejection line");

        DynamicStamp felscuda = DynamicStampList.All.First(s => s.Npc == "Felscuda");
        IltHubController none = NewHub("InfiniteLeaftide", "give-none");
        none.DynamicStamps.Give(felscuda, "Parchment of Learning");
        Check.False(FakeHost.ChatCommands.Any(c => c.StartsWith("/ub give")), "without RynthAi nothing is sent");

        IltHubController hub = NewHub("InfiniteLeaftide", "give", withAi: true);
        IltDynamicStamps stamps = hub.DynamicStamps;
        stamps.Give(felscuda, "Parchment of Learning");
        Check.True(FakeHost.ChatCommands.Contains("/ub give 1 Parchment of Learning to Felscuda"), "Give sends /ub give 1: " + string.Join("|", FakeHost.ChatCommands));
        Check.True(stamps.Status.Contains("Giving Parchment of Learning to Felscuda"), "and says so");

        // The stamp is a quest flag: /myquests is read again once the NPC had time to answer.
        stamps.Tick(IltHubContext.NowMs + 1_000);
        Check.False(FakeHost.ChatCommands.Contains("/myquests"), "not straight away");
        stamps.Tick(IltHubContext.NowMs + IltDynamicStamps.HandInRefreshMs + 1_000);
        Check.True(FakeHost.ChatCommands.Contains("/myquests"), "a /myquests after the hand-in");

        // The loaded RynthAi answers "Unrecognized /ub command": Give turns itself off.
        hub.OnChat("[RynthAi] Unrecognized /ub command.");
        stamps.Rebuild();
        Check.True(stamps.Current.GiveReason != null && stamps.Current.GiveReason.Contains("Unrecognized"), "rejected right after the send: Give is off");
        int sends = FakeHost.ChatCommands.Count(c => c.StartsWith("/ub give"));
        stamps.Give(felscuda, "Parchment of Learning");
        Check.Eq(FakeHost.ChatCommands.Count(c => c.StartsWith("/ub give")), sends, "no further /ub give goes out");
    }

    private static void Parchment()
    {
        IltHubController hub = NewHub("InfiniteLeaftide", "parchment", withAi: true);
        IltDynamicStamps stamps = hub.DynamicStamps;
        stamps.Tick(IltHubContext.NowMs);
        Check.False(stamps.Current.Built, "nothing is built while the page isn't on screen");
        stamps.MarkWatched();
        stamps.Tick(IltHubContext.NowMs);
        Check.True(stamps.Current.Built && stamps.Current.Parchment == null, "on screen: built; no Parchment carried");
        Check.Null(stamps.Current.GiveReason, "RynthAi is there: Give would work");

        FakeHost.Containers[0x7000A002] = Player;
        FakeHost.Names[0x7000A002] = "Parchment of Learning";
        stamps.Tick(IltHubContext.NowMs + 2_000);
        Check.Eq(stamps.Current.Parchment, "Parchment of Learning", "a Parchment in the main pack is found");
    }
}
