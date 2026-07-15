using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using static Unity.Netcode.FastBufferWriter;

namespace LobbyControlMidSessionJoin;

internal static class NetworkSync
{
    private const uint GENERATE_LEVEL_RPC_ID = 3073943002u;
    private const uint FINISH_GENERATING_LEVEL_RPC_ID = 2729232387u;
    private const float GENERATION_TIMEOUT_SECONDS = 60f;
    private const float SNAPSHOT_ACK_TIMEOUT_SECONDS = 10f;

    private static readonly MethodInfo? BeginSendClientRpc = AccessTools.Method(
        typeof(NetworkBehaviour), "__beginSendClientRpc");
    private static readonly MethodInfo? EndSendClientRpc = AccessTools.Method(
        typeof(NetworkBehaviour), "__endSendClientRpc");
    private static readonly Dictionary<ulong, int> ActiveSynchronizations = new();
    private static int _nextSynchronizationId;

    internal static void StartLateClientSynchronization(StartOfRound owner, ulong clientId)
    {
        if (ActiveSynchronizations.ContainsKey(clientId))
        {
            Plugin.Debug($"Ignored duplicate synchronization request for client {clientId}.");
            return;
        }

        int synchronizationId = ++_nextSynchronizationId;
        ActiveSynchronizations[clientId] = synchronizationId;
        try
        {
            owner.StartCoroutine(SynchronizeLateClientGuarded(clientId, synchronizationId));
        }
        catch
        {
            if (IsCurrentSynchronization(clientId, synchronizationId))
                ActiveSynchronizations.Remove(clientId);
            throw;
        }
    }

    internal static void ResetSynchronizations()
    {
        ActiveSynchronizations.Clear();
        _nextSynchronizationId = 0;
    }

    internal static void HandleClientDisconnected(ulong clientId)
    {
        ActiveSynchronizations.Remove(clientId);
        WorldStateSync.ClearSnapshotAcknowledgement(clientId);
        RoundManager.Instance?.playersFinishedGeneratingFloor.Remove(clientId);
        Plugin.Debug($"Cleared late-join synchronization state for disconnected client {clientId}.");
    }

    private static bool IsCurrentSynchronization(ulong clientId, int synchronizationId)
    {
        return ActiveSynchronizations.TryGetValue(clientId, out int currentId) &&
               currentId == synchronizationId;
    }

    private static IEnumerator SynchronizeLateClientGuarded(ulong clientId, int synchronizationId)
    {
        try
        {
            yield return SynchronizeLateClientCore(clientId, synchronizationId);
        }
        finally
        {
            if (IsCurrentSynchronization(clientId, synchronizationId))
            {
                ActiveSynchronizations.Remove(clientId);
                WorldStateSync.ClearSnapshotAcknowledgement(clientId);
                RoundManager.Instance?.playersFinishedGeneratingFloor.Remove(clientId);
            }
        }
    }

