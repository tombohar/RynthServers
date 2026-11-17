// DreamWeaveBank.cs - DreamWeave's banked luminance, for Lum/hr (2026-11-02). DreamWeave puts the
// luminance a character earns in its bank on the server: the client never gets the character's
// luminance (PropertyInt64 6) or a bank property, and no "You've banked N Luminance." line, so
// RynthSuite's session stats (SessionStats.cs) saw no luminance there. The only reading is the
// bank command: "/b" (DreamWeave's /bank, Source/ACE.Server/Command/Handlers/PlayerCommands.cs,
// ShowBankBalances) answers with System lines, all "[BANK] ...":
//     [BANK] Your balances:
//     [BANK] Pyreals (shared by every character on this account): 1,234
//     [BANK] Luminance: 8,904,758
//     [BANK] Legendary Keys: 3
//     [BANK] Promissory Notes: 0
//     [BANK] Offline Bonus Time: ...                      (when the server has offline bonus)
//     [BANK] Alternate Character Bonus +N% until ...       (when the character has it)
//     [BANK] Type /bank for the full list of commands.
// So this sends "/b" every minute while logged in on DreamWeave (not in the first 20 s after
// login), reads "[BANK] Luminance: N" and publishes it as the profile value "bankLuminance"
// (RynthServerProfile.ValueBankLuminance), whose rises the session stats count as luminance
// earned. A /b the player types is read the same way (and its reply always shows).
//
// Deposits move the character's luminance into the bank without earning it. A single deposit says
// how much ("[BANK] Deposited N luminance. Balance: B"); "/b d" (deposit all) only prints the new
// balances ("[BANK] Deposited all. Pyreals: ... | Luminance: B | ..."), so what the bank gained since
// the last reading is taken as moved in. Both are taken out of the published value, so they never
// count as earned. Luminance can't be withdrawn or transferred on DreamWeave; spending it from the
// bank (augmentations) is a drop, which readers don't count, and the value never goes below 0.
//
// Every reply regex is anchored at the start of the line (after an optional chat timestamp), so a
// pasted copy in a tell or a chat channel can't set the value.
// Threads: everything here runs under Gate (pump; the page draws holding it), except
// NoteTypedCommand, which the chat bar calls on AC's thread (it only touches interlocked fields).
// Pure: RynthServerPack.Tests covers it.
using System;
using System.Text.RegularExpressions;
using System.Threading;
using RynthCore.Plugin.ServerPack;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthDreamWeave;

internal sealed class DreamWeaveBank
{
    public const string SettingCheckEveryMinute = "checkBankEveryMinute";
    /// <summary>The profile value the session stats read ("RynthServer.Profile" values).</summary>
    public const string LuminanceKey = RynthServerProfile.ValueBankLuminance;
    /// <summary>The bank command (DreamWeave's /bank with no words: the balances).</summary>
    public const string Command = "/b";
    /// <summary>After login, before the first check (login's own chat and the server's welcome first).</summary>
    public const long FirstCheckDelayMs = 20_000;
    /// <summary>A check this often; a reading of any kind (a /b typed too) starts the minute again.</summary>
    public const long CheckIntervalMs = 60_000;

    private const string Ts = @"^\s*(?:\[[^\]]*\][\s:]*)?";
    private static readonly Regex LuminanceLine = new(Ts + @"\[BANK\]\s+Luminance:\s*([\d,]+)\s*$");
    // The rest of the /b reply: its header, "Name: value" balances (no digits or '.' in the name, so a
    // deposit line isn't one), the alt bonus line and the closing hint.
    private static readonly Regex ReplyLine = new(Ts + @"\[BANK\]\s+(?:Your balances:|[A-Za-z][A-Za-z '()]*:\s*\S.*"
                                                      + @"|Alternate Character Bonus\b.*|Type /bank for the full list of commands\.)\s*$");
    private static readonly Regex DepositLine = new(Ts + @"\[BANK\]\s+Deposited\s+([\d,]+)\s+luminance\.\s*Balance:\s*([\d,]+)", RegexOptions.IgnoreCase);
    private static readonly Regex DepositAllLine = new(Ts + @"\[BANK\]\s+Deposited all\..*\|\s*Luminance:\s*([\d,]+)\s*\|", RegexOptions.IgnoreCase);

    /// <summary>Held for everything below (pump and render thread).</summary>
    public readonly object Gate = new();

    /// <summary>Check the bank every minute (Lum/hr). On by default.</summary>
    public bool CheckEveryMinute = true;

    /// <summary>The luminance in the bank at the last reading (null before one this login).</summary>
    public long? Luminance { get; private set; }
    /// <summary>Luminance moved in from the character (deposits) since login, taken out of the published value.</summary>
    public long MovedIn { get; private set; }
    public DateTime ReadUtc { get; private set; } = DateTime.MinValue;

    /// <summary>What the profile value says: the bank less what deposits moved in. Null before a reading.</summary>
    public long? Published => Luminance is long l ? l - MovedIn : null;

    private readonly ServerChatRequest _request = new();
    private long _nextCheckMs = long.MaxValue;
    private int _selfSends;      // our "/b" send not yet seen by the chat bar (0 or 1)
    private int _typedByPlayer;  // the player typed /b (AC's thread sets it)

