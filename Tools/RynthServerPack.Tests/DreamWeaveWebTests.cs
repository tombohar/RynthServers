using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using RynthCore.Engine.UI.Sections;
using RynthCore.Plugin.RynthDreamWeave;
using RynthCore.Plugin.ServerPack;
using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// DreamWeave's web buttons, the pure part: the destinations and the "/weblogin" reply's wire text.
internal static class DreamWeaveWebTests
{
    public static void Register(Runner r)
    {
        r.Add("dreamweave web: a player's copy of the launch line is never taken", PlayerCopy);
        r.Add("dreamweave web: the launch URL is built from constants and a clean code", UrlFromConstants);
        r.Add("dreamweave web: destination ids are verbs no one else uses", IdsAreFree);
        r.Add("dreamweave web: LaunchWire: code alphabet is strict", Alphabet);
        r.Add("dreamweave web: ACE's refusals parse as refusals, other lines are ignored", Refusals);
        r.Add("dreamweave web: replies count only while asked, once", RepliesOnlyWhileAsked);
        r.Add("dreamweave web: one request at a time, never stuck", OneAtATime);
        r.Add("dreamweave web: the code never reaches a status or message", CodeNeverInStatus);
        r.Add("dreamweave web: a code-shaped refusal is replaced, never shown", CodeShapedRefusal);
        r.Add("dreamweave web: an expired request frees itself", SelfExpiry);
        r.Add("dreamweave web: reset keeps the throttle", ResetKeepsThrottle);
        r.Add("dreamweave web: a late code is eaten while it could still be valid", LateCodeIsStale);
        r.Add("dreamweave web: overlapping requests never leak a code to chat", OverlappingRequests);
        r.Add("dreamweave web: profile keys separate accounts", ProfileKeys);
        r.Add("dreamweave web: ImGui only in WebButtons, no context switching, draw makes no host calls (its catch logs via LogTagged)", ThreadRules);
        r.Add("dreamweave web: an open's answer lands only on its own request", ResultGeneration);
        r.Add("dreamweave web: a malformed launch-code line never reaches chat", MalformedSuccess);
        r.Add("dreamweave web: the source scan's helpers read code, not comments or strings", ScanHelpers);
        r.Add("dreamweave web: the plugin's shared hooks hand off to the web tools first", HooksHandOff);
        r.Add("dreamweave web: the bar icon's menu lists the destinations, in the engine's own terms", BarMenuItems);
        r.Add("dreamweave web: the engine draws the bar menu from API 95", EngineDrawsMenu);
        r.Add("dreamweave web: the card keeps its buttons on every engine", CardKeepsButtons);
    }

    private static void BarMenuItems()
    {
        List<ServerBarMenuItem> items = DreamWeaveWeb.BarMenu();
        string[][] want =
        {
            new[] { "abilities", "Class Abilities", ((char)0xE6A2).ToString(), "/rserver abilities" },
            new[] { "market", "Market", ((char)0xE470).ToString(), "/rserver market" },
            new[] { "suit", "Suit Builder", ((char)0xE40A).ToString(), "/rserver suit" },
        };
        Check.Eq(items.Count, want.Length, "one item per destination");
        for (int i = 0; i < want.Length && i < items.Count; i++)
        {
            Check.Eq(items[i].Id, want[i][0], "id " + i);
            Check.Eq(items[i].Tooltip, want[i][1], "tooltip " + i);
            Check.Eq(items[i].Icon, want[i][2], "icon " + i);
            Check.Eq(items[i].Command, want[i][3], "command " + i);
            Check.Eq(SectionManifestParser.Command(items[i].Command), items[i].Command, "the engine keeps the command " + i);
            Check.Eq(SectionManifestParser.Icon(items[i].Icon), items[i].Icon, "the engine keeps the icon " + i);
            Check.True(DreamWeaveWeb.ById(items[i].Id) != null, "the command's verb is a destination: " + items[i].Id);
        }
        Check.True(items.Count <= SectionManifestParser.MaxBarMenuItems, "within the engine's cap");

        // And through the manifest, as the engine reads it.
        var info = new ServerPackInfo("dreamweave", "DreamWeave", new[] { "play.acdreamweave.com" }, Array.Empty<string>());
        SectionManifest m = SectionManifestParser.Parse("RynthDreamWeave", ServerPackModel.BuildSectionsJson(info, "RynthDreamWeave", null, barMenu: items), out string error);
        Check.Eq(error, string.Empty, "parses clean: " + error);
        Check.Eq(m.Windows.Single().Menu.Length, 3, "the engine reads three items");
    }

    private static void EngineDrawsMenu()
    {
        // The plugin's const is pinned to the engine's own (the linked PluginContractVersion), so a renumber fails here.
        uint bar = RynthCore.Engine.Plugins.PluginContractVersion.BarMenu;
        Check.Eq(DreamWeaveWeb.BarMenuEngineApi, bar, "the engine API that draws the bar menu equals the engine's BarMenu");
        Check.False(DreamWeaveWeb.EngineDrawsBarMenu(bar - 1), "one before: the card keeps its buttons");
        Check.True(DreamWeaveWeb.EngineDrawsBarMenu(bar), "at BarMenu: the engine draws the menu");
        Check.True(DreamWeaveWeb.EngineDrawsBarMenu(bar + 1), "after: the engine draws the menu");
        Check.False(DreamWeaveWeb.EngineDrawsBarMenu(0), "0: unknown version keeps the buttons");
    }

    private static void CardKeepsButtons()
    {
        string hostPath = Path.Combine(ServerProjectsTests.PluginsDir(), "RynthCore.Plugin.RynthDreamWeave", "DreamWeaveWebTools.cs");
        Check.True(File.Exists(hostPath), "DreamWeaveWebTools.cs exists");
        if (!File.Exists(hostPath)) return;
        string host = StripComments(File.ReadAllText(hostPath));
        List<string> card = StatementsAfter(host, "partial void ServerCardActions()");
        Check.Eq(card.Count, 1, "ServerCardActions implemented once");
        // 2026-11-09: the card keeps its buttons on every engine (buttons in the feature's own window).
        Check.True(card.Count == 1 && card[0].Contains("WebButtons.Draw(", StringComparison.Ordinal),
                   "ServerCardActions draws the web buttons");
        Check.False(card.Count == 1 && card[0].Contains("EngineDrawsBarMenu", StringComparison.Ordinal),
                    "ServerCardActions never hides the buttons for an engine with the bar menu");
        Check.False(host.Contains("CurrentApiVersion", StringComparison.Ordinal), "the plugin never reads the compile-time CurrentApiVersion");
        Check.True(host.Contains("DreamWeaveWeb.BarMenu()", StringComparison.Ordinal), "ServerBarMenu supplies DreamWeaveWeb.BarMenu()");
    }

    // -- Host wiring (source scan: the plugin is NativeAOT, so its host code can't run here) --
    // The rules are allow-lists: what may appear is named, so a new spelling of a forbidden call fails.

    private static IEnumerable<string> SourceFiles(string root)
        => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                     && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The website feature's own files, the ones its rules hold to: DreamWeaveWeb*.cs, Web*.cs and LaunchWire.cs.
    /// The plugin's other features (the Skills and Character panels, the Bank page) draw with ImGui themselves.
    /// </summary>
    private static List<string> WebFiles(string dw)
    {
        var files = SourceFiles(dw).Where(p =>
        {
            string n = Path.GetFileName(p);
            return n.StartsWith("DreamWeaveWeb", StringComparison.Ordinal) || n.StartsWith("Web", StringComparison.Ordinal) || n == "LaunchWire.cs";
        }).ToList();
        Check.True(files.Count >= 6, "the website feature's files are found (" + files.Count + ")");
        return files;
    }

