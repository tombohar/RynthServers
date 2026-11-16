// ServerPackWeb.cs - the host side of ServerWebOpen: open a server's web page in the engine's web
// panel when it has one, else in the system browser. A server's own code calls OpenWeb on the pump
// (ServerChatCommand, a RunOnTick action); the answer goes to `done` exactly once, and it may run on
// the pump (a refusal, or the tick's poll of a queued open) or on a worker thread (the browser). Reply
// is any-thread. The answer never contains the URL, and nothing here logs it.
//
// The panel (RynthCore plugin API v94, docs\WEB_PANEL.md): ServerWebPanel.cs reads the engine's codes
// and states and holds the one open the tick waits on (OnTick -> PollWeb). The engine calls are the
// four NoInlining helpers at the bottom, the only code that names an SDK web-panel member, each called
// inside catch (MissingMethodException) and catch (TypeLoadException): on a core whose
// Runtime\PluginShared SDK predates the panel they fail that call, not the plugin's load.
//
// Never Process.Start inside acclient: the browser opens with ShellExecuteW, as the engine does
// (ProblemReport.cs), on its own short-lived STA thread so the shell call never blocks the pump.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.ServerPack;

public sealed partial class ServerPackPlugin
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ShellExecuteW")]
    private static extern IntPtr ShellExecuteW(IntPtr hwnd, string? operation, string file, string? parameters, string? directory, int showCmd);

    private ServerWebPanel? _webPanel;

    private ServerWebPanel WebPanel
        => Volatile.Read(ref _webPanel)
           ?? Interlocked.CompareExchange(ref _webPanel, new ServerWebPanel(new EngineWebPanel(this)), null)
           ?? _webPanel!;

    /// <summary>The engine has a web panel (HasWebPanel; false on an older core or SDK). Any thread.</summary>
    private bool PanelAvailable => WebPanel.Available;

    private static void Finish(Action<WebOpenOutcome> done, WebOpenOutcome outcome)
    {
        try { done(outcome); } catch { }
    }

    /// <summary>Opens the page (pump thread). `done` gets the route taken and a message without the URL, once.</summary>
    internal void OpenWeb(WebOpenRequest r, Action<WebOpenOutcome> done)
    {
        WebOpenRoute route = ServerWebOpen.Route(r, PanelAvailable);
        if (route == WebOpenRoute.Refused)
        {
            Finish(done, new WebOpenOutcome(route, false, "That page is not on this server's list of pages, so it was not opened."));
            return;
        }

        if (route == WebOpenRoute.Panel)
        {
            try
            {
                WebPanel.Open(r, Environment.TickCount64, result => AfterPanel(r, result, done));
            }
            catch (Exception)
            {
                AfterPanel(r, PanelResult.RefusedBeforeNavigate, done);
            }
            return;
        }

        OpenInBrowser(r, done);
    }

    /// <summary>The panel's answer (pump thread: at once, or from the tick's poll). Exactly one Finish or browser open.</summary>
    private void AfterPanel(WebOpenRequest r, PanelResult result, Action<WebOpenOutcome> done)
    {
        try
        {
            // The one browser decision after the panel (ServerWebOpen.FallbackToBrowser: a refusal before
            // navigating only). Every other answer below ends without the browser: the code may be spent.
            if (ServerWebOpen.FallbackToBrowser(result))
            {
                LogTagged("web panel: " + result + ", using the browser");
                OpenInBrowser(r, done);
                return;
            }
            switch (result)
            {
                case PanelResult.Opened:
                    Finish(done, new WebOpenOutcome(WebOpenRoute.Panel, true, "Opened in the web panel."));
                    return;
                case PanelResult.OpenedInBrowserByEngine:
                    // RynthCore opened the browser itself (and said so in chat): never a second time.
                    LogTagged("web panel: OpenedInBrowserByEngine");
                    Finish(done, new WebOpenOutcome(WebOpenRoute.Browser, true, "Opened in your browser."));
                    return;
                case PanelResult.UrlNotAllowed:
                case PanelResult.BadArgs:
                    // A plugin bug: the request did not pass the engine's checks. The browser would hide it.
                    LogTagged("web panel: " + result + " (a plugin bug: the engine refused the request; not sent to the browser)");
                    Finish(done, new WebOpenOutcome(WebOpenRoute.Panel, false, "The web panel refused that page (a plugin bug, logged)."));
                    return;
                case PanelResult.FailedAfterNavigate:
                    LogTagged("web panel: FailedAfterNavigate");
                    Finish(done, new WebOpenOutcome(WebOpenRoute.Panel, false, "The web panel stopped while loading the page; open it again."));
                    return;
                case PanelResult.Closed:
                    Finish(done, new WebOpenOutcome(WebOpenRoute.Panel, false, "The web panel was closed before the page loaded."));
                    return;
                default:
                    // NoAnswer (the poll timed out or the state could not be read), or Pending, which
                    // ServerWebPanel never answers with. Neutral: the panel may still show; no browser.
                    LogTagged("web panel: " + result);
                    Finish(done, new WebOpenOutcome(WebOpenRoute.Panel, false, "The web panel hasn't shown the page yet; it may still appear."));
                    return;
            }
        }
        catch (Exception)
        {
            Finish(done, new WebOpenOutcome(WebOpenRoute.Panel, false, "The web panel couldn't open that page."));
        }
    }

    private void OpenInBrowser(WebOpenRequest r, Action<WebOpenOutcome> done)
    {
        if (!ServerWebOpen.TryGetAllowedUri(r.Url, r.AllowedHosts, out string url))
        {
            Finish(done, new WebOpenOutcome(WebOpenRoute.Refused, false, "That page is not on this server's list of pages, so it was not opened."));
            return;
        }
        var thread = new Thread(() =>
        {
            string message;
            bool ok;
            try
            {
                long code = ShellExecuteW(IntPtr.Zero, "open", url, null, null, 1).ToInt64();
                ok = code > 32;
                message = ok ? "Opened in your browser." : $"Couldn't open your browser (Windows error {code}).";
            }
            catch (Exception)
            {
                ok = false;
                message = "Couldn't open your browser (Windows error 0).";
            }
            Finish(done, new WebOpenOutcome(WebOpenRoute.Browser, ok, message));
        })
        {
            IsBackground = true,
            Name = "ServerPack web open",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>The tick's poll of a queued open (OnTick, pump thread).</summary>
    private void PollWeb()
    {
        try { _webPanel?.Poll(Environment.TickCount64); }
        catch (Exception) { }
    }

    /// <summary>Closes the engine's web panel only if this plugin opened it; answers a waiting open. Pump thread.</summary>
    internal void CloseWeb()
    {
        try { _webPanel?.Close(); }
        catch (Exception) { }
    }

    // -- The engine calls. Each helper is the only code naming its SDK members, and each wrapper catches
    // the two exceptions an older Runtime\PluginShared SDK raises when it JITs the helper. --

    private sealed class EngineWebPanel : IWebPanelEngine
    {
        private readonly ServerPackPlugin _plugin;

        public EngineWebPanel(ServerPackPlugin plugin) => _plugin = plugin;

        public bool HasPanel()
        {
            try { return _plugin.WebPanelHasCore(); }
            catch (MissingMethodException) { return false; }
            catch (TypeLoadException) { return false; }
        }

        public int Open(string url, string[] allowlist, string profileKey, string title)
        {
            try { return _plugin.WebPanelOpenCore(url, allowlist, profileKey, title); }
            catch (MissingMethodException) { return WebPanelCodes.Unavailable; }
            catch (TypeLoadException) { return WebPanelCodes.Unavailable; }
        }

        public int GetState(int handle, out int flags, out int error, out uint navigationCount)
        {
            flags = 0;
            error = 0;
            navigationCount = 0;
            try { return _plugin.WebPanelStateCore(handle, out flags, out error, out navigationCount); }
            catch (MissingMethodException) { return WebPanelCodes.StatusNone; }
            catch (TypeLoadException) { return WebPanelCodes.StatusNone; }
        }

        public int Close(int handle)
        {
            try { return _plugin.WebPanelCloseCore(handle); }
            catch (MissingMethodException) { return WebPanelCodes.Unavailable; }
            catch (TypeLoadException) { return WebPanelCodes.Unavailable; }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool WebPanelHasCore() => Host.HasWebPanel;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int WebPanelOpenCore(string url, string[] allowlist, string profileKey, string title)
        => Host.OpenWebPanel(new WebPanelOptions
        {
            Url = url,
            Allowlist = allowlist,
            ProfileKey = profileKey,
            Title = title,
            Flags = WebPanelFlags.FallbackToBrowser,
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int WebPanelStateCore(int handle, out int flags, out int error, out uint navigationCount)
    {
        WebPanelStatus status = Host.GetWebPanelState(handle, out WebPanelState state);
        flags = (int)state.Flags;
        error = (int)state.Error;
        navigationCount = state.NavigationCount;
        return (int)status;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int WebPanelCloseCore(int handle) => Host.CloseWebPanel(handle);
}
