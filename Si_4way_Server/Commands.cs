using MelonLoader;
using SilicaAdminMod;
using System;
using System.Reflection;
using UnityEngine;

namespace Si_4way
{
    /// <summary>
    /// Chat commands: !4way, !wildcom, !wildlife, !alien
    /// </summary>
    public partial class Si_4way
    {
        internal static class Commands
        {
            public static void RegisterCommands()
            {
                try
                {
                    HelperMethods.RegisterAdminCommand("4way", OnCmd4Way, Power.Commander, "Enable 4-way mode");
                    HelperMethods.RegisterPlayerCommand("wildcom", OnCmdWildCom, true);
                    HelperMethods.RegisterPlayerCommand("wildlife", OnCmdWildFps, true);
                    HelperMethods.RegisterPlayerCommand("alien", OnCmdAlien, true);
                    HelperMethods.RegisterPlayerCommand("sol", OnCmdSol, true);
                    HelperMethods.RegisterPlayerCommand("centauri", OnCmdCentauri, true);
                    HelperMethods.RegisterAdminCommand("4waytol", OnCmd4WayTol, Power.Commander, "Set 4-way balance tolerance. Usage: !4waytol <number>");
                    HelperMethods.RegisterPlayerCommand("transfer", OnCmdTransfer, true);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"Could not register commands: {ex.Message}");
                }
            }
        }

        private static void OnCmd4Way(Player? player, string args)
        {
            Is4WayEnabled = !Is4WayEnabled;
            string state = Is4WayEnabled ? "ENABLED" : "DISABLED";
            SendToAll($"4-Way mode {state}. Next round: HvHvA + Wildlife nest.");
            MelonLogger.Msg($"[4WAY] {state}");
        }

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

            // Check if seat is taken by someone else
            var currentCom = _wildlifeSetup?.Commander;
            if (currentCom != null && currentCom != player)
            {
                SendToPlayer(player, $"Wildlife commander seat is taken by {currentCom.PlayerName}.");
                return;
            }

            try
            {
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
                FireRoleChangedEvent(player, 2); // 2 = COMMANDER
                MelonLogger.Msg($"[4WAY] {player.PlayerName} → Wildlife commander");
                SendToAll($"{player.PlayerName} is now Wildlife Commander!");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[4WAY] wildcom failed: {ex.Message}");
                SendToPlayer(player, "Failed.");
            }
        }

        private static void OnCmdWildFps(Player? player, string args)
        {
            if (player == null) return;
            if (!Is4WayEnabled || _wildlifeTeam == null)
            {
                SendToPlayer(player, "4-Way mode not enabled.");
                return;
            }

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

        private static void OnCmd4WayTol(Player? player, string args)
        {
            var parts = args.Split(' ');
            if (parts.Length < 2 || !int.TryParse(parts[1], out int val) || val < 0)
            {
                SendToPlayer(player, $"Balance tolerance: {_balanceTolerance}. Usage: !4waytol <number>");
                return;
            }
            _balanceTolerance = val;
            SendToAll($"4-Way balance tolerance set to {_balanceTolerance}");
            MelonLogger.Msg($"[4WAY] Balance tolerance set to {_balanceTolerance}");
        }

        private static void OnCmdTransfer(Player? player, string args)
        {
            if (player == null) return;
            if (!Is4WayEnabled) { SendToPlayer(player, "4-Way mode not enabled."); return; }
            if (!player.IsCommander) { SendToPlayer(player, "Only commanders can transfer resources."); return; }
            if (player.Team == null) { SendToPlayer(player, "You're not on a team."); return; }

            // Parse amount
            var parts = args.Split(' ');
            if (parts.Length < 2 || !int.TryParse(parts[1], out int amount) || amount <= 0)
            {
                SendToPlayer(player, "Usage: !transfer <amount>");
                return;
            }

            // Find ally team
            Team senderTeam = player.Team;
            Team? allyTeam = null;
            if (senderTeam == _alienTeam) allyTeam = _wildlifeTeam;
            else if (senderTeam == _wildlifeTeam) allyTeam = _alienTeam;
            else if (senderTeam == _solTeam) allyTeam = _centTeam;
            else if (senderTeam == _centTeam) allyTeam = _solTeam;

            if (allyTeam == null) { SendToPlayer(player, "No allied team found."); return; }

            // Check sender has enough
            int available = senderTeam.TotalResources;
            if (amount > available)
            {
                SendToPlayer(player, $"Not enough resources. You have {available}.");
                return;
            }

            // Transfer: deduct from sender, add to receiver
            senderTeam.StartingResources -= amount;
            allyTeam.StartingResources += amount;

            string msg = $"{player.PlayerName} transferred {amount} resources from {senderTeam.TeamShortName} to {allyTeam.TeamShortName}";
            MelonLogger.Msg($"[4WAY] {msg}");
            SendToAll(msg);
            WriteToGameLog($"\"{player.PlayerName}\" triggered \"resource_transfer\" (amount \"{amount}\") (from \"{senderTeam.TeamShortName}\") (to \"{allyTeam.TeamShortName}\")");
        }

        private static void OnCmdSol(Player? player, string args)
        {
            if (player == null) return;
            if (!Is4WayEnabled) { SendToPlayer(player, "4-Way mode not enabled."); return; }
            if (_solTeam == null) { SendToPlayer(player, "Sol team not found."); return; }

            UntrackWildlifeMember(player);

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
            player.Team = _solTeam;
            NetworkLayer.SendPlayerSelectTeam(player, _solTeam);
            GameMode.CurrentGameMode?.SpawnUnitForPlayer(player, _solTeam);

            SendToPlayer(player, "Switched to Sol!");
            MelonLogger.Msg($"[4WAY] {player.PlayerName} switched to Sol");
        }

        private static void OnCmdCentauri(Player? player, string args)
        {
            if (player == null) return;
            if (!Is4WayEnabled) { SendToPlayer(player, "4-Way mode not enabled."); return; }
            if (_centTeam == null) { SendToPlayer(player, "Centauri team not found."); return; }

            UntrackWildlifeMember(player);

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
            player.Team = _centTeam;
            NetworkLayer.SendPlayerSelectTeam(player, _centTeam);
            GameMode.CurrentGameMode?.SpawnUnitForPlayer(player, _centTeam);

            SendToPlayer(player, "Switched to Centauri!");
            MelonLogger.Msg($"[4WAY] {player.PlayerName} switched to Centauri");
        }
    }
}