    /// <summary>
    /// The text with // and /* */ comments removed. String literals ("...", @"...", $"...") and char
    /// literals are kept as they are, so a "//" inside one is not a comment.
    /// </summary>
    internal static string StripComments(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                sb.Append(' ');
                continue;
            }
            if (c == '"' || (c == '@' && next == '"') || (c == '$' && next == '"') || (c == '$' && next == '@') || (c == '@' && next == '$'))
            {
                int start = i;
                bool verbatim = false;
                while (i < text.Length && text[i] != '"') { if (text[i] == '@') verbatim = true; i++; }
                i++; // the opening quote
                while (i < text.Length)
                {
                    if (verbatim)
                    {
                        if (text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                        if (text[i] == '"') { i++; break; }
                    }
                    else
                    {
                        if (text[i] == '\\') { i += 2; continue; }
                        if (text[i] == '"' || text[i] == '\n') { i++; break; }
                    }
                    i++;
                }
                sb.Append(text, start, Math.Min(i, text.Length) - start);
                continue;
            }
            if (c == '\'')
            {
                int start = i++;
                while (i < text.Length && text[i] != '\'' && text[i] != '\n') i += text[i] == '\\' ? 2 : 1;
                i++;
                sb.Append(text, start, Math.Min(i, text.Length) - start);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>The statement at <paramref name="i"/>: a balanced { } block, or up to the next ';'.</summary>
    private static string StatementAt(string code, int i)
    {
        while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
        if (i < code.Length && code[i] == '{')
        {
            int depth = 0, j = i;
            for (; j < code.Length; j++)
            {
                if (code[j] == '{') depth++;
                else if (code[j] == '}' && --depth == 0) break;
            }
            return code.Substring(i, Math.Min(j + 1, code.Length) - i);
        }
        int semi = code.IndexOf(';', i);
        return code.Substring(i, (semi < 0 ? code.Length : semi + 1) - i);
    }

    /// <summary>The statement after each <paramref name="head"/>.</summary>
    private static List<string> StatementsAfter(string code, string head)
    {
        var found = new List<string>();
        int at = 0;
        while ((at = code.IndexOf(head, at, StringComparison.Ordinal)) >= 0)
        {
            found.Add(StatementAt(code, at + head.Length));
            at += head.Length;
        }
        return found;
    }

    /// <summary>Every lock statement: its target (trimmed) and its body.</summary>
    internal static List<(string Target, string Body)> Locks(string code)
    {
        var found = new List<(string, string)>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(code, @"\block\s*\("))
        {
            int open = m.Index + m.Length - 1, depth = 0, j = open;
            for (; j < code.Length; j++)
            {
                if (code[j] == '(') depth++;
                else if (code[j] == ')' && --depth == 0) break;
            }
            if (j >= code.Length) { found.Add(("<unclosed>", "")); continue; }
            found.Add((code.Substring(open + 1, j - open - 1).Trim(), StatementAt(code, j + 1)));
        }
        return found;
    }

    /// <summary>
    /// A call: an identifier or a member chain (".", "?."), an optional type-argument list, then "(".
    /// Group 1 is the chain; the type arguments are left out of it ("Notify<int>(" is "Notify").
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex CallPattern = new(
        @"(?<![A-Za-z0-9_.?])([A-Za-z_][A-Za-z0-9_]*(?:\s*\??\.\s*[A-Za-z_][A-Za-z0-9_]*)*)\s*(?:<[^<>();{}]*>)?\s*\(");

    /// <summary>
    /// Every call in <paramref name="body"/> that is not a member call on <paramref name="receiver"/>
    /// ("_web.Begin(" is allowed; "Send(", "Helper(", "if (", "x._web.Begin(", "a?.Invoke(", "Notify<int>(" are not).
    /// </summary>
    internal static List<string> CallsNotOn(string body, string receiver)
    {
        var bad = new List<string>();
        var allowed = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(receiver) + @"\.[A-Za-z_][A-Za-z0-9_]*$");
        foreach (System.Text.RegularExpressions.Match m in CallPattern.Matches(body))
        {
            string chain = System.Text.RegularExpressions.Regex.Replace(m.Groups[1].Value, @"\s+", "");
            if (!allowed.IsMatch(chain)) bad.Add(chain + "(");
        }
        return bad;
    }

    private static string Collapse(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"\s+", "");

    private static int CountOf(string text, string needle)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    // DreamWeavePlugin.cs and DreamWeaveSkills.cs own the hooks the web tools share with the bank and the Skills
    // panel: each hands off to its Web* method before its own code, so no gate of theirs comes first.
    private static void HooksHandOff()
    {
        string dw = Path.Combine(ServerProjectsTests.PluginsDir(), "RynthCore.Plugin.RynthDreamWeave");
        string plugin = StripComments(File.ReadAllText(Path.Combine(dw, "DreamWeavePlugin.cs")));
        string skills = StripComments(File.ReadAllText(Path.Combine(dw, "DreamWeaveSkills.cs")));
        (string Code, string Head, string Call)[] hooks =
        {
            (plugin, "partial void ServerLoginComplete()", "WebLoginComplete();"),
            (plugin, "partial void ServerLogout()", "WebLogout();"),
            (plugin, "partial void ServerShutdown()", "WebShutdown();"),
            (plugin, "partial void ServerChatText(string text, int chatType, ref bool eat)", "WebChatText(text, chatType, ref eat);"),
            (plugin, "partial void ServerTick(bool active)", "WebTick();"),
            (skills, "partial void ServerChatCommand(string[] parts, ref bool handled)", "WebChatCommand(parts, ref handled);"),
            (skills, "partial void ServerCommandVerbs(List<string> verbs)", null!),
        };
        foreach (var (code, head, call) in hooks)
        {
            List<string> body = StatementsAfter(code, head);
            Check.Eq(body.Count, 1, head + ": implemented once");
            if (body.Count != 1 || call == null) continue;
            Check.True(body[0].TrimStart('{', ' ', '\n', '\r').StartsWith(call, StringComparison.Ordinal), head + ": " + call + " comes first");
        }
        List<string> verbs = StatementsAfter(skills, "partial void ServerCommandVerbs(List<string> verbs)");
        Check.True(verbs.Count == 1 && verbs[0].Contains("WebCommandVerbs(verbs);", StringComparison.Ordinal), "ServerCommandVerbs adds the web verbs");
        List<string> info = StatementsAfter(plugin, "partial void ServerInfoLines(Action<string> line)");
        Check.True(info.Count == 1 && info[0].Contains("WebInfoLines(line);", StringComparison.Ordinal), "ServerInfoLines adds the web tools' line");
        // The web tools implement none of the hooks they are handed: one implementation each, in the hub files.
        string web = StripComments(File.ReadAllText(Path.Combine(dw, "DreamWeaveWebTools.cs")));
        foreach (string hook in new[] { "ServerLoginComplete", "ServerLogout", "ServerShutdown", "ServerChatText", "ServerTick", "ServerChatCommand", "ServerCommandVerbs", "ServerInfoLines" })
            Check.False(web.Contains("partial void " + hook + "(", StringComparison.Ordinal), "DreamWeaveWebTools.cs leaves " + hook + " to the hub");
    }

