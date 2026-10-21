// IltDynamicStamps.cs - The Dynamic Stamps page: Infinite Leaftide's NPC hand-ins that put a one-time
// "Dynamic_N_..." flag on the character, which of them this character has, RynthNav's way there and
// the Parchment hand-in. SilentKelpie's (RynthSuite PR 12, RynthOracle 0.6.0, ported by hand onto our
// lineage 2026-10-19), first as rows of RynthOracle's Quests list ("/ro dynamic"); moved here on
// 2026-10-21 as RynthILT's own page: the Mini Remote's Quests section ("Dynamic Stamps", surface
// "section:ilt-dynstamps") and its own window ("/ilt stamps"). The list and its rules are
// IltDynamicStampList.cs.
//
// Quest flags: RynthILT reads every /myquests reply that passes through chat (RynthOracle's at login,
// the player's, RynthAi's polls) with RynthOracle's /myquests parser (QuestFlags.cs, linked source).
// "Read quest flags" sends one of its own and hides that reply. Go / Route / Arrow ask RynthNav
// ("/rnav <verb> a || b"); Give Parchment hands one Parchment from the packs to the stamp's NPC through
// RynthAi's "/ub give" (asked first, disabled without RynthAi or when it answers "Unrecognized /ub
// command"), then the flags are read again once the NPC has had time to answer.
//
// Threads: OnChat / Tick on the pump (dormant off ILT, as every Hub feature); Render on the render
// thread, reading the pump's snapshot and posting its buttons (IltHubContext.Post / Confirm).
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using ImGuiNET;
using RynthCore.Plugin.RynthOracle.Data;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.RynthILT.Hub;

internal sealed class IltDynamicStamps : IIltFeature
{
    /// <summary>The page counts as on screen this long after it was last drawn.</summary>
    private const long WatchMs = 4000;
    /// <summary>The packs are scanned for a Parchment at most this often while the page is on screen.</summary>
    private const long ParchmentScanMs = 1500;
    /// <summary>A /myquests read this long after a Parchment hand-in (the walk there and the NPC's answer).</summary>
    internal const long HandInRefreshMs = 8000;
    /// <summary>"Unrecognized /ub command" this soon after a give is the answer to it.</summary>
    private const long GiveRejectionMs = 5000;
    /// <summary>The RynthNav plugin and its command broker action.</summary>
    private const string NavPlugin = "RynthNav", NavAction = "rnav";

    private readonly IltHubContext _ctx;
    private readonly QuestFlagStore _flags = new();

    // Pump state.
    private int _builtRevision = -1;
    private string? _builtParchment;
    private bool _builtGiveOk;
    private long _parchmentScanAt;
    private string? _parchment;
    private long _refreshAt;
    private long _giveSentAt = long.MinValue / 2;
    private bool _giveMissing;

    // Shared: the render thread's last draw, the pump's snapshot and the last action's outcome.
    private long _watchedAt = long.MinValue / 2;
    private volatile Snapshot _snap = Snapshot.Empty;
    private volatile string _status = string.Empty;

    // Render thread only: the filters and the opened stamp.
    private string _search = string.Empty;
    private int _area;      // 0 = every area, else DynamicStampList.Areas[n - 1]
    private int _show;      // DynamicStampText.Show
    private string _selected = string.Empty;

    /// <summary>What the page draws, built on the pump.</summary>
    internal sealed class Snapshot
    {
        public static readonly Snapshot Empty = new();
        public bool Built;
        public IReadOnlyList<DynamicStampText.Row> Rows = Array.Empty<DynamicStampText.Row>();
        public IReadOnlyList<string> Unlisted = Array.Empty<string>();
        public int Have;
        public bool Loading, Disabled;
        public DateTime LastReadUtc = DateTime.MinValue;
        public long ThrottledUntilMs = long.MinValue / 2;
        /// <summary>A Parchment carried (null = none).</summary>
        public string? Parchment;
        /// <summary>Why Give Parchment can't be used (null = it can).</summary>
        public string? GiveReason;
    }

    public IltDynamicStamps(IltHubContext ctx) => _ctx = ctx;

    /// <summary>The flags read from /myquests (tests read them).</summary>
    internal QuestFlagStore Flags => _flags;

