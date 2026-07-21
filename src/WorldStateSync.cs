using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace LobbyControlMidSessionJoin;

internal static class WorldStateSync
{
    private const string MESSAGE_NAME = "LobbyControlMidSessionJoin.WorldState.v3";
    private const string ACK_MESSAGE_NAME = "LobbyControlMidSessionJoin.WorldStateAck.v1";
    private const int MESSAGE_VERSION = 3;
    private const float OBJECT_MATCH_DISTANCE = 0.75f;
    private const float APPLY_TIMEOUT_SECONDS = 15f;
    private const BindingFlags INSTANCE_FIELD_FLAGS =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly FieldInfo? ShipTravelCoroutineField =
        FindInstanceField(typeof(StartOfRound), "shipTravelCoroutine");
    private static readonly MethodInfo? SetWeatherEffectsMethod =
        typeof(TimeOfDay).GetMethod(
            "SetWeatherEffects",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);
    private static readonly FieldInfo? DoorIsOpenedField =
        FindInstanceField(typeof(DoorLock), "isDoorOpened");
    private static readonly FieldInfo? TerminalDoorOpenField =
        FindInstanceField(typeof(TerminalAccessibleObject), "isDoorOpen");
    private static readonly FieldInfo? TerminalPoweredField =
        FindInstanceField(typeof(TerminalAccessibleObject), "isPoweredOn");
    private static readonly FieldInfo? TerminalInitializedValuesField =
        FindInstanceField(typeof(TerminalAccessibleObject), "initializedValues");
    private static readonly FieldInfo? TerminalMapRadarTextField =
        FindInstanceField(typeof(TerminalAccessibleObject), "mapRadarText");
    private static readonly FieldInfo? TerminalMapRadarBoxField =
        FindInstanceField(typeof(TerminalAccessibleObject), "mapRadarBox");
    private static readonly FieldInfo? LoadingScreenAnimatorField =
        FindInstanceField(typeof(HUDManager), "LoadingScreen");
    private static readonly FieldInfo? LegacyLoadingDarkenScreenField =
        FindInstanceField(typeof(HUDManager), "loadingDarkenScreen");
    private static readonly FieldInfo? RoundPowerLightsCoroutineField =
        FindInstanceField(typeof(RoundManager), "powerLightsCoroutine");
    private static readonly FieldInfo? RoundFlickerLightsCoroutineField =
        FindInstanceField(typeof(RoundManager), "flickerLightsCoroutine");
    private static readonly FieldInfo? MapScreenIsScreenOnField =
        FindInstanceField(typeof(ManualCameraRenderer), "isScreenOn");
    private static readonly FieldInfo? MapScreenEnabledOnLocalClientField =
        FindInstanceField(typeof(ManualCameraRenderer), "screenEnabledOnLocalClient");
    private static readonly FieldInfo? MapScreenUpdateCoroutineField =
        FindInstanceField(typeof(ManualCameraRenderer), "updateMapCameraCoroutine");
    private static readonly FieldInfo? MapScreenSyncingTargetField =
        FindInstanceField(typeof(ManualCameraRenderer), "syncingTargetPlayer");
    private static readonly FieldInfo? MapScreenSyncingSwitchField =
        FindInstanceField(typeof(ManualCameraRenderer), "syncingSwitchScreen");

    private static readonly FieldInfo? PlayerFallValueField =
        FindInstanceField(typeof(PlayerControllerB), "fallValue");
    private static readonly FieldInfo? PlayerFallValueUncappedField =
        FindInstanceField(typeof(PlayerControllerB), "fallValueUncapped");
    private static readonly FieldInfo? PlayerTakingFallDamageField =
        FindInstanceField(typeof(PlayerControllerB), "takingFallDamage");
    private static readonly FieldInfo? PlayerFallingFromJumpField =
        FindInstanceField(typeof(PlayerControllerB), "isFallingFromJump");
    private static readonly FieldInfo? PlayerAverageVelocityField =
        FindInstanceField(typeof(PlayerControllerB), "averageVelocity");
    private static readonly FieldInfo? PlayerPreviousYField =
        FindInstanceField(typeof(PlayerControllerB), "previousYPosition");
    private static readonly FieldInfo? PlayerExternalForcesField =
        FindInstanceField(typeof(PlayerControllerB), "externalForces");
    private static readonly FieldInfo? PlayerIsInElevatorField =
        FindInstanceField(typeof(PlayerControllerB), "isInElevator");
    private static readonly FieldInfo? PlayerIsInHangarField =
        FindInstanceField(typeof(PlayerControllerB), "isInHangarShipRoom");
    private static readonly FieldInfo? PlayerIsInsideFactoryField =
        FindInstanceField(typeof(PlayerControllerB), "isInsideFactory");
    private static readonly FieldInfo? PlayerVelocityLastFrameField =
        FindInstanceField(typeof(PlayerControllerB), "velocityLastFrame");
    private static readonly FieldInfo? PlayerIsSlidingField =
        FindInstanceField(typeof(PlayerControllerB), "isPlayerSliding");
    private static readonly FieldInfo? PlayerSlidingTimerField =
        FindInstanceField(typeof(PlayerControllerB), "playerSlidingTimer");
    private static readonly FieldInfo? PlayerIsFallingNoJumpField =
        FindInstanceField(typeof(PlayerControllerB), "isFallingNoJump");
    private static readonly FieldInfo? PlayerTeleportedLastFrameField =
        FindInstanceField(typeof(PlayerControllerB), "teleportedLastFrame");
    private static readonly FieldInfo? PlayerWasInElevatorLastFrameField =
        FindInstanceField(typeof(PlayerControllerB), "wasInElevatorLastFrame");
    private static readonly FieldInfo? PlayerPreviousElevatorPositionField =
        FindInstanceField(typeof(PlayerControllerB), "previousElevatorPosition");
    private static readonly FieldInfo? PlayerOldPositionField =
        FindInstanceField(typeof(PlayerControllerB), "oldPlayerPosition");
    private static readonly FieldInfo? PlayerServerPositionField =
        FindInstanceField(typeof(PlayerControllerB), "serverPlayerPosition");
    private static readonly FieldInfo? PlayerSnapToServerPositionField =
        FindInstanceField(typeof(PlayerControllerB), "snapToServerPosition");
    private static readonly FieldInfo? PlayerUpdatePositionForNewClientField =
        FindInstanceField(typeof(PlayerControllerB), "updatePositionForNewlyJoinedClient");

    private static readonly HashSet<ulong> ShipSnapshotAcknowledgements = new();
    private static readonly HashSet<int> DeferredTerminalInitializations = new();
    private static NetworkManager? _registeredNetwork;
    private static WorldSnapshot? _latestClientSnapshot;
    private static Coroutine? _applySnapshotCoroutine;

    internal static void RegisterHandler()
    {
        var network = NetworkManager.Singleton;
        if (network == null || network.CustomMessagingManager == null)
            return;
        if (object.ReferenceEquals(_registeredNetwork, network))
            return;

        UnregisterHandler();
        network.CustomMessagingManager.RegisterNamedMessageHandler(MESSAGE_NAME, ReceiveSnapshot);
        network.CustomMessagingManager.RegisterNamedMessageHandler(
            ACK_MESSAGE_NAME,
            ReceiveSnapshotAcknowledgement);
        network.OnClientDisconnectCallback += HandleClientDisconnected;
        _registeredNetwork = network;
        Plugin.Debug("Registered targeted world-state synchronization handler.");
    }

    internal static void UnregisterHandler()
    {
        var network = _registeredNetwork;
        _registeredNetwork = null;
        _latestClientSnapshot = null;
        ShipSnapshotAcknowledgements.Clear();
        DeferredTerminalInitializations.Clear();
        if (network != null && _applySnapshotCoroutine != null)
        {
            network.StopCoroutine(_applySnapshotCoroutine);
            _applySnapshotCoroutine = null;
        }
        if (network == null)
            return;

        network.OnClientDisconnectCallback -= HandleClientDisconnected;
        if (network.CustomMessagingManager == null)
            return;

        try
        {
            network.CustomMessagingManager.UnregisterNamedMessageHandler(MESSAGE_NAME);
            network.CustomMessagingManager.UnregisterNamedMessageHandler(ACK_MESSAGE_NAME);
        }
        catch (Exception ex)
        {
            Plugin.Debug("Could not unregister world-state handler: " + ex.Message);
        }
    }

