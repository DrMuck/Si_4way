# Si_4way — 4-Way Factions Mod for Silica

2v2 gameplay: **Wildlife+Alien** vs **Sol+Centauri**

## Features
- Wildlife as 4th playable faction (uses Alien Nest template)
- Built-in commander lottery (Alien applicants split to Alien + Wildlife commanders)
- Side-based team balance at round start (50/50 alien side vs human side)
- Alliance system (allied teams don't auto-attack each other)
- Win conditions (both queens dead = humans win, all HQs dead = aliens win)
- Mid-game migration when sub-team loses base
- Sound announcements (TTS WAV files)
- MapBalance integration (4way layout JSON support)
- StarterUnits integration (cross-spawns in 4way mode)

## Commands
| Command | Access | Description |
|---------|--------|-------------|
| `!4way` | Admin | Enable/disable 4-way mode |
| `!wildcom` | Player | Take Wildlife commander (if seat is free) |
| `!wildlife` | Player | Join Wildlife as FPS |
| `!alien` | Player | Switch to Alien sub-team |
| `!sol` | Player | Switch to Sol |
| `!centauri` | Player | Switch to Centauri |
| `!4waytol` | Admin | Set balance tolerance (default: 1) |

## Requirements

### Server (dedicated)
- `Si_4way.dll` (server build from `Si_4way_Server/`) in `Mods/`
- `Si_AdminMod.dll` — required for chat command registration and helper methods
- `Si_Logging.dll` — optional, used for HL-style log lines

### Client
- `Si_4way.dll` (client build from `Si_4way_Client/`) in `Mods/`
- **That's it.** The client build has no dependency on AdminMod or Logging.
  Those mods are server-only and should **not** be installed on clients —
  they register chat commands, hook damage events, and perform actions that
  are meaningless (or harmful) when duplicated on every client.

The server and client builds share the same assembly name (`Si_4way.dll`)
but are different binaries. Do not mix them.

## Important: Mod Compatibility
The following mods must be **DISABLED** when running Si_4way:
- **Si_CommManagement.dll** — Si_4way has built-in commander lottery
- **Si_BasicTeamBalance.dll** — Si_4way has built-in side-based balance

These mods have Harmony patches on `ProcessNetRPC` that conflict with Si_4way's
Wildlife UI protection. If enabled, the Alien dual-use UI for Wildlife will break.

## Build
```
cd Si_4way_Server && dotnet build -c Release
cd Si_4way_Client && dotnet build -c Release
```

## Sound Files
Place in `UserData/sounds/`:
- `alien_queen_lost.wav`, `wildlife_queen_lost.wav`
- `sol_hq_lost.wav`, `centauri_hq_lost.wav`
- `alien_team_wins.wav`, `human_team_wins.wav`

## MapBalance
Create 4-way layout JSONs with `"game_modes": { "4way": true }` and a `"Wildlife"` spawn entry.
