using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RynthCore.Plugin.ServerPack;
using RynthCore.RynthAiTests.Harness;

namespace RynthServerPack.Tests;

// The engine web panel's answers as PanelResult, and the one open the tick waits on (ServerWebPanel.cs),
// against a fake engine. The plugin's own engine calls (ServerPackWeb.cs) are checked by source scan.
internal static class ServerWebPanelTests
{
    public static void Register(Runner r)
    {
        r.Add("web panel: every Open return code maps to a PanelResult", OpenCodes);
        r.Add("web panel: every state maps to a PanelResult", States);
        r.Add("web panel: only a refusal before navigating falls back to the browser", Fallback);
        r.Add("web panel: a refusal answers at once, exactly once", SyncAnswers);
        r.Add("web panel: a queued open is answered by the tick's poll, exactly once", PollAnswers);
        r.Add("web panel: a queued open that never settles times out, neutral", Timeout);
        r.Add("web panel: Close closes only our own handle and answers a waiting open", CloseOwnHandle);
        r.Add("web panel: engine exceptions and a throwing answer never escape", Exceptions);
        r.Add("web panel: SDK references sit in NoInlining helpers behind both catches, no locks", SourceRules);
    }

    private const int S_None = 0, S_Starting = 1, S_Loading = 2, S_Ready = 3, S_Closed = 4, S_Failed = 5, S_Superseded = 6;
    private const int F_Visible = 1, F_Focused = 2, F_OpenedInBrowser = 4;

    private sealed class FakeEngine : IWebPanelEngine
    {
        public bool Has = true;
        public int OpenReturns = 7;
        public int Status = S_Starting, Flags, Error;
        public uint Nav;
        public bool ThrowOnOpen, ThrowOnState, ThrowOnHas;
        public readonly List<int> Closed = new();
        public readonly List<string> Opens = new();
        public int StateCalls;

        public bool HasPanel() => ThrowOnHas ? throw new InvalidOperationException("has") : Has;

        public int Open(string url, string[] allowlist, string profileKey, string title)
        {
            if (ThrowOnOpen) throw new MissingMethodException("open");
            Opens.Add(string.Join(",", allowlist) + "|" + profileKey + "|" + title);
            return OpenReturns;
        }

        public int GetState(int handle, out int flags, out int error, out uint navigationCount)
        {
            StateCalls++;
            if (ThrowOnState) throw new TypeLoadException("state");
            flags = Flags; error = Error; navigationCount = Nav;
            return Status;
        }

        public int Close(int handle) { Closed.Add(handle); return 1; }
    }

    private static readonly WebOpenRequest Req = new("https://www.acdreamweave.com/x?code=secret",
        new[] { "www.acdreamweave.com" }, "dreamweave/0123456789abcdef0123456789abcdef", "DreamWeave");

    private static void OpenCodes()
    {
        Check.Eq(ServerWebPanel.FromOpenCode(1), PanelResult.Pending, "handle 1 is queued, not opened");
        Check.Eq(ServerWebPanel.FromOpenCode(12345), PanelResult.Pending, "any handle > 0 is queued");
        Check.Eq(ServerWebPanel.FromOpenCode(-1), PanelResult.BadArgs, "BadArgs");
        Check.Eq(ServerWebPanel.FromOpenCode(-2), PanelResult.UrlNotAllowed, "UrlNotAllowed");
        Check.Eq(ServerWebPanel.FromOpenCode(-3), PanelResult.RefusedBeforeNavigate, "NoGameWindow refuses before navigating");
        Check.Eq(ServerWebPanel.FromOpenCode(-4), PanelResult.RefusedBeforeNavigate, "Unavailable refuses before navigating");
        Check.Eq(ServerWebPanel.FromOpenCode(-5), PanelResult.RefusedBeforeNavigate, "ShuttingDown refuses before navigating");
        // No handle was issued, so nothing navigated: an undocumented code is a refusal too.
        foreach (int c in new[] { 0, -6, -7, -8, -9, -10, int.MinValue })
            Check.Eq(ServerWebPanel.FromOpenCode(c), PanelResult.RefusedBeforeNavigate, "undocumented open code " + c + ": no handle, refusal");
    }

