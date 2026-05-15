using HarmonyLib;
using BepInEx;
using UnityEngine;
using Mirage;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;

namespace Bailmod
{
    [BepInPlugin("com.raksaputra.bailmod", "Bailmod", "1.1.0")]
    public class BailmodPlugin : BaseUnityPlugin
    {
        public static BailmodPlugin Instance;
        public static ManualLogSource Log;

        void Awake()
        {
            Instance = this;
            Log = Logger;
            Logger.LogInfo("Bailmod loaded");
            
            var harmony = new Harmony("com.raksaputra.bailmod");
            
            harmony.PatchAll(typeof(BailmodPlugin));

            try
            {
                var detachMethod = AccessTools.Method(typeof(UnitPart), "Detach", new Type[] { typeof(Vector3), typeof(Vector3) });
                if (detachMethod == null) detachMethod = AccessTools.Method(typeof(UnitPart), "Detach", new Type[0]);
                
                if (detachMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(BailmodPlugin).GetMethod(nameof(UnitPart_Detach_Prefix), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
                    harmony.Patch(detachMethod, prefix: prefix);
                    Logger.LogInfo($"Successfully hooked UnitPart.{detachMethod.Name} for sensitive part-detachment triggers.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to patch UnitPart for sensitive triggers: {ex}");
            }
        }

        [HarmonyPatch(typeof(GroundVehicle), "UnitDisabled")]
        [HarmonyPostfix]
        static void GroundVehicle_UnitDisabled_Postfix(GroundVehicle __instance, bool oldState, bool newState)
        {
            if (GameManager.gameState == GameState.Encyclopedia) return;
            if (!oldState && newState && __instance.IsServer && __instance.GetMass() > 29999f)
            {
                DoSpawnBail(__instance);
            }
        }

        static void UnitPart_Detach_Prefix(UnitPart __instance)
        {
            if (GameManager.gameState == GameState.Encyclopedia) return;
            if (__instance == null || Time.timeSinceLevelLoad < 5f) return;

            Ship ship = __instance.GetComponentInParent<Ship>();
            if (ship == null)
            {
                // Fallback to internal parentUnit field or GetUnit() method
                ship = __instance.parentUnit as Ship;
                if (ship == null) ship = __instance.GetUnit() as Ship;
            }

            if (ship != null && ship.IsServer)
            {
                var state = ship.gameObject.GetComponent<ShipRaftState>();
                if (state == null) state = ship.gameObject.AddComponent<ShipRaftState>();

                if (!state.hasTriggered)
                {
                    state.hasTriggered = true;
                    BailmodPlugin.Log.LogInfo($"Bailmod: Ship {ship.unitName} part detached ({__instance.name}). Triggering sensitive drop.");
                    FactionHQ hq = ship.MapHQ ?? ship.NetworkHQ;
                    BailmodPlugin.Instance.StartCoroutine(SpawnNavalContainersCoroutine(ship, hq));
                }
            }
        }

        // --- THE FAILSAFE TRIGGER ---
        [HarmonyPatch(typeof(Ship), "UnitDisabled")]
        [HarmonyPostfix]
        static void Ship_UnitDisabled_Postfix(Ship __instance, bool oldState, bool newState)
        {
            if (GameManager.gameState == GameState.Encyclopedia) return;
            if (!oldState && newState && __instance.IsServer)
            {
                if (Time.timeSinceLevelLoad < 5f) return;
                
                var state = __instance.gameObject.GetComponent<ShipRaftState>();
                if (state == null) state = __instance.gameObject.AddComponent<ShipRaftState>();

                if (!state.hasTriggered)
                {
                    state.hasTriggered = true;
                    BailmodPlugin.Log.LogInfo($"Bailmod: Ship {__instance.unitName} disabled (sinking failsafe). Triggering drop.");
                    FactionHQ hq = __instance.MapHQ ?? __instance.NetworkHQ;
                    BailmodPlugin.Instance.StartCoroutine(SpawnNavalContainersCoroutine(__instance, hq));
                }
            }
        }

        internal static IEnumerator SpawnNavalContainersCoroutine(Ship ship, FactionHQ hq)
        {
            if (ship == null || hq == null || ship.definition == null) yield break;

            UnitDefinition shipDef = ship.definition;
            float shipMass = shipDef.mass;
            float shipLength = shipDef.length;
            float shipWidth = shipDef.width;
            float shipHeight = shipDef.height;

            // Determine count based on mass
            int count = 1;
            if (shipMass >= 40000000f) count = 6;
            else if (shipMass >= 30000000f) count = 5;
            else if (shipMass >= 20000000f) count = 4;
            else if (shipMass >= 10000000f) count = 3;
            else if (shipMass >= 1000000f) count = 2;
            else count = 1;

            var allDefs = Resources.FindObjectsOfTypeAll<UnitDefinition>();
            UnitDefinition containerDef = allDefs.FirstOrDefault(d => 
                d.name == "NavalPallet1" || 
                d.name == "NavalSupplyContainerx1" ||
                d.unitName.Contains("Naval Supply"));

            if (containerDef == null || containerDef.unitPrefab == null) yield break;

            System.Random rnd = new System.Random(Guid.NewGuid().GetHashCode());

            for (int i = 0; i < count; i++)
            {
                if (ship == null) yield break;

                // Alternate Port and Starboard
                float sideSign = (i % 2 == 0) ? 1f : -1f; 
                
                // Spawn at the edge of the ship's width plus a small buffer
                float sideDist = (shipWidth / 2f) + (float)(rnd.NextDouble() * 10.0 + 5.0); 

                // Distribute along the ship's length
                float zStep = shipLength / Math.Max(1, count - 1);
                float expectedZ = -(shipLength / 2f) + (zStep * i);
                float lengthOffset = expectedZ + (float)(rnd.NextDouble() * 10.0 - 5.0);

                // Spawn at ship height / 2
                float spawnHeight = shipHeight / 2f;

                Vector3 shipRight = ship.transform.right; shipRight.y = 0; shipRight.Normalize();
                Vector3 shipForward = ship.transform.forward; shipForward.y = 0; shipForward.Normalize();
                Vector3 worldOffset = (shipRight * (sideSign * sideDist)) + (shipForward * lengthOffset) + (Vector3.up * spawnHeight);
                
                GlobalPosition spawnPos = GlobalPositionExtensions.ToGlobalPosition(ship.transform.position) + worldOffset;
                Quaternion randomRot = Quaternion.Euler(0, rnd.Next(0, 360), 0);
                string uniqueId = rnd.Next(1000, 9999).ToString();

                Container spawned = Spawner.i.SpawnContainer(containerDef.unitPrefab, spawnPos, randomRot, null, $"NavalCargo_{ship.unitName}_{uniqueId}");
                
                if (spawned != null)
                {
                    spawned.transform.SetParent(null);
                    
                    var tracker = spawned.gameObject.AddComponent<SupplyTracker>();
                    tracker.targetPos = spawnPos;
                    tracker.id = uniqueId;
                    tracker.finalHQ = hq;

                    spawned.InitializeUnit();
                }
                
                yield return new WaitForSeconds(0.2f);
            }
        }

        static void DoSpawnBail(GroundVehicle vehicle)
        {
            if (vehicle == null || Spawner.i == null) return;

            int roll = UnityEngine.Random.Range(0, 100);
            int count = roll < 50 ? 0 : (roll < 80 ? 1 : (roll < 90 ? 2 : 3));
            if (count == 0) return;

            FactionHQ hq = vehicle.MapHQ ?? vehicle.NetworkHQ;
            GameObject pilotPrefab = GameAssets.i.pilotDismounted;

            for (int i = 0; i < count; i++)
            {
                Vector2 randomCircle = UnityEngine.Random.insideUnitCircle * 5f;
                Vector3 localOffset = new Vector3(randomCircle.x, randomCircle.y, 2f);
                GlobalPosition spawnPos = GlobalPositionExtensions.ToGlobalPosition(vehicle.transform.position) + (vehicle.transform.rotation * localOffset);
                
                PilotDismounted pilot = Spawner.i.SpawnPilot(pilotPrefab, spawnPos, vehicle.transform.rotation, hq, $"Bailout_{Guid.NewGuid().ToString().Substring(0,4)}");
                if (pilot != null)
                {
                    var hpField = AccessTools.Field(typeof(PilotDismounted), "hitPoints");
                    if (hpField != null) hpField.SetValue(pilot, 100f);
                    pilot.SetPilotState(PilotDismounted.PilotState.landing);
                }
            }
        }
    }

    // Lightweight state tracker to ensure rafts only spawn once per ship
    public class ShipRaftState : MonoBehaviour
    {
        public bool hasTriggered = false;
    }

    public class SupplyTracker : MonoBehaviour
    {
        public GlobalPosition targetPos;
        public FactionHQ finalHQ;
        public string id;
        private float startTime;
        private bool hqAssigned = false;
        private Rigidbody rb;

        void Start()
        {
            startTime = Time.time;
            rb = GetComponent<Rigidbody>();
            InvokeRepeating("LogTelemetry", 0.5f, 1.0f);
        }

        void Update()
        {
            HandlePositionLock();
        }

        void LateUpdate()
        {
            // Execute in LateUpdate to ensure we run AFTER the game's internal cargo snapping logic
            HandlePositionLock();
        }

        void HandlePositionLock()
        {
            float age = Time.time - startTime;
            
            // Relentlessly force position for 2.5 seconds to beat internal initialization logic
            if (age < 2.5f)
            {
                transform.position = GlobalPositionExtensions.ToLocalPosition(targetPos);
                
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }
            }
            else if (age < 3.0f)
            {
                // Release physics so it behaves normally in the ocean
                if (rb != null && rb.isKinematic)
                {
                    rb.isKinematic = false;
                }
            }

            // Restore HQ assignment smoothly after snap logic times out
            if (age > 6f && !hqAssigned && finalHQ != null)
            {
                hqAssigned = true;
                var unit = GetComponent<Unit>();
                if (unit != null)
                {
                    unit.MapHQ = finalHQ;
                    unit.NetworkHQ = finalHQ;
                }
            }
        }

        void LogTelemetry()
        {
            if (this == null || gameObject == null) return;
            float age = Time.time - startTime;

            if (age > 15f) CancelInvoke();

            Vector3 localPos = transform.position;
            Vector3 targetLocal = GlobalPositionExtensions.ToLocalPosition(targetPos);
            float dist = Vector3.Distance(localPos, targetLocal);

            GlobalPosition currentPos = GlobalPositionExtensions.ToGlobalPosition(transform.position);
            BailmodPlugin.Log.LogInfo($"[Telemetry-{id}] Age: {age:F1}s | Pos: {currentPos} | Offset: {dist:F1}m | HQ: {(hqAssigned ? "Yes" : "No")}");
        }
    }
}