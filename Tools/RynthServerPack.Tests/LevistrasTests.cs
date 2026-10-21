using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RynthCore.App;
using RynthCore.PluginSdk.Manifest;
using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// RynthLevistras (2026-10-21): the Levistras server plugin, and the launcher's server list
// (RynthCore.App\KnownServerCatalog.cs), which names a server plugin for every server it offers.
internal static class LevistrasTests
{
    public static void Register(Runner r)
    {
        r.Add("levistras: the plugin's detection data matches the launcher's Levistras entry", Detection);
        r.Add("levistras: its card says bots are forbidden and two accounts", Card);
        r.Add("levistras: its quest pack is the 464 Levistras rows RynthOracle had", Pack);
        r.Add("launcher catalog: every server names a plugin that claims its host, port and emulator", Catalog);
    }

    private static string PluginsDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "Plugins", "Shared", "ServerPack")))
            dir = Path.GetDirectoryName(dir);
        if (dir == null) throw new DirectoryNotFoundException("RynthSuite\\Plugins not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir, "Plugins");
    }

    private sealed record Plugin(string Dir, string Id, string Name, string[] Hosts, string[] Worlds, string ServerData);

    /// <summary>The server plugin project named Rynth&lt;name&gt;, or null.</summary>
    private static Plugin? Load(string pluginName)
    {
        string dir = Path.Combine(PluginsDir(), "RynthCore.Plugin." + pluginName);
        string csproj = Path.Combine(dir, "RynthCore.Plugin." + pluginName + ".csproj");
        if (!File.Exists(csproj)) return null;
        XDocument doc = XDocument.Load(csproj);
        string Prop(string name) => doc.Descendants(name).FirstOrDefault()?.Value.Trim() ?? "";
        string[] Split(string v) => v.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string data = Path.Combine(dir, "ServerData.cs");
        return new Plugin(dir, Prop("RynthPluginServerId"), Prop("RynthPluginServerName"), Split(Prop("RynthPluginServerHosts")),
                          Split(Prop("RynthPluginServerWorldNames")), File.Exists(data) ? File.ReadAllText(data) : "");
    }

    /// <summary>The port ServerData.cs sets (9000 when it doesn't say).</summary>
    private static int PortOf(Plugin p)
    {
        Match m = Regex.Match(p.ServerData, @"\bPort\s*=\s*(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 9000;
    }

    private static string EmulatorOf(Plugin p)
    {
        Match m = Regex.Match(p.ServerData, @"\bEmulator\s*=\s*""([^""]*)""");
        return m.Success ? m.Groups[1].Value : "ACE";
    }

    private static void Detection()
    {
        Plugin? p = Load("RynthLevistras");
        Check.NotNull(p, "RynthLevistras exists");
        if (p == null) return;
        Check.Eq(p.Id, "levistras", "id");
        Check.Eq(p.Name, "Levistras", "name (also its quest pack's tag)");
        Check.True(p.Hosts.SequenceEqual(new[] { "levistras.acportalstorm.com" }), "host: " + string.Join("|", p.Hosts));
        Check.True(p.Worlds.Contains("Levistras"), "world name Levistras (the name RynthOracle matched before the move)");

        KnownServer? k = KnownServerCatalog.FindById("levistras");
        Check.NotNull(k, "the launcher lists Levistras");
        if (k == null) return;
        Check.Eq(k.PluginName, "RynthLevistras", "the launcher's entry names RynthLevistras");
        var server = new RynthPluginServer { Id = p.Id, Hosts = p.Hosts.ToList() };
        Check.True(server.MatchesHost(k.Host, out _), "the plugin claims the launcher's host " + k.Host);
        Check.Eq(PortOf(p), k.Port, "port");
        Check.Eq(EmulatorOf(p), "ACE", "emulator");
        Check.Eq(k.Emulator, AcEmulatorKind.Ace, "the launcher connects with ACE");
    }

    private static void Card()
    {
        Plugin? p = Load("RynthLevistras");
        if (p == null) { Check.True(false, "RynthLevistras exists"); return; }
        Check.True(p.ServerData.Contains("Botting = BottingPolicy.Forbidden"), "botting forbidden (the server card warns, RynthAi's combat isn't for here)");
        Check.True(p.ServerData.Contains("AccountLimit = \"2 accounts\""), "two accounts");
        Check.True(p.ServerData.Contains("Ruleset = \"PvE\""), "PvE");
    }

    private static void Pack()
    {
        Plugin? p = Load("RynthLevistras");
        if (p == null) { Check.True(false, "RynthLevistras exists"); return; }
        string file = Path.Combine(p.Dir, "Resources", "quests.levistras.csv");
        Check.True(File.Exists(file), "Resources\\quests.levistras.csv (ServerPlugin.props embeds it as the served pack)");
        if (!File.Exists(file)) return;
        string csv = File.ReadAllText(file);
        Check.Eq(RynthCore.Plugin.ServerPack.ServerPackQuestPack.CountRows(csv), 464, "464 quests");
        var info = new RynthCore.Plugin.ServerPack.ServerPackInfo(p.Id, p.Name, p.Hosts, p.Worlds);
        string json = RynthCore.Plugin.ServerPack.ServerPackQuestPack.BuildInfoJson(info, "RynthLevistras", "0.1.0", csv);
        Check.True(json.Contains("\"server\":\"levistras\"") && json.Contains("\"tag\":\"Levistras\"") && json.Contains("\"rows\":464"),
                   "offered as levistras / Levistras, 464 rows: " + json);
        Check.True(File.Exists(Path.Combine(p.Dir, "THIRD_PARTY_NOTICES.md")), "the pack's MIT notice (Advis Eveldan's rows) comes with it");
    }

    private static void Catalog()
    {
        foreach (KnownServer k in KnownServerCatalog.All)
        {
            Plugin? p = Load(k.PluginName);
            Check.NotNull(p, $"{k.Id}: the launcher's plugin {k.PluginName} exists");
            if (p == null) continue;
            Check.Eq(p.Id, k.Id, $"{k.PluginName}: the same server id as the launcher");
            var server = new RynthPluginServer { Id = p.Id, Hosts = p.Hosts.ToList() };
            Check.True(server.MatchesHost(k.Host, out _), $"{k.PluginName} claims the launcher's host {k.Host}");
            Check.Eq(PortOf(p), k.Port, $"{k.PluginName}: port");
            Check.Eq(EmulatorOf(p).Equals("GDLE", StringComparison.OrdinalIgnoreCase), k.Emulator == AcEmulatorKind.Gdle, $"{k.PluginName}: emulator");
        }
    }
}
