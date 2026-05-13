using HarmonyLib;
using BepInEx;
using UnityEngine;
using Mirage;
using System;
using System.Collections;

namespace Bailmod
{
    [BepInPlugin("com.raksaputra.bailmod", "Bailmod", "1.0.0")]
    public class BailmodPlugin : BaseUnityPlugin
    {
        public static BailmodPlugin Instance;

        void Awake()
        {
            Instance = this;
            Logger.LogInfo("Bailmod loaded");
            Harmony.CreateAndPatchAll(typeof(BailmodPlugin));
        }

        [HarmonyPatch(typeof(GroundVehicle), "UnitDisabled")]
        [HarmonyPostfix]
        static void GroundVehicle_UnitDisabled_Postfix(GroundVehicle __instance, bool oldState, bool newState)
        {
            if (!oldState && newState)
            {
                // Only for vehicles with mass > 29.99 T (29999 kg) on the server
                if (__instance.IsServer && __instance.GetMass() > 29999f)
                {
                    DoSpawnBail(__instance);
                }
            }
        }

        static void DoSpawnBail(GroundVehicle vehicle)
        {
            if (vehicle == null) return;

            // Randomized spawn count: 50% 0, 30% 1, 10% 2, 10% 3
            int roll = UnityEngine.Random.Range(0, 100);
            int count = 0;
            if (roll < 50) count = 0;
            else if (roll < 80) count = 1;
            else if (roll < 90) count = 2;
            else count = 3;

            if (count == 0) return;

            FactionHQ hq = vehicle.MapHQ;
            if (hq == null) hq = vehicle.NetworkHQ;
            
            GameObject pilotPrefab = GameAssets.i.pilotDismounted;
            if (pilotPrefab == null || Spawner.i == null) return;

            // Base height offset (assuming Z is height per user)
            float heightOffset = 2f;
            float radius = 5f;
            string vUnitName = vehicle.unitName;

            for (int i = 0; i < count; i++)
            {
                // Randomize X and Y within the requested radius
                Vector2 randomCircle = UnityEngine.Random.insideUnitCircle * radius;
                Vector3 localOffset = new Vector3(randomCircle.x, randomCircle.y, heightOffset);
                
                GlobalPosition spawnPos = GlobalPositionExtensions.GlobalPosition(vehicle) + (vehicle.transform.rotation * localOffset);
                
                string uniqueName = "Bailout_" + vUnitName + "_" + i + "_" + Guid.NewGuid().ToString().Substring(0, 4);

                PilotDismounted pilot = Spawner.i.SpawnPilot(pilotPrefab, spawnPos, vehicle.transform.rotation, hq, uniqueName);
                
                if (pilot != null)
                {
                    // Ensure the pilot is alive and healthy
                    var hpField = AccessTools.Field(typeof(PilotDismounted), "hitPoints");
                    if (hpField != null) hpField.SetValue(pilot, 100f);

                    // Set state to landing
                    pilot.SetPilotState(PilotDismounted.PilotState.landing);

                    // Force faction assignment
                    if (hq != null)
                    {
                        pilot.MapHQ = hq;
                        pilot.NetworkHQ = hq;
                    }
                }
            }
        }
    }
}
