// ThreadsCommands.cs - "/rthreads ..." words and what each answers (2026-10-28: every command answers in
// chat, RynthSuite's Plugins/Shared/CommandReply.cs). RynthThreads can't tell a typed /rthreads from a
// meta's, so each one answers. Pure: RynthThreads.Tests covers it.
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthThreads;

internal enum ThreadsVerb { Window, Stats, Export, Help, Unknown }

internal static class ThreadsCommands
{
    public const string Command = "/rthreads";

    public const string Usage = Command + " [stats | export | help]";

    public static ThreadsVerb Parse(string? arg, out string word)
    {
        string[] words = (arg ?? string.Empty).Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        word = words.Length > 0 ? words[0].ToLowerInvariant() : string.Empty;
        return word switch
        {
            "" => ThreadsVerb.Window,
            "stats" => ThreadsVerb.Stats,
            "export" => ThreadsVerb.Export,
            "help" or "?" => ThreadsVerb.Help,
            _ => ThreadsVerb.Unknown,
        };
    }

    public static string Help =>
        $"DreamWeave only: records Fragment Press results and shows the odds of each modifier per component. {Command} shows or hides the window; {Command} stats; {Command} export.";

    public static string Window(bool shown) => CommandReply.Shown("The Threads window", shown);

    public static string NoWindows =>
        $"This RynthCore engine can't show plugin windows (it needs script windows, API v71, with ImGui on). Presses are still recorded: {Command} stats.";

    public static string Unknown(string word) => $"Unknown '{Command} {word}'. Usage: {Usage}";
}
