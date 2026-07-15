using System.Collections;
using GameNetcodeStuff;
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
    [HarmonyPatch(typeof(StartOfRound), "Start")]
    private static void StartOfRoundStartPostfix()
    {
        WorldStateSync.RegisterHandler();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerControllerB), "ConnectClientToPlayerObject")]
    private static void ConnectClientToPlayerObjectPostfix()
    {
        WorldStateSync.RegisterHandler();
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameNetworkManager), "SetInstanceValuesBackToDefault")]
    private static void ResetNetworkStatePostfix()
    {
        WorldStateSync.UnregisterHandler();
        NetworkSync.ResetSynchronizations();
        MidJoinState.ResetClientState();
    }

    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(StartOfRound), "openingDoorsSequence")]
    private static bool OpeningDoorsSequencePrefix(StartOfRound __instance, ref IEnumerator __result)
    {
        if (!Plugin.Enabled.Value || __instance.IsServer || !MidJoinState.ClientLateJoinSyncActive)
            return true;

        // FinishGeneratingNewLevelClientRpc normally starts this iterator. Running it for a
        // late joiner replays OpenShip and the full landing delay after the moon is already active.
        MidJoinState.LandingSequencesSuppressed++;
        __result = WorldStateSync.RunLateJoinLandedSequence(__instance);
        Plugin.Debug("Suppressed the native openingDoorsSequence for a mid-session joining client.");
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.OnPlayerConnectedClientRpc))]
    private static void PlayerConnectedPostfix(StartOfRound __instance, ulong clientId)
    {
        if (__instance.IsServer && clientId != NetworkManager.ServerClientId && MidJoinState.IsActiveMoon)
            NetworkSync.StartLateClientSynchronization(__instance, clientId);
    }
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(RoundManager), nameof(RoundManager.TurnOnAllLights))]
    private static bool TurnOnAllLightsPrefix(RoundManager __instance, bool on)
    {
        if (!WorldStateSync.ShouldBlockFacilityPowerOn(__instance, on))
            return true;

        Plugin.Debug("Blocked a delayed client-local facility lights-on call after permanent apparatus power loss.");
        return false;
    }

}
