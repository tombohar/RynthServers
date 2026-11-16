// DreamWeave's website destinations: where each button goes. The host and the paths are constants
// (the site's registry: dreamweave-market Source/ACE.Market/Site/SiteTools.cs and HostRouting.cs);
// nothing here is ever read from chat. Pure, so RynthServerPack.Tests compiles it directly.
using System;
using System.Collections.Generic;
using RynthCore.Plugin.ServerPack;

namespace RynthCore.Plugin.RynthDreamWeave;

/// <summary>One website page a DreamWeave button opens.</summary>
internal sealed record WebDestination(string Id, string Label, string Path, string Tooltip);

internal static class DreamWeaveWeb
{
    public const string SiteBase = "https://www.acdreamweave.com";

    /// <summary>The only host the panel or the browser may be sent to on the live world.</summary>
    public static readonly string[] AllowedHosts = { "www.acdreamweave.com" };

    /// <summary>
    /// The stage world's site. A launch code is minted in the shard of the world that answered
    /// "/weblogin", so it only redeems on that world's site: a stage code sent to the live site is
    /// "expired or already used". (market-stage.acdreamweave.com only 301s to www-stage.)
    /// </summary>
    public const string StageSiteBase = "https://www-stage.acdreamweave.com";

    /// <summary>The only host the panel or the browser may be sent to on the stage world.</summary>
    public static readonly string[] StageAllowedHosts = { "www-stage.acdreamweave.com" };

    /// <summary>The stage server's world name (ACE deploy/stage/Config.js.template).</summary>
    public const string StageWorldName = "DreamWeave-Stage";

    /// <summary>True for the stage world. Anything else, unknown included, is the live site.</summary>
    public static bool IsStageWorld(string? worldName)
        => worldName != null && string.Equals(worldName.Trim(), StageWorldName, StringComparison.OrdinalIgnoreCase);

    public static string SiteBaseFor(string? worldName) => IsStageWorld(worldName) ? StageSiteBase : SiteBase;

    public static string[] AllowedHostsFor(string? worldName) => IsStageWorld(worldName) ? StageAllowedHosts : AllowedHosts;

    public const string PanelTitle = "DreamWeave";

    public static readonly WebDestination[] Destinations =
    {
        new("abilities", "Class Abilities", "/play/abilities", "Raise and lower this character's class abilities live."),
        new("market", "Market", "/market", "The player market, logged in as this account."),
        new("suit", "Suit Builder", "/tools/suit", "Plan a suit from this account's characters."),
    };

    /// <summary>The first engine API that draws a plugin window's bar-icon dropdown from the manifest's barMenu.</summary>
    internal const uint BarMenuEngineApi = 95;

    /// <summary>Whether an engine of this API version draws the bar menu (so the card's buttons are redundant).</summary>
    internal static bool EngineDrawsBarMenu(uint engineVersion) => engineVersion >= BarMenuEngineApi;

    /// <summary>The Phosphor glyph (private-use character) for a destination's menu item; "" when it has none.</summary>
    internal static string Icon(string id) => id switch
    {
        "market" => ((char)0xE470).ToString(),     // Storefront
        "suit" => ((char)0xE40A).ToString(),       // Shield
        "abilities" => ((char)0xE6A2).ToString(),  // Sparkle
        _ => string.Empty,
    };

    /// <summary>One bar-menu item per destination: id, label as tooltip, glyph, and the /rserver verb.</summary>
    internal static List<ServerBarMenuItem> BarMenu()
    {
        var items = new List<ServerBarMenuItem>(Destinations.Length);
        foreach (WebDestination d in Destinations)
            items.Add(new ServerBarMenuItem(d.Id, d.Label, Icon(d.Id), "/rserver " + d.Id));
        return items;
    }

    public static WebDestination? ById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (WebDestination d in Destinations)
            if (string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    /// <summary>The "/rserver" usage line for the destinations' verbs (ServerUsageLines).</summary>
    public static string UsageLine()
    {
        var ids = new string[Destinations.Length];
        var labels = new string[Destinations.Length];
        for (int i = 0; i < Destinations.Length; i++) { ids[i] = Destinations[i].Id; labels[i] = Destinations[i].Label; }
        string choices = labels.Length < 2 ? string.Join("", labels)
            : string.Join(", ", labels, 0, labels.Length - 1) + " or " + labels[labels.Length - 1];
        return "/rserver " + string.Join(" | ", ids) + " - open the website's " + choices + ", logged in as this account.";
    }

    /// <summary>The sign-in URL for a destination. The caller has already checked the code with LaunchWire.</summary>
    public static string LaunchUrl(string code, WebDestination d, string? worldName = null)
        => SiteBaseFor(worldName) + "/launch?code=" + code + "&to=" + Uri.EscapeDataString(d.Path);
}
