# LobbyControl Mid-Session Join

Compatibility add-on for **LobbyControl**. It allows players to connect after the ship has landed and replays the current moon state to the joining client.

## Important

- Install this on **every player**, not only the host.
- Install LobbyControl as usual.
- Do **not** install VeryLateCompany at the same time; both mods patch the same connection and level-generation paths.
- This is source-first experimental code. Mid-round synchronization is one of Lethal Company's most version-sensitive systems, so test with a disposable save and two clients.

## Version 0.3.3 synchronization flow

1. The host permits LobbyControl connections while the ship is landed.
2. The joining client immediately receives the host's landed ship flags, ship and planet transforms, ship, planet, sun, and door animator states, and travel-audio state.
3. The mod marks this client as a mid-session join before the level-finish RPC runs.
4. The native `openingDoorsSequence` is suppressed only on that joining client, preventing the full `OpenShip` and landing timeline from replaying.
5. Any stale client-side ship-travel coroutine is stopped and all pending ship and planet animator triggers are cleared.
6. The joining client receives the current moon seed, level, mold, and weather inputs.
7. The host waits for that client to report that its generated interior is complete.
8. The host sends the native level-finish RPC so the joining client refreshes interior lights and level-object lists only after the dungeon exists.
9. The host sends a targeted interior snapshot containing facility power, normal door open/closed and lock state, and terminal-controlled secure-door state.
10. When spawn-in-ship is enabled, the late client's local player is placed at its ship spawn and stale fall-damage accumulation is cleared.

## Build

1. Copy your installed `LobbyControl.dll` into `lib/`.
2. Install the .NET 8 SDK.
3. From the repository folder, run:

```powershell
dotnet restore
dotnet build -c Release
```

The DLL is produced in `bin/Release/netstandard2.1/`.
Copy it to `BepInEx/plugins/LobbyControlMidSessionJoin/` on every client.

## Terminal commands (host)

```text
midjoin status
midjoin enable
midjoin disable
midjoin debug on|off
midjoin autoopen on|off
midjoin spawninship on|off
```

## Test sequence

1. Host with LobbyControl and this mod on all players.
2. Run `midjoin debug on` on the host.
3. Start a moon and wait until the ship has fully landed.
4. Open and close several normal facility doors, unlock at least one locked door, and change a terminal-controlled secure door if available.
5. Have a second modded client join through Steam.
6. Confirm the joining client sees the ship already landed rather than replaying its landing transition.
7. Confirm the client is safely placed in the ship when `midjoin spawninship on` is active.
8. Enter through the main entrance and a fire exit.
9. Confirm facility lights, normal doors, locks, and secure doors match the host.
10. Confirm the host log reaches `Completed ship, interior, door, and lighting synchronization...`.

If the host reports a 60-second generation timeout, inspect both players' `BepInEx/LogOutput.log` for a dungeon-generation or mod compatibility exception before the acknowledgement RPC.

## Known limitations

The snapshot explicitly covers the landed ship, landed planet presentation, regular facility doors, terminal-controlled secure doors, lock interaction state, and facility power. Other mutable non-networked vanilla or modded dungeon objects may still require object-specific serializers. Netcode-spawned objects should normally synchronize through Unity Netcode.

## v0.3.3 movement stabilization

- Prevents duplicate `OnPlayerConnectedClientRpc` callbacks from starting overlapping synchronization coroutines for the same client.
- Runs client snapshot application on the active `NetworkManager` instead of the plugin singleton, preventing the `StartCoroutine` null reference shown in client logs.
- Acknowledges a ship snapshot only after its application coroutine starts successfully.
- Removes periodic ship-snapshot retransmission while the dungeon is generating.
- Positions the late client once, then clears sliding, fall, external-force, interpolation, and stale server-snap state.
- Keeps the replacement landing iterator passive while synchronization is active instead of rewriting the ship transform and player motion every frame.
