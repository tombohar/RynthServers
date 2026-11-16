// The per-account web profile key: each account gets its own WebView2 profile folder, so website
// cookies never cross accounts. Pure, so RynthServerPack.Tests compiles it directly.
using System;
using System.Security.Cryptography;
using System.Text;
using RynthCore.Plugin.ServerPack;

namespace RynthCore.Plugin.RynthDreamWeave;

internal static class WebProfile
{
    /// <summary>
    /// "serverId/" plus the first 32 lowercase hex chars of SHA-256(UTF-8(serverId + "\n" + the trimmed,
    /// lower-cased account)); null when there is no account. The server id (not the world name) keeps
    /// one site's cookies together. The hash keeps the account name out of the folder name. A result that fails
    /// ServerWebOpen.IsValidProfileKey (a serverId outside ^[a-z0-9-]{1,32}$) is null, never a bad key.
    /// </summary>
    public static string? Key(string serverId, string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(serverId + "\n" + account.Trim().ToLowerInvariant()));
        string key = serverId + "/" + Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 32);
        return ServerWebOpen.IsValidProfileKey(key) ? key : null;
    }
}