    // The draw path makes no host calls. ServerCardActions' catch (render thread) does log through
    // LogTagged once per failure, the shared pattern RenderEmbedded's own catch already follows.
    private static void ThreadRules()
    {
        string plugins = ServerProjectsTests.PluginsDir();
        string dw = Path.Combine(plugins, "RynthCore.Plugin.RynthDreamWeave");
        string buttonsPath = Path.Combine(dw, "DreamWeave", "WebButtons.cs");
        string hostPath = Path.Combine(dw, "DreamWeaveWebTools.cs");
        Check.True(File.Exists(buttonsPath), "DreamWeave\\WebButtons.cs exists");
        Check.True(File.Exists(hostPath), "DreamWeaveWebTools.cs exists");
        if (!File.Exists(buttonsPath) || !File.Exists(hostPath)) return;

        var bareImGui = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z0-9_])ImGui\.");
        foreach (string f in WebFiles(dw))
        {
            string code = StripComments(File.ReadAllText(f));
            string name = Path.GetFileName(f);
            Check.False(code.Contains("SetCurrentContext", StringComparison.Ordinal), name + ": no SetCurrentContext (RenderEmbedded/OnRender own the context)");
            bool isButtons = string.Equals(Path.GetFullPath(f), Path.GetFullPath(buttonsPath), StringComparison.OrdinalIgnoreCase);
            if (!isButtons)
            {
                Check.False(bareImGui.IsMatch(code), name + ": no ImGui. calls outside WebButtons.cs");
                Check.False(code.Contains("SafeImGui.", StringComparison.Ordinal), name + ": no SafeImGui. calls outside WebButtons.cs");
            }
        }

        // StripComments does not model raw string literals: a DreamWeave file with one must fail, not scan
        // wrongly. Only the files this scan parses in depth are held to it (the web tools' files and the two hub
        // files HooksHandOff reads), so another server, or another DreamWeave feature, can't break it.
        foreach (string f in WebFiles(dw).Concat(new[] { Path.Combine(dw, "DreamWeavePlugin.cs"), Path.Combine(dw, "DreamWeaveSkills.cs") }))
            Check.False(File.ReadAllText(f).Contains("\"\"\"", StringComparison.Ordinal), Path.GetFileName(f) + ": no raw string literal (the scan does not support them)");

        var processStart = new System.Text.RegularExpressions.Regex(@"\bProcess\s*\.\s*Start\b");
        foreach (string f in SourceFiles(plugins))
            Check.False(processStart.IsMatch(StripComments(File.ReadAllText(f))), Path.GetFileName(f) + ": no Process.Start under Plugins (ShellExecuteW only)");

        // Monitor would take the launcher's lock without a lock statement, out of the scan's sight.
        var monitor = new System.Text.RegularExpressions.Regex(@"\bMonitor\s*\.");
        foreach (string f in new[] { buttonsPath, hostPath })
            Check.False(monitor.IsMatch(StripComments(File.ReadAllText(f))), Path.GetFileName(f) + ": no Monitor (lock statements only)");

        // The launcher holds no callbacks, so nothing can run host code from inside its lock.
        string launcher = StripComments(File.ReadAllText(Path.Combine(dw, "DreamWeave", "WebLauncher.cs")));
        Check.False(System.Text.RegularExpressions.Regex.IsMatch(launcher, @"\b(Action|Func|delegate)\b"), "WebLauncher.cs: no Action, Func or delegate");

        // WebButtons.cs calls only ImGui/SafeImGui, the launcher (under its lock) and the click it was handed.
        string buttons = StripComments(File.ReadAllText(buttonsPath));
        int draw = buttons.IndexOf("public static void Draw(", StringComparison.Ordinal);
        Check.True(draw >= 0, "WebButtons.Draw exists");
        string drawBody = draw < 0 ? "" : StatementAt(buttons, buttons.IndexOf(')', draw) + 1);
        var drawAllowed = new System.Text.RegularExpressions.Regex(@"^(ImGui\.[A-Za-z]+|SafeImGui\.[A-Za-z]+|click|lock|if|foreach)\($");
        var drawCalls = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in CallPattern.Matches(drawBody))
        {
            string chain = Collapse(m.Groups[1].Value) + "(";
            if (!drawAllowed.IsMatch(chain)) drawCalls.Add(chain);
        }
        Check.Eq(string.Join(" ", drawCalls), "", "WebButtons.Draw calls only ImGui, SafeImGui and click (draw only draws)");
        Check.True(buttons.Contains("ImGui.SmallButton(d.Label + \"##dw_web_\" + d.Id)", StringComparison.Ordinal), "WebButtons.cs: one SmallButton per destination, id-suffixed");
        foreach (string pair in new[] { "Begin", "End", "Push", "Pop" })
            Check.False(System.Text.RegularExpressions.Regex.IsMatch(buttons, @"ImGui\." + pair), "WebButtons.cs: no ImGui." + pair + "* (no paired calls)");
        List<(string Target, string Body)> drawLocks = Locks(buttons);
        Check.Eq(drawLocks.Count, 1, "WebButtons.cs: one lock, to copy Pending and Status out");
        foreach (var (target, body) in drawLocks)
        {
            Check.Eq(target, "l.Gate", "WebButtons.cs: the lock is the launcher's Gate");
            Check.Eq(string.Join(" ", CallsNotOn(body, "l")), "", "WebButtons.cs: only launcher members under the lock");
            Check.Eq(Collapse(body), "{pending=l.Pending!=null;status=l.Status;}", "WebButtons.cs: the lock copies Pending and Status, nothing else");
        }

        string host = StripComments(File.ReadAllText(hostPath));
        List<string> cardHook = StatementsAfter(host, "partial void ServerCardActions()");
        Check.Eq(cardHook.Count, 1, "DreamWeaveWebTools.cs implements ServerCardActions once");
        string clickPath = cardHook.Count == 1 ? cardHook[0] : "";
        Check.True(clickPath.Contains("WebButtons.Draw(_web, Active, _webClick ??= d => RunOnTick(() => StartLaunch(d)));", StringComparison.Ordinal),
                   "the card hook draws through WebButtons.Draw, and a click only queues StartLaunch with RunOnTick");
        Check.Eq(CountOf(host, "StartLaunch("), 3, "StartLaunch: defined, queued by the click, called by the verb (nothing else)");

        // Every lock in the host is on the launcher and holds launcher member calls only.
        List<(string Target, string Body)> hostLocks = Locks(host);
        Check.Eq(hostLocks.Count, 9, "the host takes the launcher's lock in exactly 9 places");
        foreach (var (target, body) in hostLocks)
        {
            Check.Eq(target, "_web.Gate", "every lock in the host is lock (_web.Gate)");
            Check.Eq(string.Join(" ", CallsNotOn(body, "_web")), "", "only _web members under lock (_web.Gate): " + Collapse(body));
        }
        // Each lock body, exactly: launcher calls and reads, nothing else (no host property read either).
        string[] lockBodies =
        {
            "{begun=_web.Begin(d,Environment.TickCount64);status=_web.Status;}",          // StartLaunch
            "_web.Cancel(notSent);",                                                         // StartLaunch, send failed
            "{kind=_web.OnChatLine(text,chatType,Environment.TickCount64,outd,outlaunchCode,out_);status=_web.Status;generation=_web.Generation;}", // ServerChatText
            "_web.SetResult(generation,text);",                                              // SetWebResult
            "{timedOut=_web.TakeTimedOut(Environment.TickCount64);status=_web.Status;}",   // ServerTick
            "status=_web.Status;",                                                           // ServerInfoLines
            "_web.Reset();",                                                                 // ServerLoginComplete
            "_web.Reset();",                                                                 // ServerLogout
            "_web.Reset();",                                                                 // ServerShutdown
        };
        Check.Eq(string.Join("\n", hostLocks.Select(l => Collapse(l.Body))), string.Join("\n", lockBodies), "the host's lock bodies, exactly");
        Check.False(host.Contains("DateTime", StringComparison.Ordinal) || host.Contains("Stopwatch", StringComparison.Ordinal), "nowMs comes from Environment.TickCount64 only");
        Check.True(host.Contains("Environment.TickCount64", StringComparison.Ordinal), "the host uses Environment.TickCount64");

        // The code goes into the launch URL and nowhere else.
        int uses = System.Text.RegularExpressions.Regex.Matches(host, @"\blaunchCode\b").Count;
        Check.Eq(uses, 3, "launchCode: declared, filled by OnChatLine, used once (DreamWeaveWeb.LaunchUrl)");
        Check.Eq(CountOf(host, "out launchCode"), 1, "launchCode is filled only by OnChatLine");
        Check.True(host.Contains("DreamWeaveWeb.LaunchUrl(launchCode, ", StringComparison.Ordinal), "the code's one use is the launch URL");
        // Stage routing: the URL and the allowlist both come from the one world read.
        Check.True(host.Contains("DreamWeaveWeb.LaunchUrl(launchCode, d, world)", StringComparison.Ordinal), "the launch URL is built for the connected world");
        Check.True(host.Contains("DreamWeaveWeb.AllowedHostsFor(world)", StringComparison.Ordinal), "the allowlist is the same world's");
        Check.False(System.Text.RegularExpressions.Regex.IsMatch(host, @"DreamWeaveWeb\.AllowedHosts\s*[,)]"), "the live-only allowlist is never passed directly");

        // The chat hook reads every line through the launcher, whatever the world: no Active before OnChatLine.
        List<string> chatHook = StatementsAfter(host, "private void WebChatText(string text, int chatType, ref bool eat)");
        Check.Eq(chatHook.Count, 1, "DreamWeaveWebTools.cs implements WebChatText once");
        string chat = chatHook.Count == 1 ? chatHook[0] : "";
        int onChat = chat.IndexOf("OnChatLine(", StringComparison.Ordinal);
        Check.True(onChat > 0, "ServerChatText calls OnChatLine");
        // Everything before the lock, exactly: declarations only, so no gate, return or throw comes first.
        System.Text.RegularExpressions.Match firstLock = System.Text.RegularExpressions.Regex.Match(chat, @"\block\s*\(");
        Check.Eq(firstLock.Success ? Collapse(chat.Substring(0, firstLock.Index)) : "<no lock>",
                 "{LaunchLinekind=LaunchLine.NotOurs;try{WebDestination?d;stringlaunchCode;stringstatus;longgeneration;",
                 "ServerChatText: nothing but declarations before the launcher reads the line (a code must be eaten whatever the world is now)");

        // Stale eats the line and does nothing else.
        int stale = host.IndexOf("case LaunchLine.Stale:", StringComparison.Ordinal);
        Check.True(stale >= 0, "the chat hook handles LaunchLine.Stale");
        if (stale >= 0)
        {
            int from = stale + "case LaunchLine.Stale:".Length;
            int end = host.IndexOf("return;", from, StringComparison.Ordinal);
            Check.Eq(end < 0 ? "<no return>" : Collapse(host.Substring(from, end - from)), "eat=true;", "the Stale arm is exactly 'eat = true; return;'");
        }
    }

    private static void MalformedSuccess()
    {
        string head = "[WEB] Launch code: ";
        string long44 = head + Code + "A (single use, expires in 60 seconds)";
        string dotted = head + Code + ".";
        foreach (string l in new[] { long44, dotted })
        {
            Check.True(!Parse(l, 0x05, out _, out _), "TryParse still rejects it: " + l.Length + " chars");
            Check.True(LaunchWire.IsSuccessShaped(l, 0x05), "success-shaped: " + l.Length + " chars");
            Check.False(LaunchWire.IsSuccessShaped(l, 0x02), "a player's copy is not success-shaped");
        }
        Check.True(LaunchWire.IsSuccessShaped("  [WEB] Launch code", 0x05), "the stem alone, past leading space");
        Check.False(LaunchWire.IsSuccessShaped("[WEB] Signing in is turned off.", 0x05), "a refusal is not success-shaped");
        Check.False(LaunchWire.IsSuccessShaped(null, 0x05), "null is not success-shaped");

        foreach (string l in new[] { long44, dotted })
        {
            // Pending: the request takes the line as an unexpected reply; nothing of the code survives.
            var w = new WebLauncher();
            w.Begin(Market, 0);
            var k = w.OnChatLine(l, 0x05, 100, out var dest, out string code, out string msg);
            Check.True(k == LaunchLine.Refused, "pending: a malformed code line is Refused (eaten), not NotOurs");
            Check.True(ReferenceEquals(dest, Market), "pending: the destination is handed back");
            Check.Eq(code, "", "pending: no code");
            Check.Eq(msg, "an unexpected reply (is RynthDreamWeave up to date?)", "pending: the fixed message");
            Check.Eq(w.Status, "Market: DreamWeave sent an unexpected reply (is RynthDreamWeave up to date?)", "pending: the status");
            Check.True(w.Pending == null, "pending: cleared");
            Check.False(w.Status.Contains(Code.Substring(0, 12), StringComparison.Ordinal) || msg.Contains(Code.Substring(0, 12), StringComparison.Ordinal), "pending: no code in status or message");

            // A player's copy while pending is still not ours.
            var p = new WebLauncher();
            p.Begin(Market, 0);
            Check.True(p.OnChatLine(l, 0x02, 100, out _, out _, out _) == LaunchLine.NotOurs, "player copy (0x02): not ours");
            Check.True(p.Pending != null, "player copy leaves the request pending");

            // Not pending: Stale inside the window, NotOurs after it.
            var s = new WebLauncher();
            s.Begin(Market, 0);
            s.TakeTimedOut(10_000);
            Check.True(s.OnChatLine(l, 0x05, 20_000, out dest, out code, out msg) == LaunchLine.Stale, "not pending, inside the window: Stale");
            Check.True(dest == null && code == "" && msg == "", "Stale carries nothing");
            Check.True(s.OnChatLine(l, 0x05, 70_001, out _, out _, out _) == LaunchLine.NotOurs, "after the window: NotOurs");
            Check.True(new WebLauncher().OnChatLine(l, 0x05, 5, out _, out _, out _) == LaunchLine.NotOurs, "no Begin ever: NotOurs");
        }

        // An empty refusal is an unexpected reply too.
        foreach (string l in new[] { "[WEB] ", "[WEB]    " })
        {
            var e = new WebLauncher();
            e.Begin(Market, 0);
            Check.True(e.OnChatLine(l, 0x05, 10, out _, out _, out string msg) == LaunchLine.Refused, "empty refusal: Refused");
            Check.Eq(msg, "an unexpected reply (is RynthDreamWeave up to date?)", "empty refusal: the fixed message");
        }
    }

    private static void ResultGeneration()
    {
        var w = new WebLauncher();
        w.Begin(Market, 0);
        Check.True(w.OnChatLine(Line, 0x05, 100, out _, out _, out _) == LaunchLine.Code, "request 1: code");
        long gen1 = w.Generation;
        Check.True(w.Begin(DreamWeaveWeb.ById("suit")!, 3_100) == LaunchBegin.Started, "request 2, 3 s later");
        Check.True(w.OnChatLine(Line, 0x05, 3_200, out _, out _, out _) == LaunchLine.Code, "request 2: code");
        long gen2 = w.Generation;
        Check.True(gen2 != gen1, "each request has its own generation");
        Check.Eq(w.Status, "Opening Suit Builder...", "request 2 is opening");
        w.SetResult(gen1, "x");
        Check.Eq(w.Status, "Opening Suit Builder...", "request 1's late answer leaves the status alone");
        w.SetResult(gen2, "y");
        Check.Eq(w.Status, "y", "request 2's answer sets it");

        // Pending, or after a Reset (a relog), an answer is ignored too.
        var p = new WebLauncher();
        p.Begin(Market, 0);
        long g = p.Generation;
        p.SetResult(g, "x");
        Check.Eq(p.Status, "Asking DreamWeave for a login code for Market...", "ignored while a request is pending");
        p.OnChatLine(Line, 0x05, 100, out _, out _, out _);
        g = p.Generation;
        p.Reset();
        p.SetResult(g, "x");
        Check.Eq(p.Status, "", "an answer from before a Reset is ignored");

        // A refused Begin moves nothing on.
        var r = new WebLauncher();
        r.Begin(Market, 0);
        r.OnChatLine(Line, 0x05, 100, out _, out _, out _);
        long rg = r.Generation;
        Check.True(r.Begin(Market, 200) == LaunchBegin.TooSoon, "too soon");
        r.SetResult(rg, "opened");
        Check.Eq(r.Status, "opened", "a TooSoon Begin keeps the open's generation");
    }

    private static void ScanHelpers()
    {
        string src = "int a = 1; // lock (x) { Send(); }\nstring u = \"https://x//y\"; /* Reply.Ok() */ char q = '\"'; string v = @\"a\"\"//b\"; lock (_web.Gate) b = 2;\n";
        string code = StripComments(src);
        Check.False(code.Contains("Send", StringComparison.Ordinal) || code.Contains("Reply", StringComparison.Ordinal), "comments removed");
        Check.True(code.Contains("\"https://x//y\"", StringComparison.Ordinal), "// inside a string kept");
        Check.True(code.Contains("@\"a\"\"//b\"", StringComparison.Ordinal), "// inside a verbatim string kept");
        Check.True(code.Contains("lock (_web.Gate) b = 2;", StringComparison.Ordinal), "code after a char literal holding a quote kept");

        var locks = Locks("lock(_web.Gate){ Send(\"x\"); } lock ( other ) _web.Begin(); lock (_web.Gate) { _web.Begin(d, Environment.TickCount64); if (a) b(); }");
        Check.Eq(locks.Count, 3, "every lock found, with or without a space");
        Check.Eq(locks[0].Target, "_web.Gate", "target");
        Check.Eq(string.Join(" ", CallsNotOn(locks[0].Body, "_web")), "Send(", "a bare call under the lock is caught");
        Check.Eq(locks[1].Target, "other", "another target is reported as it is");
        Check.Eq(string.Join(" ", CallsNotOn(locks[2].Body, "_web")), "if( b(", "keywords and helpers are not launcher calls");
        Check.Eq(string.Join(" ", CallsNotOn("x._web.Begin(); _web.Begin(); _web.Gate.Foo();", "_web")), "x._web.Begin( _web.Gate.Foo(", "only a direct _web member call is allowed");
        Check.Eq(string.Join(" ", CallsNotOn("a?.Invoke(x); Notify<int>(y); _web.Begin();", "_web")), "a?.Invoke( Notify(", "null-conditional and generic calls are calls");
        Check.Eq(string.Join(" ", CallsNotOn("_web?.Begin(); _web.Begin<int>();", "_web")), "_web?.Begin(", "_web?. is not the allowed form; a generic _web member call is");
    }

    private const string Code = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_AbCde";   // 43 chars
    private const string Line = "[WEB] Launch code: " + Code + " (single use, expires in 60 seconds)";

    private static bool Parse(string? text, int type, out string code, out string refusal)
        => LaunchWire.TryParse(text, type, out code, out refusal);

    private static void PlayerCopy()
    {
        Check.Eq(Code.Length, LaunchWire.CodeLength, "fixture code is 43 chars");

        foreach (int type in new[] { 0x02, 0x03, 0x08, 0x0C, 0x13 })
            Check.True(!Parse(Line, type, out _, out _), "player-authored chat type 0x" + type.ToString("X2") + " refused");

        Check.True(!Parse("Bob tells you, " + Line, 0x05, out _, out _), "a tell wrapper with a system type refused");
        Check.True(!Parse("hello " + Line, 0x05, out _, out _), "the lead mid-line refused");

        Check.True(Parse(Line, 0x05, out string code, out string refusal), "valid line, system type");
        Check.Eq(code, Code, "the code");
        Check.Eq(refusal, "", "no refusal on success");
        Check.True(Parse(Line, -1, out code, out _), "unknown type: the anchor alone protects");
        Check.Eq(code, Code, "the code (type -1)");
        Check.True(!Parse(null, 0x05, out _, out _), "null refused");
        Check.True(!Parse("", 0x05, out _, out _), "empty refused");
    }

    private static void UrlFromConstants()
    {
        string head = "[WEB] Launch code: ";
        Check.True(!Parse(head + Code.Substring(1) + " (single use)", 0x05, out _, out _), "42-char code refused");
        Check.True(!Parse(head + Code + "A (single use)", 0x05, out _, out _), "44-char run (no space after 43) refused");
        Check.True(!Parse(head + Code + "&to=//evil.com", 0x05, out _, out _), "code followed by &to= refused");
        Check.True(!Parse(head + Code, 0x05, out _, out _), "no trailing space refused");
        Check.True(!Parse(head + Code.Substring(0, 42) + "! (single use)", 0x05, out _, out _), "bad code char refused");
        Check.True(!Parse(head + Code.Substring(0, 42) + "% (single use)", 0x05, out _, out _), "percent in code refused");

        // None of the malformed success lines may leak out as a refusal either.
        Check.True(!Parse(head + Code.Substring(1) + " x", 0x05, out _, out string r1) && r1 == "", "malformed success is not a refusal");

        WebDestination suit = DreamWeaveWeb.ById("suit")!;
        string url = DreamWeaveWeb.LaunchUrl(Code, suit);
        Check.True(url.StartsWith(DreamWeaveWeb.SiteBase + "/launch?code=", StringComparison.Ordinal), "url starts with the base");
        Check.Eq(url, DreamWeaveWeb.SiteBase + "/launch?code=" + Code + "&to=%2Ftools%2Fsuit", "exact suit url");
        Check.True(url.EndsWith("%2Ftools%2Fsuit", StringComparison.Ordinal), "ends with the escaped path");
        Check.Eq(url.Split("&to=").Length - 1, 1, "exactly one &to=");
        Check.Eq(DreamWeaveWeb.LaunchUrl(Code, DreamWeaveWeb.ById("abilities")!),
            DreamWeaveWeb.SiteBase + "/launch?code=" + Code + "&to=%2Fplay%2Fabilities", "abilities opens the live class ability page");

        // The stage world's code only redeems on the stage site, and the stage panel may only go there.
        Check.Eq(DreamWeaveWeb.LaunchUrl(Code, DreamWeaveWeb.ById("abilities")!, "DreamWeave-Stage"),
            "https://www-stage.acdreamweave.com/launch?code=" + Code + "&to=%2Fplay%2Fabilities", "stage world -> stage site");
        Check.True(DreamWeaveWeb.IsStageWorld(" dreamweave-stage "), "stage match is trimmed and case-insensitive");
        foreach (string? live in new[] { "DreamWeave", "Dream Weave", "", null, "DreamWeave-Stage2" })
        {
            Check.Eq(DreamWeaveWeb.SiteBaseFor(live), DreamWeaveWeb.SiteBase, "'" + live + "' -> live site");
            Check.True(DreamWeaveWeb.AllowedHostsFor(live) == DreamWeaveWeb.AllowedHosts, "'" + live + "' -> live allowlist");
        }
        foreach (WebDestination d in DreamWeaveWeb.Destinations)
        {
            string su = DreamWeaveWeb.LaunchUrl(Code, d, DreamWeaveWeb.StageWorldName);
            Check.True(ServerWebOpen.IsAllowedUrl(su, DreamWeaveWeb.AllowedHostsFor(DreamWeaveWeb.StageWorldName)), d.Id + ": stage url passes the stage allowlist");
            Check.True(!ServerWebOpen.IsAllowedUrl(su, DreamWeaveWeb.AllowedHosts), d.Id + ": stage url refused by the live allowlist");
            Check.True(!ServerWebOpen.IsAllowedUrl(DreamWeaveWeb.LaunchUrl(Code, d), DreamWeaveWeb.StageAllowedHosts), d.Id + ": live url refused by the stage allowlist");
        }

        foreach (WebDestination d in DreamWeaveWeb.Destinations)
        {
            string u = DreamWeaveWeb.LaunchUrl(Code, d);
            Check.True(ServerWebOpen.IsAllowedUrl(u, DreamWeaveWeb.AllowedHosts), d.Id + ": url passes the allowlist");
            Check.True(d.Path.StartsWith("/", StringComparison.Ordinal) && !d.Path.StartsWith("//", StringComparison.Ordinal), d.Id + ": path is rooted, not //");
            Check.True(d.Path.IndexOfAny(new[] { '?', '#', '\\' }) < 0, d.Id + ": no ? # or backslash in path");
            Check.True(d.Label.Length > 0 && d.Tooltip.Length > 0, d.Id + ": label and tooltip");
        }
        Check.True(DreamWeaveWeb.ById("SUIT") == suit, "ById is case-insensitive");
        Check.True(DreamWeaveWeb.ById("nope") == null && DreamWeaveWeb.ById(null) == null && DreamWeaveWeb.ById("") == null, "unknown id -> null");
        Check.Eq(LaunchWire.RequestFor(suit), "/weblogin", "request is /weblogin");
    }

    private static void IdsAreFree()
    {
        var seen = new HashSet<string>();
        string[] serverIds = ServerPluginIds();
        Check.True(serverIds.Length > 0, "found the server plugin ids");
        foreach (WebDestination d in DreamWeaveWeb.Destinations)
        {
            Check.True(d.Id.Length >= 1 && d.Id.Length <= 16 && d.Id.All(c => c >= 'a' && c <= 'z'), d.Id + ": 1-16 chars [a-z]");
            Check.True(Array.IndexOf(ServerPackCommands.SharedVerbs, d.Id) < 0, d.Id + ": not a shared verb");
            Check.True(Array.IndexOf(serverIds, d.Id) < 0, d.Id + ": not a server plugin id");
            Check.True(seen.Add(d.Id), d.Id + ": unique");
        }
    }

    private static void Alphabet()
    {
        const string b64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        Check.Eq(b64.Length, 64, "64 base64url chars");
        foreach (char c in b64)
            Check.True(LaunchWire.IsCodeChar(c), "accepted: " + c);

        char[] bad = { '+', '/', '=', '&', '?', '#', '%', '.', ' ', '\t', '!', '~', '\u00e9', '\u0000' };
        foreach (char c in bad)
        {
            string name = "U+" + ((int)c).ToString("X4");
            Check.True(!LaunchWire.IsCodeChar(c), "IsCodeChar rejects " + name);
            foreach (int idx in new[] { 0, 21, 42 })
            {
                char[] chars = Code.ToCharArray();
                chars[idx] = c;
                string line = "[WEB] Launch code: " + new string(chars) + " (single use, expires in 60 seconds)";
                Check.True(!Parse(line, 0x05, out string code, out string refusal), "refused: " + name + " at " + idx);
                Check.Eq(code, "", "no code: " + name + " at " + idx);
                Check.Eq(refusal, "", "no refusal: " + name + " at " + idx);
            }
        }

        // Round trip of a code holding both '-' and '_'.
        string dashed = "-_" + Code.Substring(2);
        Check.True(Parse("[WEB] Launch code: " + dashed + " (single use, expires in 60 seconds)", 0x05, out string got, out _), "dash/underscore code parses");
        Check.Eq(got, dashed, "dash/underscore code intact");
        Check.True(DreamWeaveWeb.LaunchUrl(got, DreamWeaveWeb.ById("market")!).Contains("code=" + dashed + "&to=", StringComparison.Ordinal), "url carries the code verbatim");

        // The stem alone marks the success shape: malformed, it is dropped, never a refusal.
        foreach (string l in new[] { "[WEB] Launch code:" + Code, "[WEB] Launch code " + Code, "[WEB] Launch code", "[WEB] Launch codes " + Code })
        {
            Check.True(!Parse(l, 0x05, out string c2, out string r2), "malformed success-shaped line dropped: " + l);
            Check.Eq(c2, "", "no code: " + l);
            Check.Eq(r2, "", "no refusal text: " + l);
        }
    }

    private static void Refusals()
    {
        string[] lines =
        {
            "[WEB] Signing in to the website from the game is turned off on this server.",
            "[WEB] The website sign-in service is not available right now.",
            "[WEB] You are requesting website sign-in codes too quickly. Wait a minute and try again.",
        };
        foreach (string l in lines)
        {
            Check.True(Parse(l, 0x05, out string code, out string refusal), "refusal parses: " + l);
            Check.Eq(code, "", "no code in a refusal");
            Check.Eq(refusal, l.Substring(LaunchWire.ReplyLead.Length), "refusal text after the lead");
            Check.True(!Parse(l, 0x02, out _, out _), "a player's copy of a refusal is ignored");
        }
        Check.True(!Parse("You say, \"hi\"", 0x05, out _, out _), "other chat ignored");
        Check.True(!Parse("[WEBX] Launch code: " + Code, 0x05, out _, out _), "lookalike lead ignored");
    }

    private static WebDestination Market => DreamWeaveWeb.ById("market")!;

    private static void RepliesOnlyWhileAsked()
    {
        var w = new WebLauncher();
        Check.True(w.OnChatLine(Line, 0x05, 0, out _, out _, out _) == LaunchLine.NotOurs, "nothing pending: not ours");

        Check.True(w.Begin(Market, 1000) == LaunchBegin.Started, "begin");
        Check.Eq(w.Status, "Asking DreamWeave for a login code for Market...", "asking status");
        var k = w.OnChatLine(Line, 0x05, 1000 + 9_999, out var dest, out string code, out string msg);
        Check.True(k == LaunchLine.Code, "line at +9,999 ms is the code");
        Check.Eq(code, Code, "the code");
        Check.Eq(msg, "", "no message on a code");
        Check.True(ReferenceEquals(dest, Market), "destination handed back");
        Check.True(w.Pending == null, "code clears pending");
        Check.True(w.OnChatLine(Line, 0x05, 1000 + 9_999, out dest, out code, out msg) == LaunchLine.Stale, "an identical second line is stale: eaten, opens nothing");
        Check.True(dest == null && code == "" && msg == "", "no outputs on Stale");

        // Timeout at exactly +10,000.
        var t = new WebLauncher();
        t.Begin(Market, 50_000);
        Check.True(t.OnChatLine(Line, 0x05, 60_000, out _, out _, out _) == LaunchLine.Stale, "a valid code at +10,000 ms is late: stale, not a code");
        Check.True(t.TakeTimedOut(59_999) == null, "not timed out at +9,999");
        Check.True(ReferenceEquals(t.TakeTimedOut(60_000), Market), "timed out once at +10,000");
        Check.True(t.TakeTimedOut(60_001) == null, "and only once");
        Check.True(t.Pending == null, "timeout clears pending");
        Check.Eq(t.Status, "No reply from DreamWeave to /weblogin within 10 s, so nothing was opened.", "timeout status");

        // Refusal clears pending.
        var f = new WebLauncher();
        f.Begin(Market, 0);
        string refusal = "[WEB] The website sign-in service is not available right now.";
        Check.True(f.OnChatLine(refusal, 0x05, 10, out dest, out code, out msg) == LaunchLine.Refused, "refusal");
        Check.Eq(msg, "The website sign-in service is not available right now.", "refusal message");
        Check.Eq(code, "", "no code on refusal");
        Check.Eq(f.Status, "Market: DreamWeave said: The website sign-in service is not available right now.", "refusal status");
        Check.True(f.Pending == null, "refusal clears pending");

        // A player's copy and unrelated chat leave the request pending.
        var p = new WebLauncher();
        p.Begin(Market, 0);
        Check.True(p.OnChatLine(Line, 0x02, 5, out _, out _, out _) == LaunchLine.NotOurs, "player copy not ours");
        Check.True(p.OnChatLine("You say, \"hi\"", 0x05, 5, out _, out _, out _) == LaunchLine.NotOurs, "other chat not ours");
        Check.True(p.Pending != null, "still pending");

        // Reset clears pending and status.
        p.Reset();
        Check.True(p.Pending == null, "reset clears pending");
        Check.Eq(p.Status, "", "reset clears status");
        Check.True(p.OnChatLine(Line, 0x05, 5, out _, out _, out _) == LaunchLine.Stale, "after reset: a code is stale, still eaten");
    }

    private static void OneAtATime()
    {
        var w = new WebLauncher();
        Check.True(w.Begin(Market, 100_000) == LaunchBegin.Started, "first begin");
        Check.True(w.Begin(DreamWeaveWeb.ById("suit")!, 100_001) == LaunchBegin.Busy, "second while pending: busy");
        Check.True(w.Begin(DreamWeaveWeb.ById("suit")!, 100_000 + 5_000) == LaunchBegin.Busy, "busy wins over too-soon/free");
        Check.True(ReferenceEquals(w.Pending, Market), "first request still the pending one");

        w.Cancel("Could not send /weblogin.");
        Check.True(w.Pending == null, "cancel clears pending");
        Check.Eq(w.Status, "Could not send /weblogin.", "cancel status");
        Check.True(w.Begin(Market, 100_000 + 2_999) == LaunchBegin.TooSoon, "within 3 s of the last begin: too soon");
        Check.True(w.Pending == null, "too soon starts nothing");
        Check.True(w.Begin(Market, 100_000 + 3_000) == LaunchBegin.Started, "at 3 s: started");

        // Never stuck: a timeout frees the slot.
        Check.True(w.TakeTimedOut(103_000 + 10_000) != null, "timeout");
        Check.True(w.Begin(Market, 113_000) == LaunchBegin.Started, "begin after timeout");
    }

    private static void CodeNeverInStatus()
    {
        var w = new WebLauncher();
        w.Begin(Market, 0);
        Check.True(!w.Status.Contains(Code), "status while asking");
        var k = w.OnChatLine(Line, 0x05, 100, out _, out string code, out string msg);
        Check.True(k == LaunchLine.Code, "code flow");
        Check.Eq(code.Length, 43, "43-char code returned");
        Check.True(!w.Status.Contains(Code), "status after code");
        Check.True(!msg.Contains(Code), "message after code");
        Check.True(!w.Status.Contains(code.Substring(0, 12)), "no code fragment in status");

        // Timeout and refusal paths too.
        var t = new WebLauncher();
        t.Begin(Market, 0);
        t.TakeTimedOut(10_000);
        Check.True(!t.Status.Contains(Code), "timeout status");
        var f = new WebLauncher();
        f.Begin(Market, 0);
        f.OnChatLine("[WEB] nope", 0x05, 1, out _, out _, out msg);
        Check.True(!f.Status.Contains(Code) && !msg.Contains(Code), "refusal status and message");
    }

    private static void CodeShapedRefusal()
    {
        var w = new WebLauncher();
        w.Begin(Market, 0);
        var k = w.OnChatLine("[WEB] Try again later: " + Code, 0x05, 10, out var dest, out string code, out string msg);
        Check.True(k == LaunchLine.Refused, "still a refusal, so the caller eats the line");
        Check.True(ReferenceEquals(dest, Market), "destination handed back");
        Check.Eq(code, "", "no code");
        Check.True(!msg.Contains(Code), "message has no code");
        Check.True(!w.Status.Contains(Code), "status has no code");
        Check.Eq(msg, "an unexpected reply (is RynthDreamWeave up to date?)", "fixed replacement text");
        Check.Eq(w.Status, "Market: DreamWeave sent an unexpected reply (is RynthDreamWeave up to date?)", "status from the replacement");
        Check.True(w.Pending == null, "pending cleared");

        // A long run of code chars anywhere, and a 42-char run (below the limit) left alone.
        var w2 = new WebLauncher();
        w2.Begin(Market, 0);
        w2.OnChatLine("[WEB] x " + Code + Code + " y", 0x05, 10, out _, out _, out msg);
        Check.Eq(msg, "an unexpected reply (is RynthDreamWeave up to date?)", "longer run replaced");
        var w3 = new WebLauncher();
        w3.Begin(Market, 0);
        w3.OnChatLine("[WEB] id " + Code.Substring(1) + " ok", 0x05, 10, out _, out _, out msg);
        Check.Eq(msg, "id " + Code.Substring(1) + " ok", "42-char run is ordinary text");
    }

    private static void SelfExpiry()
    {
        var w = new WebLauncher();
        w.Begin(Market, 0);
        Check.True(w.Begin(Market, 9_999) == LaunchBegin.Busy, "before the deadline: busy");
        Check.True(w.Begin(Market, 10_001) != LaunchBegin.Busy, "past the deadline: not busy");

        var x = new WebLauncher();
        x.Begin(Market, 0);
        Check.True(x.Begin(DreamWeaveWeb.ById("suit")!, 10_001) == LaunchBegin.Started, "expired, then started as normal");
        Check.True(x.Pending!.Id == "suit", "the new request is the pending one");
        Check.True(x.TakeTimedOut(10_001) == null, "the new request is not timed out yet");
        Check.Eq(x.TakeTimedOut(20_001)?.Id ?? "none", "suit", "and it times out at its own deadline");
    }

    private static void ResetKeepsThrottle()
    {
        var w = new WebLauncher();
        Check.True(w.Begin(Market, 1_000) == LaunchBegin.Started, "begin");
        w.Reset();
        Check.True(w.Pending == null, "reset clears pending");
        Check.True(w.Begin(Market, 1_001) == LaunchBegin.TooSoon, "throttle survives a relog");
    }

    private static void LateCodeIsStale()
    {
        var w = new WebLauncher();
        w.Begin(Market, 0);
        var k = w.OnChatLine(Line, 0x05, 10_500, out var dest, out string code, out string msg);
        Check.True(k == LaunchLine.Stale, "valid code at +10,500 ms is stale");
        Check.True(dest == null && code == "" && msg == "", "stale carries nothing");
        Check.True(w.Pending != null, "stale does not clear pending");
        Check.True(!w.Status.Contains(Code), "status has no code");
        Check.True(ReferenceEquals(w.TakeTimedOut(10_500), Market), "timeout still fires once");
        Check.True(w.TakeTimedOut(10_501) == null, "and only once");
        Check.Eq(w.Status, "No reply from DreamWeave to /weblogin within 10 s, so nothing was opened.", "timeout status intact");
        Check.True(w.OnChatLine(Line, 0x05, 20_000, out dest, out code, out _) == LaunchLine.Stale, "still stale after the timeout was taken");
        Check.True(dest == null && code == "", "no destination or code");
        Check.True(w.OnChatLine(Line, 0x05, 69_999, out _, out _, out _) == LaunchLine.Stale, "last stale ms (+69,999)");
        Check.True(w.OnChatLine(Line, 0x05, 70_001, out _, out _, out _) == LaunchLine.NotOurs, "at +70,001 ms: not ours");
        Check.True(w.OnChatLine("[WEB] some refusal", 0x05, 20_000, out _, out _, out _) == LaunchLine.NotOurs, "a late refusal is not ours");
        Check.True(w.OnChatLine(Line, 0x02, 20_000, out _, out _, out _) == LaunchLine.NotOurs, "a late player copy is not ours");

        var n = new WebLauncher();
        Check.True(n.OnChatLine(Line, 0x05, 10_500, out _, out _, out _) == LaunchLine.NotOurs, "no Begin ever: not ours");

        // A consumed request's window persists: a repeat is eaten.
        var c = new WebLauncher();
        c.Begin(Market, 0);
        c.OnChatLine(Line, 0x05, 100, out _, out _, out _);
        Check.True(c.OnChatLine(Line, 0x05, 10_500, out _, out _, out _) == LaunchLine.Stale, "after a consumed code: a repeat is stale");

        // A new Begin never ends the old stale window: the new request takes a code while pending,
        // and the window still covers the old code once the new request is gone.
        var b = new WebLauncher();
        b.Begin(Market, 0);
        b.TakeTimedOut(10_000);
        b.Begin(Market, 20_000);
        Check.True(b.OnChatLine(Line, 0x05, 20_100, out _, out _, out _) == LaunchLine.Code, "new request takes the code while pending");
        Check.True(b.OnChatLine(Line, 0x05, 20_200, out _, out _, out _) == LaunchLine.Stale, "then a further valid code is stale");

        // A refused Begin does not extend the stale window.
        var t = new WebLauncher();
        t.Begin(Market, 0);
        t.Cancel("send failed");   // Pending would answer Busy; TooSoon needs a free slot
        Check.True(t.Begin(Market, 2_999) == LaunchBegin.TooSoon, "second begin inside the throttle is refused");
        Check.True(t.OnChatLine(Line, 0x05, 70_000, out _, out _, out _) == LaunchLine.NotOurs, "a refused Begin leaves the window anchored at the first");

        // A Busy Begin does not extend the stale window either.
        var y = new WebLauncher();
        y.Begin(Market, 0);
        Check.True(y.Begin(Market, 9_999) == LaunchBegin.Busy, "second begin while pending is Busy");
        y.Cancel("send failed");
        Check.True(y.OnChatLine(Line, 0x05, 70_000, out _, out _, out _) == LaunchLine.NotOurs, "a Busy Begin leaves the window anchored at the first");

        // Window upper boundary.
        Check.Eq(LaunchWire.CodeLifetimeMs, 60_000L, "code lifetime");
        var u = new WebLauncher();
        u.Begin(Market, 0);
        Check.True(u.OnChatLine(Line, 0x05, 69_999, out _, out _, out _) == LaunchLine.Stale, "+69,999 stale");
        Check.True(u.OnChatLine(Line, 0x05, 70_000, out _, out _, out _) == LaunchLine.NotOurs, "+70,000 not ours");
    }

    private static void ProfileKeys()
    {
        string? a = WebProfile.Key("dreamweave", "Alice");
        string? b = WebProfile.Key("dreamweave", "Bob");
        Check.True(a != null && b != null, "keys exist");
        Check.True(a != b, "different accounts -> different keys");
        Check.Eq(WebProfile.Key("dreamweave", " alice "), a, "trimmed and case-folded");
        Check.Eq(WebProfile.Key("dreamweave", "ALICE"), a, "case-folded");
        Check.Eq(WebProfile.Key("dreamweave", "Alice"), a, "stable across calls");
        Check.True(WebProfile.Key("coldeve", "Alice") != a, "same account, other server -> different key");
        Check.True(a!.StartsWith("dreamweave/", StringComparison.Ordinal), "prefixed by the server id");
        string[] accounts = { "Alice", "Bob", "a", "x y", "caf" + (char)0xE9, "A-very-long-account-name-0123456789-0123456789-0123456789" };
        foreach (string acct in accounts)
        {
            string? k = WebProfile.Key("dreamweave", acct);
            Check.True(ServerWebOpen.IsValidProfileKey(k), "valid key for " + acct.Length + " chars");
            // A 1-2 char account could appear in the server id or the hex by chance; the rest cannot.
            if (acct.Length >= 3) Check.True(!k!.Contains(acct.Trim().ToLowerInvariant()), "key never contains the account text");
        }
        Check.Eq(WebProfile.Key("dreamweave", "alice"), "dreamweave/67b613ccd746148225de235baae58eab", "key format is frozen (changing it orphans saved web logins)");
        Check.Eq(WebProfile.Key("coldeve", "alice"), "coldeve/1101ebeaa4456aba9ca33cd292d7b99c", "key format is frozen (changing it orphans saved web logins)");
        Check.True(WebProfile.Key("DreamWeave", "alice") == null, "uppercase server id -> null");
        Check.True(WebProfile.Key("dream/weave", "alice") == null, "slash in server id -> null");
        Check.True(WebProfile.Key("", "alice") == null, "empty server id -> null");
        Check.True(WebProfile.Key("dreamweave", null) == null, "null account -> null");
        Check.True(WebProfile.Key("dreamweave", "") == null, "empty account -> null");
        Check.True(WebProfile.Key("dreamweave", "   ") == null, "blank account -> null");
    }

    private static void OverlappingRequests()
    {
        // Scenario 1: request 1 times out, request 2 begins, request 1's late code arrives.
        var w = new WebLauncher();
        w.Begin(Market, 0);
        w.TakeTimedOut(10_000);
        Check.True(w.Begin(Market, 13_000) == LaunchBegin.Started, "request 2");
        Check.True(w.OnChatLine(Line, 0x05, 14_000, out _, out _, out _) == LaunchLine.Code, "the late code is taken by request 2 (indistinguishable)");
        Check.True(w.OnChatLine(Line, 0x05, 15_000, out var dest, out string code, out string msg) == LaunchLine.Stale, "request 2's own reply is stale, never chat");
        Check.True(dest == null && code == "" && msg == "", "stale carries nothing");

        // Scenario 2: request 1 expires, request 2 begins and is cancelled, the late code arrives.
        var c = new WebLauncher();
        c.Begin(Market, 0);
        c.TakeTimedOut(10_000);
        c.Begin(Market, 13_000);
        c.Cancel("Could not send /weblogin.");
        Check.True(c.OnChatLine(Line, 0x05, 14_000, out _, out _, out _) == LaunchLine.Stale, "after cancel: stale");

        // Reset while pending, then the code.
        var r = new WebLauncher();
        r.Begin(Market, 0);
        r.Reset();
        Check.True(r.OnChatLine(Line, 0x05, 5_000, out _, out _, out _) == LaunchLine.Stale, "after reset while pending: stale");
        Check.True(r.OnChatLine("[WEB] some refusal", 0x05, 5_000, out _, out _, out _) == LaunchLine.NotOurs, "refusals outside a request stay not ours");

        // Cancel right after Begin: the ask may have been sent anyway.
        var k = new WebLauncher();
        k.Begin(Market, 0);
        k.Cancel("x");
        Check.True(k.OnChatLine(Line, 0x05, 2_000, out _, out _, out _) == LaunchLine.Stale, "cancelled request's code is stale");
    }

    private static string[] ServerPluginIds()
    {
        var ids = new List<string>();
        foreach (string p in Directory.GetDirectories(ServerProjectsTests.PluginsDir(), "RynthCore.Plugin.Rynth*"))
        {
            string csproj = Path.Combine(p, Path.GetFileName(p) + ".csproj");
            if (!File.Exists(csproj)) continue;
            string id = XDocument.Load(csproj).Descendants("RynthPluginServerId").FirstOrDefault()?.Value.Trim() ?? "";
            if (id.Length > 0) ids.Add(id);
        }
        return ids.ToArray();
    }
}
