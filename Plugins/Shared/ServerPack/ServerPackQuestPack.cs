// ServerPackQuestPack.cs - A server plugin's own quest list, offered to quest readers such as RynthOracle
// through "RynthServer.QuestPack" (Plugins/Shared/RynthServerQuestPackApi.cs; the table is in
// ServerPackInterface.cs, answered by ServerPackExports' RynthPluginQueryInterface).
//
// A server plugin has a pack when its project has Resources\quests.<server id>.csv: ServerPlugin.props
// embeds it as "ServerPack.quests.csv", and this file serves it. The pack is the server's data, kept
// with the server's plugin (the Conquest, Levistras and Aelrynth packs lived in RynthOracle until
// 2026-10-21); RynthOracle merges whichever pack the active server's plugin offers and names no server.
// Its "tag" is the server's display name, which is also what the master quest list's Server column
// calls it ("Conquest", "Levistras").
// Pure apart from the embedded resource read: RynthServerPack.Tests covers it.
using System;
using System.IO;
using System.Text;

namespace RynthCore.Plugin.ServerPack;

internal static class ServerPackQuestPack
{
    /// <summary>The embedded pack's resource name (ServerPlugin.props).</summary>
    public const string ResourceName = "ServerPack.quests.csv";

    /// <summary>The info JSON's schema (RynthServerQuestPack.Schema).</summary>
    public const int Schema = 1;

    /// <summary>The pack never changes while the plugin runs (it is embedded): one revision.</summary>
    public const int Revision = 1;

    private static string? _csv;
    private static bool _loaded;

    /// <summary>The embedded pack's CSV, or "" when this plugin has none. Read once. Any thread.</summary>
    public static string Csv
    {
        get
        {
            if (_loaded) return _csv ?? string.Empty;
            string text = string.Empty;
            try
            {
                using Stream? s = typeof(ServerPackQuestPack).Assembly.GetManifestResourceStream(ResourceName);
                if (s != null)
                {
                    using var r = new StreamReader(s, Encoding.UTF8);
                    text = r.ReadToEnd();
                }
            }
            catch
            {
                text = string.Empty;   // an unreadable pack is no pack: never take the plugin down
            }
            _csv = Usable(text) ? text : string.Empty;
            _loaded = true;
            return _csv;
        }
    }

    /// <summary>True when the text looks like a quest list: a header with a QuestFlag column and a row.</summary>
    public static bool Usable(string? csv)
    {
        if (string.IsNullOrEmpty(csv)) return false;
        int end = csv.IndexOf('\n');
        string header = (end < 0 ? csv : csv[..end]).TrimStart('﻿').Trim();
        bool hasFlag = false;
        foreach (string col in header.Split(','))
        {
            string c = col.Trim().Trim('"').Replace(" ", string.Empty);
            if (c.Equals("QuestFlag", StringComparison.OrdinalIgnoreCase) || c.Equals("Flag", StringComparison.OrdinalIgnoreCase)) hasFlag = true;
        }
        return hasFlag && CountRows(csv) > 0;
    }

    /// <summary>Non-blank lines after the header (a quoted field never spans lines in these lists).</summary>
    public static int CountRows(string? csv)
    {
        if (string.IsNullOrEmpty(csv)) return 0;
        int rows = 0;
        bool header = true;
        foreach (string line in csv.Split('\n'))
        {
            if (header) { header = false; continue; }
            if (line.Trim().Length > 0) rows++;
        }
        return rows;
    }

    /// <summary>The info JSON for a pack (plain ASCII), or "" when <paramref name="csv"/> is no pack.</summary>
    public static string BuildInfoJson(ServerPackInfo info, string pluginName, string pluginVersion, string? csv)
    {
        if (!Usable(csv)) return string.Empty;
        var sb = new StringBuilder(256);
        sb.Append("{\"schema\":").Append(Schema);
        Field(sb, "server", info.Id);
        Field(sb, "tag", info.Name);
        Field(sb, "plugin", pluginName);
        Field(sb, "version", pluginVersion);
        sb.Append(",\"rows\":").Append(CountRows(csv));
        sb.Append(",\"bytes\":").Append(Encoding.UTF8.GetByteCount(csv!));
        sb.Append(",\"revision\":").Append(Revision);
        sb.Append('}');
        return sb.ToString();
    }

    private static void Field(StringBuilder sb, string name, string? value)
    {
        sb.Append(",\"").Append(name).Append("\":\"");
        foreach (char c in value ?? string.Empty)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default:
                    if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
