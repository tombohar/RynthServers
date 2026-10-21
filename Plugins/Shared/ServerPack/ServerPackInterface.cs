// ServerPackInterface.cs - A server plugin's side of "RynthServer.Profile"
// (Plugins/Shared/RynthServerProfileApi.cs): the server's settings as JSON (ServerPackProfile) for
// other plugins, answered through RynthPluginQueryInterface (ServerPackExports). Since 2026-10-21 also
// "RynthServer.QuestPack" (Plugins/Shared/RynthServerQuestPackApi.cs): the server's own quest list
// (ServerPackQuestPack), offered only by a plugin whose project has one.
//
// The tables are allocated once per loaded copy of the plugin and never freed (a plugin DLL is never
// unloaded); after Release() they report Version 0 and their functions answer 0. Before the plugin is
// up, or between characters, the profile still describes the server (active false, no world).
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using RynthCore.Plugin.Shared;

namespace RynthCore.Plugin.ServerPack;

internal static unsafe class ServerPackInterface
{
    private static RynthServerProfileApiV1* _table;
    private static RynthServerQuestPackApiV1* _packTable;
    private static volatile bool _released;
    // The quest pack's info JSON, built once (the pack is embedded and never changes).
    private static string? _packInfo;

    /// <summary>The last JSON handed out, and what it was built for (rebuilt only when that changes).</summary>
    private sealed record Snapshot(bool Active, string World, int ValuesRevision, string Json);
    private static Snapshot? _last;

    /// <summary>The table for (<paramref name="iface"/>, <paramref name="version"/>), or 0 when it isn't ours.</summary>
    public static IntPtr Query(string iface, uint version)
    {
        if (iface == RynthServerQuestPackApiV1.InterfaceName) return QueryQuestPack(version);
        if (iface != RynthServerProfileApiV1.InterfaceName || version < 1 || version > RynthServerProfileApiV1.InterfaceVersion || _released)
            return IntPtr.Zero;
        if (_table == null)
        {
            var t = (RynthServerProfileApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthServerProfileApiV1));
            t->StructSize = (uint)sizeof(RynthServerProfileApiV1);
            t->GetProfileJson = &GetProfileJson;
            t->Version = RynthServerProfileApiV1.InterfaceVersion;
            _table = t;
        }
        return (IntPtr)_table;
    }

    /// <summary>
    /// "RynthServer.QuestPack": the table when this plugin has a pack (Resources\quests.&lt;id&gt;.csv),
    /// else 0, so a reader sees "no pack" the same way as "no such plugin".
    /// </summary>
    private static IntPtr QueryQuestPack(uint version)
    {
        if (version < 1 || version > RynthServerQuestPackApiV1.InterfaceVersion || _released) return IntPtr.Zero;
        if (PackInfoJson().Length == 0) return IntPtr.Zero;
        if (_packTable == null)
        {
            var t = (RynthServerQuestPackApiV1*)NativeMemory.AllocZeroed((nuint)sizeof(RynthServerQuestPackApiV1));
            t->StructSize = (uint)sizeof(RynthServerQuestPackApiV1);
            t->GetPackInfoJson = &GetPackInfoJson;
            t->GetPackCsv = &GetPackCsv;
            t->Version = RynthServerQuestPackApiV1.InterfaceVersion;
            _packTable = t;
        }
        return (IntPtr)_packTable;
    }

    /// <summary>The pack's info JSON ("" without a pack). Any thread.</summary>
    private static string PackInfoJson() =>
        _packInfo ??= ServerPackQuestPack.BuildInfoJson(ServerPackPlugin.StaticInfo, RynthPluginInfo.Name,
            RynthPluginInfo.Version.Length > 0 ? RynthPluginInfo.Version : "0.1.0", ServerPackQuestPack.Csv);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPackInfoJson(byte* buffer, int capacity)
    {
        try { return _released ? 0 : Write(PackInfoJson(), buffer, capacity); }
        catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetPackCsv(byte* buffer, int capacity)
    {
        try { return _released || PackInfoJson().Length == 0 ? 0 : Write(ServerPackQuestPack.Csv, buffer, capacity); }
        catch { return 0; }
    }

    /// <summary>Plugin shutdown: readers see Version 0 and re-query; the functions answer 0.</summary>
    public static void Release()
    {
        _released = true;
        if (_table != null) _table->Version = 0;
        if (_packTable != null) _packTable->Version = 0;
    }

    /// <summary>The JSON to hand out now. Any thread: the plugin's state is read through volatile fields.</summary>
    internal static string CurrentJson(ServerPackPlugin? plugin)
    {
        ServerPackInfo info = plugin?.Info ?? ServerPackPlugin.StaticInfo;
        bool active = plugin?.Active ?? false;
        string world = plugin?.World ?? string.Empty;
        int valuesRevision = plugin?.ProfileValues.Revision ?? -1;
        Snapshot? last = Volatile.Read(ref _last);
        if (last != null && last.Active == active && last.World == world && last.ValuesRevision == valuesRevision) return last.Json;
        KeyValuePair<string, double>[] values = plugin != null
            ? plugin.ProfileValues.Snapshot(out valuesRevision)
            : Array.Empty<KeyValuePair<string, double>>();
        string json = ServerPackProfile.BuildJson(info, RynthPluginInfo.Name, RynthPluginInfo.Version, active, world, values);
        Volatile.Write(ref _last, new Snapshot(active, world, valuesRevision, json));
        return json;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetProfileJson(byte* buffer, int capacity)
    {
        try
        {
            if (_released) return 0;
            return Write(CurrentJson(ServerPackExports.Plugin), buffer, capacity);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Copies <paramref name="json"/> as NUL-terminated UTF-8 into the caller's buffer when it fits,
    /// and returns its byte length either way (length >= capacity: too small, nothing written).
    /// The quest pack's CSV goes the same way.
    /// </summary>
    internal static int Write(string json, byte* buffer, int capacity)
    {
        int length = Encoding.UTF8.GetByteCount(json);
        if (buffer == null || capacity <= length) return length;
        fixed (char* src = json)
            Encoding.UTF8.GetBytes(src, json.Length, buffer, capacity);
        buffer[length] = 0;
        return length;
    }
}
