// HeldRaises.cs - presses of the raise arrow and "+10" made while the last raise is still on its way
// (2026-11-08, Tom: "I can't spam +10 unless I click off the skill and back onto it"). The engine sends
// one raise at a time and is Busy until the server's answer lands; a press made meanwhile used to be
// lost (the button was ghosted). Now it's held here and sent, at the new cost, as soon as the answer is
// in - one held press per answer, so five clicks of +10 are five raises of ten, as retail's arrows do.
//
// Pure: SkillsReader presses on the plugin tick and asks Next after each read.
using System;

namespace RynthCore.Plugin.RynthDreamWeave;

/// <summary>What a press raises: Kind 1 attribute, 2 vital, 3 skill (XP), 4 train a skill (credits).</summary>
internal readonly record struct RaiseTarget(int Kind, uint Id, bool Ten)
{
    public const int Attribute = 1, Vital = 2, Skill = 3, Train = 4;
}

internal sealed class HeldRaises
{
    /// <summary>At most this many presses wait (a stuck mouse doesn't spend everything).</summary>
    public const int Max = 10;
    /// <summary>Presses not sent this long after the last one are forgotten (the server never answered).</summary>
    public const long ForgetMs = 6000;

    private readonly object _gate = new();
    private RaiseTarget _target;
    private int _count;
    private long _lastPressMs;

    /// <summary>Presses waiting (any thread).</summary>
    public int Count { get { lock (_gate) return _count; } }

    /// <summary>
    /// A press. The same target adds to the ones waiting; another target replaces them (a click on another
    /// row or button is a new decision). Training a skill never waits more than once: once trained, the
    /// same arrow raises it, which a second click of "train" didn't ask for.
    /// </summary>
    public void Press(RaiseTarget t, long nowMs)
    {
        lock (_gate)
        {
            if (_count > 0 && t == _target) _count = Math.Min(_count + 1, t.Kind == RaiseTarget.Train ? 1 : Max);
            else { _target = t; _count = 1; }
            _lastPressMs = nowMs;
        }
    }

    /// <summary>The press to send now: none while <paramref name="busy"/> (the last raise hasn't landed).</summary>
    public bool Next(long nowMs, bool busy, out RaiseTarget t)
    {
        lock (_gate)
        {
            t = _target;
            if (_count == 0) return false;
            if (nowMs - _lastPressMs > ForgetMs) { _count = 0; return false; }
            return !busy;
        }
    }

    /// <summary>The press Next gave went out.</summary>
    public void Sent()
    {
        lock (_gate) if (_count > 0) _count--;
    }

    /// <summary>Forget them all (refused: not enough XP, at the top; or the character changed).</summary>
    public void Drop()
    {
        lock (_gate) _count = 0;
    }
}