    private static void States()
    {
        Check.Eq(ServerWebPanel.FromState(S_Starting, 0, 0, 0), PanelResult.Pending, "Starting: keep polling");
        Check.Eq(ServerWebPanel.FromState(S_Loading, F_Visible, 0, 1), PanelResult.Opened, "Loading: opened");
        Check.Eq(ServerWebPanel.FromState(S_Ready, F_Visible | F_Focused, 0, 1), PanelResult.Opened, "Ready: opened");
        Check.Eq(ServerWebPanel.FromState(S_Failed, F_OpenedInBrowser, -7, 0), PanelResult.OpenedInBrowserByEngine, "Failed+OpenedInBrowser (RuntimeMissing): the engine opened it");
        Check.Eq(ServerWebPanel.FromState(S_Failed, F_OpenedInBrowser, -8, 0), PanelResult.OpenedInBrowserByEngine, "Failed+OpenedInBrowser (CreateFailed): the engine opened it");
        Check.Eq(ServerWebPanel.FromState(S_Failed, 0, -9, 1), PanelResult.FailedAfterNavigate, "Failed+ProcessFailed: the code is spent");
        Check.Eq(ServerWebPanel.FromState(S_Failed, 0, -9, 0), PanelResult.FailedAfterNavigate, "ProcessFailed never falls back, whatever the count");
        Check.Eq(ServerWebPanel.FromState(S_Failed, 0, -7, 0), PanelResult.RefusedBeforeNavigate, "RuntimeMissing, no browser, no navigation: fall back ourselves");
        Check.Eq(ServerWebPanel.FromState(S_Failed, 0, -8, 0), PanelResult.RefusedBeforeNavigate, "CreateFailed, no browser, no navigation: fall back ourselves");
        Check.Eq(ServerWebPanel.FromState(S_Failed, 0, -7, 1), PanelResult.FailedAfterNavigate, "RuntimeMissing after a navigation: spent, no fallback");
        Check.Eq(ServerWebPanel.FromState(S_Failed, 0, -8, 2), PanelResult.FailedAfterNavigate, "CreateFailed after a navigation: spent, no fallback");
        Check.Eq(ServerWebPanel.FromState(S_Failed, 0, 0, 0), PanelResult.FailedAfterNavigate, "Failed with an unknown error: no fallback");
        Check.Eq(ServerWebPanel.FromState(S_Failed, 0, -4, 0), PanelResult.FailedAfterNavigate, "Failed with an undocumented error: no fallback");
        Check.Eq(ServerWebPanel.FromState(S_Closed, 0, 0, 0), PanelResult.Closed, "Closed before it loaded");
        Check.Eq(ServerWebPanel.FromState(S_Superseded, 0, 0, 0), PanelResult.Closed, "Superseded: replaced");
        Check.Eq(ServerWebPanel.FromState(S_None, 0, 0, 0), PanelResult.NoAnswer, "None (older engine, shutting down): neutral");
        Check.Eq(ServerWebPanel.FromState(99, 0, 0, 0), PanelResult.NoAnswer, "an undocumented status: neutral");
        Check.Eq(ServerWebPanel.FromState(-4, 0, 0, 0), PanelResult.NoAnswer, "a negative status: neutral");
    }

    private static void Fallback()
    {
        foreach (PanelResult p in Enum.GetValues<PanelResult>())
            Check.Eq(ServerWebOpen.FallbackToBrowser(p), p == PanelResult.RefusedBeforeNavigate, "fallback for " + p);
    }

    private static List<PanelResult> Answers(out Action<PanelResult> then)
    {
        var list = new List<PanelResult>();
        then = list.Add;
        return list;
    }

    private static void SyncAnswers()
    {
        foreach ((int code, PanelResult want) in new[] { (-1, PanelResult.BadArgs), (-2, PanelResult.UrlNotAllowed), (-3, PanelResult.RefusedBeforeNavigate), (-4, PanelResult.RefusedBeforeNavigate), (-5, PanelResult.RefusedBeforeNavigate) })
        {
            var e = new FakeEngine { OpenReturns = code };
            var p = new ServerWebPanel(e);
            var got = Answers(out var then);
            p.Open(Req, 0, then);
            Check.Eq(string.Join(",", got), want.ToString(), "open code " + code + ": answered at once");
            Check.False(p.Waiting, "open code " + code + ": nothing waits");
            p.Poll(1);
            p.Poll(ServerWebPanel.PollTimeoutMs + 5);
            Check.Eq(got.Count, 1, "open code " + code + ": the poll never answers again");
            Check.Eq(e.StateCalls, 0, "open code " + code + ": no state is read");
            p.Close();
            Check.Eq(e.Closed.Count, 0, "open code " + code + ": no handle, nothing to close");
        }

        var o = new FakeEngine();
        new ServerWebPanel(o).Open(Req, 0, _ => { });
        Check.Eq(string.Join(" ", o.Opens), "www.acdreamweave.com|dreamweave/0123456789abcdef0123456789abcdef|DreamWeave", "the request's allowlist, profile key and title reach the engine");
    }

