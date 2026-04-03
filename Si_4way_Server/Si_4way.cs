using HarmonyLib;
using MelonLoader;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_4way.Si_4way), "Si_4way", "1.7.0", "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]

namespace Si_4way
{
    /// <summary>
    /// Main entry point. Shared state, initialization, team setup.
    /// Modules: Alliance.cs, WinCondition.cs, TeamUI.cs, Commands.cs, NestSpawn.cs
    /// </summary>
    public partial class Si_4way : MelonMod
    {
        // === Shared state ===
        public static bool Is4WayEnabled = false;
        internal static int _balanceTolerance = 1;

        // === Events for cross-mod communication (Option A) ===
        // Other mods subscribe via reflection. Fired when 4way round starts.
        public static event Action<Team, Team> On4WayRoundStarted;  // (alienTeam, wildlifeTeam)
        public static event Action On4WayRoundEnded;

        internal static Type _mpStrategyType = null;
        internal static MethodInfo _setCommanderMethod = null;
        internal static MethodInfo _synchCommanderMethod = null;
        internal static Type _gameType = null;
        internal static MethodInfo _spawnPrefabMethod = null;
        internal static MethodInfo _onMissionStateChangedMethod = null;

        internal static BaseTeamSetup _wildlifeSetup = null;
        internal static Team _wildlifeTeam = null;
        internal static Team _alienTeam = null;
        internal static Team _solTeam = null;
        internal static Team _centTeam = null;

        internal static HashSet<ulong> _wasOnWildlife = new HashSet<ulong>();

        internal static bool IsServer => Game.GetIsServer();

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

                // Vote intercept
                var setTvMethod = _mpStrategyType.GetMethod("SetTeamVersusMode",
                    BindingFlags.Public | BindingFlags.Instance);
                if (setTvMethod != null)
                {
                    harmony.Patch(setTvMethod,
                        prefix: new HarmonyMethod(typeof(Si_4way), nameof(Prefix_SetTeamVersusMode)));
                    MelonLogger.Msg("Patched SetTeamVersusMode");
                }
            }

            // Register patches from modules
            TeamUI.RegisterPatches(harmony);
            Alliance.RegisterPatches(harmony);
            WinCondition.RegisterPatches(harmony);

            NestSpawn.ResolveSpawnMethod();
            Commands.RegisterCommands();

            FindTeams();
            MelonLogger.Msg($"Si_4way v1.7.0 loaded ({(IsServer ? "SERVER" : "CLIENT")})");
        }

        // === Vote intercept ===
        public static bool Prefix_SetTeamVersusMode(ref GameModeExt.ETeamsVersus teamsVersusMode, bool preventSpawning)
        {
            if (!Is4WayEnabled) return true;
            if (teamsVersusMode == GameModeExt.ETeamsVersus.NONE) return true;
            if (preventSpawning) return true;

            var original = teamsVersusMode;
            teamsVersusMode = GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS;
            MelonLogger.Msg($"[4WAY] Vote override: {original} → HvHvA");

            SetupWildlifeTeam();
            WinCondition.ResetState();
            _wasOnWildlife.Clear();
            _commanderApplicants.Clear();
            _lotteryDone = false;
            NestSpawn.ScheduleNestSpawn();

            // Fire event for cross-mod communication
            try
            {
                On4WayRoundStarted?.Invoke(_alienTeam, _wildlifeTeam);
                MelonLogger.Msg("[4WAY] Fired On4WayRoundStarted event");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[4WAY] On4WayRoundStarted event error: {ex.Message}");
            }

            return true;
        }

        // === Update loops ===
        public override void OnLateUpdate()
        {
            if (!Is4WayEnabled || !IsServer) return;
            NestSpawn.OnLateUpdate();
            WinCondition.OnLateUpdate();
        }

        // === Teams ===
        internal static void FindTeams()
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

        // === Wildlife team setup ===
        internal static void SetupWildlifeTeam()
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

            // Unit cap adjustment is handled by Si_NoUnitLimits (via On4WayRoundStarted event)

            _wildlifeTeam.IsSpecial = false;
            _wildlifeTeam.StartingResources = 8000;
            _wildlifeSetup = null;

            MelonLogger.Msg("[4WAY] Wildlife team setup complete");
        }

        // === Helpers ===
        internal static void TrackWildlifeMember(Player player)
        {
            if (player != null) _wasOnWildlife.Add((ulong)player.PlayerID);
        }

        internal static void UntrackWildlifeMember(Player player)
        {
            if (player != null) _wasOnWildlife.Remove((ulong)player.PlayerID);
        }

        internal static void SendToPlayer(Player player, string message)
        {
            HelperMethods.ReplyToCommand_Player(player, message);
        }

        /// <summary>
        /// Fire AdminMod's Event_Roles.OnRoleChanged so the logging mod records the change.
        /// role: 0=NONE, 1=INFANTRY, 2=COMMANDER
        /// </summary>
        internal static void FireRoleChangedEvent(Player player, byte role)
        {
            try
            {
                var eventRolesType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "Event_Roles");
                if (eventRolesType != null)
                {
                    var fireMethod = eventRolesType.GetMethod("FireOnRoleChangedEvent",
                        BindingFlags.Public | BindingFlags.Static);
                    if (fireMethod != null)
                    {
                        // ETeamRole enum: NONE=0, UNIT=1, COMMANDER=2
                        var roleEnum = Enum.ToObject(typeof(GameModeExt.ETeamRole), (int)role);
                        fireMethod.Invoke(null, new object[] { player, roleEnum });
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Write to the Half-Life Logger's log file (Si_Logging.PrintLogLine) via reflection.
        /// </summary>
        internal static void WriteToGameLog(string message)
        {
            try
            {
                var loggingType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "HL_Logging");
                if (loggingType != null)
                {
                    var printMethod = loggingType.GetMethod("PrintLogLine",
                        BindingFlags.Public | BindingFlags.Static);
                    printMethod?.Invoke(null, new object[] { message, false });
                }
            }
            catch { }
        }

        internal static void SendToAll(string message)
        {
            HelperMethods.ReplyToCommand(message);
        }
    }
}
