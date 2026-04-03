using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Si_4way
{
    /// <summary>
    /// Win conditions: GetHasLost, migration, end-round trigger, sound announcements
    /// </summary>
    public partial class Si_4way
    {
        private static bool _alienQueenDead = false;
        private static bool _alienQueenWasAlive = true;
        private static bool _wildlifeQueenWasAlive = true;
        private static bool _solHQWasAlive = true;
        private static bool _centHQWasAlive = true;
        private static bool _gameEndTriggered = false;
        private static float _winCheckTimer = 0f;
        private const float WIN_CHECK_INTERVAL = 0.2f;

        internal static class WinCondition
        {
            public static void RegisterPatches(HarmonyLib.Harmony harmony)
            {
                var getHasLost = typeof(StrategyTeamSetup).GetMethod("GetHasLost",
                    BindingFlags.Public | BindingFlags.Instance);
                if (getHasLost != null)
                {
                    harmony.Patch(getHasLost,
                        postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_GetHasLost)));
                    MelonLogger.Msg("Patched GetHasLost");
                }
            }

            public static void ResetState()
            {
                _alienQueenDead = false;
                _alienQueenWasAlive = true;
                _wildlifeQueenWasAlive = true;
                _solHQWasAlive = true;
                _centHQWasAlive = true;
                _gameEndTriggered = false;
            }

            public static void OnLateUpdate()
            {
                if (GameMode.CurrentGameMode == null || !GameMode.CurrentGameMode.GameBegun) return;
                if (_alienTeam == null || _wildlifeTeam == null) return;

                // Throttle: only check every 200ms instead of every frame
                _winCheckTimer -= UnityEngine.Time.deltaTime;
                if (_winCheckTimer > 0f) return;
                _winCheckTimer = WIN_CHECK_INTERVAL;

                bool alienHasCritical = HasCriticalStructure(_alienTeam);
                bool wildlifeHasCritical = HasCriticalStructure(_wildlifeTeam);

                // Alien queen death → migrate to Wildlife
                if (!alienHasCritical && _alienQueenWasAlive)
                {
                    _alienQueenWasAlive = false;
                    _alienQueenDead = true;
                    PlaySoundToAll("sounds\\alien_queen_lost.wav");
                    if (wildlifeHasCritical)
                        MigratePlayersToAlly(_alienTeam, _wildlifeTeam, "Alien queen fallen!");
                }

                // Wildlife queen death → migrate to Alien
                if (!wildlifeHasCritical && _wildlifeQueenWasAlive)
                {
                    _wildlifeQueenWasAlive = false;
                    PlaySoundToAll("sounds\\wildlife_queen_lost.wav");
                    if (alienHasCritical)
                        MigratePlayersToAlly(_wildlifeTeam, _alienTeam, "Wildlife queen fallen!");
                }

                // Human side migrations
                if (_solTeam != null && _centTeam != null)
                {
                    bool solHasCritical = HasCriticalStructure(_solTeam);
                    bool centHasCritical = HasCriticalStructure(_centTeam);

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

                // End-round: both queens dead
                if (!_gameEndTriggered && !alienHasCritical && !wildlifeHasCritical)
                {
                    _gameEndTriggered = true;
                    TriggerEndRound("Both Alien queens destroyed — Humans win!", aliensWin: false);
                }

                // End-round: all human HQs dead
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
        }

        /// <summary>
        /// Lightweight check: only iterates structures (not units) for Critical flag.
        /// Much cheaper than Team.GetHasAnyCritical() which also iterates all units.
        /// </summary>
        private static bool HasCriticalStructure(Team team)
        {
            if (team == null) return false;
            foreach (var structure in team.Structures)
            {
                if (structure != null && structure.ObjectInfo != null &&
                    structure.ObjectInfo.Critical && !structure.IsDestroyed)
                    return true;
            }
            return false;
        }

        // === GetHasLost: linked elimination ===
        public static void Postfix_GetHasLost(BaseTeamSetup __instance, ref bool __result)
        {
            if (!Is4WayEnabled || !__result) return;
            if (__instance.Team == null) return;

            if (_alienTeam != null && _wildlifeTeam != null)
            {
                if (__instance.Team == _alienTeam && _wildlifeTeam.GetHasAnyCritical())
                { __result = false; return; }
                if (__instance.Team == _wildlifeTeam && _alienTeam.GetHasAnyCritical())
                { __result = false; return; }
            }
        }

        // === Migration ===
        private static void MigratePlayersToAlly(Team fromTeam, Team toTeam, string announcement)
        {
            MelonLogger.Msg($"[4WAY] {announcement} Migrating {fromTeam.TeamName} → {toTeam.TeamName}");

            var players = new List<Player>();
            foreach (var p in Player.Players)
                if (p.Team == fromTeam) players.Add(p);

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
                    MelonLogger.Msg(spawned != null
                        ? $"[4WAY] Migrated + spawned {p.PlayerName} on {toTeam.TeamName}"
                        : $"[4WAY] Migrated {p.PlayerName} to {toTeam.TeamName} (spawn pending)");
                }
                catch { }
            }

            if (players.Count > 0)
                SendToAll($"{announcement} {players.Count} player(s) transferred to {toTeam.TeamName}.");
        }

        // === Sound + End Round ===
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
            catch (Exception ex) { MelonLogger.Warning($"[4WAY] Sound failed: {ex.Message}"); }
        }

        private static void TriggerEndRound(string message, bool aliensWin)
        {
            MelonLogger.Msg($"[4WAY] END ROUND: {message}");
            SendToAll(message);

            string winSound = aliensWin ? "sounds\\alien_team_wins.wav" : "sounds\\human_team_wins.wav";
            PlaySoundToAll(winSound);

            try
            {
                var gm = GameMode.CurrentGameMode;
                if (gm != null && _onMissionStateChangedMethod != null)
                {
                    _onMissionStateChangedMethod.Invoke(gm, new object[] { (MP_Strategy.EMissionState)2 });
                    MelonLogger.Msg("[4WAY] Triggered OnMissionStateChanged(ENDED)");
                }
            }
            catch (Exception ex) { MelonLogger.Error($"[4WAY] TriggerEndRound failed: {ex.Message}"); }
        }
    }
}
