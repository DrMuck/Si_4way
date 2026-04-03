using HarmonyLib;
using MelonLoader;
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

                // Shared FOW removed — DetectTarget only gives minimap dots, not fog clearing
            }
        }

        internal static bool AreTeamsAllied(Team t1, Team t2)
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

    }
}