    /// <summary>The last snapshot (tests).</summary>
    internal Snapshot Current => _snap;

    /// <summary>The last action's outcome ("" = none yet).</summary>
    internal string Status => _status;

    /// <summary>Render thread (or a test): the page is on screen, so the pump keeps its snapshot fresh.</summary>
    internal void MarkWatched() => Interlocked.Exchange(ref _watchedAt, IltHubContext.NowMs);

    /// <summary>AC's thread (the chat bar): a "/myquests" went out, RynthILT's own or typed. Only interlocked state.</summary>
    public void NoteTypedMyQuests() => _flags.NoteTypedCommand();

    // ── Pump ───────────────────────────────────────────────────────────────

    public bool OnChat(string text)
    {
        long now = IltHubContext.NowMs;
        if (now - _giveSentAt < GiveRejectionMs && !_giveMissing && DynamicStampText.IsGiveRejection(text))
        {
            _giveMissing = true;
            _status = "RynthAi has no \"/ub give\": hand the Parchment over yourself.";
        }
        MyQuestsLine kind = _flags.OnChatLine(text, now);
        // Only the reply to RynthILT's own request is hidden; the player's and other plugins' show as they would.
        return _flags.AwaitingOurs && kind is MyQuestsLine.Quest or MyQuestsLine.Empty;
    }

    public void Tick(long nowMs)
    {
        _flags.Tick(nowMs);
        RefreshAfterHandIn(nowMs);
        if (nowMs - Interlocked.Read(ref _watchedAt) >= WatchMs) return;
        if (_parchmentScanAt == 0 || nowMs - _parchmentScanAt >= ParchmentScanMs)
        {
            _parchmentScanAt = nowMs;
            _parchment = CarriedParchment();
        }
        bool giveOk = DynamicStampText.GiveUnavailableReason(_ctx.Ai.Available, _giveMissing) == null;
        if (_flags.Revision == _builtRevision && _parchment == _builtParchment && giveOk == _builtGiveOk && _snap.Built) return;
        _builtRevision = _flags.Revision;
        _builtParchment = _parchment;
        _builtGiveOk = giveOk;
        Rebuild();
    }

    /// <summary>The snapshot from the flags, the carried Parchment and RynthAi's state. Pump thread.</summary>
    internal void Rebuild()
    {
        List<DynamicStampText.Row> rows = DynamicStampText.Rows(DynamicStampList.All, _flags);
        int have = 0;
        foreach (DynamicStampText.Row r in rows) if (r.Have) have++;
        _snap = new Snapshot
        {
            Built = true,
            Rows = rows,
            Unlisted = DynamicStampList.Unlisted(_flags),
            Have = have,
            Loading = _flags.Loading,
            Disabled = _flags.ServerDisabled,
            LastReadUtc = _flags.LastReadUtc,
            ThrottledUntilMs = _flags.ThrottledUntilMs,
            Parchment = _parchment,
            GiveReason = DynamicStampText.GiveUnavailableReason(_ctx.Ai.Available, _giveMissing),
        };
    }

    public void OnLogout()
    {
        _flags.Clear();
        _snap = Snapshot.Empty;
        _builtRevision = -1;
        _parchment = _builtParchment = null;
        _parchmentScanAt = 0;
        _refreshAt = 0;
        _giveSentAt = long.MinValue / 2;
        _giveMissing = false;
        _status = string.Empty;
    }

    /// <summary>Sends /myquests (its reply hidden). Pump thread.</summary>
    internal void RequestFlags()
    {
        long now = IltHubContext.NowMs;
        if (_flags.Request(cmd => _ctx.Host.HasInvokeChatParser && _ctx.Host.InvokeChatParser(cmd), now))
        {
            _status = "Reading your quest flags (/myquests)...";
            return;
        }
        int wait = _flags.ThrottleSecondsLeft(now);
        _status = wait > 0 ? $"The server answers one /myquests a minute; try again in {wait}s (the stamps shown are kept)."
            : _flags.Loading ? "Your quest flags are already being read."
            : "Your quest flags were asked for a moment ago; wait a few seconds.";
    }

