// DreamWeaveWebTools.cs - RynthDreamWeave's website tools, through the shared server plugin's hooks
// (Plugins/Shared/ServerPack/ServerPackPlugin.cs): the card's Class Abilities, Market and Suit Builder
// buttons and "/rserver abilities | market | suit", which open the website logged in as this account.
// The hooks it shares with the bank and the Skills panel are implemented once, in DreamWeavePlugin.cs and
// DreamWeaveSkills.cs, and call the Web* methods here first.
//
// The flow: a button (render thread) queues StartLaunch for the tick; a verb runs it on the pump. It
// sends "/weblogin"; ACE answers "[WEB] Launch code: ..." and the chat hook eats that line and opens
// DreamWeaveWeb.LaunchUrl through OpenWeb (ServerPackWeb.cs). Only the code comes from chat; the host
// and the path are constants. The code is never logged, saved, chatted or put in a status line.
//
// Threads (WebLauncher's remark): every launcher call is made under lock (_web.Gate), and the lock is
// held around launcher calls only, never around Send, OpenWeb, Reply or the log. Times come from
// Environment.TickCount64. The card hook runs on the render thread and only draws (WebButtons); the
// chat, tick, command and login hooks run on the plugin pump; OpenWeb's `done` may run on its worker
// thread. No hook lets an exception out: this code runs inside acclient.
using System;
using System.Collections.Generic;
using System.Threading;
using RynthCore.Plugin.RynthDreamWeave;

namespace RynthCore.Plugin.ServerPack;

public sealed partial class ServerPackPlugin
{
    private readonly WebLauncher _web = new();
    // The card's click handler, made once (render thread only).
    private Action<WebDestination>? _webClick;
    // Counts logins, so an answer from before a relog never spends this login's "no web panel" line.
    private int _webLoginEpoch;
    // The login that has had the "no in-game web panel" line (set from OpenWeb's done, any thread).
    private int _webNoPanelNotedEpoch;
    // The card's draw failure, logged once rather than every frame (render thread only).
    private bool _webDrawFailed;

    private enum WebBrowserWhy { PanelFellBack, NoPanel, NoAccount }

    private void WebCommandVerbs(List<string> verbs)
    {
        try
        {
            foreach (WebDestination d in DreamWeaveWeb.Destinations) verbs.Add(d.Id);
        }
        catch (Exception)
        {
            // The constructor has no Host to log to yet; the verbs are constants, so this can't happen.
        }
    }

    partial void ServerUsageLines(Action<string> add)
    {
        try
        {
            add(DreamWeaveWeb.UsageLine());
        }
        catch (Exception)
        {
            // The constructor has no Host to log to yet; the destinations are constants.
        }
    }

    partial void ServerBarMenu(List<ServerBarMenuItem> items)
    {
        try
        {
            items.AddRange(DreamWeaveWeb.BarMenu());
        }
        catch (Exception)
        {
            // The constructor has no Host to log to yet; the destinations are constants.
        }
    }

    partial void ServerCardActions()
    {
        try
        {
            // The card always keeps its buttons and status line, also on an engine that draws the bar icon's
            // dropdown (Tom 2026-11-09: every feature has buttons in its own window; the dropdown is a shortcut).
            WebButtons.Draw(_web, Active, _webClick ??= d => RunOnTick(() => StartLaunch(d)));
        }
        catch (Exception ex)
        {
            if (!_webDrawFailed)
            {
                _webDrawFailed = true;
                LogTagged("The web buttons failed to draw: " + ex.GetType().Name);
            }
        }
    }

