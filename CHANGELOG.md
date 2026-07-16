# Changelog

## [0.3.7 - 0.4.0]

### Fixed

- Fixed the ship monitor for late-joining clients.
- Restored the radar/player view, monitor switching, player name, radar markers, secure-door codes, and related overlays.
- Removed the moon information video/image overlay that could remain visible over the radar after landing.
- Restored the normal moon information display when returning to orbit.
- Improved monitor recovery when the radar camera or target list was not initialized correctly.

## [0.3.6]

### Fixed

- Fixed secure-door radar UI initializing before the ship monitor was ready.
- Prevented repeated `TerminalAccessibleObject` errors that could break monitor updates for late joiners.

## [0.3.5]

### Fixed

- Fixed facility lights being turned back on for late joiners after the apparatus permanently disabled power.
- Correctly synchronized permanent apparatus power loss and blocked delayed client-side lights-on calls.

## [0.3.4]

### Fixed

- Improved apparatus and facility power synchronization for late joiners.
- Fixed stale synchronization state causing timeouts or errors when a late joiner left and rejoined the same lobby.
