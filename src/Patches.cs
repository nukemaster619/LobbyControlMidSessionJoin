using HarmonyLib;
using Unity.Netcode;

namespace LobbyControlMidSessionJoin;

[HarmonyPatch]
internal static class Patches
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(GameNetworkManager), nameof(GameNetworkManager.ConnectionApproval))]
    private static void ConnectionApprovalPostfix(
        GameNetworkManager __instance,
        NetworkManager.ConnectionApprovalRequest request,
        NetworkManager.ConnectionApprovalResponse response)
    {
        if (!Plugin.Enabled.Value || !MidJoinState.IsActiveMoon) return;
        if (request.ClientNetworkId == NetworkManager.Singleton.LocalClientId) return;

        // Only undo LobbyControl's landed-ship rejection. Never bypass full-lobby,
        // version, ban, or closed-lobby failures.
        if (!response.Approved && response.Reason == "Ship has already landed!")
        {
            response.Approved = true;
            response.Reason = string.Empty;
            response.CreatePlayerObject = true;
            response.Pending = false;
            Plugin.Debug("Overrode LobbyControl's landed-ship rejection.");
        }
    }

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.StartGame))]
    private static void StartGamePostfix(StartOfRound __instance)
    {
        if (!__instance.IsServer || !Plugin.Enabled.Value) return;
        LobbyControlBridge.PermitConnections(true);
        if (Plugin.AutoOpenLobby.Value)
            GameNetworkManager.Instance.SetLobbyJoinable(true);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.OnShipLandedMiscEvents))]
    private static void ShipLandedPostfix(StartOfRound __instance)
    {
        if (!__instance.IsServer || !Plugin.Enabled.Value) return;
        LobbyControlBridge.PermitConnections(true);
        if (Plugin.AutoOpenLobby.Value)
            GameNetworkManager.Instance.SetLobbyJoinable(true);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.OnPlayerConnectedClientRpc))]
    private static void PlayerConnectedPostfix(StartOfRound __instance, ulong clientId)
    {
        if (__instance.IsServer && clientId != NetworkManager.ServerClientId)
            NetworkSync.SendSnapshot(clientId);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.Initialize))]
    private static void NetworkInitializePostfix() => NetworkSync.Register();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameNetworkManager), "SetInstanceValuesBackToDefault")]
    private static void NetworkShutdownPrefix() => NetworkSync.Unregister();
}
