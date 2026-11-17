// SkillsReader.cs - Reads the character for the Skills panel and spends XP for it, on the plugin tick.
//
// Reading: while the panel is drawn it asks for a read about four times a second (ReadDue, render
// thread; ReadGate keeps one in flight and re-asks for one that was lost); the tick then reads the
// engine's training numbers (GetTrainingInfo, v79: the engine refreshes them every 2 s while asked, and
// at once after a raise) and the header's properties, and publishes one SkillsSheet. Nothing is read
// while the panel is closed. Logout forgets the character (Clear).
//
// Spending: the panel's raise arrow and "+10" queue a press (a raise, or a train for an untrained skill);
// the tick sends it at the cost the newest numbers say, and the engine refuses it when its own numbers
// differ (RaiseResult.CostChanged). Presses made while the last raise is on its way are held (HeldRaises)
// and sent one per answer, and the numbers are read every frame meanwhile, so +10 can be clicked as fast
// as the server answers. Like retail's arrow there is no confirm.
using System;
using System.Threading;
using RynthCore.PluginSdk;

namespace RynthCore.Plugin.RynthDreamWeave;

internal sealed class SkillsReader
{
    private const uint IntGender = 113, IntHeritage = 188, IntPkStatus = 134, IntAllegianceRank = 30, IntCharacterTitleId = 261;
    private const uint QuadAvailableLuminance = 6, QuadMaximumLuminance = 7;
    private const uint IntLevel = 25, QuadTotalExperience = 1;

    private readonly TrainingEntryNative[] _entries = new TrainingEntryNative[128];
    private SkillsSheet _sheet = SkillsSheet.Empty;
    private readonly ReadGate _gate = new();
    private string _message = "";
    private readonly HeldRaises _held = new();
    private Action<string>? _chat;

    /// <summary>The newest snapshot (any thread).</summary>
    public SkillsSheet Sheet => Volatile.Read(ref _sheet);

    /// <summary>The last raise's refusal, for the footer ("" when none). Any thread.</summary>
    public string Message => Volatile.Read(ref _message);

    /// <summary>
    /// Render thread, each frame the panel draws: true when a read is due (the caller queues
    /// <see cref="Read"/> for the tick). At most four a second, one in flight (ReadGate); every frame while
    /// a raise is on its way or a press waits, to send the next one as soon as it may go.
    /// </summary>
    public bool ReadDue()
        => _gate.Due(Environment.TickCount64, _held.Count > 0 || Volatile.Read(ref _sheet).Busy);

