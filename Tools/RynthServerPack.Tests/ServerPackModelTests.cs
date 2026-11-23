using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using RynthCore.Engine.UI.Sections;
using RynthCore.Plugin.ServerPack;
using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// The shared server-plugin model (Plugins\Shared\ServerPack\ServerPackModel.cs): RynthCore's
// "servermenu" verdict for the plugin's own server, the world-name fallback before one arrives,
// the override commands the card sends, and the sections manifest RynthCore reads.
internal static class ServerPackModelTests
{
    public static void Register(Runner r)
    {
        r.Add("server pack: RynthCore's push gives this server's verdict, override and reason", EnginePush);
        r.Add("server pack: a push without this server, or not a push, changes nothing", ForeignPush);
        r.Add("server pack: before any push the world name decides", WorldFallback);
        r.Add("server pack: override commands are checked before sending", ForceCommands);
        r.Add("server pack: the sections manifest declares the server and a server-only card", SectionsJson);
        r.Add("server pack: no bar menu writes no barMenu key", NoBarMenuKey);
        r.Add("server pack: the bar menu items reach the engine parser as given", BarMenuRoundTrip);
        r.Add("server pack: a bar menu tooltip with quotes and backslashes round-trips", BarMenuEscapes);
        r.Add("server pack: logout clears the verdict", ClearOnLogout);
        r.Add("server pack: the card's botting line and address", InfoText);
        r.Add("server pack: the RynthServer.Profile JSON carries the server's settings", ProfileJson);
        r.Add("server pack: the profile JSON is plain ASCII and escaped", ProfileJsonAscii);
        r.Add("server pack: the profile's summons rows reach RynthAi's reader; none written without any", ProfileSummons);
        r.Add("server pack: DreamWeave names its Duo Summon Gem as a holder of two summons", DreamWeaveDuoGem);
    }