    private static void PollAnswers()
    {
        var cases = new (int Status, int Flags, int Error, uint Nav, PanelResult Want)[]
        {
            (S_Loading, F_Visible, 0, 1, PanelResult.Opened),
            (S_Ready, F_Visible, 0, 1, PanelResult.Opened),
            (S_Failed, F_OpenedInBrowser, -7, 0, PanelResult.OpenedInBrowserByEngine),
            (S_Failed, 0, -9, 1, PanelResult.FailedAfterNavigate),
            (S_Failed, 0, -7, 0, PanelResult.RefusedBeforeNavigate),
            (S_Failed, 0, -8, 0, PanelResult.RefusedBeforeNavigate),
            (S_Closed, 0, 0, 0, PanelResult.Closed),
            (S_Superseded, 0, 0, 0, PanelResult.Closed),
            (S_None, 0, 0, 0, PanelResult.NoAnswer),
        };
        foreach (var c in cases)
        {
            var e = new FakeEngine { Status = S_Starting };
            var p = new ServerWebPanel(e);
            var got = Answers(out var then);
            p.Open(Req, 1_000, then);
            Check.Eq(got.Count, 0, c.Want + ": a handle is not an answer");
            Check.True(p.Waiting, c.Want + ": waiting after a handle");
            p.Poll(1_100);
            p.Poll(1_200);
            Check.Eq(got.Count, 0, c.Want + ": Starting keeps waiting");
            e.Status = c.Status; e.Flags = c.Flags; e.Error = c.Error; e.Nav = c.Nav;
            p.Poll(1_300);
            Check.Eq(string.Join(",", got), c.Want.ToString(), "state " + c.Status + "/" + c.Flags + "/" + c.Error + "/" + c.Nav + ": one poll decides");
            Check.False(p.Waiting, c.Want + ": nothing waits after the answer");
            int calls = e.StateCalls;
            p.Poll(1_400);
            p.Poll(1_000 + ServerWebPanel.PollTimeoutMs + 1);
            Check.Eq(got.Count, 1, c.Want + ": answered exactly once");
            Check.Eq(e.StateCalls, calls, c.Want + ": no polling after the answer");
        }
    }

    private static void Timeout()
    {
        Check.True(ServerWebPanel.PollTimeoutMs >= 5_000 && ServerWebPanel.PollTimeoutMs <= 60_000, "the poll timeout is between 5 and 60 seconds");
        var e = new FakeEngine { Status = S_Starting };
        var p = new ServerWebPanel(e);
        var got = Answers(out var then);
        p.Open(Req, 10_000, then);
        p.Poll(10_000 + ServerWebPanel.PollTimeoutMs - 1);
        Check.Eq(got.Count, 0, "still Starting just before the timeout: waiting");
        p.Poll(10_000 + ServerWebPanel.PollTimeoutMs);
        Check.Eq(string.Join(",", got), PanelResult.NoAnswer.ToString(), "at the timeout: NoAnswer");
        Check.False(ServerWebOpen.FallbackToBrowser(PanelResult.NoAnswer), "a timeout never opens the browser");
        p.Poll(10_000 + ServerWebPanel.PollTimeoutMs + 50);
        Check.Eq(got.Count, 1, "the timeout answers once");
        // The handle is kept: the panel may still show, and logout closes it.
        p.Close();
        Check.Eq(string.Join(",", e.Closed), "7", "a timed-out open's handle is still ours to close");
    }

