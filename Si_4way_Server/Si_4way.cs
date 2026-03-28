using HarmonyLib;
using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_4way.Si_4way), "Si_4way", "1.0.0", "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]

namespace Si_4way
{
    public class Si_4way : MelonMod
    {
        public static bool Is4WayEnabled = false;
        private static Type _mpStrategyType = null;
        private static MethodInfo _setCommanderMethod = null;
        private static MethodInfo _synchCommanderMethod = null;
        private static Type _gameType = null;
        private static MethodInfo _spawnPrefabMethod = null;

        private static BaseTeamSetup _wildlifeSetup = null;
        private static Team _wildlifeTeam = null;
        private static Team _alienTeam = null;

        private static bool _needWildlifeNestSpawn = false;
        private static float _nestSpawnDelay = 0f;
        private static bool _alienQueenWasAlive = true;
        private static bool _wildlifeQueenWasAlive = true;
        private static bool _solHQWasAlive = true;
        private static bool _centHQWasAlive = true;
        private static bool _gameEndTriggered = false;
        private static Team _solTeam = null;
        private static Team _centTeam = null;

        // Track players who were on Wildlife — if they switch via H+UI to Alien,
        // fix their team back to Wildlife at spawn time
        private static HashSet<ulong> _wasOnWildlife = new HashSet<ulong>();

        // For triggering end round via reflection
        private static MethodInfo _onMissionStateChangedMethod = null;

        private static bool IsServer => Game.GetIsServer();

        public override void OnInitializeMelon()
        {
            var harmony = new HarmonyLib.Harmony("com.drmuck.4way");

            _mpStrategyType = typeof(GameMode).Assembly.GetType("MP_Strategy");
            _gameType = typeof(GameMode).Assembly.GetType("Game");

            if (_mpStrategyType != null)
            {
                _setCommanderMethod = _mpStrategyType.GetMethod("SetCommander",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _synchCommanderMethod = _mpStrategyType.GetMethod("RPC_SynchCommander",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _onMissionStateChangedMethod = _mpStrategyType.GetMethod("OnMissionStateChanged",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                var setTvMethod = _mpStrategyType.GetMethod("SetTeamVersusMode",
                    BindingFlags.Public | BindingFlags.Instance);
                if (setTvMethod != null)
                {
                    harmony.Patch(setTvMethod,
                        prefix: new HarmonyMethod(typeof(Si_4way), nameof(Prefix_SetTeamVersusMode)));
                    MelonLogger.Msg("Patched SetTeamVersusMode");
                }
            }

            // GetTeamSetup — return synthetic BaseTeamSetup for Wildlife
            var getTeamSetup = typeof(GameModeExt).GetMethod("GetTeamSetup",
                BindingFlags.Public | BindingFlags.Instance);
            if (getTeamSetup != null)
            {
                harmony.Patch(getTeamSetup,
                    postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_GetTeamSetup)));
                MelonLogger.Msg("Patched GetTeamSetup");
            }

            // GetPlayerIsCommander — support Wildlife commander
            var getPlayerIsCommander = typeof(GameMode).GetMethod("GetPlayerIsCommander",
                BindingFlags.Public | BindingFlags.Instance);
            if (getPlayerIsCommander != null)
            {
                harmony.Patch(getPlayerIsCommander,
                    postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_GetPlayerIsCommander)));
                MelonLogger.Msg("Patched GetPlayerIsCommander");
            }