    private static void ProfileJson()
    {
        ServerPackInfo info = new("Coldeve", "Coldeve", new[] { "play.coldeve.ac" }, new[] { "Coldeve" })
        {
            Port = 9050, Ruleset = "PvP", Status = "Stable", Botting = BottingPolicy.Restricted, BottingNote = "attended only",
            AccountLimit = "3 accounts", Facts = new[] { "fact one" }, Commands = new[] { new ServerCommand("/x", "does x") },
            Discord = "https://discord.gg/x",
        };
        string json = ServerPackProfile.BuildJson(info, "RynthColdeve", "0.1.2", true, "Coldeve");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Check.Eq(root.GetProperty("schema").GetInt32(), ServerPackProfile.Schema, "schema");
        Check.Eq(root.GetProperty("id").GetString(), "coldeve", "id (lower-cased)");
        Check.Eq(root.GetProperty("plugin").GetString(), "RynthColdeve", "plugin");
        Check.Eq(root.GetProperty("version").GetString(), "0.1.2", "version");
        Check.True(root.GetProperty("active").GetBoolean(), "active");
        Check.Eq(root.GetProperty("world").GetString(), "Coldeve", "world");
        Check.Eq(root.GetProperty("hosts")[0].GetString(), "play.coldeve.ac", "hosts");
        Check.Eq(root.GetProperty("port").GetInt32(), 9050, "port");
        Check.Eq(root.GetProperty("ruleset").GetString(), "PvP", "ruleset");
        Check.Eq(root.GetProperty("botting").GetString(), "restricted", "botting");
        Check.Eq(root.GetProperty("bottingNote").GetString(), "attended only", "botting note");
        Check.Eq(root.GetProperty("facts")[0].GetString(), "fact one", "facts");
        Check.Eq(root.GetProperty("commands")[0].GetProperty("what").GetString(), "does x", "commands");
        Check.Eq(root.GetProperty("discord").GetString(), "https://discord.gg/x", "links");

        // What a reader (RynthOracle, RynthAi's /ra server) makes of it.
        var view = RynthCore.Plugin.Shared.ServerProfileView.Parse(json);
        Check.True(view != null, "the shared reader parses it");
        Check.Eq(view!.Summary(), "PvP, ACE; botting restricted - attended only; accounts: 3 accounts", "the reader's summary");

        // No salvage defined: an empty list, which the Crafting tab reads as "none defined".
        Check.Eq(root.GetProperty("salvage").GetArrayLength(), 0, "no salvage: empty list");
        Check.Eq(RynthCore.Plugin.Shared.ServerSalvageView.ParseList(json).Count, 0, "the reader sees none");

        // Salvage defined: each row round-trips through the shared reader.
        ServerPackInfo withSalvage = new("x", "X", new[] { "x.example" }, new[] { "X" })
        {
            Salvage = new[]
            {
                new ServerSalvage("melee", 52, "+1 tink past the cap", "once", UsesTinkSlot: false),
                new ServerSalvage("Armor", 64, "+30 armor level"),
                new ServerSalvage("", 64, "no bench: skipped"),
            },
        };
        var salvage = RynthCore.Plugin.Shared.ServerSalvageView.ParseList(
            ServerPackProfile.BuildJson(withSalvage, "RynthX", "1", true, "X"));
        Check.Eq(salvage.Count, 2, "rows without a bench are skipped");
        Check.Eq(salvage[0].Bench, "melee", "bench");
        Check.Eq(salvage[0].Material, 52, "material id");
        Check.Eq(salvage[0].Kind, "once", "kind");
        Check.False(salvage[0].UsesTinkSlot, "no tink slot");
        Check.Eq(salvage[1].Bench, "armor", "bench lower-cased");
        Check.Eq(salvage[1].Kind, "tink", "default kind");
        Check.True(salvage[1].UsesTinkSlot, "slot by default");
        Check.Eq(RynthCore.Plugin.Shared.ServerSalvageView.ParseList("{\"id\":\"x\"}").Count, 0, "a profile without the list (older plugins)");

        // The optional row fields: absent = off / the default, and the writer leaves them out then.
        Check.True(salvage.All(v => v.AppliedInt == 0 && !v.Prepared && v.Order == 0 && !v.AfterCap
                                    && v.StackGroup.Length == 0 && v.StackCap == 0), "optional fields default when absent");
        string plain = ServerPackProfile.BuildJson(withSalvage, "RynthX", "1", true, "X");
        Check.True(!plain.Contains("\"applied\"") && !plain.Contains("\"prepared\"") && !plain.Contains("\"order\"")
                   && !plain.Contains("\"afterCap\"") && !plain.Contains("\"stackGroup\""), "unset fields are not written");

        ServerPackInfo withRules = new("x", "X", new[] { "x.example" }, new[] { "X" })
        {
            Salvage = new[]
            {
                new ServerSalvage("melee", 52, "post-cap", "once", UsesTinkSlot: false, Order: 995, AfterCap: true),
                new ServerSalvage("jewelry", 71, "stack", UsesTinkSlot: false, Order: 50, StackGroup: "jewelry-stack", StackCap: 10),
                new ServerSalvage("trinkets", 20, "tier", "once", UsesTinkSlot: false, AppliedInt: 384, Prepared: true, Order: 10),
            },
        };
        var rules = RynthCore.Plugin.Shared.ServerSalvageView.ParseList(
            ServerPackProfile.BuildJson(withRules, "RynthX", "1", true, "X"));
        Check.Eq(rules.Count, 3, "rows with rules");
        Check.True(rules[0].AfterCap && rules[0].Order == 995 && !rules[0].Prepared && rules[0].StackGroup.Length == 0, "after cap + order");
        Check.Eq(rules[1].StackGroup, "jewelry-stack", "stack group");
        Check.Eq(rules[1].StackCap, 10, "stack cap");
        Check.True(rules[2].Prepared && rules[2].AppliedInt == 384 && rules[2].Order == 10 && !rules[2].AfterCap, "prepared + applied + order");
        Check.Eq(RynthCore.Plugin.Shared.ServerSalvageView.ParseList(
            "{\"salvage\":[{\"bench\":\"jewelry\",\"material\":64,\"stackGroup\":\"g\"},{\"bench\":\"jewelry\",\"material\":71,\"stackGroup\":\"g\",\"stackCap\":0}]}")
            .Count(v => v.StackGroup.Length > 0), 0, "a stack group without a positive cap is no group");
        var odd = RynthCore.Plugin.Shared.ServerSalvageView.ParseList(
            "{\"salvage\":[{\"bench\":\"melee\",\"material\":52,\"applied\":\"x\",\"prepared\":1,\"order\":null,\"afterCap\":\"yes\"}]}");
        Check.True(odd.Count == 1 && odd[0].AppliedInt == 0 && !odd[0].Prepared && odd[0].Order == 0 && !odd[0].AfterCap,
            "fields of the wrong type read as absent");

        string idle = ServerPackProfile.BuildJson(info, "RynthColdeve", "0.1.2", false, null);
        using JsonDocument idleDoc = JsonDocument.Parse(idle);
        Check.False(idleDoc.RootElement.GetProperty("active").GetBoolean(), "not active");
        Check.Eq(idleDoc.RootElement.GetProperty("world").GetString(), "", "no world before login");
        Check.Eq(ServerPackProfile.BottingName(BottingPolicy.Unknown), "unknown", "unknown botting");
    }

