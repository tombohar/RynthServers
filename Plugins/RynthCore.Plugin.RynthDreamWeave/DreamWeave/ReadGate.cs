// ReadGate.cs - when the Character panel asks the plugin tick for a read of the character (2026-11-14,
// report R-20261114-190620-f744: a level-497 character's panel still said level 495, with level 495's
// "XP for next level", while AC's own appraisal of the character said 497).
//
// The panel (render thread) queues one read at a time for the tick and queues the next only once that one
// has run. The wait used to be a plain flag, set when a read was queued and cleared when it ran. A read that
// never ran left it set for good: the shared plugin empties the tick's queue at logout
// (ServerPackPlugin.OnLogout), so a read the panel queued in the last frames before a logout or character
// switch was dropped, and from then on no read was ever queued again while the plugin stayed loaded. The
// panel kept drawing the last numbers it had read (level, total XP, XP for next level, skills) through
// every level gained since. Now a read still waiting after LostAfterMs is taken as lost and queued again,
// and logout resets the gate (SkillsReader.Clear).
//
// Pure: SkillsReader asks Due on the render thread and calls Done on the tick.
using System;
using System.Threading;

namespace RynthCore.Plugin.RynthDreamWeave;

internal sealed class ReadGate
{
    /// <summary>At most this often while nothing is on its way (four reads a second).</summary>
    public const long EveryMs = 250;
    /// <summary>A queued read that hasn't run this long after it was queued is lost (its queue was dropped).</summary>
    public const long LostAfterMs = 3000;

    // When the last read was queued; AskNow puts it far back so the next Due says yes.
    private long _lastAskMs = long.MinValue / 4;
    // When the read waiting for the tick was queued; 0 while none is.
    private long _queuedAtMs;

    /// <summary>A read is queued and hasn't run yet (any thread).</summary>
    public bool Waiting => Volatile.Read(ref _queuedAtMs) != 0;

    /// <summary>
    /// Render thread, each frame the panel draws: true when a read is due (the caller then queues one for
    /// the tick, which calls <see cref="Done"/>). <paramref name="everyFrame"/>: a raise is on its way or a
    /// press waits, so no 250 ms spacing. Never while a queued read is still within <see cref="LostAfterMs"/>.
    /// </summary>
    public bool Due(long nowMs, bool everyFrame)
    {
        long queued = Volatile.Read(ref _queuedAtMs);
        if (queued != 0 && nowMs - queued < LostAfterMs) return false;
        if (nowMs - Volatile.Read(ref _lastAskMs) < (everyFrame ? 0 : EveryMs)) return false;
        // 0 means "none waiting", so a clock reading of 0 is kept as 1.
        if (Interlocked.CompareExchange(ref _queuedAtMs, nowMs == 0 ? 1 : nowMs, queued) != queued) return false;
        Volatile.Write(ref _lastAskMs, nowMs);
        return true;
    }

    /// <summary>Tick: the queued read ran (whatever it found).</summary>
    public void Done() => Volatile.Write(ref _queuedAtMs, 0);

    /// <summary>Read again at the next frame (after a raise, a title, a set title). Any thread.</summary>
    public void AskNow() => Volatile.Write(ref _lastAskMs, long.MinValue / 4);

    /// <summary>Logout: nothing waiting, the next login's first frame reads.</summary>
    public void Reset()
    {
        Volatile.Write(ref _queuedAtMs, 0);
        AskNow();
    }
}
