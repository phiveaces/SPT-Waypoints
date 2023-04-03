﻿using Aki.Reflection.Patching;
using Aki.Reflection.Utils;
using Comfort.Common;
using DrakiaXYZ.Waypoints.Helpers;
using EFT;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using Random = UnityEngine.Random;

namespace DrakiaXYZ.Waypoints.Patches
{
    public class WaypointPatch : ModulePatch
    {
        private static int customWaypointCount = 0;

        protected override MethodBase GetTargetMethod()
        {
            return typeof(BotControllerClass).GetMethod(nameof(BotControllerClass.Init));
        }

        /// <summary>
        /// 
        /// </summary>
        [PatchPrefix]
        private static void PatchPrefix(BotControllerClass __instance, IBotGame botGame, IBotCreator botCreator, BotZone[] botZones, ISpawnSystem spawnSystem, BotLocationModifier botLocationModifier, bool botEnable, bool freeForAll, bool enableWaveControl, bool online, bool haveSectants, string openZones)
        {
            var gameWorld = Singleton<GameWorld>.Instance;
            if (gameWorld == null)
            {
                Logger.LogError("BotController::Init called, but GameWorld doesn't exist");
                return;
            }

            if (botZones != null)
            {
                string mapName = gameWorld.MainPlayer.Location.ToLower();
                customWaypointCount = 0;

                var stopwatch = new Stopwatch();
                stopwatch.Start();

                // Inject our loaded patrols
                foreach (BotZone botZone in botZones)
                {
                    Dictionary<string, CustomPatrol> customPatrols = CustomWaypointLoader.Instance.getMapZonePatrols(mapName, botZone.NameZone);
                    if (customPatrols != null)
                    {
                        Logger.LogDebug($"Found custom patrols for {mapName} / {botZone.NameZone}");
                        foreach (string patrolName in customPatrols.Keys)
                        {
                            AddOrUpdatePatrol(botZone, customPatrols[patrolName]);
                        }
                    }
                }

                stopwatch.Stop();
                Logger.LogDebug($"Loaded {customWaypointCount} custom waypoints in {stopwatch.ElapsedMilliseconds}ms!");

                // If enabled, dump the waypoint data
                if (Settings.ExportMapPoints.Value)
                {
                    // If we haven't written out the Waypoints for this map yet, write them out now
                    Directory.CreateDirectory(WaypointsPlugin.PointsFolder);
                    string exportFile = $"{WaypointsPlugin.PointsFolder}\\{mapName}.json";
                    if (!File.Exists(exportFile))
                    {
                        ExportWaypoints(exportFile, botZones);
                    }
                }
            }
        }

        public static void AddOrUpdatePatrol(BotZone botZone, CustomPatrol customPatrol)
        {
            // If the map already has this patrol, update its values
            PatrolWay mapPatrol = botZone.PatrolWays.FirstOrDefault(p => p.name == customPatrol.name);
            if (mapPatrol != null)
            {
                Console.WriteLine($"PatrolWay {customPatrol.name} exists, updating");
                UpdatePatrol(mapPatrol, customPatrol);
            }
            // Otherwise, add a full new patrol
            else
            {
                Console.WriteLine($"PatrolWay {customPatrol.name} doesn't exist, creating");
                AddPatrol(botZone, customPatrol);
            }
        }

        private static void UpdatePatrol(PatrolWay mapPatrol, CustomPatrol customPatrol)
        {
            mapPatrol.BlockRoles = (WildSpawnType?)customPatrol.blockRoles ?? mapPatrol.BlockRoles;
            mapPatrol.MaxPersons = customPatrol.maxPersons ?? mapPatrol.MaxPersons;
            mapPatrol.PatrolType = customPatrol.patrolType ?? mapPatrol.PatrolType;

            // Exclude any points that already exist in the map PatrolWay
            var customWaypoints = customPatrol.waypoints.Where(
                p => (mapPatrol.Points.Where(w => w.position == p.position).ToList().Count == 0)
            ).ToList();

            if (customWaypoints.Count > 0)
            {
                mapPatrol.Points.AddRange(processWaypointsToPatrolPoints(mapPatrol, customWaypoints));
            }
        }

