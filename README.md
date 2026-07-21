# LobbyControl Mid-Session Join

Compatibility add-on for **LobbyControl 2.5.12**. It allows players to connect after the ship has landed and replays the current moon state to the joining client.

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

The snapshot explicitly covers the landed ship, landed planet presentation, active moon weather, regular facility doors, terminal-controlled secure doors, lock interaction state, and facility power. Other mutable non-networked vanilla or modded dungeon objects may still require object-specific serializers. Netcode-spawned objects should normally synchronize through Unity Netcode.