    private static void CloseOwnHandle()
    {
        var none = new FakeEngine();
        new ServerWebPanel(none).Close();
        Check.Eq(none.Closed.Count, 0, "never opened: Close calls nothing");

        var e = new FakeEngine { OpenReturns = 7, Status = S_Starting };
        var p = new ServerWebPanel(e);
        var got = Answers(out var then);
        p.Open(Req, 0, then);
        p.Close();
        Check.Eq(string.Join(",", got), PanelResult.Closed.ToString(), "Close answers the waiting open as Closed");
        Check.Eq(string.Join(",", e.Closed), "7", "Close closes our handle");
        p.Close();
        Check.Eq(string.Join(",", e.Closed), "7", "a second Close closes nothing");
        Check.Eq(got.Count, 1, "and answers nothing again");

        // A newer open replaces the one we waited on; Close then closes the newer handle only.
        var e2 = new FakeEngine { OpenReturns = 7, Status = S_Starting };
        var p2 = new ServerWebPanel(e2);
        var first = Answers(out var then1);
        var second = Answers(out var then2);
        p2.Open(Req, 0, then1);
        e2.OpenReturns = 8;
        p2.Open(Req, 10, then2);
        Check.Eq(string.Join(",", first), PanelResult.Closed.ToString(), "a newer open answers the older one as Closed");
        Check.Eq(second.Count, 0, "the newer one still waits");
        e2.Status = S_Ready;
        p2.Poll(20);
        Check.Eq(string.Join(",", second), PanelResult.Opened.ToString(), "the newer one opens");
        p2.Close();
        Check.Eq(string.Join(",", e2.Closed), "8", "Close closes the newest handle only");
        Check.Eq(first.Count, 1, "the older answer is not repeated");

        // An opened panel (answered) is still closed by Close; a refused one gives nothing to close.
        var e3 = new FakeEngine { OpenReturns = 9, Status = S_Ready };
        var p3 = new ServerWebPanel(e3);
        p3.Open(Req, 0, _ => { });
        p3.Poll(1);
        e3.OpenReturns = -4;
        p3.Open(Req, 2, _ => { });
        p3.Close();
        Check.Eq(string.Join(",", e3.Closed), "9", "a refused open keeps the earlier handle ours to close");

        // A refused second open leaves the first one waiting; the tick still answers it.
        foreach (int refusal in new[] { -1, -2, -3, -4, -5 })
        {
            var e4 = new FakeEngine { OpenReturns = 7, Status = S_Starting };
            var p4 = new ServerWebPanel(e4);
            var a1 = Answers(out var t1);
            var a2 = Answers(out var t2);
            p4.Open(Req, 0, t1);
            e4.OpenReturns = refusal;
            p4.Open(Req, 10, t2);
            Check.Eq(a1.Count, 0, "refused second open (" + refusal + "): the first is not answered");
            Check.True(p4.Waiting, "refused second open (" + refusal + "): the first still waits");
            Check.Eq(a2.Count, 1, "refused second open (" + refusal + "): answered at once");
            e4.Status = S_Ready;
            p4.Poll(20);
            Check.Eq(string.Join(",", a1), PanelResult.Opened.ToString(), "refused second open (" + refusal + "): the first opens on the tick");
        }
        // Thrown second open: the same.
        var e5 = new FakeEngine { OpenReturns = 7, Status = S_Starting };
        var p5 = new ServerWebPanel(e5);
        var b1 = Answers(out var u1);
        p5.Open(Req, 0, u1);
        e5.ThrowOnOpen = true;
        p5.Open(Req, 10, _ => { });
        Check.Eq(b1.Count, 0, "a second open that throws leaves the first unanswered");
        Check.True(p5.Waiting, "and waiting");
    }

    private static void Exceptions()
    {
        var e = new FakeEngine { ThrowOnOpen = true };
        var p = new ServerWebPanel(e);
        var got = Answers(out var then);
        p.Open(Req, 0, then);
        Check.Eq(string.Join(",", got), PanelResult.RefusedBeforeNavigate.ToString(), "Open throwing: refused before navigating");

        var s = new FakeEngine { Status = S_Starting };
        var ps = new ServerWebPanel(s);
        var sg = Answers(out var sthen);
        ps.Open(Req, 0, sthen);
        s.ThrowOnState = true;
        ps.Poll(1);
        Check.Eq(string.Join(",", sg), PanelResult.NoAnswer.ToString(), "GetState throwing after a handle: neutral, never the browser");

        var h = new FakeEngine { ThrowOnHas = true };
        Check.False(new ServerWebPanel(h).Available, "HasPanel throwing: not available");
        Check.True(new ServerWebPanel(new FakeEngine()).Available, "HasPanel true: available");
        Check.False(new ServerWebPanel(new FakeEngine { Has = false }).Available, "HasPanel false: not available");

        var t = new FakeEngine { OpenReturns = -4 };
        bool threw = false;
        try { new ServerWebPanel(t).Open(Req, 0, _ => throw new InvalidOperationException("answer")); }
        catch (Exception) { threw = true; }
        Check.False(threw, "a throwing answer (sync) never escapes Open");

        var tp = new FakeEngine { Status = S_Starting };
        var ptp = new ServerWebPanel(tp);
        ptp.Open(Req, 0, _ => throw new InvalidOperationException("answer"));
        tp.Status = S_Ready;
        threw = false;
        try { ptp.Poll(1); } catch (Exception) { threw = true; }
        Check.False(threw, "a throwing answer (polled) never escapes Poll");
        Check.False(ptp.Waiting, "and the open is still answered");
    }