        private static List<PatrolPoint> processWaypointsToPatrolPoints(PatrolWay mapPatrol, List<CustomWaypoint> waypoints)
        {
            List<PatrolPoint> patrolPoints = new List<PatrolPoint>();
            if (waypoints == null)
            {
                return patrolPoints;
            }

            foreach (CustomWaypoint waypoint in waypoints)
            {
                Logger.LogDebug("Injecting custom PatrolPoint at " + waypoint.position.x + ", " + waypoint.position.y + ", " + waypoint.position.z);
                var newPatrolPointObject = new GameObject("CustomWaypoint_" + (customWaypointCount++));
                newPatrolPointObject.AddComponent<PatrolPoint>();
                var newPatrolPoint = newPatrolPointObject.GetComponent<PatrolPoint>();

                newPatrolPoint.Id = (new System.Random()).Next();
                newPatrolPoint.transform.position = new Vector3(waypoint.position.x, waypoint.position.y, waypoint.position.z);
                newPatrolPoint.CanUseByBoss = waypoint.canUseByBoss;
                newPatrolPoint.PatrolPointType = waypoint.patrolPointType;
                newPatrolPoint.ShallSit = waypoint.shallSit;
                newPatrolPoint.PointWithLookSides = null;
                newPatrolPoint.SubManual = false;
                if (mapPatrol != null && waypoint.waypoints == null)
                {
                    newPatrolPoint.CreateSubPoints(mapPatrol);
                }
                else
                {
                    newPatrolPoint.subPoints = processWaypointsToPatrolPoints(null, waypoint.waypoints);
                }
                patrolPoints.Add(newPatrolPoint);
            }

            return patrolPoints;
        }

        private static void AddPatrol(BotZone botZone, CustomPatrol customPatrol)
        {
            //Logger.LogDebug($"Creating custom patrol {customPatrol.name} in {botZone.NameZone}");
            // Validate some data
            if (customPatrol.blockRoles == null)
            {
                Logger.LogError("Invalid custom Patrol, blockRoles is null");
                return;
            }
            if (customPatrol.maxPersons == null)
            {
                Logger.LogError("Invalid custom Patrol, maxPersons is null");
                return;
            }
            if (customPatrol.patrolType == null)
            {
                Logger.LogError("Invalid custom Patrol, patrolTypes is null");
                return;
            }

            // Create the Patrol game object
            var mapPatrolObject = new GameObject(customPatrol.name);
            mapPatrolObject.AddComponent<PatrolWayCustom>();
            var mapPatrol = mapPatrolObject.GetComponent<PatrolWayCustom>();

            // Add the waypoints to the Patrol object
            UpdatePatrol(mapPatrol, customPatrol);

            // Add the patrol to our botZone
            botZone.PatrolWays = botZone.PatrolWays.Append(mapPatrol).ToArray();
        }

        static void ExportWaypoints(string exportFile, BotZone[] botZones)
        {
            ExportModel exportModel = new ExportModel();

            foreach (BotZone botZone in botZones)
            {
                exportModel.zones.Add(botZone.name, new ExportZoneModel());

                List<CustomPatrol> customPatrolWays = new List<CustomPatrol>();
                foreach (PatrolWay patrolWay in botZone.PatrolWays)
                {
                    CustomPatrol customPatrolWay = new CustomPatrol();
                    customPatrolWay.blockRoles = patrolWay.BlockRoles.GetInt();
                    customPatrolWay.maxPersons = patrolWay.MaxPersons;
                    customPatrolWay.patrolType = patrolWay.PatrolType;
                    customPatrolWay.name = patrolWay.name;
                    customPatrolWay.waypoints = CreateCustomWaypoints(patrolWay.Points);

                    customPatrolWays.Add(customPatrolWay);
                }

                exportModel.zones[botZone.name].patrols = customPatrolWays;

                exportModel.zones[botZone.name].coverPoints = botZone.CoverPoints.Select(p => customNavPointToExportNavPoint(p)).ToList();
                exportModel.zones[botZone.name].ambushPoints = botZone.AmbushPoints.Select(p => customNavPointToExportNavPoint(p)).ToList();
            }

            string jsonString = JsonConvert.SerializeObject(exportModel, Formatting.Indented);
            if (File.Exists(exportFile))
            {
                File.Delete(exportFile);
            }
            File.Create(exportFile).Dispose();
            StreamWriter streamWriter = new StreamWriter(exportFile);
            streamWriter.Write(jsonString);
            streamWriter.Flush();
            streamWriter.Close();
        }