    private static IEnumerator SynchronizeLateClientCore(ulong clientId, int synchronizationId)
    {
        if (!Plugin.Enabled.Value || !MidJoinState.IsActiveMoon)
            yield break;

        var network = NetworkManager.Singleton;
        var manager = RoundManager.Instance;
        if (network == null || manager == null || !network.IsServer)
            yield break;

        // Let OnPlayerConnectedClientRpc finish assigning the late player's objects and let
        // NGO complete the first spawned-object synchronization pass before level generation.
        yield return null;
        yield return null;

        if (!IsCurrentSynchronization(clientId, synchronizationId))
            yield break;

        if (!IsClientConnected(network, clientId))
        {
            SetStatus($"Client {clientId} disconnected before synchronization.");
            yield break;
        }

        if (!MidJoinState.IsActiveMoon)
        {
            SetStatus("Round left the landed state before synchronization.");
            yield break;
        }

        try
        {
            WorldStateSync.ResetSnapshotAcknowledgement(clientId);
            WorldStateSync.SendSnapshot(clientId, includeInterior: false);
            SetStatus($"Waiting for client {clientId} to acknowledge the landed ship snapshot.");
        }
        catch (Exception ex)
        {
            SetStatus("Landed ship snapshot synchronization failed: " + ex.Message);
            Plugin.Log.LogError(ex);
            yield break;
        }

        float acknowledgementDeadline =
            Time.realtimeSinceStartup + SNAPSHOT_ACK_TIMEOUT_SECONDS;
        float nextSnapshotResend = Time.realtimeSinceStartup + 0.5f;
        while (!WorldStateSync.HasSnapshotAcknowledgement(clientId))
        {
            if (!IsCurrentSynchronization(clientId, synchronizationId))
                yield break;
            if (!IsClientConnected(network, clientId))
            {
                WorldStateSync.ClearSnapshotAcknowledgement(clientId);
                SetStatus($"Client {clientId} disconnected before acknowledging the landed ship state.");
                yield break;
            }

            if (!MidJoinState.IsActiveMoon)
            {
                WorldStateSync.ClearSnapshotAcknowledgement(clientId);
                SetStatus("Round left the landed state before the late client acknowledged ship state.");
                yield break;
            }

            if (Time.realtimeSinceStartup >= acknowledgementDeadline)
            {
                WorldStateSync.ClearSnapshotAcknowledgement(clientId);
                SetStatus($"Timed out waiting for client {clientId} to acknowledge the landed ship state.");
                yield break;
            }

            if (Time.realtimeSinceStartup >= nextSnapshotResend)
            {
                try
                {
                    WorldStateSync.SendSnapshot(clientId, includeInterior: false);
                }
                catch (Exception ex)
                {
                    Plugin.Debug("Could not resend landed ship snapshot: " + ex.Message);
                }
                nextSnapshotResend = Time.realtimeSinceStartup + 0.5f;
            }

            yield return null;
        }

        WorldStateSync.ClearSnapshotAcknowledgement(clientId);
        try
        {
            manager.playersFinishedGeneratingFloor.Remove(clientId);
            SendGenerateLevelRpc(manager, clientId);
            SetStatus($"Waiting for client {clientId} to finish generating the interior.");
        }
        catch (Exception ex)
        {
            SetStatus("Native moon generation synchronization failed: " + ex.Message);
            Plugin.Log.LogError(ex);
            yield break;
        }

        if (StartOfRound.Instance.currentLevel.spawnEnemiesAndScrap)
        {
            float deadline = Time.realtimeSinceStartup + GENERATION_TIMEOUT_SECONDS;
            while (!manager.playersFinishedGeneratingFloor.Contains(clientId))
            {
                if (!IsCurrentSynchronization(clientId, synchronizationId))
                    yield break;
                if (!IsClientConnected(network, clientId))
                {
                    SetStatus($"Client {clientId} disconnected while generating the interior.");
                    yield break;
                }

                if (!MidJoinState.IsActiveMoon)
                {
                    SetStatus("Round left the landed state while the late client was generating.");
                    yield break;
                }

                if (Time.realtimeSinceStartup >= deadline)
                {
                    SetStatus($"Timed out waiting for client {clientId} to finish generating the interior.");
                    yield break;
                }

                yield return null;
            }
        }

        if (!IsCurrentSynchronization(clientId, synchronizationId) ||
            !IsClientConnected(network, clientId))
            yield break;

        try
        {
            // FinishGeneratingNewLevelClientRpc performs client-local post-processing, including
            // RefreshLightsList. It must not run until this specific client's dungeon exists.
            SendEmptyClientRpc(manager, FINISH_GENERATING_LEVEL_RPC_ID, clientId);
            manager.playersFinishedGeneratingFloor.Remove(clientId);
        }
        catch (Exception ex)
        {
            SetStatus("Late-client finalization failed: " + ex.Message);
            Plugin.Log.LogError(ex);
            yield break;
        }

        // Give the joining client time to execute the native finish RPC before the separate
        // targeted custom-message channel applies lights, doors, and other local scene state.
        yield return new WaitForSeconds(0.25f);

        if (!IsCurrentSynchronization(clientId, synchronizationId) ||
            !IsClientConnected(network, clientId))
            yield break;

        try
        {
            WorldStateSync.SendSnapshot(clientId, includeInterior: true);
            MidJoinState.SnapshotsSent++;
            SetStatus($"Completed ship, interior, door, and lighting synchronization for client {clientId}.");
        }
        catch (Exception ex)
        {
            SetStatus("Late-client world-state synchronization failed: " + ex.Message);
            Plugin.Log.LogError(ex);
        }
    }