    private void WebChatCommand(string[] parts, ref bool handled)
    {
        try
        {
            if (parts.Length < 2) return;
            WebDestination? d = DreamWeaveWeb.ById(parts[1]);
            if (d == null) return;
            handled = true;
            StartLaunch(d);
        }
        catch (Exception ex)
        {
            LogTagged("A web verb failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Asks DreamWeave for a login code for <paramref name="d"/>. Pump thread (a verb, or a button's RunOnTick).</summary>
    private void StartLaunch(WebDestination d)
    {
        try
        {
            if (!Active)
            {
                Reply.Fail(WebButtons.NotDreamWeave);
                return;
            }

            LaunchBegin begun;
            string status;
            lock (_web.Gate)
            {
                begun = _web.Begin(d, Environment.TickCount64);
                status = _web.Status;
            }
            if (begun == LaunchBegin.Busy)
            {
                Reply.Fail("Already asking DreamWeave for a login code; wait a moment.");
                return;
            }
            if (begun == LaunchBegin.TooSoon)
            {
                Reply.Fail("Wait a few seconds between website opens.");
                return;
            }

            if (!Send(LaunchWire.RequestFor(d)))
            {
                const string notSent = "Couldn't send /weblogin (not in the world).";
                lock (_web.Gate) _web.Cancel(notSent);
                Reply.Fail(notSent);
                return;
            }
            Reply.Ok(status);
        }
        catch (Exception ex)
        {
            try
            {
                LogTagged("Starting a web open failed: " + ex.GetType().Name);
                Reply.Fail("Couldn't open the website.");
            }
            catch (Exception)
            {
            }
        }
    }

    private void WebChatText(string text, int chatType, ref bool eat)
    {
        // No Active check before the launcher: only a request it began (or its stale window) is ever
        // not NotOurs, and a code that answers one must be eaten whatever the world is now.
        LaunchLine kind = LaunchLine.NotOurs;
        try
        {
            WebDestination? d;
            string launchCode;
            string status;
            long generation;
            lock (_web.Gate)
            {
                kind = _web.OnChatLine(text, chatType, Environment.TickCount64, out d, out launchCode, out _);
                status = _web.Status;
                generation = _web.Generation;
            }
            switch (kind)
            {
                case LaunchLine.NotOurs:
                    return;
                case LaunchLine.Stale:
                    // A late or repeated code: keep it out of chat and open nothing. No reply, no status, no log.
                    eat = true;
                    return;
                case LaunchLine.Refused:
                    eat = true;
                    Reply.Fail(status);
                    return;
                case LaunchLine.Code:
                    eat = true;
                    if (d == null) return;
                    if (!Active)
                    {
                        WebOpenFailed(d, generation, WebButtons.NotDreamWeave);
                        return;
                    }
                    string? world = WorldName(); // read once: the URL and the allowlist always name the same site
                    OpenLaunch(d, DreamWeaveWeb.LaunchUrl(launchCode, d, world), world, generation);
                    return;
            }
        }
        catch (Exception ex)
        {
            // The line that answered our request never reaches chat, whatever failed after it was read.
            if (kind != LaunchLine.NotOurs) eat = true;
            LogTagged("Reading a web reply failed: " + ex.GetType().Name);
        }
    }

    /// <summary>The connected world's name, or null when the engine can't say (then the live site is used).</summary>
    private string? WorldName() => Host.TryGetWorldName(out string w) ? w : null;

    /// <summary>
    /// Opens the launch URL (pump thread). <paramref name="generation"/> is the request the code answered;
    /// the open's answer replaces its "Opening X..." status (SetResult), so the card never stays on it.
    /// <paramref name="world"/> is the world the URL was built for; the allowlist comes from the same value.
    /// </summary>
    private void OpenLaunch(WebDestination d, string url, string? world, long generation)
    {
        int epoch = Volatile.Read(ref _webLoginEpoch);
        try
        {
            string? account = Host.TryGetAccountName(out string name) ? name : null;
            var request = new WebOpenRequest(url, DreamWeaveWeb.AllowedHostsFor(world),
                WebProfile.Key(Info.Id, account) ?? "", DreamWeaveWeb.PanelTitle);
            bool panel = PanelAvailable;
            WebBrowserWhy why = ServerWebOpen.Route(request, panel) != WebOpenRoute.Browser ? WebBrowserWhy.PanelFellBack
                : panel ? WebBrowserWhy.NoAccount : WebBrowserWhy.NoPanel;
            OpenWeb(request, outcome => WebOpened(d, outcome, why, generation, epoch));
        }
        catch (Exception ex)
        {
            LogTagged("Opening a web page failed: " + ex.GetType().Name);
            WebOpenFailed(d, generation, "Couldn't open " + d.Label + ".");
        }
    }

    /// <summary>OpenWeb's answer. Any thread (the browser opens on a worker thread).</summary>
    private void WebOpened(WebDestination d, WebOpenOutcome outcome, WebBrowserWhy why, long generation, int epoch)
    {
        try
        {
            if (!outcome.Ok)
            {
                WebOpenFailed(d, generation, d.Label + ": " + outcome.Message);
                return;
            }

            string text;
            string? note = null;
            if (outcome.Route != WebOpenRoute.Browser) text = "Opened " + d.Label + ".";
            else if (why == WebBrowserWhy.NoPanel)
            {
                text = "Opened " + d.Label + ".";
                // Once per login, and never spent by an answer from an earlier login.
                if (epoch == Volatile.Read(ref _webLoginEpoch) && Interlocked.Exchange(ref _webNoPanelNotedEpoch, epoch) != epoch)
                    note = "This RynthCore has no in-game web panel, so " + d.Label + " opened in your browser.";
            }
            else if (why == WebBrowserWhy.NoAccount)
                text = "Opened " + d.Label + " in your browser, not the web panel: RynthCore didn't report the account name.";
            else
                text = "Opened " + d.Label + " in your browser: the web panel didn't take it.";

            SetWebResult(generation, text);
            Reply.Ok(text);
            if (note != null) Reply.Ok(note);
        }
        catch (Exception)
        {
            // OpenWeb's Finish swallows this too; nothing here may reach the engine.
        }
    }

    private void WebOpenFailed(WebDestination d, long generation, string text)
    {
        try
        {
            SetWebResult(generation, text);
            Reply.Fail(text);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>The open's answer as the card's status, unless a newer request (or a relog) has taken over since.</summary>
    private void SetWebResult(long generation, string text)
    {
        lock (_web.Gate) _web.SetResult(generation, text);
    }

    private void WebTick()
    {
        try
        {
            WebDestination? timedOut;
            string status;
            lock (_web.Gate)
            {
                timedOut = _web.TakeTimedOut(Environment.TickCount64);
                status = _web.Status;
            }
            if (timedOut != null) Reply.Fail(status);
        }
        catch (Exception ex)
        {
            LogTagged("The web request timer failed: " + ex.GetType().Name);
        }
    }

    private void WebInfoLines(Action<string> line)
    {
        try
        {
            string status;
            lock (_web.Gate) status = _web.Status;
            line("Web tools (/rserver abilities | market | suit): " + (status.Length > 0 ? status : "none opened this login."));
        }
        catch (Exception ex)
        {
            LogTagged("The web tools' info line failed: " + ex.GetType().Name);
        }
    }

    private void WebLoginComplete()
    {
        try
        {
            lock (_web.Gate) _web.Reset();
            Interlocked.Increment(ref _webLoginEpoch);
        }
        catch (Exception ex)
        {
            LogTagged("Resetting the web tools at login failed: " + ex.GetType().Name);
        }
    }

    private void WebLogout()
    {
        try
        {
            lock (_web.Gate) _web.Reset();
            CloseWeb();
        }
        catch (Exception ex)
        {
            LogTagged("Closing the web tools at logout failed: " + ex.GetType().Name);
        }
    }

    private void WebShutdown()
    {
        try
        {
            lock (_web.Gate) _web.Reset();
            CloseWeb();
        }
        catch (Exception ex)
        {
            LogTagged("Closing the web tools at shutdown failed: " + ex.GetType().Name);
        }
    }
}
