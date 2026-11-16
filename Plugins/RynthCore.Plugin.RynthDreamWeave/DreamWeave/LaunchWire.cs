// The wire text of DreamWeave's website sign-in: the "/weblogin" request and ACE's reply line.
// The code comes from chat; nothing else does. The line that carries it is eaten by the caller
// and the code is never logged, saved or chatted.
// Success: "[WEB] Launch code: <43 chars A-Za-z0-9_-> (single use, expires in 60 seconds)".
// Any other "[WEB] " line is a refusal worded for the player.
using System;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthDreamWeave;

internal static class LaunchWire
{
    public const string RequestCommand = "/weblogin";
    public const string ReplyLead = "[WEB] ";
    public const string SuccessLead = "[WEB] Launch code: ";
    /// <summary>Any line starting like this has the success shape: malformed, it is dropped, never shown as a refusal.</summary>
    private const string SuccessStem = "[WEB] Launch code";
    public const int CodeLength = 43;
    public const long ReplyTimeoutMs = 10_000, MinIntervalMs = 3_000;
    /// <summary>How long a code stays valid server-side: a code arriving this long after the reply deadline is still eaten, never opened.</summary>
    public const long CodeLifetimeMs = 60_000;

    /// <summary>The chat line to send for a destination. One place to add an argument later.</summary>
    public static string RequestFor(WebDestination d) => RequestCommand;

    public static bool IsCodeChar(char c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';

    /// <summary>
    /// True when <paramref name="text"/> is a server line that starts like a success ("[WEB] Launch code"),
    /// well formed or not. TryParse rejects a malformed one; the caller must still keep it out of chat,
    /// since it may carry most of a code.
    /// </summary>
    public static bool IsSuccessShaped(string? text, int chatType)
        => text != null && !ChatSource.IsPlayerAuthored(chatType) && ChatSource.StartsWithMarker(text, SuccessStem);

    /// <summary>
    /// True when <paramref name="text"/> is a [WEB] reply from the server: then either
    /// <paramref name="code"/> is set (success) or <paramref name="refusal"/> is (a refusal).
    /// False for anything else, and for a player's copy of the line. A line that starts like a
    /// success but whose code is malformed is false, never a refusal and never a code.
    /// </summary>
    public static bool TryParse(string? text, int chatType, out string code, out string refusal)
    {
        code = "";
        refusal = "";
        if (text == null || ChatSource.IsPlayerAuthored(chatType)) return false;
        if (!ChatSource.StartsWithMarker(text, ReplyLead)) return false;

        int start = ChatSource.ContentStart(text);
        ReadOnlySpan<char> line = text.AsSpan(start);
        if (line.StartsWith(SuccessStem.AsSpan(), StringComparison.Ordinal))
        {
            if (!line.StartsWith(SuccessLead.AsSpan(), StringComparison.Ordinal)) return false;
            ReadOnlySpan<char> rest = line.Slice(SuccessLead.Length);
            if (rest.Length <= CodeLength || rest[CodeLength] != ' ') return false;
            for (int i = 0; i < CodeLength; i++)
                if (!IsCodeChar(rest[i])) return false;
            code = rest.Slice(0, CodeLength).ToString();
            return true;
        }

        refusal = line.Slice(ReplyLead.Length).ToString();
        return true;
    }
}
