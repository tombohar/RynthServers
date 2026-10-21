// AelrynthDifficulty.cs - Aelrynth's difficulty tier where the player stands, published for other plugins
// (2026-10-21; until then RynthAi read it itself, CreatureData/AwakenedTier.cs, added 2026-10-03).
//
// Aelrynth's awakened worlds and scaled dungeon copies hold the same monsters as real Dereth - same
// names, same wcids - with health, skills and damage scaled 5% a tier (resistances and weaknesses are
// not). Every monster in one copy or world shares one tier. The server tells the player's own client
// that tier as PropertyInt 9051 on the player (Aeshnidae.AwakenedRewards TierFeed.cs): a private update
// on login, teleport and every change; 0 = real Dereth.
//
// RynthAelrynth puts it in its "RynthServer.Profile" values (RynthServerProfile.ValueDifficultyTier and
// ValueDifficultyHealthPercent, Plugins/Shared/RynthServerProfileApi.cs). RynthAi reads them from the
// active server's plugin without knowing the server: learned HP, casts to kill, accuracy and fight times
// are kept per tier, a monster's HP is scaled by the percent, and the Damage panel shows the tier picker.
//
// Nothing is published off Aelrynth, nor before the property has been seen this login (an older server
// build that doesn't send it changes nothing): RynthAi then stays at tier 0, as on any other server.
// Pure (the plugin hands in the property read): RynthServerPack.Tests covers it.
using System;
using RynthCore.Plugin.ServerPack;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthAelrynth;

internal sealed class AelrynthDifficulty
{
    /// <summary>PropertyInt 9051 on the player: the difficulty tier where they stand.</summary>
    public const uint TierProperty = 9051;

    /// <summary>The server's scaling per tier (InstancesNoDat DifficultyPercentPerEnlightenment, 5.0).</summary>
    public const double HealthPercentPerTier = 5.0;

    /// <summary>How often the tier is read while on Aelrynth.</summary>
    public const long ReadMs = 500;

    private long _nextReadMs;

    /// <summary>The tier where the player stands; null until the server has sent it this login (or off Aelrynth).</summary>
    public int? Tier { get; private set; }

    /// <summary>Login and logout: nothing carries over (the values themselves are cleared at logout).</summary>
    public void Reset()
    {
        Tier = null;
        _nextReadMs = 0;
    }

    /// <summary>
    /// The plugin tick. On Aelrynth (<paramref name="active"/>) the tier is read at most every
    /// <see cref="ReadMs"/> (<paramref name="readTier"/>: the player's PropertyInt 9051, null when it isn't
    /// there) and, once seen, published in <paramref name="values"/> with the health percent per tier.
    /// Off Aelrynth both values are taken out. A read that fails after one succeeded keeps the last tier.
    /// </summary>
    public void Tick(long nowMs, bool active, Func<int?> readTier, ServerProfileValues values)
    {
        if (!active)
        {
            if (Tier != null) Reset();
            values.Remove(RynthServerProfile.ValueDifficultyTier);
            values.Remove(RynthServerProfile.ValueDifficultyHealthPercent);
            return;
        }
        if (nowMs < _nextReadMs) return;
        _nextReadMs = nowMs + ReadMs;
        if (readTier() is int tier) Tier = Math.Max(0, tier);
        if (Tier is not int known) return;
        values.Set(RynthServerProfile.ValueDifficultyTier, known);
        values.Set(RynthServerProfile.ValueDifficultyHealthPercent, HealthPercentPerTier);
    }

    /// <summary>One line for "/rserver info".</summary>
    public string Describe() => Tier is int t
        ? $"Difficulty tier here: {t} (+{HealthPercentPerTier:0.#}% monster health a tier), shared with RynthAi."
        : "Difficulty tier: not sent by the server yet this login (RynthAi stays at tier 0).";
}
