using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using System;
using System.Reflection;

[assembly: MelonInfo(typeof(Si_4way.FourWayModClient), "4-Way Factions", "0.4.4", "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]

namespace Si_4way
{
    public class FourWayModClient : MelonMod
    {
        private static Team _wildlifeTeam = null;
        private static Team _alienTeam = null;
        private static Team _centauriTeam = null;
        private static Team _gamemasterTeam = null;
        private static BaseTeamSetup _wildlifeSetup = null;
        private static bool _teamsSearched = false;
        private static bool _setupCreationFailed = false;

        public override void OnInitializeMelon()
        {
            try
            {
                var harmony = new HarmonyLib.Harmony("com.drmuck.4way.client");

                var getTeamSetup = typeof(GameModeExt).GetMethod("GetTeamSetup",
                    BindingFlags.Public | BindingFlags.Instance);
                if (getTeamSetup != null)
                {
                    harmony.Patch(getTeamSetup,
                        postfix: new HarmonyMethod(typeof(FourWayModClient), nameof(Postfix_GetTeamSetup)));
                    MelonLogger.Msg("Patched GetTeamSetup");
                }

                // Patch GetHasLost — keep Alien UI when Wildlife queen alive
                var getHasLost = typeof(StrategyTeamSetup).GetMethod("GetHasLost",
                    BindingFlags.Public | BindingFlags.Instance);
                if (getHasLost != null)
                {
                    harmony.Patch(getHasLost,
                        postfix: new HarmonyMethod(typeof(FourWayModClient), nameof(Postfix_GetHasLost)));
                    MelonLogger.Msg("Patched GetHasLost");
                }

                MelonLogger.Msg("4-Way Factions CLIENT v1.3.0 loaded");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"Init failed: {ex}");
            }
        }

        // Run Wildlife team setup early on each scene load
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            try
            {
                if (string.IsNullOrEmpty(sceneName) || sceneName == "Loading" || sceneName == "MainMenu")
                    return;

                _teamsSearched = false;
                _wildlifeTeamSetup = false;
                _wildlifeSetup = null;
                _setupCreationFailed = false;

                FindTeams();
                SetupWildlifeTeamClient();
                MelonLogger.Msg($"[4WAY-C] Scene {sceneName} — Wildlife team setup applied early");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[4WAY-C] OnSceneWasLoaded: {ex.Message}");
            }
        }

        private static void FindTeams()
        {
            if (_teamsSearched) return;
            _teamsSearched = true;
            try
            {
                foreach (var t in Team.Teams)
                {
                    if (t == null) continue;
                    string name = t.TeamName ?? "";
                    if (name.Contains("Worm") || name.Contains("Wildlife"))
                        _wildlifeTeam = t;
                    else if (name.Contains("Gamemaster") || name.Contains("GameMaster"))
                        _gamemasterTeam = t;
                    else if (name.Contains("Centauri") && !t.IsSpecial)
                        _centauriTeam = t;
                    else if (name.Contains("Alien") && !t.IsSpecial)
                        _alienTeam = t;
                }
            }
            catch
            {
                _teamsSearched = false;
            }
        }

        public static void Postfix_GetTeamSetup(object __instance, object team, ref object __result)
        {
            try
            {
                if (__result != null) return;

                var teamObj = team as Team;
                if (teamObj == null) return;

                FindTeams();
                // Check if it's any of our 4th team candidates
                if (teamObj != _wildlifeTeam && teamObj != _gamemasterTeam) return;

                // Ensure Wildlife team has BaseStructure/DefaultUnit from Alien (client-side)
                SetupWildlifeTeamClient();

                if (_wildlifeSetup == null && !_setupCreationFailed)
                    CreateWildlifeSetup(__instance as GameModeExt, teamObj);

                if (_wildlifeSetup != null)
                    __result = _wildlifeSetup;
            }
            catch { }
        }

        // GetHasLost — keep Alien UI accessible while Wildlife queen lives
        public static void Postfix_GetHasLost(object __instance, ref bool __result)
        {
            try
            {
                if (!__result) return;
                var setup = __instance as BaseTeamSetup;
                if (setup == null || setup.Team == null) return;
                FindTeams();

                // Alien side: ally still has criticals → not lost
                if (_alienTeam != null && _wildlifeTeam != null)
                {
                    if (setup.Team == _alienTeam && _wildlifeTeam.GetHasAnyCritical())
                    { __result = false; return; }
                    if (setup.Team == _wildlifeTeam && _alienTeam.GetHasAnyCritical())
                    { __result = false; return; }
                }

                // Human side: no UI override — Sol/Cent have separate UIs
                // Players get migrated server-side
            }
            catch { }
        }

        private static bool _wildlifeTeamSetup = false;
        private static void SetupWildlifeTeamClient()
        {
            if (_wildlifeTeamSetup) return;

            try
            {
                // Use Alien as source (Nest as BaseStructure, Biotics as resource)
                if (_alienTeam == null) return;
                var sourceTeam = _alienTeam;

                MelonLogger.Msg($"[4WAY-C] Using {sourceTeam.TeamName} as template (Alien Nest)");

                // Setup both Wildlife and Gamemaster
                Team[] fourthTeams = new Team[] { _wildlifeTeam, _gamemasterTeam };
                foreach (var ft in fourthTeams)
                {
                    if (ft == null) continue;
                    try
                    {
                        if (ft.BaseStructure == null && sourceTeam.BaseStructure != null)
                        {
                            ft.BaseStructure = sourceTeam.BaseStructure;
                            MelonLogger.Msg($"[4WAY-C] Set {ft.TeamName} BaseStructure = {sourceTeam.BaseStructure.DisplayName}");
                        }
                        if (ft.DefaultUnit == null && sourceTeam.DefaultUnit != null)
                        {
                            ft.DefaultUnit = sourceTeam.DefaultUnit;
                            MelonLogger.Msg($"[4WAY-C] Set {ft.TeamName} DefaultUnit = {sourceTeam.DefaultUnit.DisplayName}");
                        }
                    }
                    catch { }
                }

                try
                {
                    // Il2Cpp: UsableResource is a PROPERTY, not a field
                    var resProp = typeof(Team).GetProperty("UsableResource", BindingFlags.Public | BindingFlags.Instance);
                    if (resProp != null)
                    {
                        var sourceRes = resProp.GetValue(sourceTeam);
                        if (sourceRes != null)
                        {
                            foreach (var ft in fourthTeams)
                            {
                                if (ft == null) continue;
                                resProp.SetValue(ft, sourceRes);
                                MelonLogger.Msg($"[4WAY-C] Set {ft.TeamName} UsableResource from {sourceTeam.TeamName} (property)");
                            }
                        }
                        else
                        {
                            MelonLogger.Warning("[4WAY-C] Source UsableResource is null");
                        }
                    }
                    else
                    {
                        // Fallback: try direct assignment
                        foreach (var ft in fourthTeams)
                        {
                            if (ft == null) continue;
                            ft.UsableResource = sourceTeam.UsableResource;
                            MelonLogger.Msg($"[4WAY-C] Set {ft.TeamName} UsableResource directly");
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[4WAY-C] UsableResource: {ex.Message}");
                }

                _wildlifeTeamSetup = true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[4WAY-C] SetupWildlifeTeamClient: {ex.Message}");
            }
        }

        private static void CreateWildlifeSetup(GameModeExt gameMode, Team requestedTeam)
        {
            try
            {
                FindTeams();
                // Determine which 4th team was requested
                // (we don't know _useGamemaster on client, so check which team triggered the call)
                if (_wildlifeTeam == null && _gamemasterTeam == null) return;

                // Find Alien setup as template (Nest-based)
                BaseTeamSetup alienSetup = null;
                if (gameMode != null && gameMode.BaseTeamSetups != null)
                {
                    foreach (var s in gameMode.BaseTeamSetups)
                    {
                        if (s != null && s.Team != null && _alienTeam != null && s.Team == _alienTeam)
                        {
                            alienSetup = s;
                            break;
                        }
                    }
                }

                // Create Il2Cpp BaseTeamSetup — set Team to Wildlife (not Alien proxy)
                _wildlifeSetup = new BaseTeamSetup();
                // Set Team to whichever 4th team was requested
                _wildlifeSetup.Team = requestedTeam;
                _wildlifeSetup.Enabled = true;

                MelonLogger.Msg("[4WAY-C] Created BaseTeamSetup with Team=Wildlife");

                // Copy remaining fields from Alien template (each in try/catch to isolate failures)
                if (alienSetup != null)
                {
                    try { _wildlifeSetup.StartingResources = alienSetup.StartingResources; } catch { }
                    try { _wildlifeSetup.PlayerSpawn = alienSetup.PlayerSpawn; } catch { }
                    try { _wildlifeSetup.PlayerSpawnExt = alienSetup.PlayerSpawnExt; } catch { }
                    try { _wildlifeSetup.AICommanderSettings = alienSetup.AICommanderSettings; } catch { }
                    try { _wildlifeSetup.AICommanderPlayerLeftWaitTime = 30f; } catch { }
                    MelonLogger.Msg("[4WAY-C] Copied Alien template fields");
                }

                // ForTeamVersusModes — try Il2Cpp list, skip if fails
                try
                {
                    var modes = new Il2CppSystem.Collections.Generic.List<GameModeExt.ETeamsVersus>();
                    foreach (GameModeExt.ETeamsVersus mode in Enum.GetValues(typeof(GameModeExt.ETeamsVersus)))
                        modes.Add(mode);
                    _wildlifeSetup.ForTeamVersusModes = modes;
                    MelonLogger.Msg("[4WAY-C] Set ForTeamVersusModes");
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[4WAY-C] ForTeamVersusModes failed: {ex.Message}");
                    // Try copying from Alien
                    try
                    {
                        if (alienSetup != null)
                            _wildlifeSetup.ForTeamVersusModes = alienSetup.ForTeamVersusModes;
                    }
                    catch { }
                }

                // CRITICAL: Inject into actual BaseTeamSetups list so GetAvailableUnits() finds it
                try
                {
                    if (gameMode != null && gameMode.BaseTeamSetups != null)
                    {
                        // Duplicate guard — check if Wildlife is already in the list
                        bool alreadyInjected = false;
                        foreach (var s in gameMode.BaseTeamSetups)
                        {
                            if (s != null && s.Team == _wildlifeTeam) { alreadyInjected = true; break; }
                        }
                        if (!alreadyInjected)
                        {
                            gameMode.BaseTeamSetups.Add(_wildlifeSetup);
                            MelonLogger.Msg("[4WAY-C] Injected Wildlife setup into BaseTeamSetups list");
                        }
                    }
                }
                catch (Exception ex2)
                {
                    MelonLogger.Warning($"[4WAY-C] Could not inject into BaseTeamSetups: {ex2.Message}");
                }

                MelonLogger.Msg($"[4WAY-C] Wildlife setup complete");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[4WAY-C] CreateWildlifeSetup FAILED: {ex}");
                _wildlifeSetup = null;
                _setupCreationFailed = true;
            }
        }
    }
}
