// IltDynamicStampList.cs - Infinite Leaftide's Dynamic Stamps: the list (Resources\dynstamps.csv) and
// the rules that tell a stamp had from one missing. SilentKelpie's (RynthSuite PR 12, RynthOracle 0.6.0,
// ported by hand onto our lineage 2026-10-19), where the stamps were rows of RynthOracle's Quests list;
// moved here on 2026-10-21 with RynthOracle's other Infinite Leaftide parts (one server's features live
// in its server plugin). The page that shows them is IltDynamicStamps.cs.
// The flags come from /myquests through RynthOracle's parser (QuestFlags.cs, linked source).
// Pure: RynthILT.Tests covers it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using RynthCore.Plugin.RynthOracle.Data;

namespace RynthCore.Plugin.RynthILT.Hub;

/// <summary>
/// One Infinite Leaftide Dynamic Stamp (dynstamps.csv): an NPC hand-in that puts a
/// "Dynamic_N_..." quest flag on the character. List from the players' "ExtraDyn" sheet
/// (10.13.2026, one sheet per area, the wiki's Dynamic Stamps page); NPCs that give no stamp
/// are left out. A stamp is a one-time flag: having it in /myquests means it's done.
/// </summary>
internal sealed class DynamicStamp
{
    public string Area = "";
    /// <summary>Where in the area (a town, a dungeon, coordinates), or "".</summary>
    public string Place = "";
    public string Npc = "";
    /// <summary>What the NPC gives back (the sheet's "X gives you Y"), or "".</summary>
    public string Reward = "";
    /// <summary>The quest flag, lower case (as QuestFlagStore keys it).</summary>
    public string Stamp = "";
    public string Note = "";

    /// <summary>The NPC, else the place (a few sheet rows only name the spot).</summary>
    public string Label => Npc.Length > 0 ? Npc : Place;

    /// <summary>
    /// The flag this character has for the stamp, or null. The sheet has a few stamps copied
    /// without their closing "!" or cut short: those also match with "!" added and, when at
    /// least <see cref="MinPrefixLength"/> long, the one Dynamic flag on the character starting
    /// with them.
    /// </summary>
    public string? MatchedFlag(QuestFlagStore flags)
    {
        if (flags.TryGet(Stamp, out _)) return Stamp;
        if (Stamp.EndsWith('!')) return null;
        if (flags.TryGet(Stamp + "!", out _)) return Stamp + "!";
        if (Stamp.Length < MinPrefixLength) return null;
        string? only = null;
        foreach (string key in flags.Flags.Keys)
        {
            if (!key.StartsWith(Stamp, StringComparison.OrdinalIgnoreCase)) continue;
            if (only != null) return null;   // more than one: can't tell which
            only = key;
        }
        return only;
    }

    public bool Have(QuestFlagStore flags) => MatchedFlag(flags) != null;

    // Sheet areas that are groupings, not places on the map.
    private static readonly HashSet<string> NotPlaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "Looking Around", "Odd Stamps", "Odd Stamps 2",
    };

    /// <summary>
    /// Where RynthNav should go for this stamp, best first (RynthNav takes the first that is on its
    /// map): the coordinates in the note or the place, the place and its parts ("Mhoire Castle -
    /// Great Hall" also tries "Mhoire Castle"), the NPC, then the area. Empty when there is nothing.
    /// </summary>
    public List<string> NavCandidates()
    {
        var list = new List<string>();
        void Add(string s)
        {
            s = s.Trim().Trim('.', ',', ';', ':').Trim();
            if (s.Length >= 4 && !char.IsDigit(s[0]) && !list.Contains(s, StringComparer.OrdinalIgnoreCase)) list.Add(s);
        }

        foreach (string text in new[] { Note, Place })
            if (NavTargets.FirstCoords(text) is string c && !list.Contains(c)) list.Add(c);

        string place = NavTargets.WithoutCoords(Place);
        place = Regex.Replace(place, @"\b(at|in the|nearby)\b", " ", RegexOptions.IgnoreCase);
        place = Regex.Replace(place, @"\s+", " ").Trim();
        Add(place);
        foreach (string part in place.Split(new[] { " - ", " / ", "(", ")" }, StringSplitOptions.RemoveEmptyEntries))
            Add(part);
        Add(Npc);
        if (!NotPlaces.Contains(Area)) Add(Area);
        return list;
    }

    /// <summary>A truncated stamp shorter than this is matched exactly only (too many flags share a short stem).</summary>
    public const int MinPrefixLength = 14;
}