    /// <summary>The read after a hand-in, when it is due: now, or when the server's once-a-minute wait ends.</summary>
    private void RefreshAfterHandIn(long now)
    {
        if (_refreshAt == 0 || now < _refreshAt || _flags.Loading) return;
        int wait = _flags.ThrottleSecondsLeft(now);
        if (wait > 0) { _refreshAt = now + wait * 1000L; return; }
        _refreshAt = 0;
        _flags.Request(cmd => _ctx.Host.HasInvokeChatParser && _ctx.Host.InvokeChatParser(cmd), now);
    }

    /// <summary>"/rnav &lt;verb&gt; a || b || c" to RynthNav. Pump thread.</summary>
    internal void Nav(string verb, DynamicStamp s)
    {
        string line = DynamicStampText.NavLine(verb, s);
        if (line.Length == 0) return;
        bool sent = _ctx.Host.HasSendPluginCommand && _ctx.Host.SendPluginCommand(NavPlugin, NavAction, line);
        _status = !sent ? "RynthNav isn't loaded (it plans and walks the route)."
            : verb == "go" ? "RynthNav is planning the route and walking it (its chat lines say how)."
            : verb == "arrow" ? "RynthNav's arrow points at it."
            : "RynthNav's route is in chat. Go walks it.";
    }

    /// <summary>The stamp in chat (RynthChat makes coordinates a RynthNav arrow link). Pump thread.</summary>
    internal void Print(DynamicStamp s)
    {
        bool have = s.Have(_flags);
        foreach (string line in DynamicStampText.PrintLines(s, have)) _ctx.Chat("[ILT Stamps] " + line);
    }

    /// <summary>Hands one <paramref name="parchment"/> to the stamp's NPC (RynthAi's "/ub give"). Pump thread.</summary>
    internal void Give(DynamicStamp s, string parchment)
    {
        string? why = DynamicStampText.GiveUnavailableReason(_ctx.Ai.Available, _giveMissing);
        if (why != null) { _status = why; return; }
        long now = IltHubContext.NowMs;
        _giveSentAt = now;   // an "Unrecognized /ub command" in the next moments turns the button off
        bool sent = _ctx.Host.HasInvokeChatParser && _ctx.Host.InvokeChatParser(DynamicStampText.GiveCommand(parchment, s.Npc));
        _parchmentScanAt = 0;   // read the packs again after the give
        // The stamp is a quest flag: read /myquests again once the NPC has had time to answer.
        if (sent) _refreshAt = now + HandInRefreshMs;
        _status = sent
            ? $"Giving {parchment} to {s.Npc} (RynthAi does the give; it says so in chat if {s.Npc} isn't in sight)."
            : "Could not send the give.";
    }

    /// <summary>
    /// The name of a Parchment carried (the main pack and its side packs, one level deep as AC packs
    /// go), or null: any item whose name has "Parchment" in it. Pump thread.
    /// </summary>
    private string? CarriedParchment()
    {
        var host = _ctx.Host;
        uint me = host.GetPlayerId();
        if (me == 0 || !host.HasGetContainerContents) return null;
        var buf = new uint[512];
        var packs = new List<uint> { me };
        for (int p = 0; p < packs.Count && p < 16; p++)
        {
            int n = Math.Min(host.GetContainerContents(packs[p], buf), buf.Length);
            for (int i = 0; i < n; i++)
            {
                uint id = buf[i];
                if (!host.TryGetObjectName(id, out string name)) continue;
                if (name.IndexOf("Parchment", StringComparison.OrdinalIgnoreCase) >= 0) return name;
                if (p == 0) packs.Add(id);
            }
        }
        return null;
    }

    // ── UI (render thread) ────────────────────────────────────────────────

    private static readonly string[] ShowNames = { "Every stamp", "Missing", "Had" };

