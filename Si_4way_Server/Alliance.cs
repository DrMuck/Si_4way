using HarmonyLib;
using MelonLoader;
using SilicaAdminMod;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Si_4way
{
    /// <summary>
    /// Alliance system: GetTeamsAreEnemy, projectile friendly fire, shared FOW detection
    /// </summary>
    public partial class Si_4way
    {

        internal static class Alliance
        {
            public static void RegisterPatches(HarmonyLib.Harmony harmony)
            {
                var getTeamsAreEnemy = typeof(GameMode).GetMethod("GetTeamsAreEnemy",
                    BindingFlags.Public | BindingFlags.Instance);
                if (getTeamsAreEnemy != null)
                {
                    harmony.Patch(getTeamsAreEnemy,
                        postfix: new HarmonyMethod(typeof(Si_4way), nameof(Postfix_GetTeamsAreEnemy)));
                    MelonLogger.Msg("Patched GetTeamsAreEnemy");
                }

                var projType = typeof(GameMode).Assembly.GetType("ProjectileBasic");
                if (projType != null)
                {
                    // Cache Team accessor once — used per-projectile-impact in prefixes below
                    _projTeamProperty = projType.GetProperty("Team", BindingFlags.Public | BindingFlags.Instance);
                    _projTeamField = projType.GetField("Team", BindingFlags.Public | BindingFlags.Instance);

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

                // Allied voice chat: treat allied teams as same-team for voice relay
                var getCanReceiveVoice = typeof(Player).GetMethod("GetCanReceiveVoice",
                    BindingFlags.Public | BindingFlags.Instance);
                if (getCanReceiveVoice != null)
                {
                    harmony.Patch(getCanReceiveVoice,
                        prefix: new HarmonyMethod(typeof(Si_4way), nameof(Prefix_GetCanReceiveVoice)));
                    MelonLogger.Msg("Patched GetCanReceiveVoice for allied voice chat");
                }
            }
        }

        internal static bool AreTeamsAllied(Team t1, Team t2)
        {
            // Hot path: called by game's GetTeamsAreEnemy on every damage/targeting/FOW check.
            // Optimized to pure reference compares — no property reads, no virtual calls.
            if (t1 == t2) return true;
            if (t1 == null || t2 == null) return false;
            // Alien bloc: Alien + Wildlife
            bool t1Alien = (t1 == _alienTeam) | (t1 == _wildlifeTeam);
            bool t2Alien = (t2 == _alienTeam) | (t2 == _wildlifeTeam);
            if (t1Alien & t2Alien) return true;
            // Human bloc: Sol + Cent
            bool t1Human = (t1 == _solTeam) | (t1 == _centTeam);
            bool t2Human = (t2 == _solTeam) | (t2 == _centTeam);
            return t1Human & t2Human;
        }

        public static void Postfix_GetTeamsAreEnemy(Team team1, Team team2, ref bool __result)
        {
            if (!Is4WayEnabled || !__result) return;
            if (AreTeamsAllied(team1, team2))
                __result = false;
        }

        /// <summary>
        /// Replace GetCanReceiveVoice for allied teams.
        /// The original method forces proximityOnly=true when teams differ,
        /// overriding our ref parameter. So for allies we skip the original entirely.
        /// </summary>
        public static bool Prefix_GetCanReceiveVoice(Player __instance, Player fromPlayer, bool proximityOnly, ref bool __result)
        {
            if (!Is4WayEnabled) return true; // let original run
            if (fromPlayer == null || __instance == null) return true;
            if (fromPlayer.Team == null || __instance.Team == null) return true;
            if (fromPlayer.IsMuted) { __result = false; return false; }
            // Same team → let original handle it
            if (fromPlayer.Team == __instance.Team) return true;
            // Not allied → let original handle it
            if (!AreTeamsAllied(fromPlayer.Team, __instance.Team)) return true;

            // Allied teams: treat as same team — allow team voice (skip proximity check)
            __result = true;
            return false; // skip original
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
                var projTeam = _projTeamProperty?.GetValue(__instance) as Team
                            ?? _projTeamField?.GetValue(__instance) as Team;
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
                var projTeam = _projTeamProperty?.GetValue(__instance) as Team
                            ?? _projTeamField?.GetValue(__instance) as Team;
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

    }
}