    private static void HandleClientDisconnected(ulong clientId)
    {
        NetworkSync.HandleClientDisconnected(clientId);
        ShipSnapshotAcknowledgements.Remove(clientId);

        var network = _registeredNetwork;
        if (network == null || clientId != network.LocalClientId)
            return;

        UnregisterHandler();
        MidJoinState.ResetClientState();
    }

    internal static void ResetSnapshotAcknowledgement(ulong clientId)
    {
        ShipSnapshotAcknowledgements.Remove(clientId);
    }

    internal static bool HasSnapshotAcknowledgement(ulong clientId)
    {
        return ShipSnapshotAcknowledgements.Contains(clientId);
    }

    internal static void ClearSnapshotAcknowledgement(ulong clientId)
    {
        ShipSnapshotAcknowledgements.Remove(clientId);
    }

    internal static void SendSnapshot(ulong clientId, bool includeInterior)
    {
        var network = NetworkManager.Singleton;
        if (network == null || !network.IsServer || network.CustomMessagingManager == null)
            throw new InvalidOperationException("The server custom messaging manager is not ready.");

        RegisterHandler();
        WorldSnapshot snapshot = CaptureSnapshot(includeInterior);
        int estimatedSize = 32768 + snapshot.Doors.Count * 80 + snapshot.TerminalDoors.Count * 64;
        var writer = new FastBufferWriter(estimatedSize, Allocator.Temp);
        try
        {
            WriteSnapshot(ref writer, snapshot);
            NetworkDelivery delivery = writer.Capacity > 1300
                ? NetworkDelivery.ReliableFragmentedSequenced
                : NetworkDelivery.Reliable;
            network.CustomMessagingManager.SendNamedMessage(MESSAGE_NAME, clientId, writer, delivery);
        }
        finally
        {
            writer.Dispose();
        }

        Plugin.Debug(
            $"Sent {(includeInterior ? "complete" : "ship-only")} world snapshot to client {clientId}: " +
            $"doors={snapshot.Doors.Count}, terminalDoors={snapshot.TerminalDoors.Count}, " +
            $"power={snapshot.PowerOn}, permanentPowerLoss={snapshot.PowerOffPermanently}, " +
            $"weather={(LevelWeatherType)snapshot.CurrentWeather}, landed={snapshot.ShipHasLanded}");
    }

    private static WorldSnapshot CaptureSnapshot(bool includeInterior)
    {
        var round = StartOfRound.Instance ?? throw new InvalidOperationException("StartOfRound is not ready.");
        var manager = RoundManager.Instance;
        var breaker = UnityEngine.Object.FindObjectOfType<BreakerBox>();
        bool apparatusRemoved = false;
        foreach (LungProp apparatus in UnityEngine.Object.FindObjectsOfType<LungProp>())
        {
            if (!apparatus.isLungDocked)
            {
                apparatusRemoved = true;
                break;
            }
        }
        bool powerOffPermanently = manager != null &&
                                   (manager.powerOffPermanently || apparatusRemoved);

        var snapshot = new WorldSnapshot
        {
            IncludeInterior = includeInterior,
            SpawnInShip = Plugin.SpawnInShip.Value,
            InShipPhase = round.inShipPhase,
            ShipHasLanded = round.shipHasLanded,
            ShipIsLeaving = round.shipIsLeaving,
            ShipDoorsEnabled = round.shipDoorsEnabled,
            HangarDoorsClosed = round.hangarDoorsClosed,
            NewGameIsLoading = round.newGameIsLoading,
            TravellingToNewLevel = round.travellingToNewLevel,
            ShipAmbiancePlaying = round.shipAmbianceAudio != null && round.shipAmbianceAudio.isPlaying,
            ShipTravelAudioPlaying = round.ship3DAudio != null && round.ship3DAudio.isPlaying,
            PowerOffPermanently = powerOffPermanently,
            PowerOn = manager != null && !powerOffPermanently &&
                      (breaker == null || breaker.isPowerOn),
            CurrentWeather = (int)round.currentLevel.currentWeather,
            ShipTransform = CaptureTransform(round.shipAnimatorObject != null
                ? round.shipAnimatorObject.transform
                : round.elevatorTransform),
            CurrentPlanetTransform = CaptureTransform(round.currentPlanetPrefab != null
                ? round.currentPlanetPrefab.transform
                : null),
            CurrentPlanetActive = round.currentPlanetPrefab != null && round.currentPlanetPrefab.activeSelf,
            OuterSpaceSunActive = round.outerSpaceSunAnimator != null &&
                                  round.outerSpaceSunAnimator.gameObject.activeSelf,
            StarSphereActive = round.starSphereObject != null && round.starSphereObject.activeSelf,
            ShipAnimator = CaptureAnimator(round.shipAnimator),
            ShipBodyAnimator = CaptureAnimator(round.shipAnimatorObject != null
                ? round.shipAnimatorObject.GetComponent<Animator>()
                : null),
            ShipDoorsAnimator = CaptureAnimator(round.shipDoorsAnimator),
            CurrentPlanetAnimator = CaptureAnimator(round.currentPlanetAnimator),
            OuterSpaceSunAnimator = CaptureAnimator(round.outerSpaceSunAnimator)
        };

        if (!includeInterior)
            return snapshot;

        foreach (DoorLock door in UnityEngine.Object.FindObjectsOfType<DoorLock>())
            snapshot.Doors.Add(CaptureDoor(door));

        foreach (TerminalAccessibleObject terminalDoor in
                 UnityEngine.Object.FindObjectsOfType<TerminalAccessibleObject>())
        {
            if (terminalDoor.isBigDoor)
                snapshot.TerminalDoors.Add(CaptureTerminalDoor(terminalDoor));
        }

        return snapshot;
    }

    private static DoorSnapshot CaptureDoor(DoorLock door)
    {
        var animated = door.GetComponent<AnimatedObjectTrigger>();
        var trigger = door.GetComponent<InteractTrigger>();
        bool isOpen = animated != null
            ? animated.boolValue
            : ReadField(DoorIsOpenedField, door, false);

        return new DoorSnapshot
        {
            Position = door.transform.position,
            IsOpen = isOpen,
            IsLocked = door.isLocked,
            IsPickingLock = door.isPickingLock,
            LockPickTimeLeft = door.lockPickTimeLeft,
            TriggerInteractable = trigger == null || trigger.interactable,
            TriggerTimeToHold = trigger?.timeToHold ?? 0.3f,
            TriggerSpeedMultiplier = trigger?.timeToHoldSpeedMultiplier ?? 1f
        };
    }

    private static TerminalDoorSnapshot CaptureTerminalDoor(TerminalAccessibleObject door)
    {
        var animated = door.GetComponent<AnimatedObjectTrigger>();
        return new TerminalDoorSnapshot
        {
            Position = door.transform.position,
            IsOpen = ReadField(TerminalDoorOpenField, door, animated != null && animated.boolValue),
            IsPoweredOn = ReadField(TerminalPoweredField, door, true)
        };
    }

    private static TransformSnapshot CaptureTransform(Transform? transform)
    {
        if (transform == null)
            return new TransformSnapshot();

        return new TransformSnapshot
        {
            Exists = true,
            LocalPosition = transform.localPosition,
            LocalRotation = transform.localRotation,
            LocalScale = transform.localScale
        };
    }

    private static AnimatorSnapshot CaptureAnimator(Animator? animator)
    {
        var snapshot = new AnimatorSnapshot();
        if (animator == null)
            return snapshot;

        snapshot.Exists = true;
        snapshot.Enabled = animator.enabled;
        snapshot.Speed = animator.speed;

        foreach (AnimatorControllerParameter parameter in animator.parameters)
        {
            if (parameter.type == AnimatorControllerParameterType.Trigger)
                continue;

            var parameterSnapshot = new AnimatorParameterSnapshot
            {
                NameHash = parameter.nameHash,
                Type = parameter.type
            };
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Bool:
                    parameterSnapshot.BoolValue = animator.GetBool(parameter.nameHash);
                    break;
                case AnimatorControllerParameterType.Int:
                    parameterSnapshot.IntValue = animator.GetInteger(parameter.nameHash);
                    break;
                case AnimatorControllerParameterType.Float:
                    parameterSnapshot.FloatValue = animator.GetFloat(parameter.nameHash);
                    break;
            }
            snapshot.Parameters.Add(parameterSnapshot);
        }

