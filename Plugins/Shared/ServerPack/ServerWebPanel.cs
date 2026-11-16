// ServerWebPanel.cs - the engine's web panel (RynthCore plugin API v94, docs\WEB_PANEL.md) as the
// ServerPack sees it: the engine's codes and states read as a PanelResult, and the one open the tick
// waits on. Pure (no SDK types, no engine): the calls go through IWebPanelEngine, which ServerPackWeb.cs
// implements with NoInlining helpers and RynthServerPack.Tests fakes.
//
// An open that returns a handle (> 0) is only queued, so its answer comes later: the plugin's OnTick
// calls Poll, and the first state that decides (WEB_PANEL.md: Failed, its error and OpenedInBrowser
// appear together, so one poll is enough) answers it. `then` runs exactly once per Open: at once for a
// refusal, else from Poll (a state, or the timeout), from Close, or from a newer Open. No locks: the
// waiting open and the handle move by Interlocked only, and `then` never runs under anything.
using System;
using System.Collections.Generic;
using System.Threading;

namespace RynthCore.Plugin.ServerPack;

/// <summary>The engine's web-panel calls, in primitives and strings only (WEB_PANEL.md's values as ints).</summary>
internal interface IWebPanelEngine
{
    bool HasPanel();
    /// <summary>A handle (&gt; 0), or a negative WebPanelResult.</summary>
    int Open(string url, string[] allowlist, string profileKey, string title);
    /// <summary>The WebPanelStatus; fills the WebPanelStateFlags, the error and the navigation count.</summary>
    int GetState(int handle, out int flags, out int error, out uint navigationCount);
    int Close(int handle);
}

/// <summary>WEB_PANEL.md's numbers (RynthCore.PluginSdk WebPanelResult, WebPanelStatus, WebPanelStateFlags).</summary>
internal static class WebPanelCodes
{
    public const int BadArgs = -1, UrlNotAllowed = -2, NoGameWindow = -3, Unavailable = -4, ShuttingDown = -5;
    public const int StaleHandle = -6, RuntimeMissing = -7, CreateFailed = -8, ProcessFailed = -9;
    public const int StatusNone = 0, StatusStarting = 1, StatusLoading = 2, StatusReady = 3;
    public const int StatusClosed = 4, StatusFailed = 5, StatusSuperseded = 6;
    public const int FlagOpenedInBrowser = 4;
}

internal sealed class ServerWebPanel
{
    /// <summary>
    /// How long a queued open may stay Starting before it is answered NoAnswer (neutral, no browser).
    /// 20 s covers a cold WebView2 start (creating the environment and the browser process) with room
    /// to spare; past it something is wrong, and opening the browser too could spend the code twice.
    /// The handle is kept, so logout still closes the panel if it shows up late.
    /// </summary>
    public const long PollTimeoutMs = 20_000;

    private sealed class WaitingOpen
    {
        public WaitingOpen(int handle, long deadlineMs, Action<PanelResult> then)
        {
            Handle = handle;
            DeadlineMs = deadlineMs;
            Then = then;
        }

        public int Handle { get; }
        public long DeadlineMs { get; }
        public Action<PanelResult> Then { get; }
    }

    private readonly IWebPanelEngine _engine;
    private WaitingOpen? _waiting;
    // The handle this plugin opened last (0 = none); the only one Close ever closes.
    private int _handle;

    public ServerWebPanel(IWebPanelEngine engine) => _engine = engine;