    public void LoadSettings(ServerPackSettings s) => CheckEveryMinute = s.GetBool(SettingCheckEveryMinute, true);

    public void SaveSettings(ServerPackSettings s)
    {
        if (s.SetBool(SettingCheckEveryMinute, CheckEveryMinute)) s.Save();
    }

    /// <summary>A character logged in (or the plugin loaded in game): the bank is read fresh.</summary>
    public void Login(long nowMs)
    {
        Clear();
        _nextCheckMs = nowMs + FirstCheckDelayMs;
    }

    public void Logout()
    {
        Clear();
        _nextCheckMs = long.MaxValue;
    }

    private void Clear()
    {
        Luminance = null;
        MovedIn = 0;
        ReadUtc = DateTime.MinValue;
        _request.Clear();
        Interlocked.Exchange(ref _selfSends, 0);
        Interlocked.Exchange(ref _typedByPlayer, 0);
    }

    /// <summary>When the next every-minute /b goes (Environment.TickCount64); long.MaxValue when none is coming.
    /// The offline bonus read keeps clear of it (DreamWeaveXpBonus.GapMs).</summary>
    public long NextCheckMs => CheckEveryMinute ? _nextCheckMs : long.MaxValue;

    /// <summary>The every-minute check: "/b" when one is due now (and the setting is on), else null.</summary>
    public string? CheckDue(long nowMs)
    {
        if (!CheckEveryMinute || nowMs < _nextCheckMs) return null;
        _nextCheckMs = nowMs + CheckIntervalMs;
        return Request(nowMs);
    }

    /// <summary>A check now (the page's button, or the minute): its reply is ours. Returns the command.</summary>
    public string Request(long nowMs)
    {
        _request.Sent(nowMs);
        // One at a time: if the engine's chat bar never sees our sends, the count still can't grow.
        Interlocked.Exchange(ref _selfSends, 1);
        return Command;
    }

    /// <summary>The request didn't go out (not logged in, no chat parser).</summary>
    public void NotSent()
    {
        _request.Clear();
        Interlocked.Exchange(ref _selfSends, 0);
    }

    /// <summary>True for what the player types to see the balances ("/b", "/bank"), not "/b d" and the like.</summary>
    public static bool IsBalanceCommand(string? text)
    {
        string t = (text ?? string.Empty).Trim();
        return t.Equals("/b", StringComparison.OrdinalIgnoreCase) || t.Equals("/bank", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>AC's thread (chat bar): a "/b" went through it. Ours, or typed by the player?</summary>
    public void NoteTypedCommand()
    {
        if (Interlocked.Decrement(ref _selfSends) >= 0) return;
        Interlocked.Exchange(ref _selfSends, 0);
        Interlocked.Exchange(ref _typedByPlayer, 1);
    }

    /// <summary>Pump tick: the player asked too, so the reply is theirs and shows.</summary>
    public void Tick()
    {
        if (Interlocked.Exchange(ref _typedByPlayer, 0) != 0) _request.Clear();
    }

    /// <summary>
    /// An incoming chat line on DreamWeave: reads the bank's luminance and deposits. True when the
    /// line answers RynthDreamWeave's own "/b" (to keep it out of the chat window).
    /// </summary>
    public bool OnChatLine(string? text, long nowMs)
    {
        if (string.IsNullOrEmpty(text)) return false;
        string line = text.TrimEnd('\r', '\n');
        Match m;
        if ((m = LuminanceLine.Match(line)).Success)
        {
            if (TryNumber(m.Groups[1].Value, out long lum)) Read(lum, nowMs);
            return Ours(nowMs);
        }
        if ((m = DepositLine.Match(line)).Success)
        {
            // "Deposited N luminance. Balance: B": N moved in, and B is a reading.
            if (TryNumber(m.Groups[1].Value, out long moved) && TryNumber(m.Groups[2].Value, out long balance))
            {
                MovedIn += moved;
                Read(balance, nowMs);
            }
            return false;
        }
        if ((m = DepositAllLine.Match(line)).Success)
        {
            // Deposit all: only the new balance. What the bank gained since the last reading moved in.
            if (TryNumber(m.Groups[1].Value, out long balance))
            {
                if (Luminance is long before && balance > before) MovedIn += balance - before;
                Read(balance, nowMs);
            }
            return false;
        }
        return ReplyLine.IsMatch(line) && Ours(nowMs);
    }

    private void Read(long luminance, long nowMs)
    {
        Luminance = luminance;
        // Spent from the bank below what deposits brought: those deposits are gone too (the value stays 0 or more).
        if (MovedIn > luminance) MovedIn = luminance;
        ReadUtc = DateTime.UtcNow;
        // Read just now: the next check is a minute away.
        if (_nextCheckMs != long.MaxValue) _nextCheckMs = nowMs + CheckIntervalMs;
    }

    private bool Ours(long nowMs)
    {
        bool ours = _request.Awaiting(nowMs);
        _request.Extend(nowMs);
        return ours;
    }

    private static bool TryNumber(string text, out long value)
        => long.TryParse(text.Replace(",", string.Empty), System.Globalization.NumberStyles.None,
                         System.Globalization.CultureInfo.InvariantCulture, out value);
}