internal static class DynamicStampList
{
    /// <summary>Dynamic stamp quest flags start with this (lower case).</summary>
    public const string FlagPrefix = "dynamic_";

    /// <summary>The embedded list's resource name (the csproj).</summary>
    public const string ResourceName = "RynthILT.dynstamps.csv";

    public static List<DynamicStamp> All { get; } = Load();

    /// <summary>The areas in list order (Facility Hub, Looking Around, ...).</summary>
    public static List<string> Areas { get; } = BuildAreas();

    /// <summary>
    /// Dynamic stamps on this character that the list doesn't have (new or unlisted NPCs), so the
    /// page can show them and the count isn't only what the sheet knows.
    /// </summary>
    public static List<string> Unlisted(QuestFlagStore flags)
    {
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DynamicStamp s in All)
            if (s.MatchedFlag(flags) is string f) matched.Add(f);
        var extra = new List<string>();
        foreach (string key in flags.Flags.Keys)
            if (key.StartsWith(FlagPrefix, StringComparison.OrdinalIgnoreCase) && !matched.Contains(key))
                extra.Add(key);
        extra.Sort(StringComparer.OrdinalIgnoreCase);
        return extra;
    }

    private static List<DynamicStamp> Load()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream == null) return new List<DynamicStamp>();
            using var r = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Parse(r);
        }
        catch (Exception)
        {
            // A missing or unreadable list must not take the page down with it.
            return new List<DynamicStamp>();
        }
    }

    /// <summary>
    /// Reads a dynstamps.csv (a header row, then one NPC hand-in a row). A row whose stamp isn't a
    /// "dynamic_" flag, or repeats one already read, is skipped; a missing column reads as empty.
    /// Internal for the tests.
    /// </summary>
    internal static List<DynamicStamp> Parse(TextReader r)
    {
        var list = new List<DynamicStamp>();
        Dictionary<string, int> col = Csv.MapColumns(r.ReadLine() ?? "");
        int cArea = Csv.Column(col, "Area"), cPlace = Csv.Column(col, "Place"), cNpc = Csv.Column(col, "Npc"),
            cReward = Csv.Column(col, "Reward"), cStamp = Csv.Column(col, "Stamp"), cNote = Csv.Column(col, "Note");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (r.ReadLine() is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string[] f = Csv.ParseLine(line);
            string stamp = Csv.Field(f, cStamp).Trim().ToLowerInvariant();
            if (!stamp.StartsWith(FlagPrefix, StringComparison.Ordinal) || !seen.Add(stamp)) continue;
            list.Add(new DynamicStamp
            {
                Area = Csv.Field(f, cArea),
                Place = Csv.Field(f, cPlace),
                Npc = Csv.Field(f, cNpc),
                Reward = Csv.Field(f, cReward),
                Stamp = stamp,
                Note = Csv.Field(f, cNote),
            });
        }
        return list;
    }

    private static List<string> BuildAreas()
    {
        var areas = new List<string>();
        foreach (DynamicStamp s in All)
            if (!areas.Contains(s.Area)) areas.Add(s.Area);
        return areas;
    }
}

/// <summary>
/// What the Dynamic Stamps page lists and sends, worked out without the game (the page's buttons and
/// RynthILT.Tests use it): the rows with their status, the filters, RynthNav's line and the hand-in.
/// </summary>
internal static class DynamicStampText
{
    /// <summary>One listed stamp and whether this character has it.</summary>
    internal sealed record Row(DynamicStamp Stamp, bool Have);

