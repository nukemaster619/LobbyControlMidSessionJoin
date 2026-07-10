# LobbyControl Mid-Session Join

Experimental compatibility add-on for **LobbyControl**. It allows players to connect after the ship has landed and sends the active moon seed/level/weather to the joining client.

## Important

- Install this on **every player**, not only the host.
- Install LobbyControl as usual.
- Do **not** install VeryLateCompany at the same time; both mods patch the same connection and level-generation paths.
- This is source-first experimental code. Mid-round synchronization is one of Lethal Company's most version-sensitive systems, so test with a disposable save and two clients.

## Build

1. Create a GitHub repository and copy these files into it.
2. Create `lib/` and copy your installed `LobbyControl.dll` into it.
3. Install the .NET 8 SDK.
4. From the repository folder, run:

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

1. Host with LobbyControl + this mod.
2. Start a moon and wait until the ship has fully landed.
3. Run `midjoin status`; phase should be `landed moon`, bridge `OK`, handler `True`.
4. Have a second modded client join through Steam.
5. The new client should generate the same moon and appear in the ship.
6. On failure, run `midjoin debug on`, reproduce once, then inspect `BepInEx/LogOutput.log`.

## Known limitations in this first pass

The snapshot synchronizes level generation inputs, weather, and the late player's basic spawn. It does not yet explicitly replay every mutable round object (opened doors, breaker state, collected scrap, killed enemies, mines/turrets, apparatus state, etc.). Netcode-spawned objects should normally synchronize, but scene-object state may need additional serializers after testing.
