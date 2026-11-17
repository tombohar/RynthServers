// DreamWeaveXpBonus.cs - DreamWeave's offline bonus ("double XP time", 2026-11-15), for the Character page's
// tracker (XpBonusStrip.cs). The server's rules and wording come from its source (ACE-DreamWeave, 2026-11-02:
// Entity/OfflineBonus.cs, WorldObjects/Player_OfflineBonus.cs, Player_BonusSummary.cs, Player_Networking.cs,
// Command/Handlers/PlayerCommands.cs HandleOfflineBonus + ShowBankBalances):
//   - a character banks bonus time 1:1 while logged out, and while online but idle (no combat XP or luminance
//     for offline_bonus_idle_timeout_seconds, 5 min), up to offline_bonus_max_seconds (24 h);
//   - it drains 1:1 while active (fighting, and for the 5 min after the last combat XP);
//   - while any is left, combat XP and luminance get +offline_bonus_multiplier (1.0 = +100%).
// What it says (every line Broadcast, one chat message each; the [BANK] line System):
//   /offlinebonus   "Offline experience bonus: +100% to your combat experience and Luminance for the next 48m 31s,
//                    currently being SPENT (draining while you fight)." or "..., currently BANKING (idle for longer
//                    than the 5m 0s timeout, so it's growing again instead of draining)."
//                   "You have no offline experience bonus banked. Log this character out to bank bonus time ..."
//                   "The offline experience bonus is not enabled on this server."
//                   (the same status line also comes by itself 3 s after login when anything is banked)
//   pushes          "Your offline experience bonus is now active - 48m 31s remaining."
//                   "Your offline experience bonus is now idle and banking again - 3h 10m stored."
//                   "Your offline experience bonus is exhausted."
//                   "Welcome back - you were away for 9h 2m. Your offline experience bonus bank has been increased to 9h 2m."
//   /b              "[BANK] Offline Bonus Time: 48m 31s" or "... none"  (the bank check sends /b every minute)
//   /bonus          "  Offline bonus: +100% (48m 31s remaining)" or "  Offline bonus: +0% (none banked)"
// Durations are FormatOfflineBonusDuration's: "Xh Ym" (hours are total hours, so "37h 5m" for a long absence),
// "Xm Ys", "Xs"; "Xm" and "Xh" are read too.
//
// Between readings a local clock runs: down while active, up while banking (to the cap), never below 0. A reading
// loaded from disk doesn't run (the time since may have been online or off). A remaining-only reading (/b, /bonus)
// keeps the state, and two of them a minute apart that disagree with it by 30 s or more set it (a push missed, or a
// plugin loaded mid-session). Only the reply to RynthDreamWeave's own /offlinebonus is kept out of chat, within
// ServerChatRequest's window; a /offlinebonus the player types shows, and every reading of any kind updates it.
// Every line is anchored at the start and a player's chat types are never read, so a pasted copy can't set it.
// Pure (no host calls): RynthServerPack.Tests covers it. Threads: everything here runs under Gate, except
// NoteChatBar (AC's thread; interlocked fields only).
using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using RynthCore.Plugin.ServerPack;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthDreamWeave;

internal enum XpBonusState { Unknown, Disabled, Empty, Active, Banking }

/// <summary>Which of the server's offline-bonus lines a chat line was.</summary>
internal enum XpBonusLine { None, Status, NoneBanked, Disabled, Activated, Banking, Exhausted, Welcome, Bank, Summary }

internal sealed class DreamWeaveXpBonus
{
    public const string SettingAutoRead = "autoReadXpBonus";
    public const string Command = "/offlinebonus";
    /// <summary>After login, before the first automatic read: the bank's first /b goes at 20 s, the boards' first /top at 45 s.</summary>
    public const long FirstReadDelayMs = 35_000;
    /// <summary>An automatic read this often; any full reading (a typed /offlinebonus, the login line) starts it again.</summary>
    public const long ReadIntervalMs = 600_000;
    /// <summary>No two of RynthDreamWeave's server commands within this of each other.</summary>
    public const long GapMs = 5_000;
    /// <summary>offline_bonus_max_seconds' default (24 h); raised by any reading above it.</summary>
    public const double DefaultCapSeconds = 86_400;
    /// <summary>Two remaining-only readings this far apart can set the state from their difference.</summary>
    private const long InferAfterMs = 50_000;
    private const double InferSeconds = 30;