    /// <summary>The Show filter: every stamp, the missing ones, or the ones had.</summary>
    internal enum Show { All, Missing, Have }

    /// <summary>Every listed stamp, in list order, with its status from <paramref name="flags"/>.</summary>
    public static List<Row> Rows(IReadOnlyList<DynamicStamp> stamps, QuestFlagStore flags)
    {
        var rows = new List<Row>(stamps.Count);
        foreach (DynamicStamp s in stamps) rows.Add(new Row(s, s.Have(flags)));
        return rows;
    }

    /// <summary>
    /// The rows the page shows: in <paramref name="area"/> ("" = every area), matching
    /// <paramref name="show"/>, and with every word of <paramref name="search"/> in the NPC, place,
    /// reward, area, stamp or note (any case).
    /// </summary>
    public static List<Row> Filter(IReadOnlyList<Row> rows, string? search, string? area, Show show)
    {
        string[] terms = (search ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var shown = new List<Row>();
        foreach (Row r in rows)
        {
            DynamicStamp s = r.Stamp;
            if (!string.IsNullOrEmpty(area) && !string.Equals(s.Area, area, StringComparison.Ordinal)) continue;
            if (show == Show.Missing && r.Have || show == Show.Have && !r.Have) continue;
            string hay = $"{s.Npc} {s.Place} {s.Reward} {s.Area} {s.Stamp} {s.Note}";
            bool all = true;
            foreach (string t in terms)
                if (hay.IndexOf(t, StringComparison.OrdinalIgnoreCase) < 0) { all = false; break; }
            if (all) shown.Add(r);
        }
        return shown;
    }

    /// <summary>
    /// "&lt;verb&gt; a || b || c" for RynthNav's "/rnav" (go walks the route, route plans it in chat,
    /// arrow points at it; RynthNav takes the first candidate on its map), or "" when there is nowhere to go.
    /// </summary>
    public static string NavLine(string verb, DynamicStamp s)
    {
        List<string> targets = s.NavCandidates();
        return targets.Count == 0 ? string.Empty : verb + " " + string.Join(" || ", targets);
    }

    /// <summary>A name made safe to put inside a chat command: no control characters.</summary>
    public static string ForCommand(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char ch in name) if (ch >= ' ' && ch != '\u007f') sb.Append(ch);
        return sb.ToString().Trim();
    }

    /// <summary>"/ub give 1 &lt;parchment&gt; to &lt;npc&gt;": RynthAi finds the NPC by name and hands it over.</summary>
    public static string GiveCommand(string parchment, string npc) => $"/ub give 1 {ForCommand(parchment)} to {ForCommand(npc)}";

    /// <summary>True for RynthAi's answer to a "/ub give" it doesn't have.</summary>
    public static bool IsGiveRejection(string? line) => line != null && line.IndexOf("Unrecognized /ub", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Why Give Parchment can't be used now (its tooltip); null when it can.</summary>
    public static string? GiveUnavailableReason(bool rynthAiLoaded, bool giveMissing)
    {
        if (giveMissing)
            return "The loaded RynthAi has no \"/ub give\" (it answered \"Unrecognized /ub command\"). Update RynthAi, or hand the Parchment over yourself.";
        if (!rynthAiLoaded)
            return "Needs RynthAi: the hand-in is RynthAi's \"/ub give\". It isn't loaded (or you aren't logged in), so give the Parchment to the NPC yourself.";
        return null;
    }

    /// <summary>The chat lines for a stamp: the NPC, its stamp and whether this character has it, where, the reward and any coordinates.</summary>
    public static List<string> PrintLines(DynamicStamp s, bool have)
    {
        string where = s.Place.Length > 0 ? $"{s.Area}, {s.Place}" : s.Area;
        string details = where + (s.Reward.Length > 0 ? $" - gives {s.Reward}" : "") + (s.Note.Length > 0 ? $" - {s.Note}" : "");
        var lines = new List<string> { $"{s.Label} ({s.Stamp}) - {(have ? "have" : "missing")}" };
        if (details.Length > 0) lines.Add(details);
        return lines;
    }
}
