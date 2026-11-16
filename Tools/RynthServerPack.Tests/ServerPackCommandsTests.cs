using System;
using System.Collections.Generic;
using System.Linq;
using RynthCore.Plugin.ServerPack;
using RynthCore.Plugin.Shared;
using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// Every "/rserver" command answers in chat (2026-10-28). RunCommand needs the plugin's host, so the words
// (shared and a server's own, as RynthAelrynth's "vault"), the verb each one is, and the lines are checked
// here, plus the shared reply queue the server plugins write through.
internal static class ServerPackCommandsTests
{
    public static void Register(Runner r)
    {
        r.Add("rserver: every word is a verb or a server id, typed right and wrong", Words);
        r.Add("rserver: the window, usage and unknown-word lines", Lines);
        r.Add("rserver: replies go through the shared queue, tagged with the plugin's name", Queue);
    }

    private static readonly HashSet<string> Vault = new(StringComparer.OrdinalIgnoreCase) { "vault" };

    private static void Words()
    {
        var table = new (string Line, ServerVerb Verb)[]
        {
            ("/rserver", ServerVerb.Toggle), ("/rserver show", ServerVerb.Show), ("/rserver HIDE", ServerVerb.Hide),
            ("/rserver toggle", ServerVerb.Toggle), ("/rserver info", ServerVerb.Info), ("/rserver help", ServerVerb.Help),
            ("/rserver auto", ServerVerb.Auto), ("/rserver on", ServerVerb.On), ("/rserver off", ServerVerb.Off),
            ("/rserver vault", ServerVerb.Server), ("/rserver wobble", ServerVerb.Unknown),
        };
        var seen = new HashSet<ServerVerb>();
        foreach (var (line, verb) in table)
        {
            ServerVerb got = ServerPackCommands.Verb(line.Split(' ', StringSplitOptions.RemoveEmptyEntries), Vault);
            Check.Eq(got, verb, $"'{line}'");
            seen.Add(got);
        }
        foreach (ServerVerb v in Enum.GetValues<ServerVerb>()) Check.True(seen.Contains(v), $"the table covers /rserver's {v}");
        Check.True(ServerPackCommands.IsVerb("Info", Vault) && ServerPackCommands.IsVerb("vault", Vault), "shared and own verbs");
        Check.False(ServerPackCommands.IsVerb("coldeve", Vault), "a server id isn't a verb (\"/rserver coldeve info\")");
        Check.Eq(ServerPackCommands.Verb(new[] { "/rserver", "vault" }, new HashSet<string>()), ServerVerb.Unknown, "another server's own verb is unknown here");
    }

    private static void Lines()
    {
        Check.Eq(ServerPackCommands.Window("Coldeve", true), "The Coldeve window shown.", "show");
        Check.Eq(ServerPackCommands.Window("Coldeve", false), "The Coldeve window hidden.", "hide");
        string[] usage = ServerPackCommands.Usage("Coldeve");
        Check.Eq(usage.Length, 2, "two usage lines");
        Check.True(usage[0].Contains("/rserver [show|hide|toggle]") && usage[1].Contains("/rserver auto|on|off"), "the usage names every verb");
        Check.Eq(ServerPackCommands.Unknown("wobble"), "Unknown '/rserver wobble'.", "an unknown word");

        // A server without ServerUsageLines (Coldeve) prints exactly the two shared lines, byte for byte.
        string[] pinned =
        {
            "/rserver [show|hide|toggle] - the Coldeve server card (also in the Mini Remote's Char section).",
            "/rserver info - what is known about Coldeve; /rserver auto|on|off - this character's override.",
        };
        Check.Eq(string.Join("\n", usage), string.Join("\n", pinned), "hookless usage is unchanged");
        Check.Eq(string.Join("\n", ServerPackCommands.Usage("Coldeve", new List<string>())), string.Join("\n", pinned), "no own lines: unchanged");
        Check.Eq(string.Join("\n", ServerPackCommands.Usage("Coldeve", new List<string> { "", "  " })), string.Join("\n", pinned), "blank own lines are dropped");
        string coldeve = string.Join("\n", System.IO.Directory.GetFiles(System.IO.Path.Combine(ServerProjectsTests.PluginsDir(), "RynthCore.Plugin.RynthColdeve"), "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")).Select(System.IO.File.ReadAllText));
        Check.False(coldeve.Contains("ServerUsageLines", StringComparison.Ordinal), "Coldeve has no ServerUsageLines (so the pin above is what it prints)");

        // A server's own lines follow the shared two, which stay first and unchanged.
        string[] own = ServerPackCommands.Usage("Coldeve", new List<string> { "  /rserver x - one more line  " });
        Check.Eq(own.Length, 3, "an own line is appended");
        Check.Eq(own[2], "/rserver x - one more line", "trimmed");
        Check.True(own[0] == pinned[0] && own[1] == pinned[1], "the shared two come first, unchanged");

        // DreamWeave's verbs follow the shared two.
        string[] dw = ServerPackCommands.Usage("DreamWeave", new List<string> { RynthCore.Plugin.RynthDreamWeave.DreamWeaveWeb.UsageLine() });
        Check.Eq(dw.Length, 3, "DreamWeave: three usage lines");
        Check.Eq(dw[2], "/rserver abilities | market | suit - open the website's Class Abilities, Market or Suit Builder, logged in as this account.", "DreamWeave's own line");
        Check.True(dw[0] == ServerPackCommands.Usage("DreamWeave")[0] && dw[1] == ServerPackCommands.Usage("DreamWeave")[1], "the shared two come first, unchanged");
    }

    private static void Queue()
    {
        var chat = new List<(string, int)>();
        var r = new CommandReply("[RynthColdeve]", (t, c) => { chat.Add((t, c)); return true; });
        r.Ok(ServerPackCommands.Window("Coldeve", true));
        r.Fail("[RynthAelrynth] The account vault is Aelrynth's; RynthCore doesn't see this world as Aelrynth.");
        while (r.Flush()) { }
        Check.Eq(chat.Count, 2, "both lines written");
        Check.Eq(chat[0], ("[RynthColdeve] The Coldeve window shown.", CommandReply.OkChatType), "untagged: the plugin's tag, the ok colour");
        Check.Eq(chat[1].Item2, CommandReply.FailChatType, "a refusal: the failure colour");
        Check.True(chat[1].Item1.StartsWith("[RynthAelrynth] ", StringComparison.Ordinal), "a line with its own tag keeps it");
    }
}