            // GetTeamsAreEnemy — allies don't attack each other
            var getTeamsAreEnemy = typeof(GameMode).GetMethod("GetTeamsAreEnemy",
                BindingFlags.Public | BindingFlags.Instance);
            if (getTeamsAreEnemy != null)
            {
                harmony.Patch(getTeamsAreEnemy,
                    postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_GetTeamsAreEnemy)));
                MelonLogger.Msg("Patched GetTeamsAreEnemy for alliances");
            }

            // Projectile friendly fire — block damage between allies
            var projType = typeof(GameMode).Assembly.GetType("ProjectileBasic");
            if (projType != null)
            {
                var ffImpact = projType.GetMethod("GetAllowFriendlyFireOnImpact",
                    BindingFlags.Public | BindingFlags.Instance);
                if (ffImpact != null)
                {
                    harmony.Patch(ffImpact,
                        prefix: new HarmonyMethod(typeof(Si_4way), nameof(Prefix_GetAllowFriendlyFireOnImpact)));
                    MelonLogger.Msg("Patched GetAllowFriendlyFireOnImpact");
                }
                var ffDamage = projType.GetMethod("GetAllowFriendlyFireDamage",
                    BindingFlags.Public | BindingFlags.Instance);
                if (ffDamage != null)
                {
                    harmony.Patch(ffDamage,
                        prefix: new HarmonyMethod(typeof(Si_4way), nameof(Prefix_GetAllowFriendlyFireDamage)));
                    MelonLogger.Msg("Patched GetAllowFriendlyFireDamage");
                }
            }

            // Shared FOW — when one allied team detects a target, share with ally
            var detectTarget = typeof(Team).GetMethod("DetectTarget",
                BindingFlags.Public | BindingFlags.Instance,
                null, new Type[] { typeof(Target), typeof(Sensor) }, null);
            if (detectTarget != null)
            {
                harmony.Patch(detectTarget,
                    postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_DetectTarget)));
                MelonLogger.Msg("Patched Team.DetectTarget for shared FOW");
            }

            // Patch GetHasLost — keep Alien UI accessible while Wildlife queen lives
            var getHasLost = typeof(StrategyTeamSetup).GetMethod("GetHasLost",
                BindingFlags.Public | BindingFlags.Instance);
            if (getHasLost != null)
            {
                harmony.Patch(getHasLost,
                    postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_GetHasLost)));
                MelonLogger.Msg("Patched GetHasLost for Alien+Wildlife linked elimination");
            }

            // Patch ProcessNetRPC to intercept REQUEST_JOIN_TEAM for Wildlife players
            if (_mpStrategyType != null)
            {
                var processRpc = _mpStrategyType.GetMethod("ProcessNetRPC",
                    BindingFlags.Public | BindingFlags.Instance);
                if (processRpc != null)
                {
                    harmony.Patch(processRpc,
                        prefix: new HarmonyMethod(typeof(Si_4way), nameof(Prefix_ProcessNetRPC)));
                    MelonLogger.Msg("Patched ProcessNetRPC for Wildlife team protection");
                }
            }

            ResolveSpawnMethod();

            try
            {
                HelperMethods.RegisterAdminCommand("4way", OnCmd4Way, Power.Commander, "Enable 4-way mode");
                HelperMethods.RegisterPlayerCommand("wildcom", OnCmdWildCom, true);
                HelperMethods.RegisterPlayerCommand("wildlife", OnCmdWildFps, true);
                HelperMethods.RegisterPlayerCommand("alien", OnCmdAlien, true);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"Could not register commands: {ex.Message}");
            }

            FindTeams();
            MelonLogger.Msg($"Si_4way v1.0.0 loaded ({(IsServer ? "SERVER" : "CLIENT")})");
        }

        // =============================================
        // Teams
        // =============================================
        private static Team GetFourthTeam() => _wildlifeTeam;

        private static void FindTeams()
        {
            try
            {
                foreach (var t in Team.Teams)
                {
                    if (t.TeamName.Contains("Worm") || t.TeamName.Contains("Wildlife"))
                        _wildlifeTeam = t;
                    else if (t.TeamName.Contains("Alien") && !t.IsSpecial)
                        _alienTeam = t;
                    else if (t.TeamName.Contains("Sol"))
                        _solTeam = t;
                    else if (t.TeamName.Contains("Centauri"))
                        _centTeam = t;
                }
                if (_wildlifeTeam != null) MelonLogger.Msg($"[4WAY] Found Wildlife: {_wildlifeTeam.TeamName}");
                if (_alienTeam != null) MelonLogger.Msg($"[4WAY] Found Alien: {_alienTeam.TeamName}");
                if (_solTeam != null) MelonLogger.Msg($"[4WAY] Found Sol: {_solTeam.TeamName}");
                if (_centTeam != null) MelonLogger.Msg($"[4WAY] Found Centauri: {_centTeam.TeamName}");
            }
            catch { }
        }

        // =============================================
        // SpawnPrefab resolution
        // =============================================
        private static void ResolveSpawnMethod()
        {
            try
            {
                if (_gameType == null) return;
                _spawnPrefabMethod = _gameType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "SpawnPrefab")
                    .OrderByDescending(m => m.GetParameters().Length)
                    .FirstOrDefault();
                if (_spawnPrefabMethod != null)
                    MelonLogger.Msg($"[4WAY] Resolved SpawnPrefab ({_spawnPrefabMethod.GetParameters().Length} params)");
            }
            catch (Exception ex) { MelonLogger.Warning($"[4WAY] ResolveSpawnMethod: {ex.Message}"); }
        }

        // =============================================
        // GetTeamSetup Postfix — synthetic Wildlife setup
        // =============================================
        public static void Postfix_GetTeamSetup(GameModeExt __instance, Team team, ref BaseTeamSetup __result)
        {
            if (__result != null) return;
            if (team == null || _wildlifeTeam == null) return;
            if (team != _wildlifeTeam) return;

            if (_wildlifeSetup == null)
                CreateWildlifeSetup(__instance);

            if (_wildlifeSetup != null)
                __result = _wildlifeSetup;
        }

        private static void CreateWildlifeSetup(GameModeExt gameMode)
        {
            try
            {
                if (_wildlifeTeam == null || _alienTeam == null) FindTeams();
                if (_wildlifeTeam == null) return;

                BaseTeamSetup alienSetup = null;
                var setupsField = typeof(GameModeExt).GetField("BaseTeamSetups",
                    BindingFlags.Public | BindingFlags.Instance);
                if (setupsField != null)
                {
                    var setups = setupsField.GetValue(gameMode) as IList<BaseTeamSetup>;
                    if (setups != null)
                        alienSetup = setups.FirstOrDefault(s => s.Team == _alienTeam);
                    else
                    {
                        var rawSetups = setupsField.GetValue(gameMode) as System.Collections.IList;
                        if (rawSetups != null)
                            foreach (var s in rawSetups)
                            {
                                var bts = s as BaseTeamSetup;
                                if (bts?.Team == _alienTeam) { alienSetup = bts; break; }
                            }
                    }
                }

                _wildlifeSetup = new BaseTeamSetup();
                _wildlifeSetup.Team = _wildlifeTeam;
                _wildlifeSetup.Enabled = true;
                _wildlifeSetup.StartingResources = alienSetup?.StartingResources ?? 8000;
                _wildlifeSetup.PlayerSpawn = alienSetup?.PlayerSpawn;
                _wildlifeSetup.PlayerSpawnExt = alienSetup?.PlayerSpawnExt;
                _wildlifeSetup.AICommanderSettings = alienSetup?.AICommanderSettings;
                _wildlifeSetup.AICommanderPlayerLeftWaitTime = 30f;
                _wildlifeSetup.HelperName = "Wildlife";

                _wildlifeSetup.ForTeamVersusModes = new List<GameModeExt.ETeamsVersus>();
                foreach (GameModeExt.ETeamsVersus mode in Enum.GetValues(typeof(GameModeExt.ETeamsVersus)))
                    _wildlifeSetup.ForTeamVersusModes.Add(mode);

                // Inject into BaseTeamSetups list
                if (setupsField != null)
                {
                    var setups = setupsField.GetValue(gameMode) as IList<BaseTeamSetup>;
                    if (setups != null)
                    {
                        // Avoid duplicates
                        if (!setups.Any(s => s.Team == _wildlifeTeam))
                        {
                            setups.Add(_wildlifeSetup);
                            MelonLogger.Msg("[4WAY] Injected Wildlife into BaseTeamSetups list");
                        }
                    }
                }

                MelonLogger.Msg($"[4WAY] Created Wildlife BaseTeamSetup (spawn={_wildlifeSetup.PlayerSpawn?.DisplayName ?? "null"})");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[4WAY] CreateWildlifeSetup failed: {ex.Message}");
            }
        }

        // =============================================
        // GetPlayerIsCommander Postfix
        // =============================================
        public static void Postfix_GetPlayerIsCommander(Player player, ref bool __result)
        {
            if (__result) return;
            if (player == null || _wildlifeSetup == null) return;
            if (player.Team != _wildlifeTeam) return;
            if (_wildlifeSetup.Commander == player)
                __result = true;
        }

        // =============================================
        // Vote intercept → force HvHvA + setup Wildlife
        // =============================================
        public static bool Prefix_SetTeamVersusMode(ref GameModeExt.ETeamsVersus teamsVersusMode, bool preventSpawning)
        {
            if (!Is4WayEnabled) return true;
            if (teamsVersusMode == GameModeExt.ETeamsVersus.NONE) return true;
            if (preventSpawning) return true;

            var original = teamsVersusMode;
            teamsVersusMode = GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS;
            MelonLogger.Msg($"[4WAY] Vote override: {original} → HvHvA");

            SetupWildlifeTeam();
            // Clear ALL state for new round
            _alienQueenDead = false;
            _alienQueenWasAlive = true;
            _wildlifeQueenWasAlive = true;
            _solHQWasAlive = true;
            _centHQWasAlive = true;
            _gameEndTriggered = false;
            _wasOnWildlife.Clear();
            _needWildlifeNestSpawn = true;
            _nestSpawnDelay = 3f;

            return true;
        }

        private static bool _alienQueenDead = false;

        // =============================================
        // Nest spawn timer
        // =============================================
        public override void OnLateUpdate()
        {
            if (!Is4WayEnabled || !IsServer) return;

            if (_needWildlifeNestSpawn && GameMode.CurrentGameMode != null)
            {
                _nestSpawnDelay -= Time.deltaTime;
                if (_nestSpawnDelay <= 0f)
                {
                    _needWildlifeNestSpawn = false;
                    SpawnWildlifeNest();
                    DisableWildlifeAmbientLife();
                }
            }

            if (GameMode.CurrentGameMode == null || !GameMode.CurrentGameMode.GameBegun) return;
            if (_alienTeam == null || _wildlifeTeam == null) return;

            bool alienHasCritical = _alienTeam.GetHasAnyCritical();
            bool wildlifeHasCritical = _wildlifeTeam.GetHasAnyCritical();

            // Detect Alien queen death → migrate Alien players to Wildlife
            if (!alienHasCritical && _alienQueenWasAlive)
            {
                _alienQueenWasAlive = false;
                _alienQueenDead = true;
                PlaySoundToAll("sounds\\alien_queen_lost.wav");
                if (wildlifeHasCritical)
                    MigratePlayersToAlly(_alienTeam, _wildlifeTeam, "Alien queen fallen!");
            }

            // Detect Wildlife queen death → migrate Wildlife players to Alien
            if (!wildlifeHasCritical && _wildlifeQueenWasAlive)
            {
                _wildlifeQueenWasAlive = false;
                PlaySoundToAll("sounds\\wildlife_queen_lost.wav");
                if (alienHasCritical)
                    MigratePlayersToAlly(_wildlifeTeam, _alienTeam, "Wildlife queen fallen!");
            }

            // Detect Sol HQ death → migrate Sol players to Centauri
            if (_solTeam != null && _centTeam != null)
            {
                bool solHasCritical = _solTeam.GetHasAnyCritical();
                bool centHasCritical = _centTeam.GetHasAnyCritical();

                if (!solHasCritical && _solHQWasAlive)
                {
                    _solHQWasAlive = false;
                    PlaySoundToAll("sounds\\sol_hq_lost.wav");
                    if (centHasCritical)
                        MigratePlayersToAlly(_solTeam, _centTeam, "Sol HQ destroyed!");
                }

                if (!centHasCritical && _centHQWasAlive)
                {
                    _centHQWasAlive = false;
                    PlaySoundToAll("sounds\\centauri_hq_lost.wav");
                    if (solHasCritical)
                        MigratePlayersToAlly(_centTeam, _solTeam, "Centauri HQ destroyed!");
                }
            }

            // Custom end-round: if BOTH queens dead → alien side lost
            if (!_gameEndTriggered && !alienHasCritical && !wildlifeHasCritical)
            {
                _gameEndTriggered = true;
                TriggerEndRound("Both Alien queens destroyed — Humans win!", aliensWin: false);
            }

            // Custom end-round: if ALL human HQs dead → human side lost
            if (!_gameEndTriggered)
            {
                bool anyHumanCritical = false;
                foreach (var t in Team.Teams)
                {
                    if (t != null && t != _alienTeam && t != _wildlifeTeam && !t.IsSpecial)
                    {
                        if (t.GetHasAnyCritical()) { anyHumanCritical = true; break; }
                    }
                }
                if (!anyHumanCritical)
                {
                    _gameEndTriggered = true;
                    TriggerEndRound("All Human HQs destroyed — Aliens win!", aliensWin: true);
                }
            }
        }

        private static void DisableWildlifeAmbientLife()
        {
            try
            {
                int disabled = 0;
                foreach (var al in UnityEngine.Object.FindObjectsOfType<AmbientLife>())
                {
                    if (al != null && al.Team == _wildlifeTeam)
                    {
                        al.Team = null;
                        disabled++;
                    }
                }
                if (disabled > 0)
                    MelonLogger.Msg($"[4WAY] Disabled {disabled} Wildlife ambient life spawner(s)");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[4WAY] DisableWildlifeAmbientLife: {ex.Message}");
            }
        }

        private static void MigratePlayersToAlly(Team fromTeam, Team toTeam, string announcement)
        {
            MelonLogger.Msg($"[4WAY] {announcement} Migrating {fromTeam.TeamName} players to {toTeam.TeamName}");

            var players = new List<Player>();
            foreach (var p in Player.Players)
            {
                if (p.Team == fromTeam)
                    players.Add(p);
            }

            var gm = GameMode.CurrentGameMode;
            foreach (var p in players)
            {
                if (p.IsCommander && _setCommanderMethod != null && gm != null)
                {
                    try
                    {
                        _setCommanderMethod.Invoke(gm, new object[] { fromTeam, null });
                        if (_synchCommanderMethod != null)
                            _synchCommanderMethod.Invoke(gm, new object[] { fromTeam });
                    }
                    catch { }
                }

                gm?.DestroyAllUnitsForPlayer(p);
                p.Team = toTeam;
                NetworkLayer.SendPlayerSelectTeam(p, toTeam);

                if (toTeam == _wildlifeTeam) TrackWildlifeMember(p);
                else UntrackWildlifeMember(p);

                try
                {
                    var spawned = gm?.SpawnUnitForPlayer(p, toTeam);
                    if (spawned != null)
                        MelonLogger.Msg($"[4WAY] Migrated + spawned {p.PlayerName} on {toTeam.TeamName}");
                    else
                        MelonLogger.Msg($"[4WAY] Migrated {p.PlayerName} to {toTeam.TeamName} (spawn pending)");
                }
                catch { }
            }

            if (players.Count > 0)
                SendToAll($"{announcement} {players.Count} player(s) transferred to {toTeam.TeamName}.");
        }

        private static void PlaySoundToAll(string soundFile)
        {
            try
            {
                var audioHelperType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "AudioHelper");
                if (audioHelperType != null)
                {
                    var playMethod = audioHelperType.GetMethod("PlaySoundFile",
                        BindingFlags.Public | BindingFlags.Static);
                    playMethod?.Invoke(null, new object[] { soundFile, null });
                    MelonLogger.Msg($"[4WAY] Playing: {soundFile}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[4WAY] Sound failed: {ex.Message}");
            }
        }

        private static void TriggerEndRound(string message, bool aliensWin)
        {
            MelonLogger.Msg($"[4WAY] END ROUND: {message}");
            SendToAll(message);

            // The sub-team loss sound already played in the detection block
            // Now play the win announcement
            string winSound = aliensWin ? "sounds\\alien_team_wins.wav" : "sounds\\human_team_wins.wav";
            PlaySoundToAll(winSound);

            // Trigger end round
            try
            {
                var gm = GameMode.CurrentGameMode;
                if (gm != null && _onMissionStateChangedMethod != null)
                {
                    _onMissionStateChangedMethod.Invoke(gm, new object[] { (MP_Strategy.EMissionState)2 });
                    MelonLogger.Msg("[4WAY] Triggered OnMissionStateChanged(ENDED)");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[4WAY] TriggerEndRound failed: {ex.Message}");
            }
        }

        // =============================================
        // !4way command
        // =============================================
        private static void OnCmd4Way(Player? player, string args)
        {
            Is4WayEnabled = !Is4WayEnabled;
            string state = Is4WayEnabled ? "ENABLED" : "DISABLED";
            SendToAll($"4-Way mode {state}. Next round: HvHvA + Wildlife nest.");
            MelonLogger.Msg($"[4WAY] {state}");
        }

        // =============================================
        // Setup Wildlife team properties
        // =============================================
        private static void SetupWildlifeTeam()
        {
            if (_wildlifeTeam == null || _alienTeam == null) FindTeams();
            if (_wildlifeTeam == null) { MelonLogger.Warning("[4WAY] Wildlife team not found"); return; }

            if (_alienTeam?.BaseStructure != null)
            {
                _wildlifeTeam.BaseStructure = _alienTeam.BaseStructure;
                MelonLogger.Msg($"[4WAY] Set Wildlife BaseStructure = {_alienTeam.BaseStructure.DisplayName}");
            }
            if (_alienTeam?.DefaultUnit != null)
            {
                _wildlifeTeam.DefaultUnit = _alienTeam.DefaultUnit;
                MelonLogger.Msg($"[4WAY] Set Wildlife DefaultUnit = {_alienTeam.DefaultUnit.DisplayName}");
            }

            var resField = typeof(Team).GetField("UsableResource", BindingFlags.Public | BindingFlags.Instance);
            if (resField != null)
            {
                resField.SetValue(_wildlifeTeam, resField.GetValue(_alienTeam));
                MelonLogger.Msg("[4WAY] Copied UsableResource from Alien");
            }

            _wildlifeTeam.IsSpecial = false;
            _wildlifeTeam.StartingResources = 8000;
            _wildlifeSetup = null; // Force re-creation on next GetTeamSetup call

            MelonLogger.Msg("[4WAY] Wildlife team setup complete");
        }

        // =============================================
        // Spawn Wildlife Nest at map center
        // =============================================
        private static void SpawnWildlifeNest()
        {
            if (!IsServer) return;
            try
            {
                if (_wildlifeTeam == null) { FindTeams(); if (_wildlifeTeam == null) return; }

                ObjectInfo nestInfo = _alienTeam?.BaseStructure;
                if (nestInfo == null) { MelonLogger.Error("[4WAY] No Alien BaseStructure"); return; }

                var prefab = nestInfo.Prefab;
                if (prefab == null) { MelonLogger.Error("[4WAY] Nest prefab null"); return; }
                if (_spawnPrefabMethod == null) ResolveSpawnMethod();
                if (_spawnPrefabMethod == null) { MelonLogger.Error("[4WAY] SpawnPrefab not resolved"); return; }

                // Try to get Wildlife spawn position from MapBalance config
                Vector3 nestPos = GetWildlifeSpawnFromMapBalance();
                string posSource = "map center";
                if (nestPos == Vector3.zero)
                {
                    nestPos = CalculateMapCenter();
                }
                else
                {
                    posSource = "MapBalance config";
                }

                var paramCount = _spawnPrefabMethod.GetParameters().Length;

                // Main nest first (gets queen)
                MelonLogger.Msg($"[4WAY] Spawning main Nest at ({nestPos.x:F0}, {nestPos.y:F1}, {nestPos.z:F0}) from {posSource}");
                _spawnPrefabMethod.Invoke(null, BuildSpawnArgs(prefab, _wildlifeTeam, nestPos, paramCount));

                // Anchor nest underground
                var anchorPos = new Vector3(nestPos.x, nestPos.y - 100f, nestPos.z);
                MelonLogger.Msg($"[4WAY] Spawning anchor Nest underground");
                _spawnPrefabMethod.Invoke(null, BuildSpawnArgs(prefab, _wildlifeTeam, anchorPos, paramCount));

                MelonLogger.Msg($"[4WAY] Wildlife nests spawned ({posSource})!");
                SendToAll($"Wildlife nest spawned ({posSource})!");
            }
            catch (Exception ex) { MelonLogger.Error($"[4WAY] SpawnWildlifeNest: {ex.Message}"); }
        }

        /// <summary>
        /// Read Wildlife spawn position from Si_MapBalance.WildlifeSpawnPos via reflection.
        /// Returns Vector3.zero if not available.
        /// </summary>
        private static Vector3 GetWildlifeSpawnFromMapBalance()
        {
            try
            {
                var mbType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.FullName == "Si_MapBalance.MapBalance");
                if (mbType != null)
                {
                    var field = mbType.GetField("WildlifeSpawnPos", BindingFlags.Public | BindingFlags.Static);
                    if (field != null)
                    {
                        var pos = field.GetValue(null) as float[];
                        if (pos != null && pos.Length >= 2)
                        {
                            float x = pos[0], z = pos[1];
                            float y = SampleTerrainHeight(x, z);
                            MelonLogger.Msg($"[4WAY] Got Wildlife spawn from MapBalance: ({x:F0}, {y:F1}, {z:F0})");
                            return new Vector3(x, y + 1f, z);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[4WAY] Could not read MapBalance Wildlife pos: {ex.Message}");
            }
            return Vector3.zero;
        }

        private static object[] BuildSpawnArgs(GameObject prefab, Team team, Vector3 pos, int paramCount)
        {
            return paramCount >= 7
                ? new object[] { prefab, null, team, pos, Quaternion.identity, true, true }
                : new object[] { prefab, null, team, pos, Quaternion.identity };
        }

        private static Vector3 CalculateMapCenter()
        {
            Terrain[] terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0) return new Vector3(0, 50, 0);

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var terrain in terrains)
            {
                var pos = terrain.transform.position;
                var size = terrain.terrainData.size;
                minX = Mathf.Min(minX, pos.x); maxX = Mathf.Max(maxX, pos.x + size.x);
                minZ = Mathf.Min(minZ, pos.z); maxZ = Mathf.Max(maxZ, pos.z + size.z);
            }

            float cx = (minX + maxX) / 2f, cz = (minZ + maxZ) / 2f;
            float cy = SampleTerrainHeight(cx, cz);
            return new Vector3(cx, cy + 1f, cz);
        }

        private static float SampleTerrainHeight(float x, float z)
        {
            foreach (var t in Terrain.activeTerrains)
            {
                var pos = t.transform.position;
                var size = t.terrainData.size;
                if (x >= pos.x && x <= pos.x + size.x && z >= pos.z && z <= pos.z + size.z)
                    return t.SampleHeight(new Vector3(x, 0f, z)) + pos.y;
            }
            return Terrain.activeTerrain != null
                ? Terrain.activeTerrain.SampleHeight(new Vector3(x, 0f, z)) + Terrain.activeTerrain.transform.position.y
                : 50f;
        }

        // =============================================
        // !wildcom — Force Wildlife commander
        // =============================================
        private static void OnCmdWildCom(Player? player, string args)
        {
            if (player == null) return;
            if (!Is4WayEnabled || _wildlifeTeam == null)
            {
                SendToPlayer(player, "4-Way mode not enabled.");
                return;
            }

            var gm = GameMode.CurrentGameMode;
            if (gm == null || _setCommanderMethod == null)
            {
                SendToPlayer(player, "Game mode not ready.");
                return;
            }

            try
            {
                // Demote from current commander if needed
                if (player.IsCommander && player.Team != null && player.Team != _wildlifeTeam)
                {
                    _setCommanderMethod.Invoke(gm, new object[] { player.Team, null });
                    if (_synchCommanderMethod != null)
                        _synchCommanderMethod.Invoke(gm, new object[] { player.Team });
                }

                gm.DestroyAllUnitsForPlayer(player);

                if (player.Team != _wildlifeTeam)
                {
                    player.Team = _wildlifeTeam;
                    NetworkLayer.SendPlayerSelectTeam(player, _wildlifeTeam);
                }

                _setCommanderMethod.Invoke(gm, new object[] { _wildlifeTeam, player });
                if (_synchCommanderMethod != null)
                    _synchCommanderMethod.Invoke(gm, new object[] { _wildlifeTeam });

                TrackWildlifeMember(player);
                MelonLogger.Msg($"[4WAY] {player.PlayerName} → Wildlife commander");
                SendToAll($"{player.PlayerName} is now Wildlife Commander!");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[4WAY] wildcom failed: {ex.Message}");
                SendToPlayer(player, "Failed.");
            }
        }

        // =============================================
        // !wildlife — Join Wildlife as FPS
        // =============================================
        private static void OnCmdWildFps(Player? player, string args)
        {
            if (player == null) return;
            if (!Is4WayEnabled || _wildlifeTeam == null)
            {
                SendToPlayer(player, "4-Way mode not enabled.");
                return;
            }

            // Demote from commander if needed
            if (player.IsCommander && player.Team != null)
            {
                try
                {
                    var gm = GameMode.CurrentGameMode;
                    if (gm != null && _setCommanderMethod != null)
                    {
                        _setCommanderMethod.Invoke(gm, new object[] { player.Team, null });
                        if (_synchCommanderMethod != null)
                            _synchCommanderMethod.Invoke(gm, new object[] { player.Team });
                    }
                }
                catch { }
            }

            GameMode.CurrentGameMode?.DestroyAllUnitsForPlayer(player);

            if (player.Team != _wildlifeTeam)
            {
                player.Team = _wildlifeTeam;
                NetworkLayer.SendPlayerSelectTeam(player, _wildlifeTeam);
            }
            TrackWildlifeMember(player);

            try
            {
                var gm = GameMode.CurrentGameMode;
                if (gm != null)
                {
                    var spawned = gm.SpawnUnitForPlayer(player, _wildlifeTeam);
                    if (spawned != null)
                    {
                        MelonLogger.Msg($"[4WAY] {player.PlayerName} joined Wildlife as FPS");
                        SendToPlayer(player, "Joined Wildlife!");
                    }
                    else
                    {
                        // Fallback: spawn at structure
                        var unitPrefab = gm.GetUnitPrefabForPlayer(player);
                        if (unitPrefab != null)
                        {
                            foreach (var structure in Structure.Structures)
                            {
                                if (structure != null && structure.Team == _wildlifeTeam)
                                {
                                    var pos = structure.transform.position + Vector3.up * 2f;
                                    spawned = gm.SpawnUnitForPlayer(player, unitPrefab, pos, Quaternion.identity);
                                    if (spawned != null)
                                    {
                                        MelonLogger.Msg($"[4WAY] {player.PlayerName} joined Wildlife (fallback)");
                                        SendToPlayer(player, "Joined Wildlife!");
                                        break;
                                    }
                                }
                            }
                        }
                        if (spawned == null)
                            SendToPlayer(player, "Joined Wildlife but spawn failed.");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[4WAY] wildlife spawn failed: {ex.Message}");
            }
        }

        // =============================================
        // SpawnUnitForPlayer prefix — fix team for Wildlife H+UI respawns
        // When a Wildlife player presses H and selects Alien+FPS, the game
        // changes their team to Alien. This prefix fixes it back to Wildlife.
        // =============================================
        /// <summary>
        /// Prefix on MP_Strategy.ProcessNetRPC — intercept REQUEST_JOIN_TEAM.
        /// If a Wildlife-tracked player tries to join Alien via H+UI, block the
        /// team change so they stay on Wildlife. The respawn menu will then
        /// naturally show Wildlife structures.
        /// </summary>
        private const byte RPC_REQUEST_JOIN_TEAM = 1;
        private const byte RPC_CLEAR_REQUEST = 3;

        public static bool Prefix_ProcessNetRPC(object __instance, GameByteStreamReader __0, byte __1)
        {
            if (!Is4WayEnabled || !IsServer) return true;
            if (_wildlifeTeam == null || _alienTeam == null) return true;
            if (__1 != RPC_REQUEST_JOIN_TEAM) return true; // not a join request

            // Peek at the packet to check player and target team
            // Save stream position to restore if we don't handle it
            try
            {
                ulong steam64 = __0.ReadUInt64();
                var netId = new NetworkID(steam64);
                int channel = __0.ReadByte();
                Team targetTeam = __0.ReadTeam();

                Player player = Player.FindPlayer(netId, channel);
                if (player == null) return false; // consumed the stream, can't re-run original

                // Wildlife-tracked player trying to join Alien → block, keep on Wildlife
                if (targetTeam == _alienTeam && _wasOnWildlife.Contains(steam64))
                {
                    // Always clear Wildlife commander seat when this player uses H+UI
                    // (IsCommander is already false by now — game sends ROLE_NONE first)
                    if (_wildlifeSetup != null && _wildlifeSetup.Commander == player)
                    {
                        var gm2 = GameMode.CurrentGameMode;
                        if (gm2 != null && _setCommanderMethod != null)
                        {
                            _setCommanderMethod.Invoke(gm2, new object[] { _wildlifeTeam, null });
                            if (_synchCommanderMethod != null)
                                _synchCommanderMethod.Invoke(gm2, new object[] { _wildlifeTeam });
                            MelonLogger.Msg($"[4WAY] Cleared Wildlife commander seat (was {player.PlayerName})");
                        }
                    }

                    MelonLogger.Msg($"[4WAY] {player.PlayerName} tried to join Alien via UI — keeping on Wildlife");

                    // Send clear request so client unblocks from "waiting for server"
                    var gm = GameMode.CurrentGameMode;
                    var writer = gm?.CreateRPCPacket(RPC_CLEAR_REQUEST);
                    if (writer != null)
                    {
                        writer.WriteUInt64(steam64);
                        writer.WriteByte((byte)channel);
                        gm.SendRPCPacket(writer);
                    }

                    return false; // skip original — don't change team
                }

                // For all other joins: we already consumed the stream, process manually
                if (player.Team == targetTeam)
                {
                    var gm = GameMode.CurrentGameMode;
                    var writer = gm?.CreateRPCPacket(RPC_CLEAR_REQUEST);
                    if (writer != null)
                    {
                        writer.WriteUInt64(steam64);
                        writer.WriteByte((byte)channel);
                        gm.SendRPCPacket(writer);
                    }
                }
                else
                {
                    player.Team = targetTeam;
                    NetworkLayer.SendPlayerSelectTeam(player, targetTeam);
                }

                return false; // we handled it
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[4WAY] ProcessNetRPC error: {ex.Message}");
                return false; // stream is consumed, can't let original run
            }
        }

        private static void TrackWildlifeMember(Player player)
        {
            if (player != null)
                _wasOnWildlife.Add((ulong)player.PlayerID);
        }

        private static void UntrackWildlifeMember(Player player)
        {
            if (player != null)
                _wasOnWildlife.Remove((ulong)player.PlayerID);
        }

        // =============================================
        // GetHasLost Postfix — in 4-way mode, Alien hasn't lost if Wildlife queen is alive
        // This keeps the Alien UI accessible for Wildlife players
        // =============================================
        public static void Postfix_GetHasLost(BaseTeamSetup __instance, ref bool __result)
        {
            if (!Is4WayEnabled || !__result) return; // only override if game says team lost
            if (__instance.Team == null) return;

            // 2v2 win condition: a team hasn't lost if its ALLY still has critical structures
            // Alien side: Alien hasn't lost if Wildlife has criticals, and vice versa
            if (_alienTeam != null && _wildlifeTeam != null)
            {
                if (__instance.Team == _alienTeam && _wildlifeTeam.GetHasAnyCritical())
                {
                    __result = false;
                    return;
                }
                if (__instance.Team == _wildlifeTeam && _alienTeam.GetHasAnyCritical())
                {
                    __result = false;
                    return;
                }
            }

            // Human side: each team shows as eliminated independently.
            // Players get migrated to the surviving ally via server logic.
            // No UI override needed — Sol and Cent have separate UIs.
        }

        // =============================================
        // Alliance system — shared between FOW, targeting, and damage
        // =============================================
        private static bool AreTeamsAllied(Team t1, Team t2)
        {
            if (t1 == t2) return true;
            if (t1 == null || t2 == null) return false;
            bool t1Alien = (t1 == _alienTeam || t1 == _wildlifeTeam);
            bool t2Alien = (t2 == _alienTeam || t2 == _wildlifeTeam);
            if (t1Alien && t2Alien) return true;
            bool t1Human = (!t1Alien && !t1.IsSpecial);
            bool t2Human = (!t2Alien && !t2.IsSpecial);
            if (t1Human && t2Human) return true;
            return false;
        }

        public static void Postfix_GetTeamsAreEnemy(Team team1, Team team2, ref bool __result)
        {
            if (!Is4WayEnabled || !__result) return;
            if (AreTeamsAllied(team1, team2))
                __result = false;
        }

        public static bool Prefix_GetAllowFriendlyFireOnImpact(object __instance, GameObject target, ref bool __result)
        {
            if (!Is4WayEnabled) return true;
            if (target == null) return true;
            try
            {
                var baseObj = target.GetComponent<BaseGameObject>();
                if (baseObj == null) baseObj = target.GetComponentInParent<BaseGameObject>();
                if (baseObj == null || baseObj.Team == null) return true;
                var projTeam = __instance.GetType().GetProperty("Team")?.GetValue(__instance) as Team
                            ?? __instance.GetType().GetField("Team")?.GetValue(__instance) as Team;
                if (projTeam == null) return true;
                if (projTeam != baseObj.Team && AreTeamsAllied(projTeam, baseObj.Team))
                {
                    __result = false;
                    return false;
                }
            }
            catch { }
            return true;
        }

        public static bool Prefix_GetAllowFriendlyFireDamage(object __instance, DamageManager targetDamageManager, ref bool __result)
        {
            if (!Is4WayEnabled) return true;
            if (targetDamageManager == null) return true;
            try
            {
                var owner = targetDamageManager.Owner;
                if (owner == null || owner.Team == null) return true;
                var projTeam = __instance.GetType().GetProperty("Team")?.GetValue(__instance) as Team
                            ?? __instance.GetType().GetField("Team")?.GetValue(__instance) as Team;
                if (projTeam == null) return true;
                if (projTeam != owner.Team && AreTeamsAllied(projTeam, owner.Team))
                {
                    __result = false;
                    return false;
                }
            }
            catch { }
            return true;
        }

        // =============================================
        // Shared FOW — share detections between allied teams
        // =============================================
        private static bool _sharingDetection = false; // prevent infinite recursion

        public static void Postfix_DetectTarget(Team __instance, Target target, Sensor sensor)
        {
            if (!Is4WayEnabled || _sharingDetection) return;
            if (__instance == null || target == null) return;
            if (_alienTeam == null || _wildlifeTeam == null) return;

            Team allyTeam = null;
            if (__instance == _alienTeam) allyTeam = _wildlifeTeam;
            else if (__instance == _wildlifeTeam) allyTeam = _alienTeam;
            else
            {
                // Human side: find the other human team
                // Sol and Centauri are non-alien, non-wildlife, non-special
                foreach (var t in Team.Teams)
                {
                    if (t != null && t != __instance && t != _alienTeam && t != _wildlifeTeam && !t.IsSpecial)
                    {
                        allyTeam = t;
                        break;
                    }
                }
            }

            if (allyTeam == null) return;

            _sharingDetection = true;
            try
            {
                allyTeam.DetectTarget(target, sensor);
            }
            catch { }
            _sharingDetection = false;
        }

        // =============================================
        // !alien — Switch to Alien sub-team
        // =============================================
        private static void OnCmdAlien(Player? player, string args)
        {
            if (player == null) return;
            if (!Is4WayEnabled)
            {
                SendToPlayer(player, "4-Way mode not enabled.");
                return;
            }

            UntrackWildlifeMember(player);
            SendToPlayer(player, "Set to Alien side. Use H to join Alien via UI, or !wildlife to switch back.");
            MelonLogger.Msg($"[4WAY] {player.PlayerName} switched to Alien sub-team");
        }

        // =============================================
        // Helpers
        // =============================================
        private static void SendToPlayer(Player player, string message)
        {
            HelperMethods.ReplyToCommand_Player(player, message);
        }

        private static void SendToAll(string message)
        {
            HelperMethods.ReplyToCommand(message);
        }
    }
}
