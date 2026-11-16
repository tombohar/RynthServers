// ServerPackWindows.cs - Windows of its own a server plugin adds to RynthCore's bar, beside the server's
// window (2026-11-07: RynthDreamWeave's Skills panel, the retail Character panel drawn by the plugin).
//
// The ServerPackPlugin.ServerWindows hook lists them once, at construction. Each is declared in the
// sections manifest's "windows" (plugin windows, API v89; "chrome": "none" for one that draws its own
// frame, v90), only while the server is active, and drawn through RynthPluginRenderEmbed "window:<id>"
// on the render thread. A frameless window is declared only on engines with frameless windows (an older
// engine would wrap it in its own frame), so it simply isn't there on those.
// Pure (no ImGui, no host calls): RynthServerPack.Tests covers it.
using System;
using System.Collections.Generic;
using System.Text;

namespace RynthCore.Plugin.ServerPack;

/// <summary>
/// One window a server plugin adds. <paramref name="Id"/> is 1-16 of a-z, 0-9 and '-' (its surface is
/// "window:&lt;id&gt;"); <paramref name="Title"/> is its panel key (bar tooltip, saved place, /rc commands).
/// <paramref name="Draw"/> draws the body (popped out or not) and returns the size it wants: the height it used and, for a window that sizes its width too, that width (0: the width it was given). Render thread.
/// </summary>
internal sealed record ServerWindow(string Id, string Title, string Icon, int Width, int Height, int MinWidth, int MinHeight,
    bool Frameless, Func<bool, System.Numerics.Vector2> Draw, bool FitHeight = false, bool UnderUi = false, bool DrawScale = false);

internal static class ServerPackWindows
{
    /// <summary>At most this many windows besides the server's own.</summary>
    public const int MaxWindows = 3;

    public static string SurfaceFor(string id) => "window:" + id;

    /// <summary>The window id a surface names ("window:skills" -> "skills"), or null. ":popout" is the caller's to strip.</summary>
    public static string? IdOf(string? surface)
    {
        if (surface == null || !surface.StartsWith("window:", StringComparison.Ordinal)) return null;
        string id = surface["window:".Length..];
        return ServerPackPages.IsValidId(id) && id != ServerPackModel.WindowId ? id : null;
    }

    /// <summary>The windows that can be declared: a valid id that isn't the server window's, a title, each id once.</summary>
    public static List<ServerWindow> Validate(List<ServerWindow> windows, Action<string> note)
    {
        var ok = new List<ServerWindow>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (ServerWindow w in windows)
        {
            if (w == null) continue;
            if (!ServerPackPages.IsValidId(w.Id) || w.Id == ServerPackModel.WindowId) { note($"Window \"{w.Id}\" left out: its id must be 1-16 of a-z, 0-9 and '-', not \"server\"."); continue; }
            if (string.IsNullOrWhiteSpace(w.Title)) { note($"Window \"{w.Id}\" left out: it has no title."); continue; }
            if (!ids.Add(w.Id)) { note($"Window \"{w.Id}\" left out: the id is used twice."); continue; }
            if (ok.Count >= MaxWindows) { note($"Window \"{w.Id}\" left out: at most {MaxWindows} windows."); continue; }
            ok.Add(w);
        }
        return ok;
    }

    /// <summary>The manifest entries (",{...}" each) for <paramref name="windows"/>; frameless ones only when <paramref name="frameless"/>.</summary>
    public static string Json(IReadOnlyList<ServerWindow> windows, string serverId, bool frameless)
    {
        var sb = new StringBuilder();
        foreach (ServerWindow w in windows)
        {
            if (w.Frameless && !frameless) continue;
            sb.Append(",{\"id\":").Append(ServerPackModel.Q(w.Id));
            sb.Append(",\"title\":").Append(ServerPackModel.Q(w.Title.Trim()));
            sb.Append(",\"surface\":").Append(ServerPackModel.Q(SurfaceFor(w.Id)));
            sb.Append(",\"icon\":").Append(ServerPackModel.Q(w.Icon));
            sb.Append(",\"width\":").Append(w.Width).Append(",\"height\":").Append(w.Height);
            sb.Append(",\"minWidth\":").Append(w.MinWidth).Append(",\"minHeight\":").Append(w.MinHeight);
            sb.Append(",\"bar\":true,\"server\":").Append(ServerPackModel.Q(serverId));
            if (w.Frameless) sb.Append(",\"chrome\":\"none\"");
            // The window is as tall as the height the draw reports (the plugin sizes it itself, v90).
            if (w.FitHeight && w.Frameless) sb.Append(",\"fitHeight\":true");
            // Drawn under AC's own UI, like a native AC window (engines from 2026-11-07; older ones ignore it).
            if (w.UnderUi && w.Frameless) sb.Append(",\"layer\":\"underUi\"");
            // It draws itself at any size: Ctrl+wheel steps its own draw size (PanelScale, 30-250%), not its text.
            if (w.DrawScale && w.Frameless) sb.Append(",\"scale\":\"draw\"");
            sb.Append('}');
        }
        return sb.ToString();
    }
}