    private static void ProfileSummons()
    {
        ServerPackInfo none = new("x", "X", new[] { "x.example" }, new[] { "X" });
        string plain = ServerPackProfile.BuildJson(none, "RynthX", "1", true, "X");
        Check.False(plain.Contains("\"summons\""), "no holders: no summons key");
        Check.Eq(RynthCore.Plugin.Shared.ServerSummonView.ParseList(plain).Count, 0, "and the reader reads none");

        ServerPackInfo with = new("x", "X", new[] { "x.example" }, new[] { "X" })
        {
            Summons = new[]
            {
                new ServerSummon(1007741, "Duo \"Gem\"", "Summon ", Slots: 2, ElementIsLast: true),
                new ServerSummon(0, "Trio Bauble", "Held ", Slots: 3),
            },
        };
        string json = ServerPackProfile.BuildJson(with, "RynthX", "1", true, "X");
        using (JsonDocument doc = JsonDocument.Parse(json)) Check.Eq(doc.RootElement.GetProperty("summons").GetArrayLength(), 2, "two rows written");
        var rows = RynthCore.Plugin.Shared.ServerSummonView.ParseList(json);
        Check.Eq(rows.Count, 2, "two rows read");
        Check.Eq(rows[0].Wcid, 1007741u, "wcid");
        Check.Eq(rows[0].NameMatch, "Duo \"Gem\"", "name, escaped and read back");
        Check.Eq(rows[0].SlotPrefix, "Summon ", "slot prefix keeps its space");
        Check.True(rows[0].Slots == 2 && rows[0].ElementIsLast, "slots and elementIsLast");
        Check.True(rows[1].Wcid == 0 && rows[1].Slots == 3 && !rows[1].ElementIsLast, "a by-name row");
        Check.True(rows[0].Matches(1007741, "anything") && !rows[0].Matches(1007740, "Duo \"Gem\""), "told by wcid when known");
        Check.True(rows[1].Matches(0, "A Trio Bauble"), "a by-name row by name");
    }

