using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RynthCore.Plugin.ServerPack;
using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// "RynthServer.QuestPack" (Plugins\Shared\ServerPack\ServerPackQuestPack.cs): a server plugin's own quest
// list for RynthOracle. The info JSON says whose pack it is and how big; junk is no pack at all.
internal static class QuestPackTests
{
    public static void Register(Runner r)
    {
        r.Add("quest pack: the info JSON names the server, its tag and the pack's size", InfoJson);
        r.Add("quest pack: no pack, junk or a header alone is no pack", NoPack);
        r.Add("quest pack: the info JSON is plain ASCII and escaped", InfoAscii);
        r.Add("quest pack: the embedded pack is served as it is (RynthConquest's)", Embedded);
        r.Add("quest packs: Conquest's, Levistras's and Aelrynth's are their own plugins' (none left in RynthOracle)", Projects);
    }

    private static string PluginsDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "Plugins", "Shared", "ServerPack")))
            dir = Path.GetDirectoryName(dir);
        if (dir == null) throw new DirectoryNotFoundException("RynthSuite\\Plugins not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir, "Plugins");
    }

    /// <summary>RynthSuite's Plugins: the sibling checkout this repo builds against (RynthOracle lives there).</summary>
    private static string SuitePluginsDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "RynthSuite", "Plugins", "RynthCore.Plugin.RynthOracle")))
            dir = Path.GetDirectoryName(dir);
        if (dir == null) throw new DirectoryNotFoundException("the sibling RynthSuite\\Plugins not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir, "RynthSuite", "Plugins");
    }

    private static void Embedded()
    {
        string csv = ServerPackQuestPack.Csv;
        Check.Eq(ServerPackQuestPack.CountRows(csv), 546, "RynthConquest's 546 quests are served");
        var conquest = new ServerPackInfo("conquest", "Conquest", new[] { "acconquest.ddns.net" }, new[] { "Conquest" });
        using JsonDocument doc = JsonDocument.Parse(ServerPackQuestPack.BuildInfoJson(conquest, "RynthConquest", "0.2.0", csv));
        Check.Eq(doc.RootElement.GetProperty("rows").GetInt32(), 546, "the info says so");
        Check.Eq(doc.RootElement.GetProperty("tag").GetString(), "Conquest", "under the tag the master list uses");
        Check.True(ReferenceEquals(csv, ServerPackQuestPack.Csv), "read once");
    }

    /// <summary>
    /// The packs that were RynthOracle's (2026-10-21): each in its server's plugin, as
    /// Resources\quests.&lt;server id&gt;.csv (ServerPlugin.props embeds it), naming only that server; the
    /// same rows as before; none left in RynthOracle, which keeps only the retail list.
    /// </summary>
    private static void Projects()
    {
        string plugins = PluginsDir();
        var expected = new Dictionary<string, (string Dir, string Name, int Rows)>
        {
            ["conquest"] = ("RynthCore.Plugin.RynthConquest", "Conquest", 546),
            ["levistras"] = ("RynthCore.Plugin.RynthLevistras", "Levistras", 464),
            ["aelrynth"] = ("RynthCore.Plugin.RynthAelrynth", "Aelrynth", 4277),
        };
        foreach (var kv in expected)
        {
            string file = Path.Combine(plugins, kv.Value.Dir, "Resources", $"quests.{kv.Key}.csv");
            Check.True(File.Exists(file), kv.Value.Dir + " has its pack " + Path.GetFileName(file));
            if (!File.Exists(file)) continue;
            string csv = File.ReadAllText(file);
            Check.True(ServerPackQuestPack.Usable(csv), kv.Key + ": a quest list (QuestFlag header and rows)");
            Check.Eq(ServerPackQuestPack.CountRows(csv), kv.Value.Rows, kv.Key + ": the same rows as before the move");
            string[] lines = csv.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string[] header = lines[0].Split(',');
            int server = Array.FindIndex(header, h => h.Trim().Equals("Server", StringComparison.OrdinalIgnoreCase));
            Check.True(server >= 0, kv.Key + ": has a Server column");
            int other = lines.Skip(1).Count(l => l.Split(',')[server].Trim() != kv.Value.Name);
            Check.Eq(other, 0, $"{kv.Key}: every row's Server is {kv.Value.Name} (the pack's tag, the plugin's server name)");
            string csproj = File.ReadAllText(Path.Combine(plugins, kv.Value.Dir, kv.Value.Dir + ".csproj"));
            Check.True(csproj.Contains("<RynthPluginServerId>" + kv.Key + "</RynthPluginServerId>")
                       && csproj.Contains("<RynthPluginServerName>" + kv.Value.Name + "</RynthPluginServerName>"),
                       kv.Value.Dir + ": the pack's file name is its server id, the rows' Server its server name");
        }

        // No other server plugin carries a pack, and none carries another server's.
        foreach (string dir in Directory.GetDirectories(plugins, "RynthCore.Plugin.Rynth*"))
        {
            string res = Path.Combine(dir, "Resources");
            if (!Directory.Exists(res)) continue;
            foreach (string f in Directory.GetFiles(res, "quests.*.csv"))
            {
                string id = Path.GetFileNameWithoutExtension(f).Substring("quests.".Length);
                Check.True(expected.TryGetValue(id, out var e) && Path.GetFileName(dir) == e.Dir, $"{Path.GetFileName(dir)}/{Path.GetFileName(f)} is in its own server's plugin");
            }
        }
        string oracle = Path.Combine(SuitePluginsDir(), "RynthCore.Plugin.RynthOracle");
        string[] left = Directory.GetFiles(Path.Combine(oracle, "Resources"), "quests*.csv").Select(Path.GetFileName).ToArray()!;
        Check.True(left.SequenceEqual(new[] { "quests.csv" }), "RynthOracle keeps only the retail list: " + string.Join(",", left));
        Check.False(File.ReadAllText(Path.Combine(oracle, "RynthCore.Plugin.RynthOracle.csproj")).Contains("quests.conquest", StringComparison.OrdinalIgnoreCase),
                    "and embeds no server's pack");
        string props = File.ReadAllText(Path.Combine(plugins, "Shared", "ServerPack", "ServerPlugin.props"));
        Check.True(props.Contains("quests.$(RynthPluginServerId).csv") && props.Contains("LogicalName=\"" + ServerPackQuestPack.ResourceName + "\""),
                   "ServerPlugin.props embeds Resources\\quests.<server id>.csv as the served pack");
    }

    private const string Sample =
        "QuestFlag,Server,Quest,Url,Info,Hint,Repeatable\n"
        + "aaaflag,Example,A quest,,,,FALSE\n"
        + "\n"
        + "bbbflag,Example,\"Another, with a comma\",,,,TRUE\n";

    private static ServerPackInfo Example() => new("Example", "Example", new[] { "play.example.test" }, new[] { "Example" });

    private static void InfoJson()
    {
        string json = ServerPackQuestPack.BuildInfoJson(Example(), "RynthExample", "0.1.0", Sample);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        Check.Eq(root.GetProperty("schema").GetInt32(), ServerPackQuestPack.Schema, "schema");
        Check.Eq(root.GetProperty("server").GetString(), "example", "server: the RynthCore id (lower case)");
        Check.Eq(root.GetProperty("tag").GetString(), "Example", "tag: the server's name (the master list's Server column)");
        Check.Eq(root.GetProperty("plugin").GetString(), "RynthExample", "plugin");
        Check.Eq(root.GetProperty("version").GetString(), "0.1.0", "version");
        Check.Eq(root.GetProperty("rows").GetInt32(), 2, "rows: data lines, blank ones skipped");
        Check.Eq(root.GetProperty("bytes").GetInt32(), System.Text.Encoding.UTF8.GetByteCount(Sample), "bytes: the CSV's UTF-8 length");
        Check.Eq(root.GetProperty("revision").GetInt32(), ServerPackQuestPack.Revision, "revision");
        Check.Eq(ServerPackQuestPack.CountRows(Sample), 2, "CountRows");
        Check.True(ServerPackQuestPack.Usable("﻿QuestFlag,Server\nx,Example\n"), "a BOM before the header is fine");
        Check.True(ServerPackQuestPack.Usable("Flag,Quest\r\nx,y\r\n"), "Flag works as the column name too (CRLF lines)");
    }

    private static void NoPack()
    {
        ServerPackInfo info = Example();
        Check.Eq(ServerPackQuestPack.BuildInfoJson(info, "RynthExample", "0.1.0", null), "", "null: no pack");
        Check.Eq(ServerPackQuestPack.BuildInfoJson(info, "RynthExample", "0.1.0", ""), "", "empty: no pack");
        Check.Eq(ServerPackQuestPack.BuildInfoJson(info, "RynthExample", "0.1.0", "QuestFlag,Server\n"), "", "a header alone: no pack");
        Check.Eq(ServerPackQuestPack.BuildInfoJson(info, "RynthExample", "0.1.0", "Name,Notes\na,b\n"), "", "no QuestFlag column: no pack");
        Check.False(ServerPackQuestPack.Usable("<html>404</html>"), "junk is not usable");
    }

    private static void InfoAscii()
    {
        var odd = new ServerPackInfo("odd", "Odd \"Name\" é", new[] { "a" }, Array.Empty<string>());
        string json = ServerPackQuestPack.BuildInfoJson(odd, "Rynth\\Odd", "1", Sample);
        foreach (char c in json) Check.True(c >= 0x20 && c <= 0x7e, "plain ASCII: " + (int)c);
        using JsonDocument doc = JsonDocument.Parse(json);
        Check.Eq(doc.RootElement.GetProperty("tag").GetString(), "Odd \"Name\" é", "quotes and accents survive");
        Check.Eq(doc.RootElement.GetProperty("plugin").GetString(), "Rynth\\Odd", "backslash survives");
    }
}
