using MelonLoader;
using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Si_4way
{
    /// <summary>
    /// Nest spawning: terrain, MapBalance integration, ambient life cleanup
    /// </summary>
    public partial class Si_4way
    {
        private static bool _needWildlifeNestSpawn = false;
        private static float _nestSpawnDelay = 0f;

        internal static class NestSpawn
        {
            public static void ResolveSpawnMethod()
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

            public static void ScheduleNestSpawn()
            {
                _needWildlifeNestSpawn = true;
                _nestSpawnDelay = 3f;
            }

            public static void OnLateUpdate()
            {
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
            }
        }

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
                if (_spawnPrefabMethod == null) NestSpawn.ResolveSpawnMethod();
                if (_spawnPrefabMethod == null) { MelonLogger.Error("[4WAY] SpawnPrefab not resolved"); return; }

                Vector3 nestPos = GetWildlifeSpawnFromMapBalance();
                string posSource = "map center";
                if (nestPos == Vector3.zero)
                    nestPos = CalculateMapCenter();
                else
                    posSource = "MapBalance config";

                var paramCount = _spawnPrefabMethod.GetParameters().Length;

                MelonLogger.Msg($"[4WAY] Spawning main Nest at ({nestPos.x:F0}, {nestPos.y:F1}, {nestPos.z:F0}) from {posSource}");
                _spawnPrefabMethod.Invoke(null, BuildSpawnArgs(prefab, _wildlifeTeam, nestPos, paramCount));

                var anchorPos = new Vector3(nestPos.x, nestPos.y - 100f, nestPos.z);
                MelonLogger.Msg("[4WAY] Spawning anchor Nest underground");
                _spawnPrefabMethod.Invoke(null, BuildSpawnArgs(prefab, _wildlifeTeam, anchorPos, paramCount));

                MelonLogger.Msg($"[4WAY] Wildlife nests spawned ({posSource})!");
                SendToAll($"Wildlife nest spawned ({posSource})!");
            }
            catch (Exception ex) { MelonLogger.Error($"[4WAY] SpawnWildlifeNest: {ex.Message}"); }
        }

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
            catch (Exception ex) { MelonLogger.Warning($"[4WAY] MapBalance read: {ex.Message}"); }
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
                        al.enabled = false; // fully disable spawner
                        disabled++;
                    }
                }
                if (disabled > 0)
                    MelonLogger.Msg($"[4WAY] Disabled {disabled} Wildlife ambient life spawner(s)");
            }
            catch (Exception ex) { MelonLogger.Warning($"[4WAY] DisableAmbientLife: {ex.Message}"); }
        }
    }
}
