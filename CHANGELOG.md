# Si_4way — Change Log

---

## v1.8.0 (2026-04-12) — Event-based win conditions, allied voice chat, DualHQ

### Major
- **Event-based win conditions**: replaced 200ms polling (`OnLateUpdate`) with Harmony postfixes on `MP_Strategy.OnStructureDestroyed` and `StrategyMode.OnUnitDestroyed`. Zero cost when nothing dies; eliminates race condition where round ended immediately at start if structures weren't yet registered on teams.
- **Allied voice chat**: Harmony prefix on `Player.GetCanReceiveVoice` — skips original method entirely for allied teams and returns `true`, so team voice (B key) works across Alien↔Wildlife and Sol↔Centauri allies. Prior `ref proximityOnly` approach failed because the original method reassigns the variable internally.
- **DualHQ mode** (`!DualHQ`, admin): toggles spawning a Cent HQ 30m from Sol's HQ and a Sol HQ 30m from Cent's HQ at round start. Each human faction gets access to both tech trees. Uses `Team.GetFirstStructureOfType` + `Game.SpawnPrefab` on a 3s delay.

### Fixes
- **`!alien` commander seat**: clearing Wildlife commander seat when a player uses `!alien` while being Wildlife commander (matched behavior already in `!sol` / `!centauri`). Prior bug left `_wildlifeSetup.Commander` dangling so `!wildcom` rejected other players.
- **Projectile friendly-fire reflection**: cached `PropertyInfo`/`FieldInfo` for `ProjectileBasic.Team` at init instead of per-impact `GetType().GetProperty()` + `GetType().GetField()`. Was running hundreds of times per second in heavy combat.

### Notes
- Multi-alien-team super-weapon handling and range-based cannon aim live in Si_CrabCannon (v2.7.0), not here. Si_4way no longer has any cross-mod patches on CrabCannon — the CrabCannon mod detects multiple alien-side teams (Alien + Wildlife) on its own.
- `ProcessNetRPC` prefix remains (TeamUI.cs) — early-returns on non-matching RPC byte, overhead is negligible.

---

## v1.7.1 (2026-04-12) — Alliance hot-path optimization

### Perf
- `AreTeamsAllied`: dropped two `Team.IsSpecial` property reads (virtual calls in Il2Cpp) in favor of pure reference compares against cached `_solTeam` / `_centTeam`. Branchless bool ops (`&`/`|`).
- Rationale: `GetTeamsAreEnemy` is called by the engine ~500k times per 20s (damage, targeting, FOW, voice). Profiler (Si_ModPerfMonitor) showed the postfix burning ~95ms/20s sustained; this trims roughly half of the body cost.
- Behavior unchanged: same alliance rules, same null/same-team edge cases.

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