    // ServerPackWeb.cs runs inside acclient against whatever SDK the installed core ships.
    private static void SourceRules()
    {
        string shared = Path.Combine(ServerProjectsTests.PluginsDir(), "Shared", "ServerPack");
        string webPath = Path.Combine(shared, "ServerPackWeb.cs");
        string panelPath = Path.Combine(shared, "ServerWebPanel.cs");
        string pluginPath = Path.Combine(shared, "ServerPackPlugin.cs");
        string web = DreamWeaveWebTests.StripComments(File.ReadAllText(webPath));
        string panel = DreamWeaveWebTests.StripComments(File.ReadAllText(panelPath));

        // The SDK web-panel names, and the only methods allowed to name them.
        var sdkName = new System.Text.RegularExpressions.Regex(@"\b(HasWebPanel|OpenWebPanel|NavigateWebPanel|CloseWebPanel|GetWebPanelState|WebPanelOptions|WebPanelState|WebPanelStatus|WebPanelFlags|WebPanelResult|WebPanelStateFlags|WebPanelOpenNative|WebPanelStateNative|WebPanelOpenFn|WebPanelNavigateFn|WebPanelCloseFn|WebPanelGetStateFn)\b");
        string[] helpers = { "WebPanelHasCore", "WebPanelOpenCore", "WebPanelStateCore", "WebPanelCloseCore" };

        // Every server plugin source other than ServerPackWeb.cs names none of them (the pure seam uses ints).
        foreach (string f in Directory.GetFiles(ServerProjectsTests.PluginsDir(), "*.cs", SearchOption.AllDirectories)
                     .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                              && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            if (string.Equals(Path.GetFullPath(f), Path.GetFullPath(webPath), StringComparison.OrdinalIgnoreCase)) continue;
            Check.False(sdkName.IsMatch(DreamWeaveWebTests.StripComments(File.ReadAllText(f))), Path.GetFileName(f) + ": no SDK web-panel names outside ServerPackWeb.cs");
        }

        // In ServerPackWeb.cs, each SDK name sits inside one of the helpers, and each helper is NoInlining
        // and takes and returns primitives and strings only.
        var methodHead = new System.Text.RegularExpressions.Regex(
            @"\[MethodImpl\(MethodImplOptions\.NoInlining\)\]\s*private\s+(bool|int)\s+(" + string.Join("|", helpers) + @")\(([^)]*)\)");
        var spans = new List<(int Start, int End)>();
        var found = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in methodHead.Matches(web))
        {
            found.Add(m.Groups[2].Value);
            foreach (string param in m.Groups[3].Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string type = param.Trim().Split(' ')[0] == "out" ? param.Trim().Split(' ')[1] : param.Trim().Split(' ')[0];
                Check.True(type is "int" or "uint" or "string" or "string[]", m.Groups[2].Value + ": parameter type " + type + " is a primitive or string");
            }
            int bodyStart = m.Index + m.Length;
            int end = BodyEnd(web, bodyStart);
            spans.Add((m.Index, end));
        }
        Check.Eq(string.Join(",", found.OrderBy(x => x)), string.Join(",", helpers.OrderBy(x => x)), "the four helpers, each NoInlining, returning bool or int");
        var stray = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in sdkName.Matches(web))
            if (!spans.Any(s => m.Index >= s.Start && m.Index < s.End)) stray.Add(m.Value + "@" + m.Index);
        Check.Eq(string.Join(" ", stray), "", "ServerPackWeb.cs: SDK web-panel names only inside the NoInlining helpers");

        // Each helper is called exactly once, inside a try with both catches.
        foreach (string h in helpers)
        {
            var call = new System.Text.RegularExpressions.Regex(@"try\s*\{\s*return\s+_plugin\." + h + @"\([^;]*\);\s*\}\s*catch\s*\(MissingMethodException\)\s*\{[^{}]*\}\s*catch\s*\(TypeLoadException\)\s*\{[^{}]*\}");
            Check.Eq(call.Matches(web).Count, 1, h + ": called once, as try { return _plugin." + h + "(...); } catch (MissingMethodException) { } catch (TypeLoadException) { }");
            Check.Eq(System.Text.RegularExpressions.Regex.Matches(web, @"\b" + h + @"\(").Count, 2, h + ": declared and called once (nothing else calls it)");
        }
        Check.True(web.Contains("Flags = WebPanelFlags.FallbackToBrowser", StringComparison.Ordinal), "the open asks the engine to fall back to the browser");

        // The browser opens from two places only: OpenWeb's browser route, and right after AfterPanel's
        // FallbackToBrowser guard. A browser open in any switch arm would spend the code twice.
        string collapsed = System.Text.RegularExpressions.Regex.Replace(web, @"\s+", "");
        Check.Eq(CountOf(web, "OpenInBrowser(r, done)"), 2, "ServerPackWeb.cs: OpenInBrowser(r, done) appears exactly twice");
        Check.Eq(CountOf(collapsed, "if(ServerWebOpen.FallbackToBrowser(result)){LogTagged(\"webpanel:\"+result+\",usingthebrowser\");OpenInBrowser(r,done);return;}"), 1,
                 "AfterPanel: the browser open sits right after the FallbackToBrowser guard");
        Check.Eq(CountOf(collapsed, "}OpenInBrowser(r,done);}"), 1, "OpenWeb: the other is the browser route at its end");
        Check.Eq(CountOf(web, "ServerWebOpen.FallbackToBrowser("), 1, "AfterPanel asks FallbackToBrowser once");

        // No locks in the web code: the waiting open and the handle move by Interlocked only.
        Check.Eq(DreamWeaveWebTests.Locks(web).Count, 0, "ServerPackWeb.cs takes no lock");
        Check.Eq(DreamWeaveWebTests.Locks(panel).Count, 0, "ServerWebPanel.cs takes no lock");
        Check.False(System.Text.RegularExpressions.Regex.IsMatch(web + panel, @"\bMonitor\s*\."), "no Monitor in the web code");

        // The poll runs from the plugin's OnTick once, after the tick's queued actions and before ServerTick.
        string plugin = DreamWeaveWebTests.StripComments(File.ReadAllText(pluginPath));
        Check.Eq(System.Text.RegularExpressions.Regex.Matches(plugin, @"\bPollWeb\(\);").Count, 1, "ServerPackPlugin.cs calls PollWeb() once");
        int tick = plugin.IndexOf("public override void OnTick()", StringComparison.Ordinal);
        int poll = plugin.IndexOf("PollWeb();", StringComparison.Ordinal);
        int login = plugin.IndexOf("if (!_loginComplete) return;", tick < 0 ? 0 : tick, StringComparison.Ordinal);
        int serverTick = plugin.IndexOf("ServerTick(active);", tick < 0 ? 0 : tick, StringComparison.Ordinal);
        Check.True(tick >= 0 && login > tick && poll > login && poll < serverTick, "PollWeb runs in OnTick, logged in, before ServerTick");
        Check.Eq(System.Text.RegularExpressions.Regex.Matches(web, @"\b_webPanel\?\.Poll\(Environment\.TickCount64\)").Count, 1, "PollWeb polls with Environment.TickCount64");
    }

    private static int CountOf(string text, string needle)
    {
        int n = 0, at = 0;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    private static int BodyEnd(string code, int from)
    {
        int open = code.IndexOf('{', from);
        int arrow = code.IndexOf("=>", from, StringComparison.Ordinal);
        if (arrow >= 0 && (open < 0 || arrow < open)) { int semi = code.IndexOf(';', arrow); return semi < 0 ? code.Length : semi + 1; }
        if (open < 0) return code.Length;
        int depth = 0;
        for (int j = open; j < code.Length; j++)
        {
            if (code[j] == '{') depth++;
            else if (code[j] == '}' && --depth == 0) return j + 1;
        }
        return code.Length;
    }
}