    /// <summary>
    /// Logout (pump thread): forget the character, and any read the logout dropped from the tick's queue.
    /// 2026-11-14 (R-20261114-190620-f744): nothing called this, so the last character's numbers stayed on
    /// the panel after a relog until a read replaced them, and with a dropped read none ever did (ReadGate).
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _sheet, SkillsSheet.Empty);
        Volatile.Write(ref _message, "");
        _held.Drop();
        _gate.Reset();
    }

    /// <summary>Plugin tick: reads the numbers and publishes a new sheet when they are in.</summary>
    public void Read(RynthCoreHost host)
    {
        try
        {
            if (!host.HasTraining) { Volatile.Write(ref _message, "This RynthCore is too old for the Skills panel (needs plugin API v79)."); return; }
            if (!host.TryGetTrainingInfo(out TrainingInfoNative info, _entries, out int count)) return;
            var skills = new System.Collections.Generic.List<SkillLine>(64);
            var attrs = new AttributeLine?[AttributesPage.Rows.Length];
            uint health = 0, stam = 0, mana = 0;
            bool haveVitals = host.TryGetPlayerVitals(out health, out _, out stam, out _, out mana, out _);
            for (int i = 0; i < Math.Min(count, _entries.Length); i++)
            {
                TrainingEntryNative e = _entries[i];
                if (e.Kind is TrainingKind.Attribute or TrainingKind.Vital)
                {
                    int kind = e.Kind == TrainingKind.Attribute ? 1 : 2;
                    int row = AttributesPage.RowIndex(kind, e.Stype);
                    if (row < 0) continue;
                    var r = AttributesPage.Rows[row];
                    int current = kind == 2 && haveVitals ? (int)(e.Stype switch { 1 => health, 3 => stam, _ => mana }) : -1;
                    attrs[row] = new AttributeLine(kind, e.Stype, r.Name, r.Icon, e.Base, e.Buffed, e.CostOne, e.CostTen,
                        e.Raisable, e.AtTop, e.Ranks, e.XpSpent, current);
                    continue;
                }
                if (e.Kind != TrainingKind.Skill || e.Class is < 1 or > 3) continue;
                RetailSkill? table = RetailData.Skill(e.Stype);
                if (table == null) continue;   // a skill the retail table doesn't list (retired): retail doesn't show it either
                bool usable = (e.Flags & TrainingEntryFlags.UsableUntrained) != 0 || e.Class >= 2 || table.UsableUntrained;
                skills.Add(new SkillLine(e.Stype, table.Name, table.Icon, (SkillClass)e.Class, usable,
                    e.Base, e.Buffed, e.CostOne, e.CostTen, e.TrainCredits, e.Raisable, e.Trainable, e.AtTop,
                    e.Ranks, e.XpSpent));
            }

            uint me = host.GetPlayerId();
            string name = me != 0 && host.HasGetObjectName && host.TryGetObjectName(me, out string n) ? n : "";
            int gender = Int(host, me, IntGender), heritage = Int(host, me, IntHeritage), pk = Int(host, me, IntPkStatus);
            // Retail's header: the allegiance rank's title before the name ("Yeoman Drakkon"), and the
            // character title chosen in the Titles tab ("Obviously Bored") after sex and heritage.
            name = SkillsLayout.NameLine(name, gender, heritage, Int(host, me, IntAllegianceRank));
            int titleId = Int(host, me, IntCharacterTitleId);
            string title = SkillsLayout.Title(titleId);
            long? lumMax = Quad(host, me, QuadMaximumLuminance);
            long? lumAvail = Quad(host, me, QuadAvailableLuminance);
            // Level and total XP straight from the player's properties (2026-11-14, R-20261114-190620-f744):
            // the engine's property caches take the server's private updates (Level 25 on a level-up,
            // TotalExperience 1 on every award) as they land, while the training numbers are a snapshot the
            // engine republishes every 2 s at best, and only while its main-thread read of the character
            // succeeds. The snapshot's own values (the same caches, read on AC's main thread) stand in when
            // a property isn't known.
            int level = Int(host, me, IntLevel) is int lv and > 0 ? lv : info.Level;
            long totalXp = Quad(host, me, QuadTotalExperience) is long tx and >= 0 ? tx : info.TotalXp;
            TitleList titles = TitlesFor(me).WithDisplayed((uint)Math.Max(0, titleId));

            var attrList = new System.Collections.Generic.List<AttributeLine>(attrs.Length);
            foreach (AttributeLine? a in attrs) if (a != null) attrList.Add(a);
            var sheet = new SkillsSheet(name, SkillsLayout.Identity(gender, heritage, title), SkillsLayout.PkStatus(pk),
                level, totalXp, info.UnassignedXp, info.SkillCredits,
                lumMax is > 0 ? lumAvail ?? 0 : null, lumMax is > 0 ? lumMax : null,
                skills, info.Busy, info.SnapshotVersion, attrList, titles);
            Volatile.Write(ref _sheet, sheet);
            SendHeld(host);
        }
        finally
        {
            _gate.Done();
        }
    }

    /// <summary>
    /// Plugin tick: the raise arrow or "+10" was pressed (<see cref="RaiseTarget"/>). Sent at once when
    /// nothing is on its way, else held until the last raise's answer lands. A refusal is said in the
    /// footer and chat; the answer comes back in the numbers.
    /// </summary>
    public void Press(RynthCoreHost host, RaiseTarget target, Action<string> chat)
    {
        _chat = chat;
        _held.Press(target, Environment.TickCount64);
        Volatile.Write(ref _message, "");
        SendHeld(host);
    }

    /// <summary>Plugin tick: sends the next held press when the last raise has landed.</summary>
    private void SendHeld(RynthCoreHost host)
    {
        SkillsSheet sheet = Volatile.Read(ref _sheet);
        if (!_held.Next(Environment.TickCount64, sheet.Busy, out RaiseTarget t)) return;
        string name;
        RaiseResult r;
        if (t.Kind is RaiseTarget.Skill or RaiseTarget.Train)
        {
            SkillLine? skill = null;
            foreach (SkillLine s in sheet.Skills) if (s.Id == t.Id) { skill = s; break; }
            // Trained meanwhile (a train press) or not trained (a raise press): not what was clicked.
            if (skill == null || (t.Kind == RaiseTarget.Train) != (skill.Class < SkillClass.Trained)) { _held.Drop(); return; }
            name = skill.Name;
            if (t.Kind == RaiseTarget.Train)
                r = host.TrainSkill(skill.Id, skill.TrainCredits);
            else
            {
                // +10 is ten ranks, or the last ones when fewer than ten are left (retail's; SkillsLayout.TenStep).
                (uint ranks, long cost) = t.Ten ? SkillsLayout.Ten(skill) : (1u, skill.CostOne);
                if (ranks == 0) { _held.Drop(); return; }   // at the top now: the rest of the presses buy nothing
                r = host.Raise(TrainingKind.Skill, skill.Id, ranks, cost, out _);
            }
        }
        else
        {
            AttributeLine? a = null;
            foreach (AttributeLine x in sheet.AttributeRows) if (x.Kind == t.Kind && x.Stype == t.Id) { a = x; break; }
            if (a == null) { _held.Drop(); return; }
            name = a.Name;
            (uint ranks, long cost) = t.Ten ? AttributesPage.Ten(a) : (1u, a.CostOne);
            if (ranks == 0) { _held.Drop(); return; }
            r = host.Raise(a.IsVital ? TrainingKind.Vital : TrainingKind.Attribute, a.Stype, ranks, cost, out _);
        }
        _gate.AskNow();   // read again at once
        switch (r)
        {
            case RaiseResult.Sent:
                _held.Sent();
                return;
            // The numbers moved under it, or the last raise hasn't landed: it waits for the next read.
            case RaiseResult.CostChanged or RaiseResult.Stale or RaiseResult.NotReady or RaiseResult.Busy:
                return;
        }
        _held.Drop();
        string what = t.Kind is RaiseTarget.Skill or RaiseTarget.Train ? "skills" : "attributes";
        string why = r switch
        {
            RaiseResult.NotEnough => t.Kind == RaiseTarget.Train ? "Not enough skill credits." : "Not enough unassigned experience.",
            RaiseResult.NotRaisable => $"{name} can't be raised any further.",
            RaiseResult.Unavailable => $"This client can't raise {what} from a plugin.",
            RaiseResult.EngineTooOld => $"This RynthCore is too old to raise {what} (needs plugin API v79).",
            _ => $"{name} wasn't raised ({r}).",
        };
        Volatile.Write(ref _message, why);
        _chat?.Invoke(why);
    }

    // ── Titles (the server's list, kept per character: AttributesTitles.cs) ──

    private readonly object _titlesGate = new();
    private readonly System.Collections.Generic.Dictionary<uint, TitleList> _titles = new();
    private Func<uint, string?>? _loadTitles;
    private Action<uint, string>? _saveTitles;

    /// <summary>Where the titles are kept between sessions (the plugin's settings, by character id).</summary>
    public void SetTitleStore(Func<uint, string?> load, Action<uint, string> save)
    {
        _loadTitles = load;
        _saveTitles = save;
    }

    private TitleList TitlesFor(uint me)
    {
        if (me == 0) return TitleList.Empty;
        lock (_titlesGate)
        {
            if (_titles.TryGetValue(me, out TitleList? t)) return t;
            t = TitleList.Load(_loadTitles?.Invoke(me));
            _titles[me] = t;
            return t;
        }
    }

    /// <summary>Pump thread: game event 0x0029 (the whole list, at login) or 0x002B (one title) for <paramref name="me"/>.</summary>
    public void OnTitleEvent(uint me, uint eventType, ReadOnlySpan<byte> body)
    {
        if (me == 0) return;
        TitleList next;
        lock (_titlesGate)
        {
            TitleList now = _titles.TryGetValue(me, out TitleList? t) ? t : TitleList.Load(_loadTitles?.Invoke(me));
            if (eventType == 0x0029) { if (!TitleList.TryParseTable(body, out next)) return; }
            else next = now.WithUpdate(body);
            _titles[me] = next;
        }
        _saveTitles?.Invoke(me, next.Save());
        _gate.AskNow();   // show it at once
    }

    /// <summary>Plugin tick: show <paramref name="titleId"/> as the character's title (retail's "Set as Display Title").</summary>
    public void SetDisplayTitle(RynthCoreHost host, uint titleId, Action<string> chat)
    {
        if (titleId == 0) return;
        if (!host.HasInvokeChatParser || !host.InvokeChatParser("/rc title " + titleId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            chat("Couldn't set the title: this RynthCore can't send it (needs the engine from 2026-11-07).");
        _gate.AskNow();
    }

    private static int Int(RynthCoreHost host, uint id, uint stype)
        => id != 0 && host.HasGetObjectIntProperty && host.TryGetObjectIntProperty(id, stype, out int v) ? v : 0;


    private static long? Quad(RynthCoreHost host, uint id, uint stype)
        => id != 0 && host.TryGetObjectQuadProperty(id, stype, out long v) ? v : null;
}