    private const string Ts = @"^\s*(?:\[[^\]]*\][\s:]*)?";
    private const string Dur = @"(\d+h \d+m|\d+m \d+s|\d+[hms])";
    private static readonly Regex StatusLine = new(Ts + @"Offline experience bonus: \+(\d+)% to your combat experience and Luminance for the next " + Dur + @", (.*)$");
    private static readonly Regex NoneLine = new(Ts + @"You have no offline experience bonus banked\.");
    private static readonly Regex DisabledLine = new(Ts + @"The offline experience bonus is not enabled on this server\.");
    private static readonly Regex ActivatedLine = new(Ts + @"Your offline experience bonus is now active - " + Dur + @" remaining\.\s*$");
    private static readonly Regex BankingLine = new(Ts + @"Your offline experience bonus is now idle and banking again - " + Dur + @" stored\.\s*$");
    private static readonly Regex ExhaustedLine = new(Ts + @"Your offline experience bonus is exhausted\.\s*$");
    private static readonly Regex WelcomeLine = new(Ts + @"Welcome back - you were away for " + Dur + @"\. Your offline experience bonus bank has been increased to " + Dur + @"\.\s*$");
    private static readonly Regex BankLine = new(Ts + @"\[BANK\]\s+Offline Bonus Time:\s*(none|" + Dur[1..^1] + @")\s*$");
    private static readonly Regex SummaryLine = new(@"^\s*Offline bonus: \+(\d+(?:\.\d+)?)% \(" + Dur + @" remaining\)\s*$");
    private static readonly Regex SummaryNoneLine = new(@"^\s*Offline bonus: \+0% \(none banked\)\s*$");
    private static readonly Regex DurationText = new(@"^(?:(\d+)h (\d+)m|(\d+)m (\d+)s|(\d+)([hms]))$");

    /// <summary>Held for everything here (pump and render thread).</summary>
    public readonly object Gate = new();

    /// <summary>Send /offlinebonus by itself (after login, then every ten minutes). Off by default.</summary>
    public bool AutoRead;

    public XpBonusState State { get; private set; }
    /// <summary>The bank, in seconds, at the last reading.</summary>
    public double Seconds { get; private set; }
    /// <summary>Environment.TickCount64 of the last reading; 0 before one, or for one loaded from disk (the clock doesn't run).</summary>
    public long ReadMs { get; private set; }
    public DateTime ReadUtc { get; private set; } = DateTime.MinValue;
    /// <summary>The bonus in percent (+100 = double), from a status or /bonus line; kept with the reading.</summary>
    public int? Percent { get; private set; }
    /// <summary>The cap the clock counts up to.</summary>
    public double CapSeconds { get; private set; } = DefaultCapSeconds;
    public XpBonusLine LastLine { get; private set; }
    /// <summary>What the last own request came to, for the page ("" before one).</summary>
    public string LastMiss { get; private set; } = "";
    /// <summary>Bumped on every change.</summary>
    public int Revision { get; private set; }
    /// <summary>Read since the last save.</summary>
    public bool Dirty { get; private set; }

    public bool HasReading => ReadUtc != DateTime.MinValue;

    private readonly ServerChatRequest _request = new();
    private bool _asked;              // an own request went out and hasn't been answered or given up
    private long _nextReadMs = long.MaxValue;
    private bool _forced;             // a Check now waiting for a quiet moment (also with the automatic read off)
    private int _selfSends;           // our /offlinebonus not yet seen by the chat bar (0 or 1)
    private int _typed;               // the player typed /offlinebonus (AC's thread sets it)
    private double _lastRemainingOnly = -1;   // the last /b or /bonus value, for the state from two of them
    private long _lastRemainingOnlyMs;

    // ── Settings, login, logout ────────────────────────────────────────────

    public void LoadSettings(Func<string, bool, bool> getBool) => AutoRead = getBool(SettingAutoRead, false);

    /// <summary>A character is in the world (or the plugin reloaded in game). The reading stays (a push may have come first).</summary>
    public void Login(long nowMs)
    {
        ClearRequest();
        _nextReadMs = nowMs + FirstReadDelayMs;
    }

    public void Logout()
    {
        ClearRequest();
        _nextReadMs = long.MaxValue;
        State = XpBonusState.Unknown;
        Seconds = 0;
        ReadMs = 0;
        ReadUtc = DateTime.MinValue;
        Percent = null;
        CapSeconds = DefaultCapSeconds;
        LastLine = XpBonusLine.None;
        LastMiss = "";
        _lastRemainingOnly = -1;
        Dirty = false;
        Revision++;
    }