    /// <summary>The Duo Summon Gem row in DreamWeave's ServerData.cs (ACE-DreamWeave wcid 1007741).</summary>
    private static void DreamWeaveDuoGem()
    {
        string dir = AppContext.BaseDirectory;
        string? file = null;
        for (var d = new DirectoryInfo(dir); d != null && file == null; d = d.Parent)
        {
            string f = Path.Combine(d.FullName, "Plugins", "RynthCore.Plugin.RynthDreamWeave", "ServerData.cs");
            if (File.Exists(f)) file = f;
        }
        Check.True(file != null, "DreamWeave's ServerData.cs is found");
        if (file == null) return;
        string src = File.ReadAllText(file);
        Check.True(src.Contains("new ServerSummon(1007741, \"Duo Summon Gem\", \"Summon \", Slots: 2, ElementIsLast: true)"),
                   "the gem's row: wcid 1007741, by its name too, \"Summon N:\" lines, two slots, its element the second's");
    }

    private static void ProfileJsonAscii()
    {
        ServerPackInfo info = new("x", "Caf\u00e9 \"Q\"\n", Array.Empty<string>(), Array.Empty<string>()) { Summary = "a\\b\tc \u2014 d" };
        string json = ServerPackProfile.BuildJson(info, "RynthX", "1", false, "");
        bool ascii = true;
        foreach (char c in json) if (c > 0x7e || c < 0x20) ascii = false;
        Check.True(ascii, "only printable ASCII: " + json);
        using JsonDocument doc = JsonDocument.Parse(json);
        Check.Eq(doc.RootElement.GetProperty("name").GetString(), "Caf\u00e9 \"Q\"", "name round-trips (trimmed)");
        Check.Eq(doc.RootElement.GetProperty("summary").GetString(), "a\\b\tc \u2014 d", "summary round-trips");
    }

    private static ServerPackInfo Coldeve() =>
        new("Coldeve", "Coldeve", new[] { "play.coldeve.ac" }, new[] { "Coldeve" }) { Discord = "https://discord.gg/x" };

    private const string Push =
        "V\tcoldeve\tColdeve\t1\ton\t0\tforced on for this character\n" +
        "V\tilt\tInfinite Leaftide\t0\tauto\t0\tnot Infinite Leaftide\n" +
        "X\tchar\tChar\n" +
        "S\tcoldeve\tColdeve\t1\t/rc server coldeve on\n" +
        "I\tColdeve server (RynthColdeve)\t/rc miniremote section char\tWhat is known.\tsection:char\n";

    private static void EnginePush()
    {
        var model = new ServerPackModel(Coldeve());
        Check.True(model.ApplyEngine(Push, 1234), "taken");
        ServerVerdictView? v = model.Engine;
        Check.True(v != null, "this server's verdict");
        Check.True(v!.Active, "active");
        Check.Eq(v.Force, "on", "override");
        Check.False(v.Detected, "forced, not detected");
        Check.Eq(v.Reason, "forced on for this character", "reason");
        Check.Eq(model.EngineAtMs, 1234L, "when");
        Check.True(model.IsActive("SomewhereElse"), "RynthCore's verdict beats the world name");
    }

    private static void ForeignPush()
    {
        var model = new ServerPackModel(Coldeve());
        Check.False(model.ApplyEngine("X\tchar\tChar\n", 1), "no server lines: not a push");
        Check.False(model.ApplyEngine(null, 1), "null");
        Check.False(model.ApplyEngine(new string('V', ServerPackModel.MaxPayload + 1), 1), "too long");
        Check.True(model.Engine == null, "nothing published");

        // A push that lists other servers but not this one yet (RynthCore hasn't read the manifest).
        Check.True(model.ApplyEngine("V\tilt\tInfinite Leaftide\t1\tauto\t1\thost matches\n", 2), "a push");
        Check.True(model.Engine == null, "no verdict for this server");
        ServerPackModel.ParseEngine("V\tCOLDEVE\tColdeve\t0\tweird\t1\n", "coldeve", out bool listed);
        Check.True(listed, "listed");
        Check.Eq(ServerPackModel.ParseEngine("V\tCOLDEVE\tColdeve\t0\tweird\t1\n", "coldeve", out _)!.Force, "auto",
                 "id case ignored; an unknown override reads as auto; no reason field is fine");
    }