        static ExportNavigationPoint customNavPointToExportNavPoint(CustomNavigationPoint customNavPoint)
        {
            ExportNavigationPoint exportNavPoint = new ExportNavigationPoint();
            exportNavPoint.AltPosition = customNavPoint.AltPosition;
            exportNavPoint.HaveAltPosition = customNavPoint.HaveAltPosition;
            exportNavPoint.BasePosition = customNavPoint.BasePosition;
            exportNavPoint.ToWallVector = customNavPoint.ToWallVector;
            exportNavPoint.FirePosition = customNavPoint.FirePosition;
            exportNavPoint.TiltType = customNavPoint.TiltType.GetInt();
            exportNavPoint.CoverLevel = customNavPoint.CoverLevel.GetInt();
            exportNavPoint.AlwaysGood = customNavPoint.AlwaysGood;
            exportNavPoint.BordersLightHave = customNavPoint.BordersLightHave;
            exportNavPoint.LeftBorderLight = customNavPoint.LeftBorderLight;
            exportNavPoint.RightBorderLight = customNavPoint.RightBorderLight;
            exportNavPoint.CanLookLeft = customNavPoint.CanLookLeft;
            exportNavPoint.CanLookRight = customNavPoint.CanLookRight;
            exportNavPoint.HideLevel = customNavPoint.HideLevel;

            return exportNavPoint;
        }

        static List<CustomWaypoint> CreateCustomWaypoints(List<PatrolPoint> patrolPoints)
        {
            List<CustomWaypoint> customWaypoints = new List<CustomWaypoint>();
            if (patrolPoints == null)
            {
                //Logger.LogDebug("patrolPoints is null, skipping");
                return customWaypoints;
            }

            foreach (PatrolPoint patrolPoint in patrolPoints)
            {
                CustomWaypoint customWaypoint = new CustomWaypoint();
                customWaypoint.canUseByBoss = patrolPoint.CanUseByBoss;
                customWaypoint.patrolPointType = patrolPoint.PatrolPointType;
                customWaypoint.position = patrolPoint.Position;
                customWaypoint.shallSit = patrolPoint.ShallSit;

                customWaypoints.Add(customWaypoint);
            }

            return customWaypoints;
        }
    }

    public class BotOwnerRunPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(BotOwner), "CalcGoal");
        }

        [PatchPostfix]
        public static void PatchPostfix(BotOwner __instance)
        {
            // If we're not patrolling, don't do anything
            if (!__instance.Memory.IsPeace || __instance.PatrollingData.Status != PatrolStatus.go)
            {
                //Logger.LogInfo($"({Time.time})BotOwner::RunPatch[{__instance.name}] - Bot not in peace, or not patrolling");
                return;
            }

            // If we're already running, check if our stamina is too low (< 30%), and stop running
            if (__instance.Mover.Sprinting && __instance.GetPlayer.Physical.Stamina.NormalValue < 0.3f)
            {
                //Logger.LogInfo($"({Time.time})BotOwner::RunPatch[{__instance.name}] - Bot was sprinting but stamina hit {Math.Floor(__instance.GetPlayer.Physical.Stamina.NormalValue * 100)}%. Stopping sprint");
                __instance.Sprint(false);
            }

            // If we aren't running, and our stamina is near capacity (> 80%), allow us to run
            if (!__instance.Mover.Sprinting && __instance.GetPlayer.Physical.Stamina.NormalValue > 0.8f)
            {
                //Logger.LogInfo($"({Time.time})BotOwner::RunPatch[{__instance.name}] - Bot wasn't sprinting but stamina hit {Math.Floor(__instance.GetPlayer.Physical.Stamina.NormalValue * 100)}%. Giving bot chance to run");
                if (Random.Range(0, 750) < __instance.Settings.FileSettings.Patrol.SPRINT_BETWEEN_CACHED_POINTS)
                {
                    //Logger.LogInfo($"({Time.time})BotOwner::RunPatch[{__instance.name}] - Bot decided to run");
                    __instance.Sprint(true);
                }
            }
        }
    }
}