    /// <summary>The page's body, into the current window (the Mini Remote's view or "/ilt stamps").</summary>
    public void Render()
    {
        MarkWatched();
        Snapshot s = _snap;
        SafeImGui.TextColored(IltStyle.ColTeal, "Dynamic Stamps");
        ImGui.SameLine();
        ImGui.TextDisabled("one-time quest flags (Dynamic_1_...) from handing items to NPCs");
        if (!s.Built)
        {
            ImGui.TextDisabled("Reading the list...");
            return;
        }

        int listed = s.Rows.Count;
        string bar = $"{s.Have} / {listed} listed stamps" + (s.Unlisted.Count > 0 ? $" (+{s.Unlisted.Count} not in the list)" : "");
        ImGui.ProgressBar(listed == 0 ? 0f : (float)s.Have / listed, new Vector2(-1, 0), bar);
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip("List: the players' ExtraDyn sheet (10.13.2026). Click a stamp for RynthNav's route to it and the Parchment hand-in.");

        long now = IltHubContext.NowMs;
        if (s.Disabled) SafeImGui.TextColored(IltStyle.ColHp, "This server has /myquests turned off, so the stamps can't be read.");
        else if (s.Loading) SafeImGui.TextColored(IltStyle.ColAmber, "Reading your quest flags...");
        else if (s.LastReadUtc == DateTime.MinValue) ImGui.TextDisabled("Quest flags not read yet: press Read quest flags (or type /myquests).");
        else SafeImGui.TextDisabled($"Quest flags read at {s.LastReadUtc.ToLocalTime():HH:mm}.");
        if (ImGui.SmallButton("Read quest flags##dystamps")) _ctx.Post(RequestFlags);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Sends /myquests (the reply is hidden). The server answers one a minute.");
        if (now < s.ThrottledUntilMs)
        {
            ImGui.SameLine();
            SafeImGui.TextDisabled($"again in {(s.ThrottledUntilMs - now + 999) / 1000}s");
        }

        ImGui.SetNextItemWidth(180);
        ImGui.InputText("Search##dystamps", ref _search, 64u);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Words to find: an NPC, place, area, reward, stamp or note.");
        ImGui.SameLine();
        var areas = new List<string>(DynamicStampList.Areas.Count + 1) { "Every area" };
        areas.AddRange(DynamicStampList.Areas);
        _area = Math.Clamp(_area, 0, areas.Count - 1);
        ImGui.SetNextItemWidth(150);
        ImGui.Combo("##dystamps.area", ref _area, areas.ToArray(), areas.Count);
        ImGui.SameLine();
        _show = Math.Clamp(_show, 0, ShowNames.Length - 1);
        ImGui.SetNextItemWidth(110);
        ImGui.Combo("##dystamps.show", ref _show, ShowNames, ShowNames.Length);

        string? area = _area > 0 ? areas[_area] : null;
        List<DynamicStampText.Row> rows = DynamicStampText.Filter(s.Rows, _search, area, (DynamicStampText.Show)_show);
        SafeImGui.TextDisabled($"{rows.Count} shown");

        DynamicStamp? open = null;
        bool openHave = false;
        float height = Math.Max(120f, ImGui.GetContentRegionAvail().Y - (_selected.Length > 0 ? 170f : 40f));
        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        if (ImGui.BeginTable("##dystamps", 4, flags, new Vector2(0, height)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("##have", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("NPC", ImGuiTableColumnFlags.None, 1.2f);
            ImGui.TableSetupColumn("Area", ImGuiTableColumnFlags.None, 0.9f);
            ImGui.TableSetupColumn("Place", ImGuiTableColumnFlags.None, 1.3f);
            ImGui.TableHeadersRow();
            for (int i = 0; i < rows.Count; i++)
            {
                DynamicStampText.Row r = rows[i];
                DynamicStamp st = r.Stamp;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                SafeImGui.TextColored(r.Have ? IltStyle.ColGreen : IltStyle.ColHp, r.Have ? "have" : "need");
                ImGui.TableNextColumn();
                bool selected = _selected == st.Stamp;
                if (ImGui.Selectable(Cut(st.Label, 40) + "##dy" + i, selected, ImGuiSelectableFlags.SpanAllColumns))
                {
                    _selected = selected ? string.Empty : st.Stamp;
                    // As its Quests row did: picking a stamp asks RynthNav for the route at once.
                    if (!selected && st.NavCandidates().Count > 0) _ctx.Post(() => Nav("route", st));
                }
                if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(Tooltip(st));
                ImGui.TableNextColumn();
                SafeImGui.TextDisabled(Cut(st.Area, 28));
                ImGui.TableNextColumn();
                SafeImGui.TextDisabled(Cut(st.Place, 40));
                if (selected) { open = st; openHave = r.Have; }
            }
            ImGui.EndTable();
        }
        if (rows.Count == 0) ImGui.TextDisabled("No stamps match.");

        if (_selected.Length > 0 && open == null)
            foreach (DynamicStampText.Row r in s.Rows)
                if (r.Stamp.Stamp == _selected) { open = r.Stamp; openHave = r.Have; break; }
        if (open != null) RenderSelected(s, open, openHave);

        if (s.Unlisted.Count > 0 && ImGui.CollapsingHeader($"On this character, not in the list ({s.Unlisted.Count})##dyunlisted"))
            foreach (string f in s.Unlisted) SafeImGui.TextDisabled(f);
        string status = _status;
        if (status.Length > 0) SafeImGui.TextDisabled(status);
    }

    /// <summary>The opened stamp: where RynthNav goes, Go / Route / Arrow, Chat, and the Parchment hand-in.</summary>
    private void RenderSelected(Snapshot s, DynamicStamp st, bool have)
    {
        List<string> targets = st.NavCandidates();
        ImGui.SeparatorText("Navigate to " + Cut(st.Label, 40));
        SafeImGui.TextDisabled(targets.Count == 0
            ? "No place, coordinates or area to go to for this stamp."
            : "Where: " + Cut(string.Join("  or  ", targets), 90));
        if (targets.Count > 0)
        {
            if (ImGui.Button("Go (auto nav)##dynav")) _ctx.Post(() => Nav("go", st));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("RynthNav plans the route (walking, portals, recalls, the dungeon's entrance) and walks it.");
            ImGui.SameLine();
            if (ImGui.Button("Route##dynav")) _ctx.Post(() => Nav("route", st));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Only plan it: RynthNav prints the steps in chat.");
            ImGui.SameLine();
            if (ImGui.Button("Arrow##dynav")) _ctx.Post(() => Nav("arrow", st));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Only point RynthNav's arrow at it.");
            ImGui.SameLine();
        }
        if (ImGui.Button("Chat##dynav")) _ctx.Post(() => Print(st));
        ImGui.SameLine();
        if (ImGui.Button("Close##dynav")) _selected = string.Empty;
        SafeImGui.TextColored(have ? IltStyle.ColGreen : IltStyle.ColAmber, have ? "You have this stamp." : "You don't have this stamp yet.");

        // The hand-in: a Parchment from your packs to the stamp's NPC (RynthAi's /ub give), asked first.
        if (st.Npc.Length == 0) return;
        string? parchment = s.Parchment;
        if (parchment == null)
        {
            SafeImGui.TextDisabled("No Parchment in your packs to give " + Cut(st.Npc, 30) + ".");
            return;
        }
        if (s.GiveReason != null)
        {
            // Greyed text in place of the button, so it can't be clicked into a command nobody answers.
            SafeImGui.TextDisabled("[Give " + Cut(parchment, 24) + "]");
            if (ImGui.IsItemHovered()) SafeImGui.SetTooltip(s.GiveReason);
            return;
        }
        if (ImGui.Button("Give " + Cut(parchment, 24).Replace("#", "") + "##dygive"))
            _ctx.Confirm("Give Parchment", $"Give one {parchment} to {st.Npc}? It leaves your pack for good.", () => Give(st, parchment), "Give it");
        if (ImGui.IsItemHovered())
            SafeImGui.SetTooltip($"Hands one {parchment} to {st.Npc} (it asks first). Stand near them (or Go first): AC walks you the last bit.");
    }

    /// <summary>A stamp's hover text: area, place, reward and note.</summary>
    private static string Tooltip(DynamicStamp s)
    {
        var parts = new List<string>(4);
        foreach (string p in new[] { s.Area, s.Place, s.Reward.Length > 0 ? "gives " + s.Reward : "", s.Note })
            if (!string.IsNullOrWhiteSpace(p)) parts.Add(p);
        return Cut(string.Join("  -  ", parts), 300);
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 3)) + "...";
}
