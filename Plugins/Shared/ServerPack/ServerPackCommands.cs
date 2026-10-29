// ServerPackCommands.cs - "/rserver ..." for every server plugin: which words are its verbs and what each
// answers (2026-10-28: every command answers in chat, RynthSuite's Plugins/Shared/CommandReply.cs).
// The plugin can't tell a typed /rserver from a meta's, so each one answers. The window verbs say the
// window's state; auto/on/off are answered by RynthCore's "/rc server" (the override it saved); info
// lists what is known; help and an unknown word give the usage (the unknown word named first).
// Pure: RynthServerPack.Tests covers it.
using System;
using System.Collections.Generic;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.ServerPack;

internal enum ServerVerb { Show, Hide, Toggle, Info, Help, Auto, On, Off, Server, Unknown }

internal static class ServerPackCommands
{
    /// <summary>The verbs every server plugin answers (the server's own come from ServerCommandVerbs).</summary>
    public static readonly string[] SharedVerbs = { "show", "hide", "toggle", "info", "help", "on", "off", "auto" };

    /// <summary>Whether <paramref name="word"/> is a verb here (else it may be a server id: "/rserver coldeve info").</summary>
    public static bool IsVerb(string word, ICollection<string> serverVerbs)
    {
        string w = (word ?? string.Empty).ToLowerInvariant();
        return Array.IndexOf(SharedVerbs, w) >= 0 || serverVerbs.Contains(w);
    }

    /// <summary>The verb of "/rserver [verb ...]" (parts[0] is "/rserver"; none = toggle).</summary>
    public static ServerVerb Verb(string[] parts, ICollection<string> serverVerbs)
    {
        if (parts.Length < 2) return ServerVerb.Toggle;
        string w = parts[1].ToLowerInvariant();
        return w switch
        {
            "show" => ServerVerb.Show,
            "hide" => ServerVerb.Hide,
            "toggle" => ServerVerb.Toggle,
            "info" => ServerVerb.Info,
            "help" => ServerVerb.Help,
            "auto" => ServerVerb.Auto,
            "on" => ServerVerb.On,
            "off" => ServerVerb.Off,
            _ => serverVerbs.Contains(w) ? ServerVerb.Server : ServerVerb.Unknown,
        };
    }

    /// <summary>"/rserver [show|hide|toggle]": the window after it.</summary>
    public static string Window(string serverName, bool shown) => CommandReply.Shown($"The {serverName} window", shown);

    /// <summary>The usage, two lines.</summary>
    public static string[] Usage(string serverName) => new[]
    {
        $"/rserver [show|hide|toggle] - the {serverName} server card (also in the Mini Remote's Char section).",
        $"/rserver info - what is known about {serverName}; /rserver auto|on|off - this character's override.",
    };

    public static string Unknown(string word) => $"Unknown '/rserver {word}'.";
}