    private void ClearRequest()
    {
        _request.Clear();
        _asked = false;
        _forced = false;
        Interlocked.Exchange(ref _selfSends, 0);
        Interlocked.Exchange(ref _typed, 0);
    }

    // ── Requests ───────────────────────────────────────────────────────────

    /// <summary>The next automatic (or asked-for) read, Environment.TickCount64; long.MaxValue when none is coming.</summary>
    public long NextReadMs => AutoRead || _forced ? _nextReadMs : long.MaxValue;

    /// <summary>An own request waits for its reply, or a Check now waits to go.</summary>
    public bool Waiting(long nowMs) => _forced || (_asked && _request.Awaiting(nowMs));

    /// <summary>
    /// The pump's tick: "/offlinebonus" when a read is due now, else null. <paramref name="canSend"/> is false outside the
    /// world and in portal space; <paramref name="quiet"/> false while another of RynthDreamWeave's commands is within
    /// <see cref="GapMs"/> (the read then goes on a later tick).
    /// </summary>
    public string? Due(long nowMs, bool canSend, bool quiet)
    {
        if ((!AutoRead && !_forced) || nowMs < _nextReadMs) return null;
        if (!canSend || !quiet || (_asked && _request.Awaiting(nowMs))) return null;
        _forced = false;
        _nextReadMs = nowMs + ReadIntervalMs;
        return Request(nowMs);
    }

    /// <summary>Check now: the read goes at the next quiet moment, the automatic read on or off.</summary>
    public void CheckSoon(long nowMs)
    {
        _forced = true;
        _nextReadMs = nowMs;
        LastMiss = "";
        Revision++;
    }

    /// <summary>Marks a request as RynthDreamWeave's own (its reply is hidden) and returns the command to send.</summary>
    public string Request(long nowMs)
    {
        _request.Sent(nowMs);
        _asked = true;
        LastMiss = "";
        // One at a time: if the engine's chat bar never sees our sends, the count still can't grow.
        Interlocked.Exchange(ref _selfSends, 1);
        return Command;
    }

    /// <summary>The request didn't go out (not in the world, no chat parser).</summary>
    public void NotSent()
    {
        _request.Clear();
        _asked = false;
        Interlocked.Exchange(ref _selfSends, 0);
    }

    /// <summary>True for "/offlinebonus" with or without words after it (the server ignores them).</summary>
    public static bool IsCommand(string? text)
    {
        string t = (text ?? "").Trim();
        if (!t.StartsWith(Command, StringComparison.OrdinalIgnoreCase)) return false;
        return t.Length == Command.Length || char.IsWhiteSpace(t[Command.Length]);
    }

    /// <summary>AC's thread (chat bar): a /offlinebonus went through it. Ours, or typed by the player?</summary>
    public void NoteChatBar()
    {
        if (Interlocked.Decrement(ref _selfSends) >= 0) return;
        Interlocked.Exchange(ref _selfSends, 0);
        Interlocked.Exchange(ref _typed, 1);
    }

    /// <summary>
    /// The pump's tick: a typed /offlinebonus makes the reply the player's; an own request with no reply in time is
    /// given up. True when one was given up just now.
    /// </summary>
    public bool Tick(long nowMs)
    {
        TakeTyped();
        if (!_request.Awaiting(nowMs)) Interlocked.Exchange(ref _selfSends, 0);
        if (!_asked || _request.Awaiting(nowMs)) return false;
        _asked = false;
        LastMiss = "No reply to " + Command + ".";
        Revision++;
        return true;
    }

    private void TakeTyped()
    {
        if (Interlocked.Exchange(ref _typed, 0) == 0) return;
        _request.Clear();
        _asked = false;
    }

    /// <summary>Turns the automatic read on or off; turned on, the first read goes soon (at the next quiet moment).</summary>
    public void SetAutoRead(bool on, long nowMs)
    {
        AutoRead = on;
        if (on && _nextReadMs != long.MaxValue && _nextReadMs > nowMs + GapMs) _nextReadMs = nowMs + GapMs;
        Revision++;
    }

    // ── Reading the server's lines ─────────────────────────────────────────

