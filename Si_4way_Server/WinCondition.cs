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
    /// Win conditions: event-based detection via OnStructureDestroyed / OnUnitDestroyed.
    /// Handles migration, end-round trigger, sound announcements.
    /// </summary>
    public partial class Si_4way
    {
        private static bool _alienQueenDead = false;
        private static bool _wildlifeQueenDead = false;
        private static bool _solHQDead = false;
        private static bool _centHQDead = false;
        private static bool _gameEndTriggered = false;

        internal static class WinCondition
        {
            public static void RegisterPatches(HarmonyLib.Harmony harmony)
            {
                // GetHasLost — linked elimination (keeps allied team alive)
                var getHasLost = typeof(StrategyTeamSetup).GetMethod("GetHasLost",
                    BindingFlags.Public | BindingFlags.Instance);
                if (getHasLost != null)
                {
                    harmony.Patch(getHasLost,
                        postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_GetHasLost)));
                    MelonLogger.Msg("Patched GetHasLost");
                }

                // OnStructureDestroyed — fires when a structure is destroyed in combat
                var onStructDestroyed = typeof(MP_Strategy).GetMethod("OnStructureDestroyed",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (onStructDestroyed != null)
                {
                    harmony.Patch(onStructDestroyed,
                        postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_OnStructureDestroyed)));
                    MelonLogger.Msg("Patched MP_Strategy.OnStructureDestroyed");
                }

                // OnUnitDestroyed — fires when a unit is destroyed (queen is a unit)
                var onUnitDestroyed = typeof(StrategyMode).GetMethod("OnUnitDestroyed",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (onUnitDestroyed != null)
                {
                    harmony.Patch(onUnitDestroyed,
                        postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_OnUnitDestroyed)));
                    MelonLogger.Msg("Patched StrategyMode.OnUnitDestroyed");
                }
            }

            public static void ResetState()
            {
                _alienQueenDead = false;
                _wildlifeQueenDead = false;
                _solHQDead = false;
                _centHQDead = false;
                _gameEndTriggered = false;
            }
        }

        // === Event handler: structure destroyed in combat ===
        public static void Postfix_OnStructureDestroyed(Structure __0, GameObject __1)
        {
            if (!Is4WayEnabled || !IsServer || _gameEndTriggered) return;
            if (__0 == null) return;
            if (GameMode.CurrentGameMode == null || !GameMode.CurrentGameMode.GameOngoing) return;

            var team = ((BaseGameObject)__0).Team;
            if (team == null) return;
            if (__0.ObjectInfo == null || !__0.ObjectInfo.Critical) return;

            MelonLogger.Msg($"[4WAY] Critical structure destroyed: {__0.ObjectInfo.DisplayName} on {team.TeamShortName}");
            OnCriticalLost(team);
        }

        // === Event handler: unit destroyed (queen) ===
        public static void Postfix_OnUnitDestroyed(Unit __0, GameObject __1)
        {
            if (!Is4WayEnabled || !IsServer || _gameEndTriggered) return;
            if (__0 == null) return;
            if (GameMode.CurrentGameMode == null || !GameMode.CurrentGameMode.GameOngoing) return;

            var team = ((BaseGameObject)__0).Team;
            if (team == null) return;
            if (__0.ObjectInfo == null || !__0.ObjectInfo.Critical) return;

            MelonLogger.Msg($"[4WAY] Critical unit destroyed: {__0.ObjectInfo.DisplayName} on {team.TeamShortName}");
            OnCriticalLost(team);
        }

        // === Shared logic: a critical object was destroyed on a team ===
        private static void OnCriticalLost(Team team)
        {
            // Only act if the team has NO remaining criticals
            if (team.GetHasAnyCritical()) return;

            // Alien queen lost
            if (team == _alienTeam && !_alienQueenDead)
            {
                _alienQueenDead = true;
                PlaySoundToAll("sounds\\alien_queen_lost.wav");
                if (_wildlifeTeam != null && !_wildlifeQueenDead)
                    MigratePlayersToAlly(_alienTeam, _wildlifeTeam, "Alien queen fallen!");
            }

            // Wildlife queen lost
            if (team == _wildlifeTeam && !_wildlifeQueenDead)
            {
                _wildlifeQueenDead = true;
                PlaySoundToAll("sounds\\wildlife_queen_lost.wav");
                if (_alienTeam != null && !_alienQueenDead)
                    MigratePlayersToAlly(_wildlifeTeam, _alienTeam, "Wildlife queen fallen!");
            }

            // Sol HQ lost
            if (team == _solTeam && !_solHQDead)
            {
                _solHQDead = true;
                PlaySoundToAll("sounds\\sol_hq_lost.wav");
                if (_centTeam != null && !_centHQDead)
                    MigratePlayersToAlly(_solTeam, _centTeam, "Sol HQ destroyed!");
            }

            // Centauri HQ lost
            if (team == _centTeam && !_centHQDead)
            {
                _centHQDead = true;
                PlaySoundToAll("sounds\\centauri_hq_lost.wav");
                if (_solTeam != null && !_solHQDead)
                    MigratePlayersToAlly(_centTeam, _solTeam, "Centauri HQ destroyed!");
            }

            // End-round: both alien queens dead → humans win
            if (!_gameEndTriggered && _alienQueenDead && _wildlifeQueenDead)
            {
                _gameEndTriggered = true;
                TriggerEndRound("Both Alien queens destroyed — Humans win!", aliensWin: false);
                return;
            }

            // End-round: all human HQs dead → aliens win
            if (!_gameEndTriggered && _solHQDead && _centHQDead)
            {
                _gameEndTriggered = true;
                TriggerEndRound("All Human HQs destroyed — Aliens win!", aliensWin: true);
            }
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
