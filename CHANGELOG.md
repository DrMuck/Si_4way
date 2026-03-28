# Si_4way — Change Log

---

## v1.6.0 (2026-03-28) — Win Conditions + Sounds + Migration

### New
- Custom win conditions: both queens dead = humans win, all HQs dead = aliens win
- End round triggered via mod (OnMissionStateChanged reflection)
- Mid-game migration all 4 directions (Alien↔Wildlife, Sol↔Centauri)
- TTS sound announcements: sub-team loss + side win (6 WAV files)
- Human-side: dead team UI greys out, players migrate to surviving ally

### Fixes
- State cleared between rounds (team tracking, queen flags)
- GetHasLost only keeps Alien UI alive for Wildlife (not human teams)

## v1.5.0 (2026-03-28) — Alliances + No Worms

- GetTeamsAreEnemy: Wildlife+Alien allies, Sol+Cent allies
- Projectile FF patches (impact + damage blocked between allies)
- Wildlife ambient life disabled (no worm spawns)

## v1.4.0 (2026-03-28) — Alien Queen Migration

- Alien queen death detected via polling (not events)
- All Alien players auto-migrate to Wildlife + respawn
- GetHasLost (server+client): Alien UI stays while Wildlife queen alive

## v1.3.0 (2026-03-28) — Alien Command

- `!alien` command to switch to Alien sub-team (untracked)

## v1.2.0 (2026-03-28) — Commander Seat Clear

- Commander seat cleared when stepping down via H+UI
- Uses `_wildlifeSetup.Commander == player` (not IsCommander which is already false)

## v1.1.0 (2026-03-28) — Wildlife UI Working

- ProcessNetRPC prefix intercepts REQUEST_JOIN_TEAM (RPC index 1)
- Wildlife-tracked players blocked from joining Alien → client shows Wildlife UI
- CLEAR_REQUEST sent to unblock client

## v1.0.0 (2026-03-28) — Clean restart

Stripped back to proven core after over-engineering caused state corruption bugs.
No BTB/CommMgmt patches, no redirect logic, no member tracking, no event subscriptions.

### Features
- `!4way` — enable/disable 4-way mode (admin)
- `!wildcom` — join Wildlife as commander (demotes from previous role, first come first served)
- `!wildlife` — join Wildlife as FPS (demotes from previous role, fallback spawn at structure)
- Wildlife team setup: copies BaseStructure/DefaultUnit/UsableResource from Alien
- Double nest spawn: main nest at map center (gets queen), anchor nest 100m underground
- Synthetic BaseTeamSetup injected into `BaseTeamSetups` list (with duplicate guard)
- GetPlayerIsCommander postfix for Wildlife commander

### Harmony Patches
- `MP_Strategy.SetTeamVersusMode` — force HvHvA when 4way enabled
- `GameModeExt.GetTeamSetup` — return Wildlife BaseTeamSetup
- `GameMode.GetPlayerIsCommander` — recognize Wildlife commander

### To be added incrementally (test after each)
1. Alliance patches (GetTeamsAreEnemy, projectile FF)
2. Auto-respawn (10s server-side timer)
3. Team balance (BTB integration)
4. Commander lottery (CommMgmt integration)
5. Player distribution (Alien→Wildlife via UI)
6. Win conditions
7. Shared FOW

### Lessons from v0.5.0 session
- `GameEvents.OnPlayerChangedTeam` redirect logic corrupted client UI state
- `_wildlifeMembers` tracking + forced redirects broke respawn menu
- BTB `ProcessJoinTeam` prefix may corrupt packet reader for non-join RPCs
- Multiple interacting Harmony patches create hard-to-debug state issues
- Add one feature at a time, test thoroughly before adding next

---

## v0.5.0 (2026-03-27) — Session with ElCodero (broken, backed up)

### What worked
- Nest spawning (double nest, main first for queen)
- !wildcom / !wildlife commands
- Auto-respawn after 10s death
- Commander lottery (stealing Alien applicant for Wildlife)
- Alliance patch (GetTeamsAreEnemy)
- Projectile friendly fire patches
- SpawnUnitForPlayer redirect (Wildlife players at Alien nest → redirected to Wildlife nest)

### What broke
- Respawn UI never showed Wildlife structures (only Alien nest)
- H+UI flow caused freecam, stale commander seats, wrong team spawns
- !alien + H caused "waiting for server" hang
- Root cause: too many interacting patches corrupted game state

### Files
- `Si_CoCommander_full_backup_20260327.cs` — full broken version
- `Si_CoCommander_cocom_backup.cs` — co-commander version

---

## v0.4.4 (2026-03-24) — Working 4-Way Prototype

### 4th Faction: Wildlife (Team_AlienWorms)
- Wildlife usable as 4th playable faction using Centauri HQ as base structure
- Commander view works via `!wildcom`
- Full Centauri construction tree available
- Resource display on minimap fixed (Il2Cpp UsableResource is property not field)

### Vote Intercept System
- Harmony Prefix on `MP_Strategy.SetTeamVersusMode`
- `!4way` / `!3way` / `!4waygm` commands

### Client Mod (Il2Cpp)
- Separate build: net6.0 for Il2Cpp client
- GetTeamSetup postfix for synthetic Wildlife BaseTeamSetup
- Il2Cpp gotchas: object params, property vs field, try/catch per assignment

### Map Center Spawn
- Terrain bounds + height sampling
- Game.SpawnPrefab for networked spawning

---

## v0.0.0 (2026-03-23) — Research & Co-Commander Experiments

- Co-commander research (abandoned — needs client mod)
- Wildlife faction discovery (Team_AlienWorms)
- Team system analysis (5 teams, BaseTeamSetup, GetTeamsAreEnemy)
- FOW is per-team, shared FOW needs cross-team detection hook
