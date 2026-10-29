using System;
using System.IO;
using System.Linq;
using RynthCore.Plugin.RynthILT;
using RynthCore.Plugin.RynthILT.Hub;
using RynthCore.RynthAiHostTests.Fakes;
using RynthCore.RynthAiTests.Harness;

namespace RynthILT.Tests;

// Every ILT command answers in chat (2026-10-28). The Hub's verbs ("/ilt hub ...", "/ra hub|guardian|
// bounty|rates ...") run here on the fake engine host, each typed right and typed wrong, and must each
// write a line of their own. The plugin-level window verbs (settings, stamps, breeding, fellow) need the
// plugin's %APPDATA% folder, so their line (WindowReply) is checked on its own.
internal static class ReplyTests
{
    private const uint Player = 0x50000001;

    public static void Register(Runner r)
    {
        r.Add("replies: every Hub verb answers in chat, typed right and wrong", HubVerbs);
        r.Add("replies: the plugin's window verbs say the window's state", WindowVerbs);
    }

    private static IltHubController NewHub(string folder)
    {
        FakeHost.Reset();
        FakeAiHub.Reset();
        FakeHost.WorldName = "InfiniteLeaftide";
        var host = FakeHost.Create(Player);
        var ai = new RynthAiClient(host);
        ai.UseTableForTests(FakeAiHub.Table);
        string dir = Path.Combine(Program.TempRoot, "replies-" + folder);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        var hub = new IltHubController(host, dir, new IltSettings(), ai);
        hub.OnLoginComplete("Replier", forceFromMigration: false);
        return hub;
    }

    /// <summary>Runs one Hub command; the lines it wrote (it must write one).</summary>
    private static string[] Answer(IltHubController hub, string verb, params string[] args)
    {
        FakeHost.Chat.Clear();
        bool handled = hub.HandleCommand(verb, args);
        string typed = ("/ra " + verb + " " + string.Join(" ", args)).Trim();
        Check.True(handled, typed + ": handled");
        Check.True(FakeHost.Chat.Count > 0, typed + ": answers in chat");
        return FakeHost.Chat.ToArray();
    }

    private static void Says(IltHubController hub, string needle, string verb, params string[] args)
    {
        string[] lines = Answer(hub, verb, args);
        Check.True(lines.Any(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase)),
            $"/ra {verb} {string.Join(" ", args)}: says '{needle}' (got: {string.Join(" / ", lines)})");
    }

    private static void HubVerbs()
    {
        var hub = NewHub("hub");

        // right forms
        Says(hub, "rates reset", "rates", "reset");
        Says(hub, "asked RynthCore to toggle", "bounty");
        Says(hub, "asked RynthCore to show", "guardian", "window", "show");
        Says(hub, "Mini Remote", "hub", "show");
        Says(hub, "Mini Remote", "hub", "open", "gear");
        Says(hub, "nothing to hide", "hub", "open", "character", "hide");
        Says(hub, "World 'InfiniteLeaftide'", "hub", "status");
        Says(hub, "Leaftide features", "hub", "features");
        Says(hub, "Force ILT world: on", "hub", "force", "on");
        Says(hub, "ILT world names", "hub", "servers");
        Says(hub, "Profiles:", "hub", "profile", "list");
        Says(hub, "Suits:", "hub", "suit", "list");
        Says(hub, "Reading the bank balance", "hub", "bank");
        Says(hub, "Sending /clap all", "hub", "clap");
        Says(hub, "Attribute raiser", "hub", "attr");
        Says(hub, "Attribute raiser", "hub", "attr", "run");
        Says(hub, "Attribute raiser", "hub", "attr", "stop");
        Says(hub, "Attribute raiser", "hub", "attr", "costs");
        Says(hub, "auto-raise on", "hub", "attr", "auto", "on");
        Says(hub, "every 10 min", "hub", "attr", "every", "10");
        Says(hub, "Attribute raiser", "hub", "attr", "raise", "str", "1");
        Says(hub, "Nothing to cancel", "hub", "cancel");
        Says(hub, "/ra hub [show|hide|toggle]", "hub", "help");

        // "/ra hub open character" opens RynthOracle's tab: RynthOracle answers ("/ro character").
        FakeHost.ChatCommands.Clear();
        Check.True(hub.HandleCommand("hub", new[] { "open", "character" }) && FakeHost.ChatCommands.Contains("/ro character"),
            "/ra hub open character: runs /ro character (RynthOracle answers it)");

        // wrong forms: why, and how it's typed
        Says(hub, "already waiting", "hub", "bank");          // the one sent above is still out
        Says(hub, "already waiting", "hub", "clap");
        Says(hub, "Usage: /ra hub open", "hub", "open", "wobble");
        Says(hub, "Usage: /ra hub profile", "hub", "profile", "wobble", "x");
        Says(hub, "Usage: /ra hub profile", "hub", "profile", "save");
        Says(hub, "Usage: /ra hub suit", "hub", "suit", "wobble");
        Says(hub, "Usage: /ra hub attr", "hub", "attr", "auto", "maybe");
        Says(hub, "Usage: /ra hub attr", "hub", "attr", "every", "soon");
        Says(hub, "Usage: /ra hub attr", "hub", "attr", "raise", "luck", "1");
        Says(hub, "Usage: /ra hub attr", "hub", "attr", "raise");
        Says(hub, "Usage: /ra hub attr", "hub", "attr", "wobble");
        Says(hub, "Usage: /ra hub servers", "hub", "servers", ",");
        Says(hub, "Nothing to confirm", "hub", "confirm");
        Says(hub, "/ra hub [show|hide|toggle]", "hub", "wobble");
        Says(hub, "/ra guardian", "guardian", "wobble");

        // Vaulted Dungeons answers every word (off here: its master switch is off by default).
        foreach (string w in new[] { "on", "off", "toggle", "status", "wobble" })
        {
            FakeHost.Chat.Clear();
            hub.VaultedDungeons.HandleCommand(new[] { w });
            Check.True(FakeHost.Chat.Count > 0, $"/ilt vaulted {w}: answers");
        }

        // The Hub's verbs as RynthILT takes them from RynthAi's /ra.
        foreach (string line in new[] { "hub bank", "hub attr run", "guardian status", "bounty", "rates reset", "t11", "ub qblist" })
            Check.True(RynthILTPlugin.IsIltVerb(line.Split(' ')), $"/ra {line} is RynthILT's");
    }

    private static void WindowVerbs()
    {
        Check.Eq(RynthILTPlugin.WindowReply("ILT settings", true), "[RynthILT] ILT settings window shown.", "/ilt settings: shown");
        Check.Eq(RynthILTPlugin.WindowReply("Dynamic Stamps", false), "[RynthILT] Dynamic Stamps window hidden.", "/ilt stamps: hidden");
        foreach (string w in new[] { "ILT settings", "Dynamic Stamps", "Pet Breeding", "ILT fellowship" })
            Check.True(RynthILTPlugin.WindowReply(w, true).StartsWith("[RynthILT] " + w, StringComparison.Ordinal), $"{w}: its own line");
    }
}