    private static bool IsClientConnected(NetworkManager network, ulong clientId)
    {
        return network.ConnectedClients.ContainsKey(clientId);
    }

    private static void SendGenerateLevelRpc(RoundManager manager, ulong clientId)
    {
        var round = StartOfRound.Instance;
        if (round == null || round.currentLevel == null)
            throw new InvalidOperationException("Round state is not ready.");

        var rpcParams = TargetClient(clientId);
        var writer = BeginRpc(manager, GENERATE_LEVEL_RPC_ID, rpcParams);

        BytePacker.WriteValueBitPacked(writer, round.randomMapSeed);
        BytePacker.WriteValueBitPacked(writer, round.currentLevelID);
        BytePacker.WriteValueBitPacked(writer, round.currentLevel.moldSpreadIterations);
        BytePacker.WriteValueBitPacked(writer, round.currentLevel.moldStartPosition);

        var moldManager = UnityEngine.Object.FindObjectOfType<MoldSpreadManager>();
        bool hasMoldState = moldManager != null && round.currentLevelID >= 0;
        writer.WriteValueSafe(hasMoldState);
        if (hasMoldState)
        {
            int[] destroyedMold = moldManager!.planetMoldStates[round.currentLevelID]
                .destroyedMold.ToArray();
            writer.WriteValueSafe(destroyedMold, default(ForPrimitives));
        }

        BytePacker.WriteValueBitPacked(writer, (int)round.currentLevel.currentWeather + 255);
        EndRpc(manager, writer, GENERATE_LEVEL_RPC_ID, rpcParams);

        Plugin.Debug(
            $"Started native level sync client={clientId}, seed={round.randomMapSeed}, " +
            $"level={round.currentLevelID}, mold={round.currentLevel.moldSpreadIterations}/" +
            $"{round.currentLevel.moldStartPosition}, weather={(int)round.currentLevel.currentWeather}, " +
            $"moldState={hasMoldState}");
    }

    private static void SendEmptyClientRpc(NetworkBehaviour target, uint rpcId, ulong clientId)
    {
        var rpcParams = TargetClient(clientId);
        var writer = BeginRpc(target, rpcId, rpcParams);
        EndRpc(target, writer, rpcId, rpcParams);
    }

    private static FastBufferWriter BeginRpc(
        NetworkBehaviour target,
        uint rpcId,
        ClientRpcParams rpcParams)
    {
        if (BeginSendClientRpc == null || EndSendClientRpc == null)
            throw new MissingMethodException("Unity Netcode ClientRpc send methods were not found.");

        return (FastBufferWriter)BeginSendClientRpc.Invoke(
            target,
            new object[] { rpcId, rpcParams, RpcDelivery.Reliable })!;
    }

    private static void EndRpc(
        NetworkBehaviour target,
        FastBufferWriter writer,
        uint rpcId,
        ClientRpcParams rpcParams)
    {
        EndSendClientRpc!.Invoke(
            target,
            new object[] { writer, rpcId, rpcParams, RpcDelivery.Reliable });
    }

    private static ClientRpcParams TargetClient(ulong clientId)
    {
        return new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new List<ulong> { clientId }
            }
        };
    }

    private static void SetStatus(string status)
    {
        MidJoinState.LastStatus = status;
        Plugin.Debug(status);
    }
}
