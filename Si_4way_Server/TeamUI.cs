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
    /// Team UI handling: GetTeamSetup, GetPlayerIsCommander, ProcessNetRPC (Wildlife team protection)
    /// </summary>
    public partial class Si_4way
    {
        private const byte RPC_REQUEST_JOIN_TEAM = 1;
        private const byte RPC_CLEAR_REQUEST = 3;

        // Built-in commander lottery (pre-game only, replaces CommManagement)
        private static Dictionary<int, List<Player>> _commanderApplicants = new Dictionary<int, List<Player>>();
        private static bool _lotteryDone = false;

        internal static class TeamUI
        {
            public static void RegisterPatches(HarmonyLib.Harmony harmony)
            {
                var getTeamSetup = typeof(GameModeExt).GetMethod("GetTeamSetup",
                    BindingFlags.Public | BindingFlags.Instance);
                if (getTeamSetup != null)
                {
                    harmony.Patch(getTeamSetup,
                        postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_GetTeamSetup)));
                    MelonLogger.Msg("Patched GetTeamSetup");
                }

                var getPlayerIsCommander = typeof(GameMode).GetMethod("GetPlayerIsCommander",
                    BindingFlags.Public | BindingFlags.Instance);
                if (getPlayerIsCommander != null)
                {
                    harmony.Patch(getPlayerIsCommander,
                        postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_GetPlayerIsCommander)));
                    MelonLogger.Msg("Patched GetPlayerIsCommander");
                }

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

                    // Block direct commander assignment during pre-game → lottery pool
                    var setCommander = _mpStrategyType.GetMethod("SetCommander",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (setCommander != null)
                    {
                        harmony.Patch(setCommander,
                            prefix: new HarmonyMethod(typeof(Si_4way), nameof(Prefix_SetCommander)));
                        MelonLogger.Msg("Patched SetCommander for pre-game lottery");
                    }

                    // Run lottery + distribute FPS at round start
                    var onMissionState = _mpStrategyType.GetMethod("OnMissionStateChanged",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (onMissionState != null)
                    {
                        harmony.Patch(onMissionState,
                            postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_OnMissionStateChanged)));
                        MelonLogger.Msg("Patched OnMissionStateChanged for lottery + distribution");
                    }
                }
            }
        }

        // === GetTeamSetup: return synthetic Wildlife BaseTeamSetup ===
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
                    if (setups != null && !setups.Any(s => s.Team == _wildlifeTeam))
                    {
                        setups.Add(_wildlifeSetup);
                        MelonLogger.Msg("[4WAY] Injected Wildlife into BaseTeamSetups list");
                    }
                }

                MelonLogger.Msg($"[4WAY] Created Wildlife BaseTeamSetup (spawn={_wildlifeSetup.PlayerSpawn?.DisplayName ?? "null"})");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[4WAY] CreateWildlifeSetup failed: {ex.Message}");
            }
        }

        // === GetPlayerIsCommander: Wildlife commander support ===
        public static void Postfix_GetPlayerIsCommander(Player player, ref bool __result)
        {
            if (__result) return;
            if (player == null || _wildlifeSetup == null) return;
            if (player.Team != _wildlifeTeam) return;
            if (_wildlifeSetup.Commander == player)
                __result = true;
        }

        // === SetCommander prefix: block during pre-game, queue for lottery ===
        public static bool Prefix_SetCommander(object __instance, Team __0, Player __1)
        {
            if (!Is4WayEnabled || !IsServer) return true;

            // Only block during pre-game team selection phase
            var gm = GameMode.CurrentGameMode;
            if (gm == null || gm.GameBegun) return true; // after round start → allow normally
            if (__1 == null) return true; // clearing commander → allow

            // Add to lottery pool, spawn as infantry
            int teamIdx = __0.Index;
            if (!_commanderApplicants.ContainsKey(teamIdx))
                _commanderApplicants[teamIdx] = new List<Player>();

            if (!_commanderApplicants[teamIdx].Contains(__1))
            {
                _commanderApplicants[teamIdx].Add(__1);
                MelonLogger.Msg($"[4WAY] {__1.PlayerName} applied for {__0.TeamShortName} commander ({_commanderApplicants[teamIdx].Count} in pool)");
                SendToPlayer(__1, "Applied for commander! Lottery at round start.");

                // Spawn as infantry (same as CommManagement: PreventSpawnWhenBlocked=false)
                gm.SpawnUnitForPlayer(__1, __1.Team);
            }

            return false; // block SetCommander — seat stays empty, button stays available
        }

        // === OnMissionStateChanged postfix: lottery + FPS distribution at round start ===
        public static void Postfix_OnMissionStateChanged(object __instance, MP_Strategy.EMissionState __0)
        {
            if (!Is4WayEnabled || !IsServer) return;
            if (__0 != MP_Strategy.EMissionState.STARTED) return;
            if (_lotteryDone) return;
            _lotteryDone = true;

            RunCommanderLottery();
            DistributePlayers();
        }

        private static void RunCommanderLottery()
        {
            MelonLogger.Msg("[4WAY] Running commander lottery...");
            var rng = new System.Random();
            var gm = GameMode.CurrentGameMode;

            foreach (var kvp in _commanderApplicants)
            {
                var applicants = kvp.Value;
                if (applicants.Count == 0) continue;

                Team team = Team.Teams[kvp.Key];
                if (team == null) continue;

                // Alien: split one to Wildlife if 2+ applicants
                if (team == _alienTeam && applicants.Count >= 2 && _wildlifeTeam != null)
                {
                    int wildPick = rng.Next(0, applicants.Count);
                    Player wildCom = applicants[wildPick];
                    applicants.RemoveAt(wildPick);

                    gm?.DestroyAllUnitsForPlayer(wildCom);
                    wildCom.Team = _wildlifeTeam;
                    NetworkLayer.SendPlayerSelectTeam(wildCom, _wildlifeTeam);
                    TrackWildlifeMember(wildCom);

                    if (_setCommanderMethod != null && gm != null)
                    {
                        _setCommanderMethod.Invoke(gm, new object[] { _wildlifeTeam, wildCom });
                        if (_synchCommanderMethod != null)
                            _synchCommanderMethod.Invoke(gm, new object[] { _wildlifeTeam });
                    }

                    // Fire role change event so logging mod picks it up
                    FireRoleChangedEvent(wildCom, 2); // 2 = COMMANDER

                    MelonLogger.Msg($"[4WAY] Lottery: {wildCom.PlayerName} → Wildlife commander");
                    SendToAll($"{wildCom.PlayerName} promoted to Wildlife Commander!");
                }

                // Pick one for this team's commander
                if (applicants.Count > 0)
                {
                    int pick = rng.Next(0, applicants.Count);
                    Player commander = applicants[pick];
                    applicants.RemoveAt(pick);

                    gm?.DestroyAllUnitsForPlayer(commander);
                    if (_setCommanderMethod != null && gm != null)
                    {
                        _setCommanderMethod.Invoke(gm, new object[] { team, commander });
                        if (_synchCommanderMethod != null)
                            _synchCommanderMethod.Invoke(gm, new object[] { team });
                    }

                    FireRoleChangedEvent(commander, 2); // 2 = COMMANDER

                    MelonLogger.Msg($"[4WAY] Lottery: {commander.PlayerName} → {team.TeamShortName} commander");
                    SendToAll($"{commander.PlayerName} promoted to {team.TeamShortName} Commander!");
                }

                // Losers stay as infantry (already spawned)
                foreach (var loser in applicants)
                {
                    if (loser != null)
                        MelonLogger.Msg($"[4WAY] {loser.PlayerName} lost lottery → stays infantry");
                }
                applicants.Clear();
            }
        }

        /// <summary>
        /// Balance all players into 50/50 sides, then split sub-teams evenly.
        /// Called at round start after lottery.
        /// </summary>
        private static void DistributePlayers()
        {
            if (_alienTeam == null || _wildlifeTeam == null) return;

            // Count sides (excluding commanders)
            var alienSideFps = new List<Player>();
            var humanSideFps = new List<Player>();

            foreach (var p in Player.Players)
            {
                if (p.Team == null || p.IsCommander) continue;
                if (p.Team == _alienTeam || p.Team == _wildlifeTeam)
                    alienSideFps.Add(p);
                else if (!p.Team.IsSpecial)
                    humanSideFps.Add(p);
            }

            int totalFps = alienSideFps.Count + humanSideFps.Count;
            int targetPerSide = totalFps / 2;

            MelonLogger.Msg($"[4WAY] Balance: {alienSideFps.Count} alien-side, {humanSideFps.Count} human-side FPS ({totalFps} total, target {targetPerSide}/side)");

            // Move excess from overpopulated side (respecting tolerance)
            while (alienSideFps.Count - humanSideFps.Count > _balanceTolerance)
            {
                var p = alienSideFps[alienSideFps.Count - 1];
                alienSideFps.RemoveAt(alienSideFps.Count - 1);
                Team targetHuman = (_solTeam != null && _centTeam != null)
                    ? (_solTeam.GetNumPlayers() <= _centTeam.GetNumPlayers() ? _solTeam : _centTeam)
                    : (_solTeam ?? _centTeam);
                if (targetHuman != null)
                {
                    gm_DestroyAndMove(p, targetHuman);
                    humanSideFps.Add(p);
                    MelonLogger.Msg($"[4WAY] Balance: moved {p.PlayerName} alien→{targetHuman.TeamShortName}");
                }
            }

            while (humanSideFps.Count - alienSideFps.Count > _balanceTolerance)
            {
                var p = humanSideFps[humanSideFps.Count - 1];
                humanSideFps.RemoveAt(humanSideFps.Count - 1);
                // Move to alien side — will be distributed to Alien/Wildlife below
                gm_DestroyAndMove(p, _alienTeam);
                alienSideFps.Add(p);
                MelonLogger.Msg($"[4WAY] Balance: moved {p.PlayerName} human→Alien");
            }

            // Now distribute within alien side: half to Wildlife
            var onAlien = new List<Player>();
            foreach (var p in Player.Players)
            {
                if (p.Team == _alienTeam && !p.IsCommander)
                    onAlien.Add(p);
            }

            int toWildlife = onAlien.Count / 2;
            for (int i = 0; i < toWildlife; i++)
            {
                gm_DestroyAndMove(onAlien[i], _wildlifeTeam);
                MelonLogger.Msg($"[4WAY] Distributed {onAlien[i].PlayerName} → Wildlife");
            }

            // Balance within human side: equalize Sol/Cent
            if (_solTeam != null && _centTeam != null)
            {
                while (Math.Abs(_solTeam.GetNumPlayers() - _centTeam.GetNumPlayers()) > 1)
                {
                    Team from = _solTeam.GetNumPlayers() > _centTeam.GetNumPlayers() ? _solTeam : _centTeam;
                    Team to = from == _solTeam ? _centTeam : _solTeam;
                    Player? moveP = null;
                    foreach (var p in Player.Players)
                    {
                        if (p.Team == from && !p.IsCommander) { moveP = p; break; }
                    }
                    if (moveP == null) break;
                    gm_DestroyAndMove(moveP, to);
                    MelonLogger.Msg($"[4WAY] Balance: moved {moveP.PlayerName} {from.TeamShortName}→{to.TeamShortName}");
                }
            }

            MelonLogger.Msg("[4WAY] Distribution complete");
        }

        private static void gm_DestroyAndMove(Player p, Team toTeam)
        {
            var gm = GameMode.CurrentGameMode;
            gm?.DestroyAllUnitsForPlayer(p);
            p.Team = toTeam;
            NetworkLayer.SendPlayerSelectTeam(p, toTeam);
            if (toTeam == _wildlifeTeam) TrackWildlifeMember(p);
            gm?.SpawnUnitForPlayer(p, toTeam);
        }

        // === ProcessNetRPC: block Wildlife→Alien team switch via H+UI ===
        public static bool Prefix_ProcessNetRPC(object __instance, GameByteStreamReader __0, byte __1)
        {
            if (!Is4WayEnabled || !IsServer) return true;
            if (_wildlifeTeam == null || _alienTeam == null) return true;
            if (__1 != RPC_REQUEST_JOIN_TEAM) return true;

            try
            {
                ulong steam64 = __0.ReadUInt64();
                var netId = new NetworkID(steam64);
                int channel = __0.ReadByte();
                Team targetTeam = __0.ReadTeam();

                Player player = Player.FindPlayer(netId, channel);
                if (player == null) return false;

                // Wildlife-tracked player trying to join Alien → block
                if (targetTeam == _alienTeam && _wasOnWildlife.Contains(steam64))
                {
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

                    var gm = GameMode.CurrentGameMode;
                    var writer = gm?.CreateRPCPacket(RPC_CLEAR_REQUEST);
                    if (writer != null)
                    {
                        writer.WriteUInt64(steam64);
                        writer.WriteByte((byte)channel);
                        gm.SendRPCPacket(writer);
                    }
                    return false;
                }

                // All other joins: process manually (we consumed the stream)
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

                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[4WAY] ProcessNetRPC error: {ex.Message}");
                return false;
            }
        }
    }
}
