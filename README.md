# LobbyControl Mid-Session Join

Compatibility add-on for **LobbyControl**. It allows players to connect after the ship has landed and replays the current moon state to the joining client.

## Important

- Install this on **every player**, not only the host.
- Install LobbyControl as usual.
<<<<<<< HEAD

## Terminal commands (host)

```text
midjoin status
midjoin enable
midjoin disable
midjoin debug on|off
midjoin autoopen on|off
midjoin spawninship on|off
```
=======
- Do **not** install VeryLateCompany at the same time; both mods patch the same connection and level-generation paths.
- This is source-first experimental code. Mid-round synchronization is one of Lethal Company's most version-sensitive systems, so test with a disposable save and two clients.
>>>>>>> c707aca (Release v0.4.0)

## Known limitations

The snapshot explicitly covers the landed ship, landed planet presentation, regular facility doors, terminal-controlled secure doors, lock interaction state, and facility power. Other mutable non-networked vanilla or modded dungeon objects may still require object-specific serializers. Netcode-spawned objects should normally synchronize through Unity Netcode.