    /// <summary>
    /// An incoming chat message on DreamWeave. True when it should be kept out of the chat window: the reply to
    /// RynthDreamWeave's own /offlinebonus. Every offline-bonus line of the server's updates the reading.
    /// </summary>
    public bool OnChatText(string? text, int chatType, long nowMs, DateTime utcNow)
    {
        if (string.IsNullOrEmpty(text) || ChatSource.IsPlayerAuthored(chatType)) return false;
        TakeTyped();
        bool hide = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0) continue;
            XpBonusLine kind = ReadLine(line, nowMs, utcNow);
            if (kind is XpBonusLine.Status or XpBonusLine.NoneBanked or XpBonusLine.Disabled)
            {
                // A full reading: the next automatic read is ten minutes away.
                if (_nextReadMs != long.MaxValue && !_forced) _nextReadMs = nowMs + ReadIntervalMs;
                if (_asked && _request.Awaiting(nowMs))
                {
                    hide = true;
                    _asked = false;
                    _request.Clear();
                }
            }
        }
        return hide;
    }

    /// <summary>Reads one line; what it was (None when it isn't one of the server's offline-bonus lines).</summary>
    private XpBonusLine ReadLine(string line, long nowMs, DateTime utcNow)
    {
        Match m;
        if ((m = StatusLine.Match(line)).Success && TryDuration(m.Groups[2].Value, out double s, out double res))
        {
            string state = m.Groups[3].Value;
            XpBonusState st = state.Contains("BANKING", StringComparison.Ordinal) ? XpBonusState.Banking
                : state.Contains("SPENT", StringComparison.Ordinal) || state.Contains("ACTIVE", StringComparison.Ordinal) ? XpBonusState.Active
                : XpBonusState.Unknown;
            if (st == XpBonusState.Unknown && State is XpBonusState.Active or XpBonusState.Banking) st = State;
            Apply(XpBonusLine.Status, st, s, res, Int(m.Groups[1].Value), nowMs, utcNow);
            return XpBonusLine.Status;
        }
        if (NoneLine.IsMatch(line)) { Apply(XpBonusLine.NoneBanked, XpBonusState.Empty, 0, 1, null, nowMs, utcNow); return XpBonusLine.NoneBanked; }
        if (DisabledLine.IsMatch(line)) { Apply(XpBonusLine.Disabled, XpBonusState.Disabled, 0, 1, null, nowMs, utcNow); return XpBonusLine.Disabled; }
        if ((m = ActivatedLine.Match(line)).Success && TryDuration(m.Groups[1].Value, out s, out res))
        {
            Apply(XpBonusLine.Activated, XpBonusState.Active, s, res, null, nowMs, utcNow);
            return XpBonusLine.Activated;
        }
        if ((m = BankingLine.Match(line)).Success && TryDuration(m.Groups[1].Value, out s, out res))
        {
            Apply(XpBonusLine.Banking, XpBonusState.Banking, s, res, null, nowMs, utcNow);
            return XpBonusLine.Banking;
        }
        if (ExhaustedLine.IsMatch(line)) { Apply(XpBonusLine.Exhausted, XpBonusState.Empty, 0, 1, null, nowMs, utcNow); return XpBonusLine.Exhausted; }
        if ((m = WelcomeLine.Match(line)).Success && TryDuration(m.Groups[2].Value, out s, out res))
        {
            // A fresh login is idle (banking) until its first kill.
            Apply(XpBonusLine.Welcome, XpBonusState.Banking, s, res, null, nowMs, utcNow);
            return XpBonusLine.Welcome;
        }
        if ((m = BankLine.Match(line)).Success)
        {
            if (m.Groups[1].Value == "none") RemainingOnly(XpBonusLine.Bank, 0, 1, null, nowMs, utcNow);
            else if (TryDuration(m.Groups[1].Value, out s, out res)) RemainingOnly(XpBonusLine.Bank, s, res, null, nowMs, utcNow);
            else return XpBonusLine.None;
            return XpBonusLine.Bank;
        }
        if (SummaryNoneLine.IsMatch(line)) { RemainingOnly(XpBonusLine.Summary, 0, 1, null, nowMs, utcNow); return XpBonusLine.Summary; }
        if ((m = SummaryLine.Match(line)).Success && TryDuration(m.Groups[2].Value, out s, out res))
        {
            int? pct = double.TryParse(m.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double p) ? (int)Math.Round(p, MidpointRounding.AwayFromZero) : null;
            RemainingOnly(XpBonusLine.Summary, s, res, pct, nowMs, utcNow);
            return XpBonusLine.Summary;
        }
        return XpBonusLine.None;
    }

    /// <summary>A /b or /bonus reading: the bank without its state. The state stays, or is set from two such readings.</summary>
    private void RemainingOnly(XpBonusLine kind, double seconds, double res, int? pct, long nowMs, DateTime utcNow)
    {
        XpBonusState st = State;
        if (seconds <= 0) st = XpBonusState.Empty;
        else
        {
            if (st is XpBonusState.Empty or XpBonusState.Disabled) st = XpBonusState.Banking;   // only idle time grows an empty bank
            if (_lastRemainingOnly >= 0 && nowMs - _lastRemainingOnlyMs >= InferAfterMs)
            {
                double delta = seconds - _lastRemainingOnly;
                if (delta <= -InferSeconds) st = XpBonusState.Active;
                else if (delta >= InferSeconds) st = XpBonusState.Banking;
                else if (st == XpBonusState.Unknown && seconds >= CapSeconds) st = XpBonusState.Banking;   // full and not draining
            }
        }
        if (_lastRemainingOnly < 0 || nowMs - _lastRemainingOnlyMs >= InferAfterMs)
        {
            _lastRemainingOnly = seconds;
            _lastRemainingOnlyMs = nowMs;
        }
        Apply(kind, st, seconds, res, pct, nowMs, utcNow);
    }

    private void Apply(XpBonusLine kind, XpBonusState state, double seconds, double res, int? pct, long nowMs, DateTime utcNow)
    {
        // A reading shown to the minute ("3h 10m") that the running clock is inside of keeps the clock's seconds.
        if (state == State && ReadMs != 0 && res > 1 && seconds > 0)
        {
            double e = Remaining(nowMs);
            if (e >= seconds && e < seconds + res) seconds = e;
        }
        // A line that says the state (a push, the status reply) starts the two-reading comparison again: a /b a
        // minute later must not undo a push that came between.
        if (kind is not (XpBonusLine.Bank or XpBonusLine.Summary)) _lastRemainingOnly = -1;
        State = state;
        Seconds = Math.Max(0, seconds);
        if (Seconds > CapSeconds) CapSeconds = Seconds;
        ReadMs = nowMs == 0 ? 1 : nowMs;
        ReadUtc = utcNow;
        if (pct is int p && p > 0) Percent = p;
        LastLine = kind;
        LastMiss = "";
        Dirty = true;
        Revision++;
    }

    private static int? Int(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : null;

    /// <summary>
    /// A duration in FormatOfflineBonusDuration's words ("3h 10m", "48m 31s", "12s", also "5m" and "2h"): its seconds,
    /// and the step it is shown to (60 for "3h 10m", 1 for "48m 31s").
    /// </summary>
    public static bool TryDuration(string? text, out double seconds, out double resolution)
    {
        seconds = 0;
        resolution = 1;
        Match m = DurationText.Match((text ?? "").Trim());
        if (!m.Success) return false;
        static long N(Group g) => long.Parse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture);
        if (m.Groups[1].Success) { seconds = N(m.Groups[1]) * 3600 + N(m.Groups[2]) * 60; resolution = 60; }
        else if (m.Groups[3].Success) { seconds = N(m.Groups[3]) * 60 + N(m.Groups[4]); }
        else
        {
            long n = N(m.Groups[5]);
            switch (m.Groups[6].Value)
            {
                case "h": seconds = n * 3600; resolution = 3600; break;
                case "m": seconds = n * 60; resolution = 60; break;
                default: seconds = n; break;
            }
        }
        return true;
    }

    /// <summary>A number of seconds the way the server writes it ("3h 10m", "48m 31s", "12s").</summary>
    public static string Format(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, Math.Floor(seconds)));
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds}s";
        return $"{span.Seconds}s";
    }

    // ── The local clock and what the tracker says ─────────────────────────

    /// <summary>The clock runs: a live reading, active or banking.</summary>
    public bool Running => ReadMs != 0 && State is XpBonusState.Active or XpBonusState.Banking;

    /// <summary>The bank now: down while active, up while banking (to the cap), never below 0.</summary>
    public double Remaining(long nowMs)
    {
        if (ReadMs == 0 || nowMs <= ReadMs) return Seconds;
        double elapsed = (nowMs - ReadMs) / 1000.0;
        return State switch
        {
            XpBonusState.Active => Math.Max(0, Seconds - elapsed),
            XpBonusState.Banking => Math.Min(Math.Max(CapSeconds, Seconds), Seconds + elapsed),
            _ => Seconds,
        };
    }

    /// <summary>"Double XP +100%", "XP bonus +50%", or "Double XP" before the percent is known.</summary>
    public string Title => Percent is int p && p != 100 ? $"XP bonus +{p}%" : Percent == 100 ? "Double XP +100%" : "Double XP";

    /// <summary>True while the bonus is being spent (the tracker colours it).</summary>
    public bool IsActive(long nowMs) => State == XpBonusState.Active && Remaining(nowMs) > 0;

    /// <summary>The tracker's line: "Double XP +100%: 48m 31s left (active)", "...: 3h 10m banked (idle)", "...: None banked".</summary>
    public string Line(long nowMs)
    {
        if (!HasReading) return "Double XP: not read yet";
        if (State == XpBonusState.Disabled) return "Offline XP bonus: not enabled on this server";
        double r = Remaining(nowMs);
        if (State == XpBonusState.Empty || r <= 0) return Title + ": None banked";
        string d = Format(r);
        return State switch
        {
            XpBonusState.Active => $"{Title}: {d} left (active)",
            XpBonusState.Banking => r >= CapSeconds ? $"{Title}: {d} banked (idle, full)" : $"{Title}: {d} banked (idle)",
            _ => $"{Title}: {d} banked",
        };
    }

    /// <summary>How old the reading is and whether the clock runs: "Server read 3 min ago; counting down".</summary>
    public string Detail(long nowMs, DateTime utcNow)
    {
        if (!HasReading) return LastMiss.Length > 0 ? LastMiss : "Not read yet: press Check now.";
        string age = Age(ReadUtc, utcNow);
        if (ReadMs == 0) return $"Saved reading, {age}; not counting until the next one.";
        string clock = State == XpBonusState.Active && Remaining(nowMs) > 0 ? "; counting down"
            : State == XpBonusState.Banking && Remaining(nowMs) < CapSeconds ? "; counting up"
            : State == XpBonusState.Unknown && Seconds > 0 ? "; active or idle not known yet" : "";
        return "Server read " + age + clock + (LastMiss.Length > 0 ? " (" + LastMiss + ")" : "");
    }

    /// <summary>"just now", "3 min ago", "2 h ago", "4 d ago".</summary>
    public static string Age(DateTime readUtc, DateTime utcNow)
    {
        TimeSpan a = utcNow - readUtc;
        if (a < TimeSpan.FromMinutes(1)) return "just now";
        if (a < TimeSpan.FromHours(1)) return $"{(int)a.TotalMinutes} min ago";
        if (a < TimeSpan.FromDays(1)) return $"{(int)a.TotalHours} h ago";
        return $"{(int)a.TotalDays} d ago";
    }

    // ── The saved reading (hand-written JSON: plugin types never go through the serializer) ──

    public static string SavePath(string settingsFile, uint characterId)
        => Path.Combine(Path.GetDirectoryName(settingsFile) ?? "", "rynthdreamweave.xpbonus." + characterId.ToString("X8", CultureInfo.InvariantCulture) + ".json");

    public string SaveJson()
    {
        Dirty = false;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("state", State.ToString().ToLowerInvariant());
            w.WriteNumber("seconds", Math.Round(Seconds, 1));
            if (Percent is int p) w.WriteNumber("percent", p);
            w.WriteNumber("cap", CapSeconds);
            w.WriteString("updated", ReadUtc == DateTime.MinValue ? "" : ReadUtc.ToString("o", CultureInfo.InvariantCulture));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// The reading saved for this character. It doesn't replace a newer one read this session (the login lines may
    /// come before the character is known), and the clock doesn't run on it. A broken file is left out. Never throws.
    /// </summary>
    public void LoadSaved(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object) return;
            if (!DateTime.TryParse(Str(e, "updated"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime u)) return;
            u = u.ToUniversalTime();
            int? pct = e.TryGetProperty("percent", out JsonElement pe) && pe.TryGetInt32(out int p) && p > 0 ? p : null;
            if (HasReading && ReadUtc >= u)
            {
                Percent ??= pct;   // the percent is the server's setting: an older reading still knows it
                return;
            }
            if (!Enum.TryParse(Str(e, "state"), ignoreCase: true, out XpBonusState state)) state = XpBonusState.Unknown;
            State = state;
            Seconds = e.TryGetProperty("seconds", out JsonElement s) && s.TryGetDouble(out double sec) && sec > 0 ? sec : 0;
            CapSeconds = e.TryGetProperty("cap", out JsonElement c) && c.TryGetDouble(out double cap) && cap > 0 ? Math.Max(cap, Seconds) : DefaultCapSeconds;
            Percent = pct ?? Percent;
            ReadUtc = u;
            ReadMs = 0;
            Dirty = false;
            Revision++;
        }
        catch (JsonException)
        {
        }
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
