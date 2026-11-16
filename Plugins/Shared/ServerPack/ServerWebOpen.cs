// ServerWebOpen.cs - the rules for opening a server's web page from a server plugin: which URLs are
// allowed and where the page goes (the engine's web panel when it has one, else the system browser).
// Pure (no engine, no threads): RynthServerPack.Tests covers it. The host side is ServerPackWeb.cs.
// No Regex: the plugins are NativeAOT and Regex costs size, so the key is parsed by hand.
using System;
using System.Collections.Generic;

namespace RynthCore.Plugin.ServerPack;

/// <summary>
/// A page to open. The URL comes from constants in the plugin, never from chat, but it may carry a
/// one-time code, so ToString names the title only (a record's generated ToString would print the URL).
/// </summary>
internal sealed record WebOpenRequest(string Url, IReadOnlyList<string> AllowedHosts, string ProfileKey, string Title)
{
    public override string ToString() => "WebOpenRequest(" + Title + ")";
}

internal enum WebOpenRoute { Panel, Browser, Refused }

/// <summary>What the engine's panel did. Only a refusal before navigating may fall back to the browser.</summary>
/// <remarks>
/// OpenedInBrowserByEngine: the engine's own fallback opened the system browser (do nothing more).
/// BadArgs and UrlNotAllowed: plugin bugs, never sent to the browser. Pending: the engine queued the
/// open and the tick polls its state (ServerWebPanel). Closed: closed or replaced before it loaded.
/// NoAnswer: the state never settled before the poll timeout (or could not be read); neutral, no fallback.
/// </remarks>
internal enum PanelResult { Opened, RefusedBeforeNavigate, UrlNotAllowed, FailedAfterNavigate, OpenedInBrowserByEngine, BadArgs, Pending, Closed, NoAnswer }

/// <summary>What happened. The message never contains the URL.</summary>
internal sealed record WebOpenOutcome(WebOpenRoute Route, bool Ok, string Message);

internal static class ServerWebOpen
{
    /// <summary>
    /// Absolute https, no userinfo, the default port, and a host equal to an allowed entry
    /// (OrdinalIgnoreCase). An entry "*.x" matches subdomains of x only, never x itself.
    /// </summary>
    public static bool IsAllowedUrl(string? url, IReadOnlyList<string> allowedHosts)
        => TryGetAllowedUri(url, allowedHosts, out _);

    /// <summary>The same check, giving the validated absolute URI (what the browser should be handed).</summary>
    public static bool TryGetAllowedUri(string? url, IReadOnlyList<string> allowedHosts, out string absoluteUri)
    {
        absoluteUri = string.Empty;
        if (string.IsNullOrEmpty(url) || allowedHosts == null) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        if (!string.Equals(uri.Scheme, "https", StringComparison.Ordinal)) return false;
        if (uri.UserInfo.Length != 0 || !uri.IsDefaultPort) return false;
        string host = uri.Host;
        if (host.Length == 0) return false;
        if (!HostAllowed(host, allowedHosts)) return false;
        absoluteUri = uri.AbsoluteUri;
        return true;
    }

    private static bool HostAllowed(string host, IReadOnlyList<string> allowedHosts)
    {

        foreach (string entry in allowedHosts)
        {
            if (string.IsNullOrEmpty(entry)) continue;
            if (entry.StartsWith("*.", StringComparison.Ordinal))
            {
                string suffix = entry.Substring(1); // ".x"
                if (suffix.Length > 1
                    && host.Length > suffix.Length
                    && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (string.Equals(host, entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Fall back to the browser only when the panel refused before navigating. After a
    /// navigate the one-time code is spent (a dead link), and UrlNotAllowed must not bypass the engine's list.</summary>
    public static bool FallbackToBrowser(PanelResult r) => r == PanelResult.RefusedBeforeNavigate;

    /// <summary>^[a-z0-9-]{1,32}/[0-9a-f]{32}$ - the engine's panel profile key (server/token).</summary>
    public static bool IsValidProfileKey(string? key)
    {
        if (key == null) return false;
        int slash = key.IndexOf('/');
        if (slash < 1 || slash > 32) return false;
        if (key.Length - slash - 1 != 32) return false;
        for (int i = 0; i < slash; i++)
        {
            char c = key[i];
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-')) return false;
        }
        for (int i = slash + 1; i < key.Length; i++)
        {
            char c = key[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    /// <summary>Refused if the URL is not allowed; Browser if there is no panel or no valid key; else Panel.</summary>
    public static WebOpenRoute Route(WebOpenRequest r, bool panelAvailable)
    {
        if (!IsAllowedUrl(r.Url, r.AllowedHosts)) return WebOpenRoute.Refused;
        if (!panelAvailable || !IsValidProfileKey(r.ProfileKey)) return WebOpenRoute.Browser;
        return WebOpenRoute.Panel;
    }
}
