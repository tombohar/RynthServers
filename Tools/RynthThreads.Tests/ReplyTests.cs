using System;
using System.Collections.Generic;
using RynthCore.Plugin.RynthThreads;
using RynthCore.RynthAiTests.Harness;

namespace RynthThreads.Tests;

// Every /rthreads command answers in chat (2026-10-28). RunCommand needs the plugin's host, so the words,
// the verb each one is, and the lines are checked here.
internal static class ReplyTests
{
    public static void Register(Runner r)
    {
        r.Add("replies: every /rthreads word, typed right and wrong", Words);
        r.Add("replies: the window, help and unknown-word lines", Lines);
    }

    private static void Words()
    {
        var table = new (string Typed, ThreadsVerb Verb)[]
        {
            ("", ThreadsVerb.Window), ("stats", ThreadsVerb.Stats), ("STATS now", ThreadsVerb.Stats), ("export", ThreadsVerb.Export),
            ("help", ThreadsVerb.Help), ("?", ThreadsVerb.Help), ("odds", ThreadsVerb.Unknown),
        };
        var seen = new HashSet<ThreadsVerb>();
        foreach (var (typed, verb) in table)
        {
            Check.Eq(ThreadsCommands.Parse(typed, out _), verb, $"'/rthreads {typed}'");
            seen.Add(verb);
        }
        foreach (ThreadsVerb v in Enum.GetValues<ThreadsVerb>()) Check.True(seen.Contains(v), $"the table covers /rthreads' {v}");
        ThreadsCommands.Parse("Odds please", out string word);
        Check.Eq(word, "odds", "the unknown word, for its line");
    }

    private static void Lines()
    {
        Check.Eq(ThreadsCommands.Window(true), "The Threads window shown.", "toggle: shown");
        Check.Eq(ThreadsCommands.Window(false), "The Threads window hidden.", "toggle: hidden");
        Check.True(ThreadsCommands.Help.Contains("/rthreads stats") && ThreadsCommands.Help.Contains("/rthreads export"), "help names the verbs");
        Check.Eq(ThreadsCommands.Unknown("odds"), "Unknown '/rthreads odds'. Usage: /rthreads [stats | export | help]", "an unknown word");
        Check.True(ThreadsCommands.NoWindows.Contains("can't show plugin windows"), "no plugin windows: why");
    }
}
