// The launch request state machine for the website buttons: one "/weblogin" at a time, a 10 s
// reply window, and a status line for the card. Pure (no engine calls), so RynthServerPack.Tests
// compiles it directly. The code is handed to the caller through OnChatLine and goes nowhere
// else: never into Status, never into the message, never into a field.
using System;

namespace RynthCore.Plugin.RynthDreamWeave;

internal enum LaunchBegin { Started, Busy, TooSoon }
internal enum LaunchLine { NotOurs, Code, Refused, Stale }

/// <summary>
/// Not thread-safe. Every member, including the Pending and Status getters, must be called under
/// <c>lock (Gate)</c>. The callers are the chat pump, the tick thread and the render thread. Hold
/// the lock only around calls into this class, never around Send or ShellExecute. nowMs must come
/// from one monotonic clock (Environment.TickCount64). After a Code reply, Status reads
/// "Opening X..."; the caller then reports the open's answer with SetResult, never by setting Status
/// another way. Generation names the request a Code or Refused reply resolved: read it under the same
/// lock, right after OnChatLine, and pass it to SetResult. Begin (when Started) and Reset move it on,
/// so an answer for an older request, or one from before a relog, never overwrites a newer status.
/// </summary>
internal sealed class WebLauncher
{
    public readonly object Gate = new();

    private const string UnexpectedReply = "an unexpected reply (is RynthDreamWeave up to date?)";

    private WebDestination? _pending;
    private long _deadlineMs;
    private long _lastBeginMs;
    private bool _everBegun;
    // Forward-only: the moment after which no code from any request begun so far can still be valid.
    // Nothing lowers it (not Begin, Cancel, Reset, Code, Refused or Expire), so a late code from any
    // earlier request is still eaten as Stale, whatever happened to the requests since.
    private long _staleUntilMs = long.MinValue;
    private long _generation;

    public WebDestination? Pending => _pending;

    /// <summary>The current request's generation: after OnChatLine returns Code or Refused, the one it resolved.</summary>
    public long Generation => _generation;
    public string Status { get; private set; } = "";

    /// <summary>
    /// A pending request already past its deadline is expired here first. Then: Busy while a request
    /// is pending; else TooSoon within MinIntervalMs of the last Begin; else Started.
    /// </summary>
    public LaunchBegin Begin(WebDestination d, long nowMs)
    {
        if (_pending != null && nowMs >= _deadlineMs) Expire();
        if (_pending != null) return LaunchBegin.Busy;
        if (_everBegun && nowMs - _lastBeginMs < LaunchWire.MinIntervalMs) return LaunchBegin.TooSoon;
        _pending = d;
        _generation++;
        _everBegun = true;
        _lastBeginMs = nowMs;
        _deadlineMs = nowMs + LaunchWire.ReplyTimeoutMs;
        _staleUntilMs = Math.Max(_staleUntilMs, nowMs + LaunchWire.ReplyTimeoutMs + LaunchWire.CodeLifetimeMs);
        Status = "Asking DreamWeave for a login code for " + d.Label + "...";
        return LaunchBegin.Started;
    }

    /// <summary>The send failed: nothing was asked, so nothing is pending.</summary>
    public void Cancel(string status)
    {
        _pending = null;
        Status = status;
    }

    /// <summary>
    /// A pending request before its deadline takes the line: Code or Refused clears Pending and the
    /// caller eats it (on Code, message is empty). Otherwise a line TryParse reads as a valid code is
    /// Stale (eat it, open nothing; dest null, code and message empty) while nowMs is before the
    /// forward-only stale deadline, and NotOurs after. Any other line outside a request is NotOurs.
    /// A success-shaped line TryParse rejects (a malformed code) is treated the same way, never shown:
    /// a pending request takes it as Refused with the unexpected-reply message; otherwise Stale inside
    /// the window and NotOurs after. An empty refusal, or one holding a code-length run, is unexpected too.
    /// </summary>
    public LaunchLine OnChatLine(string text, int chatType, long nowMs, out WebDestination? dest, out string code, out string message)
    {
        dest = null;
        code = "";
        message = "";
        if (!LaunchWire.TryParse(text, chatType, out string parsedCode, out string refusal))
        {
            if (!LaunchWire.IsSuccessShaped(text, chatType)) return LaunchLine.NotOurs;
            WebDestination? sp = _pending;
            if (sp == null || nowMs >= _deadlineMs) return nowMs < _staleUntilMs ? LaunchLine.Stale : LaunchLine.NotOurs;
            _pending = null;
            dest = sp;
            message = UnexpectedReply;
            Status = sp.Label + ": DreamWeave sent " + UnexpectedReply;
            return LaunchLine.Refused;
        }

        WebDestination? p = _pending;
        if (p == null || nowMs >= _deadlineMs)
            return parsedCode.Length > 0 && nowMs < _staleUntilMs ? LaunchLine.Stale : LaunchLine.NotOurs;

        _pending = null;
        dest = p;
        if (parsedCode.Length > 0)
        {
            code = parsedCode;
            Status = "Opening " + p.Label + "...";
            return LaunchLine.Code;
        }
        if (refusal.Trim().Length == 0 || HasCodeRun(refusal))
        {
            message = UnexpectedReply;
            Status = p.Label + ": DreamWeave sent " + UnexpectedReply;
        }
        else
        {
            message = refusal;
            Status = p.Label + ": DreamWeave said: " + refusal;
        }
        return LaunchLine.Refused;
    }

    /// <summary>The pending destination once, at or after Begin + ReplyTimeoutMs; null otherwise.</summary>
    public WebDestination? TakeTimedOut(long nowMs)
    {
        WebDestination? p = _pending;
        if (p == null || nowMs < _deadlineMs) return null;
        Expire();
        return p;
    }

    /// <summary>Login/logout: forget the pending request and the status. The throttle survives.</summary>
    public void Reset()
    {
        _pending = null;
        _generation++;
        Status = "";
    }

    /// <summary>
    /// The answer to the request of <paramref name="generation"/> (the open's result): set as Status only
    /// while nothing is pending and no Begin or Reset has happened since; otherwise ignored.
    /// </summary>
    public void SetResult(long generation, string status)
    {
        if (_pending == null && generation == _generation) Status = status;
    }

    private void Expire()
    {
        _pending = null;
        Status = "No reply from DreamWeave to " + LaunchWire.RequestCommand + " within "
            + (LaunchWire.ReplyTimeoutMs / 1000) + " s, so nothing was opened.";
    }

    /// <summary>True when the text holds CodeLength or more consecutive code characters.</summary>
    private static bool HasCodeRun(string s)
    {
        int run = 0;
        foreach (char c in s)
        {
            run = LaunchWire.IsCodeChar(c) ? run + 1 : 0;
            if (run >= LaunchWire.CodeLength) return true;
        }
        return false;
    }
}
