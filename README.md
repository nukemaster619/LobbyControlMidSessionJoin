# LobbyControl Mid-Session Join

Compatibility add-on for **LobbyControl**. It allows players to connect after the ship has landed and replays the current moon state to the joining client.

## Important

- Install this on **every player**, not only the host.
- Install LobbyControl as usual.

## Terminal commands (host)

```text
midjoin status
midjoin enable
midjoin disable
midjoin debug on|off
midjoin autoopen on|off
midjoin spawninship on|off
```

## Known limitations

The snapshot explicitly covers the landed ship, landed planet presentation, regular facility doors, terminal-controlled secure doors, lock interaction state, and facility power. Other mutable non-networked vanilla or modded dungeon objects may still require object-specific serializers. Netcode-spawned objects should normally synchronize through Unity Netcode.

## v0.3.5 movement stabilization

- Prevents duplicate `OnPlayerConnectedClientRpc` callbacks from starting overlapping synchronization coroutines for the same client.
- Runs client snapshot application on the active `NetworkManager` instead of the plugin singleton, preventing the `StartCoroutine` null reference shown in client logs.
- Acknowledges a ship snapshot only after its application coroutine starts successfully.
- Removes periodic ship-snapshot retransmission while the dungeon is generating.
- Positions the late client once, then clears sliding, fall, external-force, interpolation, and stale server-snap state.
- Keeps the replacement landing iterator passive while synchronization is active instead of rewriting the ship transform and player motion every frame.

## v0.3.5 apparatus power and reconnect fixes

- Treats an undocked apparatus as authoritative permanent facility power loss.
- Reapplies only facility power for a short stabilization window after native level finalization.
- Stops stale local light/flicker coroutines before applying the host power state.
- Cancels synchronization immediately when a client disconnects.
- Uses per-attempt synchronization IDs so a stale coroutine cannot interfere with a rejoin using the same client ID.
- Clears stale generation acknowledgements and floor-generation entries on disconnect.


## v0.3.5 apparatus facility-light synchronization

- Fixes the apparatus-derived permanent power-loss value being calculated but not written into the world snapshot.
- Prevents delayed client-local level startup from calling `TurnOnAllLights(true)` after permanent apparatus power loss.
- Adds permanent-power-loss state to synchronization debug output.

