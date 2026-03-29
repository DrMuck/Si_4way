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