    private static void WorldFallback()
    {
        var model = new ServerPackModel(Coldeve());
        Check.True(model.IsActive("Coldeve"), "exact world name");
        Check.True(model.IsActive(" coldeve "), "case and spaces");
        Check.False(model.IsActive("ColdeveTwo"), "no partial match without '*'");
        Check.False(model.IsActive(null), "unknown world");
        Check.True(ServerPackModel.WorldMatches("Aeshnidae Live", new[] { "Aelrynth*", "Aeshnidae*" }), "wildcard");
        Check.False(ServerPackModel.WorldMatches("LeafDawn", new[] { "Leafdawning" }), "LeafDawn is not Leafdawning");
        Check.True(ServerPackModel.WorldMatches("Infinite Leaftide", new[] { "*Leaftide*" }), "both ends");
    }

    private static void ForceCommands()
    {
        Check.Eq(ServerPackModel.ForceCommand("coldeve", "On"), "/rc server coldeve on", "on");
        Check.Eq(ServerPackModel.ForceCommand("coldeve", "auto"), "/rc server coldeve auto", "auto");
        Check.Eq(ServerPackModel.ForceCommand("coldeve", "maybe"), "", "unknown mode");
        Check.Eq(ServerPackModel.ForceCommand("cold eve", "on"), "", "an id with a space");
        Check.Eq(ServerPackModel.ForceCommand("coldeve; /quit", "on"), "", "no command injection through the id");
        Check.False(ServerPackModel.IsSafeId(new string('a', 33)), "too long");
    }

    private static void SectionsJson()
    {
        string json = ServerPackModel.BuildSectionsJson(Coldeve(), "RynthColdeve");
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Check.Eq(root.GetProperty("version").GetInt32(), 1, "schema 1");
        JsonElement server = root.GetProperty("servers")[0];
        Check.Eq(server.GetProperty("id").GetString(), "coldeve", "server id lower-cased");
        Check.Eq(server.GetProperty("hosts")[0].GetString(), "play.coldeve.ac", "hosts");
        Check.Eq(server.GetProperty("worldNames")[0].GetString(), "Coldeve", "world names");
        // Tom 10-24: every server its own spot on RynthCore's bar, not mixed in with the rest.
        Check.False(root.TryGetProperty("contributions", out _) || root.TryGetProperty("sections", out _), "nothing in the Mini Remote");
        JsonElement window = root.GetProperty("windows")[0];
        Check.Eq(window.GetProperty("title").GetString(), "Coldeve", "its own window, named for the server");
        Check.True(window.GetProperty("bar").GetBoolean(), "with a button on the bar");
        Check.Eq(window.GetProperty("server").GetString(), "coldeve", "only while Coldeve is active");
        Check.Eq(window.GetProperty("surface").GetString(), "window:server", "drawn by the plugin");

        var quoted = new ServerPackInfo("odd", "Odd \"Name\"\n", new[] { "a\\b" }, Array.Empty<string>());
        using JsonDocument odd = JsonDocument.Parse(ServerPackModel.BuildSectionsJson(quoted, "RynthOdd"));
        Check.Eq(odd.RootElement.GetProperty("servers")[0].GetProperty("hosts")[0].GetString(), "a\\b", "escaped");
    }

    private static ServerBarMenuItem[] MenuItems() => new[]
    {
        new ServerBarMenuItem("market", "Market", ((char)0xE470).ToString(), "/rserver market"),
        new ServerBarMenuItem("suit", "Suit Builder", ((char)0xE40A).ToString(), "/rserver suit"),
        new ServerBarMenuItem("abilities", "Class Abilities", ((char)0xE6A2).ToString(), "/rserver abilities"),
    };