    /// <summary>The engine has a web panel. False when the call throws.</summary>
    public bool Available
    {
        get
        {
            try { return _engine.HasPanel(); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>An open is queued and not yet answered.</summary>
    public bool Waiting => Volatile.Read(ref _waiting) != null;

    /// <summary>OpenWebPanel's return: a handle is Pending (queued, not opened); else the refusal.</summary>
    public static PanelResult FromOpenCode(int code)
    {
        if (code > 0) return PanelResult.Pending;
        switch (code)
        {
            case WebPanelCodes.BadArgs: return PanelResult.BadArgs;
            case WebPanelCodes.UrlNotAllowed: return PanelResult.UrlNotAllowed;
            case WebPanelCodes.NoGameWindow:
            case WebPanelCodes.Unavailable:
            case WebPanelCodes.ShuttingDown:
                return PanelResult.RefusedBeforeNavigate;
            default:
                // Not a documented Open return. No handle was issued, so nothing navigated: a refusal.
                return PanelResult.RefusedBeforeNavigate;
        }
    }

    /// <summary>
    /// A polled state. Loading/Ready: Opened. Failed with OpenedInBrowser: the engine opened the browser.
    /// Failed with RuntimeMissing/CreateFailed, no browser and no navigation: RefusedBeforeNavigate (we
    /// try the browser; with FallbackToBrowser set this means the engine's own ShellExecute failed).
    /// Any other Failed (ProcessFailed, or an error after a navigation): FailedAfterNavigate, the code is
    /// spent. Closed/Superseded: Closed. Starting: Pending. None or anything else: NoAnswer.
    /// </summary>
    public static PanelResult FromState(int status, int flags, int error, uint navigationCount)
    {
        switch (status)
        {
            case WebPanelCodes.StatusStarting:
                return PanelResult.Pending;
            case WebPanelCodes.StatusLoading:
            case WebPanelCodes.StatusReady:
                return PanelResult.Opened;
            case WebPanelCodes.StatusFailed:
                if ((flags & WebPanelCodes.FlagOpenedInBrowser) != 0) return PanelResult.OpenedInBrowserByEngine;
                if ((error == WebPanelCodes.RuntimeMissing || error == WebPanelCodes.CreateFailed) && navigationCount == 0)
                    return PanelResult.RefusedBeforeNavigate;
                return PanelResult.FailedAfterNavigate;
            case WebPanelCodes.StatusClosed:
            case WebPanelCodes.StatusSuperseded:
                return PanelResult.Closed;
            default:
                return PanelResult.NoAnswer;
        }
    }

    /// <summary>
    /// Opens <paramref name="r"/> in the panel. <paramref name="then"/> gets the answer exactly once: now
    /// for a refusal, else later from Poll, Close or a newer Open. A newer Open that the engine queued
    /// answers the older one Closed (the engine superseded it); a refused one leaves the older one waiting.
    /// </summary>
    public void Open(WebOpenRequest r, long nowMs, Action<PanelResult> then)
    {
        int code;
        try
        {
            var hosts = new List<string>(r.AllowedHosts);
            code = _engine.Open(r.Url, hosts.ToArray(), r.ProfileKey, r.Title);
        }
        catch (Exception)
        {
            code = WebPanelCodes.Unavailable;
        }

        PanelResult result = FromOpenCode(code);
        if (result != PanelResult.Pending)
        {
            Run(then, result);
            return;
        }
        Interlocked.Exchange(ref _handle, code);
        Answer(Interlocked.Exchange(ref _waiting, new WaitingOpen(code, nowMs + PollTimeoutMs, then)), PanelResult.Closed);
    }

    /// <summary>The tick: answers the waiting open once its state decides, or at the timeout.</summary>
    public void Poll(long nowMs)
    {
        WaitingOpen? w = Volatile.Read(ref _waiting);
        if (w == null) return;

        PanelResult result;
        try
        {
            int status = _engine.GetState(w.Handle, out int flags, out int error, out uint navigationCount);
            result = FromState(status, flags, error, navigationCount);
        }
        catch (Exception)
        {
            // The handle was issued, so the engine may still navigate: never the browser from here.
            result = PanelResult.NoAnswer;
        }
        if (result == PanelResult.Pending)
        {
            if (nowMs < w.DeadlineMs) return;
            result = PanelResult.NoAnswer;
        }
        if (Interlocked.CompareExchange(ref _waiting, null, w) == w) Run(w.Then, result);
    }

    /// <summary>Closes the panel only if this plugin opened it, and answers a waiting open Closed.</summary>
    public void Close()
    {
        Answer(Interlocked.Exchange(ref _waiting, null), PanelResult.Closed);
        int handle = Interlocked.Exchange(ref _handle, 0);
        if (handle <= 0) return;
        try { _engine.Close(handle); }
        catch (Exception) { }
    }

    private static void Answer(WaitingOpen? w, PanelResult result)
    {
        if (w != null) Run(w.Then, result);
    }

    private static void Run(Action<PanelResult> then, PanelResult result)
    {
        try { then(result); }
        catch (Exception) { }
    }
}
