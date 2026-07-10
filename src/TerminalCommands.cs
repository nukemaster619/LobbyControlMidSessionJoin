using System;
using HarmonyLib;
using UnityEngine;

namespace LobbyControlMidSessionJoin;

[HarmonyPatch(typeof(Terminal), nameof(Terminal.ParsePlayerSentence))]
internal static class TerminalCommands
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(Terminal __instance, ref TerminalNode __result)
    {
        string input = __instance.screenText.text
            .Substring(__instance.screenText.text.Length - __instance.textAdded)
            .Trim();
        string[] args = input.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (args.Length == 0 || !args[0].Equals("midjoin", StringComparison.OrdinalIgnoreCase))
            return true;

        __result = Node(Execute(args));
        return false;
    }

    private static string Execute(string[] args)
    {
        if (!GameNetworkManager.Instance.isHostingGame)
            return "MIDJOIN commands are host-only.\n\n";

        if (args.Length == 1 || args[1].Equals("help", StringComparison.OrdinalIgnoreCase))
            return "MIDJOIN COMMANDS\n\n" +
                   "midjoin status\n" +
                   "midjoin enable|disable\n" +
                   "midjoin debug on|off\n" +
                   "midjoin autoopen on|off\n" +
                   "midjoin spawninship on|off\n\n";

        switch (args[1].ToLowerInvariant())
        {
            case "status":
                return Status();
            case "enable":
                Plugin.Enabled.Value = true;
                LobbyControlBridge.PermitConnections(true);
                return "Mid-session joining enabled.\n\n";
            case "disable":
                Plugin.Enabled.Value = false;
                return "Mid-session joining disabled.\n\n";
            case "debug":
                return SetBool(args, Plugin.DebugLogging, "Debug logging");
            case "autoopen":
                return SetBool(args, Plugin.AutoOpenLobby, "Automatic lobby reopening");
            case "spawninship":
                return SetBool(args, Plugin.SpawnInShip, "Spawn-in-ship");
            default:
                return "Unknown MIDJOIN command. Type 'midjoin help'.\n\n";
        }
    }

    private static string SetBool(string[] args, BepInEx.Configuration.ConfigEntry<bool> entry, string label)
    {
        if (args.Length < 3 || (args[2] != "on" && args[2] != "off"))
            return $"Usage: midjoin {args[1]} on|off\n\n";
        entry.Value = args[2] == "on";
        return $"{label}: {(entry.Value ? "ON" : "OFF")}\n\n";
    }

    private static string Status()
    {
        var round = StartOfRound.Instance;
        string phase = round == null ? "no round" :
            round.inShipPhase ? "orbit/ship phase" :
            round.shipHasLanded ? "landed moon" : "transition";
        return "MID-SESSION JOIN STATUS\n\n" +
               $"Enabled: {Plugin.Enabled.Value}\n" +
               $"Phase: {phase}\n" +
               $"LobbyControl bridge: {(LobbyControlBridge.Available ? "OK" : "NOT FOUND")}\n" +
               $"Message handler: {MidJoinState.HandlerRegistered}\n" +
               $"Snapshots sent/received: {MidJoinState.SnapshotsSent}/{MidJoinState.SnapshotsReceived}\n" +
               $"Auto-open lobby: {Plugin.AutoOpenLobby.Value}\n" +
               $"Spawn in ship: {Plugin.SpawnInShip.Value}\n" +
               $"Debug: {Plugin.DebugLogging.Value}\n" +
               $"Last result: {MidJoinState.LastStatus}\n\n";
    }

    private static TerminalNode Node(string text)
    {
        var node = ScriptableObject.CreateInstance<TerminalNode>();
        node.displayText = text;
        node.clearPreviousText = true;
        node.maxCharactersToType = 50;
        return node;
    }
}
