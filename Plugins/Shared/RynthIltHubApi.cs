// ============================================================================
//  RynthSuite - Plugins/Shared/RynthIltHubApi.cs
//  RynthILT's own names around the server hub (Shared/RynthServerHubApi.cs).
//  RynthILT serves the "RynthServer.Hub" table (RynthServerHubApiV1) under that
//  neutral name and under its older one, "RynthILT.Hub" (every RynthILT since the
//  interface's first release answers that one). The section, tab, text, feature
//  and setting ids RynthAi uses are RynthServerHub's; the ids here are Infinite
//  Leaftide's alone (its sub-tab, its quest list, ACECustom's feature keys).
//  Until 2026-10-21 this file held the whole interface (class RynthIltHub,
//  structs RynthIltHubApiV1 / IltTabV1); the ids kept their values.
//  Compiled into RynthILT only (linked source).
// ============================================================================

namespace RynthCore.Plugin.Shared;

/// <summary>RynthILT's constants around the "RynthServer.Hub" interface.</summary>
public static class RynthIltHub
{
    /// <summary>The providing plugin's display name.</summary>
    public const string PluginName = "RynthILT";

    /// <summary>
    /// RynthILT's own name for the RynthServer.Hub table (the same table; RynthServerHubApiV1 is its layout).
    /// Older RynthAi asked for this name first.
    /// </summary>
    public const string InterfaceName = "RynthILT.Hub";

    /// <summary>Quests tab: the "Quest bonus (/qb)" list.</summary>
    public const string SectionQuestBonus = "quests-qb";
    /// <summary>
    /// Pets tab, Breeding sub-tab: the pet breeding tracker, the sub-tab's whole body (RynthILT 0.3.1+;
    /// older copies draw nothing). Since 2026-10-20 RynthAi shows it because GetSubTabs(TabParentPets)
    /// lists it (RynthILT: an ILT world unless FeaturePetBreeding is off); older RynthAi decided itself.
    /// </summary>
    public const string SectionPetBreeding = "pets-breeding";

    /// <summary>ACECustom feature keys (GetFeature) beside RynthServerHub.FeatureQuests.</summary>
    public const string FeaturePetRefill = "pet_refill";
    public const string FeatureUniversalMastery = "universal_mastery";
    /// <summary>ACECustom siphon lenses (monster capture into a pet essence).</summary>
    public const string FeatureMonsterCapture = "monster_capture";
    public const string FeatureRequireComps = "require_spell_comps";
    /// <summary>Pet breeding (the Pets tab's Breeding sub-tab is hidden while the server reports it off).</summary>
    public const string FeaturePetBreeding = "pet_breeding";
}
