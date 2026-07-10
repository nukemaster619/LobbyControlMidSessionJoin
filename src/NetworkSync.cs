using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace LobbyControlMidSessionJoin;

internal static class NetworkSync
{
    private static readonly FieldInfo? RpcExecStage =
        AccessTools.Field(typeof(NetworkBehaviour), "__rpc_exec_stage");

    internal static void Register()
    {
        var manager = NetworkManager.Singleton;
        if (manager?.CustomMessagingManager == null || MidJoinState.HandlerRegistered) return;

        manager.CustomMessagingManager.RegisterNamedMessageHandler(
            MidJoinState.SnapshotMessage, ReceiveSnapshot);
        MidJoinState.HandlerRegistered = true;
        Plugin.Debug("Registered level snapshot handler.");
    }

    internal static void Unregister()
    {
        var manager = NetworkManager.Singleton;
        if (manager?.CustomMessagingManager != null && MidJoinState.HandlerRegistered)
            manager.CustomMessagingManager.UnregisterNamedMessageHandler(MidJoinState.SnapshotMessage);
        MidJoinState.HandlerRegistered = false;
    }

    internal static void SendSnapshot(ulong clientId)
    {
        if (!Plugin.Enabled.Value || !MidJoinState.IsActiveMoon) return;

        var network = NetworkManager.Singleton;
        var round = StartOfRound.Instance;
        var roundManager = RoundManager.Instance;
        if (network?.CustomMessagingManager == null || round == null || roundManager == null) return;

        int moldIterations = round.currentLevel?.moldSpreadIterations ?? 0;
        int moldStart = round.currentLevel?.moldStartPosition ?? 0;
        int weather = round.currentLevel == null ? -1 : (int)round.currentLevel.currentWeather;

        using var writer = new FastBufferWriter(64, Allocator.Temp);
        writer.WriteValueSafe(round.randomMapSeed);
        writer.WriteValueSafe(round.currentLevelID);
        writer.WriteValueSafe(moldIterations);
        writer.WriteValueSafe(moldStart);
        writer.WriteValueSafe(weather);
        writer.WriteValueSafe(round.shipHasLanded);

        network.CustomMessagingManager.SendNamedMessage(
            MidJoinState.SnapshotMessage, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);

        MidJoinState.SnapshotsSent++;
        MidJoinState.LastStatus = $"Sent moon snapshot to client {clientId}.";
        Plugin.Debug(MidJoinState.LastStatus);
    }

    private static void ReceiveSnapshot(ulong senderClientId, FastBufferReader reader)
    {
        try
        {
            reader.ReadValueSafe(out int seed);
            reader.ReadValueSafe(out int levelId);
            reader.ReadValueSafe(out int moldIterations);
            reader.ReadValueSafe(out int moldStart);
            reader.ReadValueSafe(out int weather);
            reader.ReadValueSafe(out bool landed);

            MidJoinState.SnapshotsReceived++;
            ApplySnapshot(seed, levelId, moldIterations, moldStart, weather, landed);
        }
        catch (Exception ex)
        {
            MidJoinState.LastStatus = "Snapshot read failed: " + ex.Message;
            Plugin.Log.LogError(ex);
        }
    }

    private static void ApplySnapshot(int seed, int levelId, int moldIterations, int moldStart,
        int weather, bool landed)
    {
        var round = StartOfRound.Instance;
        var manager = RoundManager.Instance;
        if (round == null || manager == null)
        {
            MidJoinState.LastStatus = "Snapshot arrived before round objects were ready.";
            Plugin.Log.LogWarning(MidJoinState.LastStatus);
            return;
        }

        Plugin.Debug($"Applying seed={seed}, level={levelId}, mold={moldIterations}/{moldStart}, weather={weather}");
        MidJoinState.ApplyingSnapshot = true;
        try
        {
            round.inShipPhase = false;
            round.shipHasLanded = landed;
            round.randomMapSeed = seed;
            round.currentLevelID = levelId;

            if (levelId >= 0 && levelId < round.levels.Length)
            {
                round.currentLevel = round.levels[levelId];
                round.currentLevel.moldSpreadIterations = moldIterations;
                round.currentLevel.moldStartPosition = moldStart;
                round.currentLevel.currentWeather = (LevelWeatherType)weather;
            }

            object? previousStage = RpcExecStage?.GetValue(manager);
            RpcExecStage?.SetValue(manager, 1); // NetworkBehaviour.__RpcExecStage.Execute
            manager.GenerateNewLevelClientRpc(seed, levelId, moldIterations, moldStart, null);
            if (previousStage != null) RpcExecStage?.SetValue(manager, previousStage);

            if (Plugin.SpawnInShip.Value)
                MoveLocalPlayerIntoShip(round);

            MidJoinState.LastStatus = $"Applied moon snapshot (level {levelId}, seed {seed}).";
            Plugin.Debug(MidJoinState.LastStatus);
        }
        catch (Exception ex)
        {
            MidJoinState.LastStatus = "Snapshot application failed: " + ex.Message;
            Plugin.Log.LogError(ex);
        }
        finally
        {
            MidJoinState.ApplyingSnapshot = false;
        }
    }

    private static void MoveLocalPlayerIntoShip(StartOfRound round)
    {
        var player = round.localPlayerController;
        if (player == null || round.elevatorTransform == null) return;

        Vector3 position = round.elevatorTransform.position + Vector3.up * 0.5f;
        player.TeleportPlayer(position);
        player.isInElevator = true;
        player.isInHangarShipRoom = true;
        player.isInsideFactory = false;
    }
}
