// ServerData.cs - What is known about Levistras (RynthLevistras). Identity (id, hosts, world names) is in the csproj.
namespace RynthCore.Plugin.ServerPack;

internal static class ServerData
{
    public static ServerPackInfo Create() =>
        new(RynthPluginInfo.ServerId, RynthPluginInfo.ServerName, RynthPluginInfo.ServerHosts, RynthPluginInfo.ServerWorldNames)
        {
            Port = 9000,
            Emulator = "ACE",
            Ruleset = "PvE",
            Summary = "Botting-free PvE server (automation is not allowed). 2 account limit.",
            Facts = new[]
            {
                "Botting-free: automation is not allowed.",
                "2 account limit.",
                "Its quest pack (quests.levistras.csv, Levistras's own quests) shows in RynthOracle's Quests tab.",
            },
            AccountLimit = "2 accounts",
            Botting = BottingPolicy.Forbidden,
            BottingNote = "automation is not allowed",
            Source = "RynthCore's launcher server list (KnownServerCatalog.cs), checked 2026-10-21",
        };
}