        for (int layer = 0; layer < animator.layerCount; layer++)
        {
            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
            snapshot.Layers.Add(new AnimatorLayerSnapshot
            {
                StateHash = state.fullPathHash,
                NormalizedTime = state.normalizedTime,
                Weight = animator.GetLayerWeight(layer)
            });
        }

        return snapshot;
    }

    private static void ReceiveSnapshot(ulong senderClientId, FastBufferReader reader)
    {
        var network = NetworkManager.Singleton;
        if (network == null || senderClientId != NetworkManager.ServerClientId)
            return;

        try
        {
            WorldSnapshot snapshot = ReadSnapshot(ref reader);
            _latestClientSnapshot = snapshot;
            if (!MidJoinState.ClientLateJoinSyncCompleted)
                MidJoinState.ClientLateJoinSyncActive = true;

            if (_applySnapshotCoroutine != null)
                network.StopCoroutine(_applySnapshotCoroutine);
            _applySnapshotCoroutine = network.StartCoroutine(ApplySnapshot(snapshot));
            SendSnapshotAcknowledgement();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError("Failed to receive late-join world snapshot: " + ex);
        }
    }

    private static void SendSnapshotAcknowledgement()
    {
        var network = NetworkManager.Singleton;
        if (network == null || !network.IsClient || network.IsServer ||
            network.CustomMessagingManager == null)
            return;

        var writer = new FastBufferWriter(sizeof(int), Allocator.Temp);
        try
        {
            writer.WriteValueSafe(MESSAGE_VERSION);
            network.CustomMessagingManager.SendNamedMessage(
                ACK_MESSAGE_NAME,
                NetworkManager.ServerClientId,
                writer,
                NetworkDelivery.Reliable);
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void ReceiveSnapshotAcknowledgement(
        ulong senderClientId,
        FastBufferReader reader)
    {
        var network = NetworkManager.Singleton;
        if (network == null || !network.IsServer || senderClientId == NetworkManager.ServerClientId)
            return;

        try
        {
            reader.ReadValueSafe(out int version);
            if (version != MESSAGE_VERSION)
                return;
            ShipSnapshotAcknowledgements.Add(senderClientId);
            Plugin.Debug($"Client {senderClientId} acknowledged the landed ship snapshot.");
        }
        catch (Exception ex)
        {
            Plugin.Debug(
                $"Could not read landed ship snapshot acknowledgement from client {senderClientId}: " +
                ex.Message);
        }
    }

    private static IEnumerator ApplySnapshot(WorldSnapshot snapshot)
    {
        float deadline = Time.realtimeSinceStartup + APPLY_TIMEOUT_SECONDS;
        while (StartOfRound.Instance == null)
        {
            if (Time.realtimeSinceStartup >= deadline)
                yield break;
            yield return null;
        }

        ApplyWeatherState(snapshot);
        ApplyShipState(
            snapshot,
            moveLocalPlayer: !MidJoinState.ClientPlayerPositioned,
            holdAnimations: ShouldHoldShipAnimations());
        yield return null;
        ApplyShipState(
            snapshot,
            moveLocalPlayer: false,
            holdAnimations: ShouldHoldShipAnimations());

        if (snapshot.IncludeInterior)
        {
            while ((RoundManager.Instance == null || !RoundManager.Instance.dungeonCompletedGenerating) &&
                   Time.realtimeSinceStartup < deadline)
                yield return null;

            yield return null;
            yield return null;
            ApplyInteriorState(snapshot);
        }

        yield return new WaitForSeconds(0.25f);
        ApplyShipState(
            snapshot,
            moveLocalPlayer: false,
            holdAnimations: ShouldHoldShipAnimations());
        if (snapshot.IncludeInterior)
        {
            ApplyInteriorState(snapshot);
            yield return StabilizePowerState(snapshot);
            yield return StabilizeWeatherState(snapshot);
            MidJoinState.ClientLateJoinSyncCompleted = true;
            MidJoinState.ClientLateJoinSyncActive = false;
            ApplyShipState(snapshot, moveLocalPlayer: false, holdAnimations: false);
        }

        MidJoinState.LastStatus = snapshot.IncludeInterior
            ? "Applied ship, weather, door, lock, and facility-power snapshot."
            : "Applied landed ship snapshot.";
        Plugin.Debug(MidJoinState.LastStatus);
        _applySnapshotCoroutine = null;
    }

    private static void ApplyWeatherState(WorldSnapshot snapshot)
    {
        var round = StartOfRound.Instance;
        var timeOfDay = TimeOfDay.Instance;
        if (round?.currentLevel == null || timeOfDay == null)
            return;

        LevelWeatherType weather = (LevelWeatherType)snapshot.CurrentWeather;
        round.currentLevel.currentWeather = weather;
        timeOfDay.currentLevelWeather = weather;

        try
        {
            SetWeatherEffectsMethod?.Invoke(timeOfDay, null);
        }
        catch (Exception ex)
        {
            Plugin.Debug("Could not invoke TimeOfDay.SetWeatherEffects: " + ex.Message);
        }

        ForceWeatherEffectObjects(timeOfDay, weather);

        Plugin.Debug($"Applied authoritative late-join weather state: {weather}.");
    }

    private static void ForceWeatherEffectObjects(TimeOfDay timeOfDay, LevelWeatherType weather)
    {
        WeatherEffect[] effects = timeOfDay.effects;
        if (effects == null)
            return;

        int activeWeatherIndex = (int)weather;
        for (int i = 0; i < effects.Length; i++)
        {
            WeatherEffect effect = effects[i];
            if (effect == null)
                continue;

            bool shouldEnable = i == activeWeatherIndex;
            effect.effectEnabled = shouldEnable;
            effect.transitioning = false;

            if (effect.effectObject != null && effect.effectObject.activeSelf != shouldEnable)
                effect.effectObject.SetActive(shouldEnable);

            if (effect.effectPermanentObject != null &&
                effect.effectPermanentObject.activeSelf != shouldEnable)
                effect.effectPermanentObject.SetActive(shouldEnable);
        }
    }

    private static IEnumerator StabilizeWeatherState(WorldSnapshot snapshot)
    {
        float deadline = Time.realtimeSinceStartup + 2f;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsClient)
                yield break;

            ApplyWeatherState(snapshot);
            yield return new WaitForSecondsRealtime(0.2f);
        }
    }

    private static bool ShouldHoldShipAnimations()
    {
        return MidJoinState.ClientLateJoinSyncActive &&
               !MidJoinState.ClientLateJoinSyncCompleted;
    }

    private static void ApplyShipState(
        WorldSnapshot snapshot,
        bool moveLocalPlayer,
        bool holdAnimations)
    {
        var round = StartOfRound.Instance;
        if (round == null)
            return;

        StopShipTravelCoroutine(round);

        round.inShipPhase = snapshot.InShipPhase;
        round.shipHasLanded = snapshot.ShipHasLanded;
        round.shipIsLeaving = snapshot.ShipIsLeaving;
        round.shipDoorsEnabled = snapshot.ShipDoorsEnabled;
        round.hangarDoorsClosed = snapshot.HangarDoorsClosed;
        round.newGameIsLoading = snapshot.NewGameIsLoading;
        round.travellingToNewLevel = snapshot.TravellingToNewLevel;

        ApplyAnimator(round.shipAnimator, snapshot.ShipAnimator, holdAnimations);
        ApplyAnimator(round.shipAnimatorObject != null
            ? round.shipAnimatorObject.GetComponent<Animator>()
            : null, snapshot.ShipBodyAnimator, holdAnimations);
        ApplyAnimator(round.shipDoorsAnimator, snapshot.ShipDoorsAnimator, holdAnimations);
        ApplyAnimator(round.currentPlanetAnimator, snapshot.CurrentPlanetAnimator, holdAnimations);
        ApplyAnimator(round.outerSpaceSunAnimator, snapshot.OuterSpaceSunAnimator, holdAnimations);
        ApplyTransform(round.shipAnimatorObject != null
            ? round.shipAnimatorObject.transform
            : round.elevatorTransform, snapshot.ShipTransform);
        ApplyTransform(round.currentPlanetPrefab != null
            ? round.currentPlanetPrefab.transform
            : null, snapshot.CurrentPlanetTransform);

        if (round.currentPlanetPrefab != null)
            round.currentPlanetPrefab.SetActive(snapshot.CurrentPlanetActive);
        if (round.outerSpaceSunAnimator != null)
            round.outerSpaceSunAnimator.gameObject.SetActive(snapshot.OuterSpaceSunActive);
        if (round.starSphereObject != null)
            round.starSphereObject.SetActive(snapshot.StarSphereActive);

        SynchronizeAudio(round.shipAmbianceAudio, snapshot.ShipAmbiancePlaying);
        SynchronizeAudio(round.ship3DAudio, snapshot.ShipTravelAudioPlaying);

        PlayerControllerB? player = round.localPlayerController ??
                                    GameNetworkManager.Instance?.localPlayerController;
        if (player == null || player.isPlayerDead)
            return;

        if (snapshot.SpawnInShip && moveLocalPlayer && MoveLocalPlayerIntoShip(round, player))
        {
            MidJoinState.ClientPlayerPositioned = true;
            ResetPlayerMotion(round, player);
            Plugin.Debug($"Positioned the local late-join player at {player.transform.position} and cleared stale motion state.");
        }
    }

    internal static IEnumerator RunLateJoinLandedSequence(StartOfRound round)
    {
        float deadline = Time.realtimeSinceStartup + APPLY_TIMEOUT_SECONDS + 60f;
        WorldSnapshot? snapshot = _latestClientSnapshot;
        if (snapshot != null)
        {
            ApplyShipState(
                snapshot,
                moveLocalPlayer: !MidJoinState.ClientPlayerPositioned,
                holdAnimations: true);
        }
        ApplyImmediateLandedState(round, resetPlayerMotion: true);
        RestoreShipRadarMonitor(round);

        while (MidJoinState.ClientLateJoinSyncActive &&
               Time.realtimeSinceStartup < deadline &&
               NetworkManager.Singleton != null &&
               NetworkManager.Singleton.IsClient &&
               !NetworkManager.Singleton.IsServer)
            yield return null;

        WorldSnapshot? finalSnapshot = _latestClientSnapshot;
        if (finalSnapshot != null)
            ApplyShipState(finalSnapshot, moveLocalPlayer: false, holdAnimations: false);
        ApplyImmediateLandedState(round, resetPlayerMotion: true);
        yield return null;
        RestoreShipRadarMonitor(round);
    }

    private static void RestoreShipRadarMonitor(StartOfRound round)
    {
        try
        {
            ManualCameraRenderer? mapScreen = round.mapScreen;
            DisableLevelInformationOverlay(round, mapScreen);

            if (mapScreen == null || mapScreen.mapCamera == null)
                return;

            Coroutine? previousTargetUpdate = ReadField<Coroutine?>(
                MapScreenUpdateCoroutineField,
                mapScreen,
                null);
            if (previousTargetUpdate != null)
                mapScreen.StopCoroutine(previousTargetUpdate);

            mapScreen.overrideCameraForOtherUse = false;
            mapScreen.currentCameraDisabled = false;
            mapScreen.cam = mapScreen.mapCamera;
            SetField(MapScreenIsScreenOnField, mapScreen, true);
            SetField(MapScreenEnabledOnLocalClientField, mapScreen, true);
            SetField(MapScreenUpdateCoroutineField, mapScreen, null);
            SetField(MapScreenSyncingTargetField, mapScreen, false);
            SetField(MapScreenSyncingSwitchField, mapScreen, false);

            mapScreen.SwitchScreenOn(true);

            if (mapScreen.radarTargets == null || mapScreen.radarTargets.Count == 0)
            {
                RebuildRadarTargets(round, mapScreen);
            }

            if (mapScreen.radarTargets != null && mapScreen.radarTargets.Count > 0)
            {
                int targetIndex = FindUsableRadarTarget(mapScreen);
                mapScreen.targetTransformIndex = targetIndex;
                TransformAndName target = mapScreen.radarTargets[targetIndex];
                mapScreen.targetedPlayer = target.transform != null
                    ? target.transform.GetComponent<PlayerControllerB>()
                    : null;
                if (round.mapScreenPlayerName != null)
                    round.mapScreenPlayerName.text = "MONITORING: " + target.name;
                mapScreen.SwitchRadarTargetAndSync(targetIndex);
            }

            mapScreen.mapCamera.enabled = true;
            Plugin.Debug(
                $"Restored landed radar renderer: targets={mapScreen.radarTargets?.Count ?? 0}, " +
                $"target={mapScreen.targetTransformIndex}, camera={mapScreen.cam.name}.");
        }
        catch (Exception ex)
        {
            Plugin.Debug("Could not restore the landed ship radar monitor: " + ex);
        }
    }


    private static void DisableLevelInformationOverlay(
        StartOfRound round,
        ManualCameraRenderer? mapScreen)
    {
        if (round.screenLevelDescription != null)
        {
            round.screenLevelDescription.enabled = false;
            round.screenLevelDescription.gameObject.SetActive(false);
        }

        if (round.screenLevelVideoReel != null)
        {
            var videoPlayer = round.screenLevelVideoReel;
            videoPlayer.Stop();
            videoPlayer.enabled = false;

            // The level reel's visible quad is not reliably exposed through
            // targetMaterialRenderer. Disable the complete reel object so its last decoded
            // frame cannot remain composited above the radar after the VideoPlayer stops.
            if (videoPlayer.gameObject != null &&
                (mapScreen == null || videoPlayer.gameObject != mapScreen.gameObject))
            {
                videoPlayer.gameObject.SetActive(false);
            }
            else
            {
                DisableVideoOverlayRenderers(videoPlayer, mapScreen);
            }
        }

        if (round.radarCanvas != null)
        {
            round.radarCanvas.gameObject.SetActive(true);
            round.radarCanvas.enabled = true;
        }

        if (round.mapScreenPlayerName != null)
        {
            round.mapScreenPlayerName.gameObject.SetActive(true);
            round.mapScreenPlayerName.enabled = true;
        }

        if (mapScreen != null && mapScreen.mapCameraStationaryUI != null)
            mapScreen.mapCameraStationaryUI.gameObject.SetActive(true);
    }

    internal static void RestoreLevelInformationOverlayForOrbit(StartOfRound round)
    {
        if (round == null || !round.inShipPhase)
            return;

        if (round.screenLevelDescription != null)
            round.screenLevelDescription.gameObject.SetActive(true);

        if (round.screenLevelVideoReel != null)
        {
            var videoPlayer = round.screenLevelVideoReel;
            if (videoPlayer.gameObject != null)
                videoPlayer.gameObject.SetActive(true);

            foreach (Renderer renderer in videoPlayer.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = true;

            Renderer? targetRenderer = videoPlayer.targetMaterialRenderer;
            if (targetRenderer != null)
                targetRenderer.enabled = true;

            videoPlayer.enabled = true;
        }
    }

    private static void DisableVideoOverlayRenderers(
        UnityEngine.Video.VideoPlayer videoPlayer,
        ManualCameraRenderer? mapScreen)
    {
        Renderer? protectedRadarRenderer = mapScreen?.mesh;
        Renderer? targetRenderer = videoPlayer.targetMaterialRenderer;
        if (targetRenderer != null && targetRenderer != protectedRadarRenderer)
            targetRenderer.enabled = false;

        foreach (Renderer renderer in videoPlayer.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer != protectedRadarRenderer)
                renderer.enabled = false;
        }
    }

    private static void RebuildRadarTargets(StartOfRound round, ManualCameraRenderer mapScreen)
    {
        mapScreen.radarTargets ??= new List<TransformAndName>();
        mapScreen.radarTargets.Clear();
        foreach (PlayerControllerB player in round.allPlayerScripts)
        {
            if (player == null)
                continue;
            mapScreen.radarTargets.Add(new TransformAndName(player.transform, player.playerUsername));
        }
    }

    private static int FindUsableRadarTarget(ManualCameraRenderer mapScreen)
    {
        int count = mapScreen.radarTargets.Count;
        int startIndex = Mathf.Clamp(mapScreen.targetTransformIndex, 0, count - 1);
        for (int offset = 0; offset < count; offset++)
        {
            int index = (startIndex + offset) % count;
            TransformAndName target = mapScreen.radarTargets[index];
            if (target != null && target.transform != null)
                return index;
        }
        return 0;
    }

    private static void ApplyImmediateLandedState(
        StartOfRound round,
        bool resetPlayerMotion)
    {
        StopShipTravelCoroutine(round);
        round.inShipPhase = false;
        round.shipHasLanded = true;
        round.shipIsLeaving = false;
        round.newGameIsLoading = false;
        round.travellingToNewLevel = false;

        if (GameNetworkManager.Instance != null)
            GameNetworkManager.Instance.gameHasStarted = true;

        if (HUDManager.Instance != null)
            HideLoadingScreen(HUDManager.Instance);

        var lever = UnityEngine.Object.FindObjectOfType<StartMatchLever>();
        if (lever != null && lever.triggerScript != null)
        {
            lever.triggerScript.timeToHold = 0.7f;
            lever.triggerScript.animationString = "SA_PushLeverBack";
            lever.triggerScript.disabledHoverTip = string.Empty;
            lever.triggerScript.interactable = true;
            lever.hasDisplayedTimeWarning = false;
        }

        var hangarDoor = UnityEngine.Object.FindObjectOfType<HangarShipDoor>();
        if (hangarDoor != null)
            hangarDoor.SetDoorButtonsEnabled(round.shipDoorsEnabled);

        if (round.currentLevel != null && round.currentLevel.planetHasTime && TimeOfDay.Instance != null)
        {
            TimeOfDay.Instance.currentDayTimeStarted = true;
            TimeOfDay.Instance.movingGlobalTimeForward = true;
        }

        if (!resetPlayerMotion)
            return;
        PlayerControllerB? player = round.localPlayerController ??
                                    GameNetworkManager.Instance?.localPlayerController;
        if (player != null && !player.isPlayerDead)
            ResetPlayerMotion(round, player);
    }

    private static void HideLoadingScreen(HUDManager hud)
    {
        if (hud.loadingText != null)
            hud.loadingText.enabled = false;

        object? loadingScreen = LoadingScreenAnimatorField?.GetValue(hud);
        if (loadingScreen is Animator animator)
            animator.SetBool("IsLoading", false);

        object? legacyDarkenScreen = LegacyLoadingDarkenScreenField?.GetValue(hud);
        if (legacyDarkenScreen is Behaviour behaviour)
            behaviour.enabled = false;
    }

    private static void ApplyInteriorState(WorldSnapshot snapshot)
    {
        var manager = RoundManager.Instance;
        if (manager == null)
            return;

        ApplyPowerState(manager, snapshot.PowerOn, snapshot.PowerOffPermanently);

        DoorLock[] localDoors = UnityEngine.Object.FindObjectsOfType<DoorLock>();
        var usedDoors = new HashSet<int>();
        int matchedDoors = 0;
        foreach (DoorSnapshot doorState in snapshot.Doors)
        {
            DoorLock? door = FindClosest(localDoors, usedDoors, doorState.Position);
            if (door == null)
                continue;
            ApplyDoor(door, doorState);
            usedDoors.Add(door.GetInstanceID());
            matchedDoors++;
        }

        TerminalAccessibleObject[] localTerminalDoors =
            UnityEngine.Object.FindObjectsOfType<TerminalAccessibleObject>();
        var usedTerminalDoors = new HashSet<int>();
        int matchedTerminalDoors = 0;
        foreach (TerminalDoorSnapshot doorState in snapshot.TerminalDoors)
        {
            TerminalAccessibleObject? door =
                FindClosest(localTerminalDoors, usedTerminalDoors, doorState.Position, x => x.isBigDoor);
            if (door == null)
                continue;
            ApplyTerminalDoor(door, doorState);
            usedTerminalDoors.Add(door.GetInstanceID());
            matchedTerminalDoors++;
        }

        Plugin.Debug(
            $"Applied interior snapshot: regularDoors={matchedDoors}/{snapshot.Doors.Count}, " +
            $"terminalDoors={matchedTerminalDoors}/{snapshot.TerminalDoors.Count}, power={snapshot.PowerOn}");
    }

    private static IEnumerator StabilizePowerState(WorldSnapshot snapshot)
    {
        float deadline = Time.realtimeSinceStartup +
                         (snapshot.PowerOffPermanently ? 3f : 0.75f);
        while (Time.realtimeSinceStartup < deadline)
        {
            var manager = RoundManager.Instance;
            if (manager == null || NetworkManager.Singleton == null ||
                !NetworkManager.Singleton.IsClient)
                yield break;

            ApplyPowerState(manager, snapshot.PowerOn, snapshot.PowerOffPermanently);
            yield return new WaitForSecondsRealtime(0.1f);
        }
    }

    internal static bool ShouldBlockFacilityPowerOn(RoundManager manager, bool powerOn)
    {
        if (!powerOn || manager == null || !manager.powerOffPermanently)
            return false;

        var network = NetworkManager.Singleton;
        return network != null && network.IsClient && !network.IsServer;
    }

    private static void ApplyPowerState(RoundManager manager, bool powerOn, bool permanentlyOff)
    {
        if (permanentlyOff)
            powerOn = false;

        StopRoundCoroutine(manager, RoundFlickerLightsCoroutineField);
        StopRoundCoroutine(manager, RoundPowerLightsCoroutineField);
        manager.powerOffPermanently = permanentlyOff;

        var breaker = UnityEngine.Object.FindObjectOfType<BreakerBox>();
        if (breaker != null)
            breaker.isPowerOn = powerOn;

        foreach (LungProp apparatus in UnityEngine.Object.FindObjectsOfType<LungProp>())
        {
            if (permanentlyOff)
                apparatus.isLungDocked = false;
        }

        manager.onPowerSwitch.Invoke(powerOn);
        manager.TurnOnAllLights(powerOn);
    }

    private static void StopRoundCoroutine(RoundManager manager, FieldInfo? field)
    {
        if (field?.GetValue(manager) is not Coroutine coroutine)
            return;
        manager.StopCoroutine(coroutine);
        field.SetValue(manager, null);
    }

    private static void ApplyDoor(DoorLock door, DoorSnapshot snapshot)
    {
        door.isLocked = snapshot.IsLocked;
        door.isPickingLock = snapshot.IsPickingLock;
        door.lockPickTimeLeft = snapshot.LockPickTimeLeft;

        var animated = door.GetComponent<AnimatedObjectTrigger>();
        SetAnimatedBoolean(animated, snapshot.IsOpen);
        door.SetDoorAsOpen(snapshot.IsOpen);
        SetField(DoorIsOpenedField, door, snapshot.IsOpen);

        var trigger = door.GetComponent<InteractTrigger>();
        if (trigger != null)
        {
            trigger.interactable = snapshot.TriggerInteractable;
            trigger.timeToHold = snapshot.TriggerTimeToHold;
            trigger.timeToHoldSpeedMultiplier = snapshot.TriggerSpeedMultiplier;
            trigger.hoverTip = snapshot.IsLocked ? "Locked (pickable)" : "Use door : [LMB]";
            trigger.holdTip = snapshot.IsLocked ? "Picking lock" : string.Empty;
        }

        var obstacle = door.GetComponent<NavMeshObstacle>();
        if (obstacle != null)
        {
            obstacle.enabled = !snapshot.IsOpen;
            obstacle.carving = snapshot.IsLocked && !snapshot.IsOpen;
            obstacle.carveOnlyStationary = true;
        }
    }

    private static void ApplyTerminalDoor(
        TerminalAccessibleObject door,
        TerminalDoorSnapshot snapshot)
    {
        SetField(TerminalDoorOpenField, door, snapshot.IsOpen);
        SetField(TerminalPoweredField, door, snapshot.IsPoweredOn);
        SetAnimatedBoolean(
            door.GetComponent<AnimatedObjectTrigger>(),
            snapshot.IsOpen || !snapshot.IsPoweredOn);

        if (!HasTerminalMonitorUi(door))
        {
            QueueTerminalInitialization(door);
            return;
        }

        try
        {
            SetField(TerminalPoweredField, door, true);
            SetField(TerminalDoorOpenField, door, !snapshot.IsOpen);
            door.SetDoorOpen(snapshot.IsOpen);
            SetField(TerminalDoorOpenField, door, snapshot.IsOpen);
            door.OnPowerSwitch(snapshot.IsPoweredOn);
        }
        catch (Exception ex)
        {
            Plugin.Debug($"Terminal door native state application failed at {door.transform.position}: {ex.Message}");
        }

        SetField(TerminalDoorOpenField, door, snapshot.IsOpen);
        SetField(TerminalPoweredField, door, snapshot.IsPoweredOn);
    }

    internal static bool ShouldDeferTerminalInitialization(TerminalAccessibleObject door)
    {
        if (door == null || !IsLateClient())
            return false;
        if (IsTerminalMonitorReady())
            return false;

        QueueTerminalInitialization(door);
        return true;
    }

    private static bool IsLateClient()
    {
        var network = NetworkManager.Singleton;
        return Plugin.Enabled.Value && network != null && network.IsClient && !network.IsServer &&
               MidJoinState.ClientLateJoinSyncActive;
    }

    private static bool IsTerminalMonitorReady()
    {
        var round = StartOfRound.Instance;
        return round != null && round.objectCodePrefab != null && round.mapScreen != null &&
               round.mapScreen.mapCameraStationaryUI != null;
    }

    private static bool HasTerminalMonitorUi(TerminalAccessibleObject door)
    {
        return TerminalMapRadarTextField?.GetValue(door) is UnityEngine.Object text && text != null &&
               TerminalMapRadarBoxField?.GetValue(door) is UnityEngine.Object box && box != null;
    }

    private static void QueueTerminalInitialization(TerminalAccessibleObject door)
    {
        if (door == null)
            return;

        int instanceId = door.GetInstanceID();
        if (!DeferredTerminalInitializations.Add(instanceId))
            return;

        var network = NetworkManager.Singleton;
        if (network == null)
        {
            DeferredTerminalInitializations.Remove(instanceId);
            return;
        }

        network.StartCoroutine(InitializeTerminalWhenReady(door, instanceId));
    }

    private static IEnumerator InitializeTerminalWhenReady(
        TerminalAccessibleObject door,
        int instanceId)
    {
        float deadline = Time.realtimeSinceStartup + APPLY_TIMEOUT_SECONDS;
        while (door != null && !IsTerminalMonitorReady() &&
               Time.realtimeSinceStartup < deadline)
            yield return null;

        if (door == null || !IsTerminalMonitorReady())
        {
            DeferredTerminalInitializations.Remove(instanceId);
            yield break;
        }

        try
        {
            if (!HasTerminalMonitorUi(door))
            {
                SetField(TerminalInitializedValuesField, door, false);
                door.InitializeValues();
            }

            if (HasTerminalMonitorUi(door))
            {
                bool poweredOn = ReadField(TerminalPoweredField, door, true);
                door.OnPowerSwitch(poweredOn);
                Plugin.Debug($"Initialized deferred secure-door monitor UI at {door.transform.position}.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Debug($"Deferred secure-door monitor initialization failed: {ex.Message}");
        }
        finally
        {
            DeferredTerminalInitializations.Remove(instanceId);
        }
    }

    private static void SetAnimatedBoolean(AnimatedObjectTrigger? animated, bool value)
    {
        if (animated == null)
            return;

        animated.boolValue = value;
        if (animated.triggerAnimator != null && !string.IsNullOrEmpty(animated.animationString))
            animated.triggerAnimator.SetBool(animated.animationString, value);
        if (animated.triggerAnimatorB != null)
            animated.triggerAnimatorB.SetBool("on", value);
    }

    private static T? FindClosest<T>(
        T[] candidates,
        HashSet<int> used,
        Vector3 position,
        Func<T, bool>? predicate = null) where T : Component
    {
        T? closest = null;
        float closestDistance = OBJECT_MATCH_DISTANCE * OBJECT_MATCH_DISTANCE;
        foreach (T candidate in candidates)
        {
            if (candidate == null || used.Contains(candidate.GetInstanceID()) ||
                predicate != null && !predicate(candidate))
                continue;

            float distance = (candidate.transform.position - position).sqrMagnitude;
            if (distance > closestDistance)
                continue;
            closestDistance = distance;
            closest = candidate;
        }
        return closest;
    }

    private static void StopShipTravelCoroutine(StartOfRound round)
    {
        if (ShipTravelCoroutineField == null)
            return;
        try
        {
            if (ShipTravelCoroutineField.GetValue(round) is Coroutine coroutine)
                round.StopCoroutine(coroutine);
            ShipTravelCoroutineField.SetValue(round, null);
        }
        catch (Exception ex)
        {
            Plugin.Debug("Could not stop stale ship-travel coroutine: " + ex.Message);
        }
    }

    private static bool MoveLocalPlayerIntoShip(StartOfRound round, PlayerControllerB player)
    {
        int playerIndex = Array.IndexOf(round.allPlayerScripts, player);
        if (playerIndex < 0 || playerIndex >= round.playerSpawnPositions.Length)
            playerIndex = Mathf.Clamp(round.thisClientPlayerId, 0, round.playerSpawnPositions.Length - 1);
        if (playerIndex < 0 || playerIndex >= round.playerSpawnPositions.Length)
            return false;

        Transform spawn = round.playerSpawnPositions[playerIndex];
        player.TeleportPlayer(spawn.position);
        player.transform.SetPositionAndRotation(spawn.position, spawn.rotation);
        player.ResetZAndXRotation();
        SetField(PlayerIsInElevatorField, player, true);
        SetField(PlayerIsInHangarField, player, true);
        SetField(PlayerIsInsideFactoryField, player, false);
        Physics.SyncTransforms();
        return true;
    }

    private static void ResetPlayerMotion(StartOfRound round, PlayerControllerB player)
    {
        Vector3 position = player.transform.position;
        SetField(PlayerFallValueField, player, 0f);
        SetField(PlayerFallValueUncappedField, player, -7f);
        SetField(PlayerTakingFallDamageField, player, false);
        SetField(PlayerFallingFromJumpField, player, false);
        SetField(PlayerIsFallingNoJumpField, player, false);
        SetField(PlayerAverageVelocityField, player, 0f);
        SetField(PlayerVelocityLastFrameField, player, Vector3.zero);
        SetField(PlayerPreviousYField, player, position.y);
        SetField(PlayerExternalForcesField, player, Vector3.zero);
        SetField(PlayerIsSlidingField, player, false);
        SetField(PlayerSlidingTimerField, player, 0f);
        SetField(PlayerTeleportedLastFrameField, player, true);
        SetField(PlayerWasInElevatorLastFrameField, player, true);
        SetField(PlayerPreviousElevatorPositionField, player,
            round.elevatorTransform != null ? round.elevatorTransform.position : Vector3.zero);
        SetField(PlayerOldPositionField, player, position);
        SetField(PlayerServerPositionField, player, position);
        SetField(PlayerSnapToServerPositionField, player, false);
        SetField(PlayerUpdatePositionForNewClientField, player, false);
    }

    private static void SynchronizeAudio(AudioSource? source, bool shouldPlay)
    {
        if (source == null)
            return;
        if (shouldPlay)
        {
            if (!source.isPlaying && source.clip != null)
                source.Play();
        }
        else if (source.isPlaying)
        {
            source.Stop();
        }
    }

    private static void ApplyTransform(Transform? transform, TransformSnapshot snapshot)
    {
        if (transform == null || !snapshot.Exists)
            return;
        transform.localPosition = snapshot.LocalPosition;
        transform.localRotation = snapshot.LocalRotation;
        transform.localScale = snapshot.LocalScale;
    }

    private static void ApplyAnimator(
        Animator? animator,
        AnimatorSnapshot snapshot,
        bool holdAnimation)
    {
        if (animator == null || !snapshot.Exists)
            return;

        bool targetEnabled = snapshot.Enabled;
        animator.enabled = true;
        foreach (AnimatorControllerParameter parameter in animator.parameters)
        {
            if (parameter.type == AnimatorControllerParameterType.Trigger)
                animator.ResetTrigger(parameter.nameHash);
        }
        foreach (AnimatorParameterSnapshot parameter in snapshot.Parameters)
        {
            switch (parameter.Type)
            {
                case AnimatorControllerParameterType.Bool:
                    animator.SetBool(parameter.NameHash, parameter.BoolValue);
                    break;
                case AnimatorControllerParameterType.Int:
                    animator.SetInteger(parameter.NameHash, parameter.IntValue);
                    break;
                case AnimatorControllerParameterType.Float:
                    animator.SetFloat(parameter.NameHash, parameter.FloatValue);
                    break;
            }
        }

        int layers = Math.Min(animator.layerCount, snapshot.Layers.Count);
        for (int layer = 0; layer < layers; layer++)
        {
            AnimatorLayerSnapshot state = snapshot.Layers[layer];
            animator.SetLayerWeight(layer, state.Weight);
            if (state.StateHash != 0)
                animator.Play(state.StateHash, layer, Mathf.Clamp01(state.NormalizedTime));
        }
        animator.Update(0f);
        animator.speed = holdAnimation ? 0f : snapshot.Speed;
        animator.enabled = targetEnabled;
    }

    private static FieldInfo? FindInstanceField(Type type, string name)
    {
        return type.GetField(name, INSTANCE_FIELD_FLAGS);
    }

    private static bool ReadField(FieldInfo? field, object target, bool fallback)
    {
        if (field == null)
            return fallback;
        try
        {
            return field.GetValue(target) is bool value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static T ReadField<T>(FieldInfo? field, object target, T fallback)
    {
        if (field == null)
            return fallback;
        try
        {
            return field.GetValue(target) is T value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void SetField(FieldInfo? field, object target, object? value)
    {
        if (field == null)
            return;
        try
        {
            field.SetValue(target, value);
        }
        catch (Exception ex)
        {
            Plugin.Debug($"Could not set {field.DeclaringType?.Name}.{field.Name}: {ex.Message}");
        }
    }

    private static void WriteSnapshot(ref FastBufferWriter writer, WorldSnapshot snapshot)
    {
        writer.WriteValueSafe(MESSAGE_VERSION);
        writer.WriteValueSafe(snapshot.IncludeInterior);
        writer.WriteValueSafe(snapshot.SpawnInShip);
        writer.WriteValueSafe(snapshot.InShipPhase);
        writer.WriteValueSafe(snapshot.ShipHasLanded);
        writer.WriteValueSafe(snapshot.ShipIsLeaving);
        writer.WriteValueSafe(snapshot.ShipDoorsEnabled);
        writer.WriteValueSafe(snapshot.HangarDoorsClosed);
        writer.WriteValueSafe(snapshot.NewGameIsLoading);
        writer.WriteValueSafe(snapshot.TravellingToNewLevel);
        writer.WriteValueSafe(snapshot.ShipAmbiancePlaying);
        writer.WriteValueSafe(snapshot.ShipTravelAudioPlaying);
        writer.WriteValueSafe(snapshot.PowerOffPermanently);
        writer.WriteValueSafe(snapshot.PowerOn);
        writer.WriteValueSafe(snapshot.CurrentWeather);
        WriteTransform(ref writer, snapshot.ShipTransform);
        WriteTransform(ref writer, snapshot.CurrentPlanetTransform);
        writer.WriteValueSafe(snapshot.CurrentPlanetActive);
        writer.WriteValueSafe(snapshot.OuterSpaceSunActive);
        writer.WriteValueSafe(snapshot.StarSphereActive);
        WriteAnimator(ref writer, snapshot.ShipAnimator);
        WriteAnimator(ref writer, snapshot.ShipBodyAnimator);
        WriteAnimator(ref writer, snapshot.ShipDoorsAnimator);
        WriteAnimator(ref writer, snapshot.CurrentPlanetAnimator);
        WriteAnimator(ref writer, snapshot.OuterSpaceSunAnimator);

        writer.WriteValueSafe(snapshot.Doors.Count);
        foreach (DoorSnapshot door in snapshot.Doors)
        {
            WriteVector3(ref writer, door.Position);
            writer.WriteValueSafe(door.IsOpen);
            writer.WriteValueSafe(door.IsLocked);
            writer.WriteValueSafe(door.IsPickingLock);
            writer.WriteValueSafe(door.LockPickTimeLeft);
            writer.WriteValueSafe(door.TriggerInteractable);
            writer.WriteValueSafe(door.TriggerTimeToHold);
            writer.WriteValueSafe(door.TriggerSpeedMultiplier);
        }

        writer.WriteValueSafe(snapshot.TerminalDoors.Count);
        foreach (TerminalDoorSnapshot door in snapshot.TerminalDoors)
        {
            WriteVector3(ref writer, door.Position);
            writer.WriteValueSafe(door.IsOpen);
            writer.WriteValueSafe(door.IsPoweredOn);
        }
    }

    private static WorldSnapshot ReadSnapshot(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out int version);
        if (version != MESSAGE_VERSION)
            throw new InvalidOperationException($"Unsupported world snapshot version {version}.");

        var snapshot = new WorldSnapshot();
        reader.ReadValueSafe(out snapshot.IncludeInterior);
        reader.ReadValueSafe(out snapshot.SpawnInShip);
        reader.ReadValueSafe(out snapshot.InShipPhase);
        reader.ReadValueSafe(out snapshot.ShipHasLanded);
        reader.ReadValueSafe(out snapshot.ShipIsLeaving);
        reader.ReadValueSafe(out snapshot.ShipDoorsEnabled);
        reader.ReadValueSafe(out snapshot.HangarDoorsClosed);
        reader.ReadValueSafe(out snapshot.NewGameIsLoading);
        reader.ReadValueSafe(out snapshot.TravellingToNewLevel);
        reader.ReadValueSafe(out snapshot.ShipAmbiancePlaying);
        reader.ReadValueSafe(out snapshot.ShipTravelAudioPlaying);
        reader.ReadValueSafe(out snapshot.PowerOffPermanently);
        reader.ReadValueSafe(out snapshot.PowerOn);
        reader.ReadValueSafe(out snapshot.CurrentWeather);
        snapshot.ShipTransform = ReadTransform(ref reader);
        snapshot.CurrentPlanetTransform = ReadTransform(ref reader);
        reader.ReadValueSafe(out snapshot.CurrentPlanetActive);
        reader.ReadValueSafe(out snapshot.OuterSpaceSunActive);
        reader.ReadValueSafe(out snapshot.StarSphereActive);
        snapshot.ShipAnimator = ReadAnimator(ref reader);
        snapshot.ShipBodyAnimator = ReadAnimator(ref reader);
        snapshot.ShipDoorsAnimator = ReadAnimator(ref reader);
        snapshot.CurrentPlanetAnimator = ReadAnimator(ref reader);
        snapshot.OuterSpaceSunAnimator = ReadAnimator(ref reader);

        reader.ReadValueSafe(out int doorCount);
        if (doorCount < 0 || doorCount > 4096)
            throw new InvalidOperationException($"Invalid regular door count {doorCount}.");
        for (int i = 0; i < doorCount; i++)
        {
            var door = new DoorSnapshot { Position = ReadVector3(ref reader) };
            reader.ReadValueSafe(out door.IsOpen);
            reader.ReadValueSafe(out door.IsLocked);
            reader.ReadValueSafe(out door.IsPickingLock);
            reader.ReadValueSafe(out door.LockPickTimeLeft);
            reader.ReadValueSafe(out door.TriggerInteractable);
            reader.ReadValueSafe(out door.TriggerTimeToHold);
            reader.ReadValueSafe(out door.TriggerSpeedMultiplier);
            snapshot.Doors.Add(door);
        }

        reader.ReadValueSafe(out int terminalDoorCount);
        if (terminalDoorCount < 0 || terminalDoorCount > 4096)
            throw new InvalidOperationException($"Invalid terminal door count {terminalDoorCount}.");
        for (int i = 0; i < terminalDoorCount; i++)
        {
            var door = new TerminalDoorSnapshot { Position = ReadVector3(ref reader) };
            reader.ReadValueSafe(out door.IsOpen);
            reader.ReadValueSafe(out door.IsPoweredOn);
            snapshot.TerminalDoors.Add(door);
        }

        return snapshot;
    }

    private static void WriteTransform(ref FastBufferWriter writer, TransformSnapshot snapshot)
    {
        writer.WriteValueSafe(snapshot.Exists);
        if (!snapshot.Exists)
            return;
        WriteVector3(ref writer, snapshot.LocalPosition);
        WriteQuaternion(ref writer, snapshot.LocalRotation);
        WriteVector3(ref writer, snapshot.LocalScale);
    }

    private static TransformSnapshot ReadTransform(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out bool exists);
        if (!exists)
            return new TransformSnapshot();
        return new TransformSnapshot
        {
            Exists = true,
            LocalPosition = ReadVector3(ref reader),
            LocalRotation = ReadQuaternion(ref reader),
            LocalScale = ReadVector3(ref reader)
        };
    }

    private static void WriteAnimator(ref FastBufferWriter writer, AnimatorSnapshot snapshot)
    {
        writer.WriteValueSafe(snapshot.Exists);
        if (!snapshot.Exists)
            return;
        writer.WriteValueSafe(snapshot.Enabled);
        writer.WriteValueSafe(snapshot.Speed);
        writer.WriteValueSafe(snapshot.Parameters.Count);
        foreach (AnimatorParameterSnapshot parameter in snapshot.Parameters)
        {
            writer.WriteValueSafe(parameter.NameHash);
            writer.WriteValueSafe((int)parameter.Type);
            switch (parameter.Type)
            {
                case AnimatorControllerParameterType.Bool:
                    writer.WriteValueSafe(parameter.BoolValue);
                    break;
                case AnimatorControllerParameterType.Int:
                    writer.WriteValueSafe(parameter.IntValue);
                    break;
                case AnimatorControllerParameterType.Float:
                    writer.WriteValueSafe(parameter.FloatValue);
                    break;
            }
        }

        writer.WriteValueSafe(snapshot.Layers.Count);
        foreach (AnimatorLayerSnapshot layer in snapshot.Layers)
        {
            writer.WriteValueSafe(layer.StateHash);
            writer.WriteValueSafe(layer.NormalizedTime);
            writer.WriteValueSafe(layer.Weight);
        }
    }

    private static AnimatorSnapshot ReadAnimator(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out bool exists);
        var snapshot = new AnimatorSnapshot { Exists = exists };
        if (!exists)
            return snapshot;

        reader.ReadValueSafe(out snapshot.Enabled);
        reader.ReadValueSafe(out snapshot.Speed);
        reader.ReadValueSafe(out int parameterCount);
        if (parameterCount < 0 || parameterCount > 512)
            throw new InvalidOperationException($"Invalid animator parameter count {parameterCount}.");
        for (int i = 0; i < parameterCount; i++)
        {
            var parameter = new AnimatorParameterSnapshot();
            reader.ReadValueSafe(out parameter.NameHash);
            reader.ReadValueSafe(out int parameterType);
            parameter.Type = (AnimatorControllerParameterType)parameterType;
            switch (parameter.Type)
            {
                case AnimatorControllerParameterType.Bool:
                    reader.ReadValueSafe(out parameter.BoolValue);
                    break;
                case AnimatorControllerParameterType.Int:
                    reader.ReadValueSafe(out parameter.IntValue);
                    break;
                case AnimatorControllerParameterType.Float:
                    reader.ReadValueSafe(out parameter.FloatValue);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported animator parameter type {parameterType}.");
            }
            snapshot.Parameters.Add(parameter);
        }

        reader.ReadValueSafe(out int layerCount);
        if (layerCount < 0 || layerCount > 64)
            throw new InvalidOperationException($"Invalid animator layer count {layerCount}.");
        for (int i = 0; i < layerCount; i++)
        {
            var layer = new AnimatorLayerSnapshot();
            reader.ReadValueSafe(out layer.StateHash);
            reader.ReadValueSafe(out layer.NormalizedTime);
            reader.ReadValueSafe(out layer.Weight);
            snapshot.Layers.Add(layer);
        }
        return snapshot;
    }

    private static void WriteVector3(ref FastBufferWriter writer, Vector3 value)
    {
        writer.WriteValueSafe(value.x);
        writer.WriteValueSafe(value.y);
        writer.WriteValueSafe(value.z);
    }

    private static Vector3 ReadVector3(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out float x);
        reader.ReadValueSafe(out float y);
        reader.ReadValueSafe(out float z);
        return new Vector3(x, y, z);
    }

    private static void WriteQuaternion(ref FastBufferWriter writer, Quaternion value)
    {
        writer.WriteValueSafe(value.x);
        writer.WriteValueSafe(value.y);
        writer.WriteValueSafe(value.z);
        writer.WriteValueSafe(value.w);
    }

    private static Quaternion ReadQuaternion(ref FastBufferReader reader)
    {
        reader.ReadValueSafe(out float x);
        reader.ReadValueSafe(out float y);
        reader.ReadValueSafe(out float z);
        reader.ReadValueSafe(out float w);
        return new Quaternion(x, y, z, w);
    }

    private sealed class WorldSnapshot
    {
        internal bool IncludeInterior;
        internal bool SpawnInShip;
        internal bool InShipPhase;
        internal bool ShipHasLanded;
        internal bool ShipIsLeaving;
        internal bool ShipDoorsEnabled;
        internal bool HangarDoorsClosed;
        internal bool NewGameIsLoading;
        internal bool TravellingToNewLevel;
        internal bool ShipAmbiancePlaying;
        internal bool ShipTravelAudioPlaying;
        internal bool PowerOffPermanently;
        internal bool PowerOn;
        internal int CurrentWeather;
        internal bool CurrentPlanetActive;
        internal bool OuterSpaceSunActive;
        internal bool StarSphereActive;
        internal TransformSnapshot ShipTransform = new();
        internal TransformSnapshot CurrentPlanetTransform = new();
        internal AnimatorSnapshot ShipAnimator = new();
        internal AnimatorSnapshot ShipBodyAnimator = new();
        internal AnimatorSnapshot ShipDoorsAnimator = new();
        internal AnimatorSnapshot CurrentPlanetAnimator = new();
        internal AnimatorSnapshot OuterSpaceSunAnimator = new();
        internal readonly List<DoorSnapshot> Doors = new();
        internal readonly List<TerminalDoorSnapshot> TerminalDoors = new();
    }

    private sealed class TransformSnapshot
    {
        internal bool Exists;
        internal Vector3 LocalPosition;
        internal Quaternion LocalRotation;
        internal Vector3 LocalScale;
    }

    private sealed class AnimatorSnapshot
    {
        internal bool Exists;
        internal bool Enabled;
        internal float Speed;
        internal readonly List<AnimatorParameterSnapshot> Parameters = new();
        internal readonly List<AnimatorLayerSnapshot> Layers = new();
    }

    private sealed class AnimatorParameterSnapshot
    {
        internal int NameHash;
        internal AnimatorControllerParameterType Type;
        internal bool BoolValue;
        internal int IntValue;
        internal float FloatValue;
    }

    private sealed class AnimatorLayerSnapshot
    {
        internal int StateHash;
        internal float NormalizedTime;
        internal float Weight;
    }

    private sealed class DoorSnapshot
    {
        internal Vector3 Position;
        internal bool IsOpen;
        internal bool IsLocked;
        internal bool IsPickingLock;
        internal float LockPickTimeLeft;
        internal bool TriggerInteractable;
        internal float TriggerTimeToHold;
        internal float TriggerSpeedMultiplier;
    }

    private sealed class TerminalDoorSnapshot
    {
        internal Vector3 Position;
        internal bool IsOpen;
        internal bool IsPoweredOn;
    }
}