    private static void NoBarMenuKey()
    {
        string plain = ServerPackModel.BuildSectionsJson(Coldeve(), "RynthColdeve");
        Check.Eq(ServerPackModel.BuildSectionsJson(Coldeve(), "RynthColdeve", null, barMenu: null), plain, "null pages and null menu are the default");
        Check.Eq(ServerPackModel.BuildSectionsJson(Coldeve(), "RynthColdeve", null, barMenu: new List<ServerBarMenuItem>()), plain, "an empty menu is the default");
        Check.False(plain.Contains("barMenu"), "no barMenu key");
    }

    private static void BarMenuRoundTrip()
    {
        ServerBarMenuItem[] items = MenuItems();
        string json = ServerPackModel.BuildSectionsJson(Coldeve(), "RynthColdeve", null, barMenu: items);
        SectionManifest m = SectionManifestParser.Parse("RynthColdeve", json, out string error);
        Check.Eq(error, string.Empty, "parses clean: " + error);
        SectionBarMenuItem[] menu = m.Windows.Single().Menu;
        Check.Eq(menu.Length, 3, "three items");
        for (int i = 0; i < items.Length; i++)
        {
            Check.Eq(menu[i].Id, items[i].Id, "id " + i);
            Check.Eq(menu[i].Tooltip, items[i].Tooltip, "tooltip " + i);
            Check.Eq(menu[i].Icon, items[i].Icon, "icon " + i);
            Check.Eq(menu[i].Command, items[i].Command, "command " + i);
        }

        // With windows of the server's own (ServerWindows), the menu stays on the server's window only.
        var own = new[] { new ServerWindow("skills", "Skills", "x", 300, 400, 200, 200, false, _ => default) };
        SectionManifest both = SectionManifestParser.Parse("RynthColdeve",
            ServerPackModel.BuildSectionsJson(Coldeve(), "RynthColdeve", null, own, barMenu: items), out error);
        Check.Eq(error, string.Empty, "with a window of its own: parses clean: " + error);
        Check.Eq(both.Windows.Length, 2, "with a window of its own: two windows");
        Check.Eq(both.Windows[0].Menu.Length, 3, "with a window of its own: the server window has the menu");
        Check.Eq(both.Windows[1].Menu.Length, 0, "with a window of its own: that window has none");
    }

    private static void BarMenuEscapes()
    {
        const string tip = "Say \"hi\" \\ there";
        var items = new[] { new ServerBarMenuItem("odd", tip, ((char)0xE470).ToString(), "/rserver market") };
        string json = ServerPackModel.BuildSectionsJson(Coldeve(), "RynthColdeve", null, barMenu: items);
        SectionManifest m = SectionManifestParser.Parse("RynthColdeve", json, out string error);
        Check.Eq(error, string.Empty, "parses clean: " + error);
        Check.Eq(m.Windows.Single().Menu.Single().Tooltip, tip, "tooltip survives escaping");
    }

    private static void ClearOnLogout()
    {
        var model = new ServerPackModel(Coldeve());
        model.ApplyEngine(Push, 5);
        model.Clear();
        Check.True(model.Engine == null, "verdict cleared");
        Check.Eq(model.EngineAtMs, 0L, "time cleared");
        Check.False(model.IsActive("Elsewhere"), "back to the world name");
    }

    private static void InfoText()
    {
        ServerPackInfo c = Coldeve();
        Check.Eq(c.Address, "play.coldeve.ac:9000", "address");
        Check.True(c.BottingText.StartsWith("Not published"), "unknown policy says so");
        Check.True(c.BottingText.Contains("Discord"), "and points at the Discord");
        var lev = new ServerPackInfo("example", "Example", new[] { "play.example.test" }, new[] { "Example" })
        { Botting = BottingPolicy.Forbidden, BottingNote = "no automation" };
        Check.Eq(lev.BottingText, "Forbidden - no automation", "forbidden");
        Check.Eq(new ServerPackInfo("x", "", Array.Empty<string>(), Array.Empty<string>()).Address, "", "no host, no address");
        Check.True(ServerPackModel.LauncherSteps(c)[0].Contains("play.coldeve.ac:9000"), "setup names the address");
    }
}
