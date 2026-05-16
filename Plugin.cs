using HarmonyLib;
using BepInEx;
using UnityEngine;
using Mirage;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Logging;
using BepInEx.Configuration;

namespace Bailmod
{
    [BepInPlugin("com.bailmod", "Bailmod", "1.2")]
    public class BailmodPlugin : BaseUnityPlugin
    {
        public static BailmodPlugin Instance;
        public static ManualLogSource Log;

        // Config Entries
        public static ConfigEntry<bool> EnableCrewBailout;
        public static ConfigEntry<bool> EnableNavalLiferafts;
        public static ConfigEntry<float> MinVehicleMass;
        
        public static ConfigEntry<float> MassTier0;
        public static ConfigEntry<float> MassTier1;
        public static ConfigEntry<float> MassTier2;
        public static ConfigEntry<float> MassTier3;
        public static ConfigEntry<float> MassTier4;
        public static ConfigEntry<float> MassTier5;
        public static ConfigEntry<float> MassTier6;

        public static ConfigEntry<float> RatioTier1;
        public static ConfigEntry<float> RatioTier2;
        public static ConfigEntry<float> RatioTier3;
        public static ConfigEntry<float> RatioTier4;
        public static ConfigEntry<float> RatioTier5;
        public static ConfigEntry<float> RatioTier6;

        public static ConfigEntry<bool> CleanupButton;

        public static Dictionary<string, ConfigEntry<bool>> ShipToggles = new Dictionary<string, ConfigEntry<bool>>();

        void Awake()
        {
            Instance = this;
            Log = Logger;
            Logger.LogInfo("Bailmod loaded with Config System");

            // General Config
            EnableCrewBailout = Config.Bind("General", "Enable Crew Bailout", true, "Enable the crew bailing out of land vehicles.");
            EnableNavalLiferafts = Config.Bind("General", "Enable Naval Liferafts", true, "Enable life rafts spawning from sinking ships.");
            MinVehicleMass = Config.Bind("Thresholds", "Minimum Vehicle Mass", 29999f, "Minimum mass for a ground vehicle to trigger a bailout.");

            // Mass Tiers
            MassTier0 = Config.Bind("Mass Tiers", "Minimum Naval Mass", 10000f, "Absolute minimum ship mass to trigger any drop.");
            MassTier1 = Config.Bind("Mass Tiers", "Mass for 1 Raft", 30000f, "Ship mass required for 1 raft.");
            MassTier2 = Config.Bind("Mass Tiers", "Mass for 2 Rafts", 1000000f, "Ship mass required for 2 rafts.");
            MassTier3 = Config.Bind("Mass Tiers", "Mass for 3 Rafts", 10000000f, "Ship mass required for 3 rafts.");
            MassTier4 = Config.Bind("Mass Tiers", "Mass for 4 Rafts", 20000000f, "Ship mass required for 4 rafts.");
            MassTier5 = Config.Bind("Mass Tiers", "Mass for 5 Rafts", 30000000f, "Ship mass required for 5 rafts.");
            MassTier6 = Config.Bind("Mass Tiers", "Mass for 6 Rafts", 40000000f, "Ship mass required for 6 rafts.");
            
            // Ratio Sliders (0 = All Pallets, 1 = All Containers)
            var ratioDesc = "Ratio of Containers vs Pallets (0.0 = 100% Pallets, 1.0 = 100% Containers)";
            RatioTier1 = Config.Bind("Raft Ratios", "Tier 1 Container Ratio", 0.0f, new ConfigDescription(ratioDesc, new AcceptableValueRange<float>(0f, 1f)));
            RatioTier2 = Config.Bind("Raft Ratios", "Tier 2 Container Ratio", 0.2f, new ConfigDescription(ratioDesc, new AcceptableValueRange<float>(0f, 1f)));
            RatioTier3 = Config.Bind("Raft Ratios", "Tier 3 Container Ratio", 0.4f, new ConfigDescription(ratioDesc, new AcceptableValueRange<float>(0f, 1f)));
            RatioTier4 = Config.Bind("Raft Ratios", "Tier 4 Container Ratio", 0.6f, new ConfigDescription(ratioDesc, new AcceptableValueRange<float>(0f, 1f)));
            RatioTier5 = Config.Bind("Raft Ratios", "Tier 5 Container Ratio", 0.8f, new ConfigDescription(ratioDesc, new AcceptableValueRange<float>(0f, 1f)));
            RatioTier6 = Config.Bind("Raft Ratios", "Tier 6 Container Ratio", 1.0f, new ConfigDescription(ratioDesc, new AcceptableValueRange<float>(0f, 1f)));

            CleanupButton = Config.Bind("Utilities", "Cleanup Wrecks and Pilots", false, new ConfigDescription("Clicking this (turning it ON) will instantly despawn all vehicle wrecks and pilots on the map to boost performance.", null, new ConfigurationManagerAttributes { HideDefaultButton = true }));
            CleanupButton.SettingChanged += (s, e) => { if (CleanupButton.Value) { CleanupWorld(); CleanupButton.Value = false; } };
            
            var harmony = new Harmony("com.bailmod");
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

            StartCoroutine(InitDynamicShipConfig());
        }

        static void CleanupWorld()
        {
            BailmodPlugin.Log.LogInfo("Bailmod: Cleaning up wrecks and pilots...");
            int wreckCount = 0;
            int pilotCount = 0;

            foreach (var unit in FindObjectsOfType<Unit>())
            {
                if (unit == null || unit.gameObject == null) continue;

                // Check if it's a ground vehicle wreck
                if (unit is GroundVehicle)
                {
                    if (unit.disabled)
                    {
                        Destroy(unit.gameObject);
                        wreckCount++;
                    }
                }
            }

            var allPilots = FindObjectsOfType<PilotDismounted>();
            foreach (var pilot in allPilots)
            {
                if (pilot != null && pilot.gameObject != null)
                {
                    Destroy(pilot.gameObject);
                    pilotCount++;
                }
            }

            BailmodPlugin.Log.LogInfo($"Bailmod: Cleanup complete. Removed {wreckCount} wrecks and {pilotCount} pilots.");
        }

        IEnumerator InitDynamicShipConfig()
        {
            // Wait for mods to load
            yield return new WaitForSeconds(5f);

            var allDefs = Resources.FindObjectsOfTypeAll<UnitDefinition>();
            foreach (var def in allDefs)
            {
                if (def == null || def.unitPrefab == null) continue;
                
                // If it has a Ship component, it's a ship
                if (def.unitPrefab.GetComponent<Ship>() != null)
                {
                    string key = def.unitName;
                    if (!ShipToggles.ContainsKey(key))
                    {
                        ShipToggles[key] = Config.Bind("Ship Whitelist", $"Enable {key}", true, $"Enable life rafts for {key}.");
                    }
                }
            }
            Logger.LogInfo($"Dynamic Ship Config Initialized: {ShipToggles.Count} ships found.");
        }

        [HarmonyPatch(typeof(GroundVehicle), "UnitDisabled")]
        [HarmonyPostfix]
        static void GroundVehicle_UnitDisabled_Postfix(GroundVehicle __instance, bool oldState, bool newState)
        {
            if (GameManager.gameState == GameState.Encyclopedia) return;
            if (!EnableCrewBailout.Value) return;

            if (!oldState && newState && __instance.IsServer && __instance.GetMass() > MinVehicleMass.Value)
            {
                DoSpawnBail(__instance);
            }
        }

        static void UnitPart_Detach_Prefix(UnitPart __instance)
        {
            if (GameManager.gameState == GameState.Encyclopedia) return;
            if (!EnableNavalLiferafts.Value) return;
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
            if (!EnableNavalLiferafts.Value) return;

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

            // Check if this specific ship is disabled in config
            if (ShipToggles.TryGetValue(ship.unitName, out var shipToggle) && !shipToggle.Value)
            {
                BailmodPlugin.Log.LogInfo($"Bailmod: Liferafts disabled for {ship.unitName} via config.");
                yield break;
            }

            UnitDefinition shipDef = ship.definition;
            float shipMass = shipDef.mass;
            float shipLength = shipDef.length;
            float shipWidth = shipDef.width;
            float shipHeight = shipDef.height;

            // Determine count based on config thresholds
            int count = 0;
            if (shipMass >= MassTier6.Value) count = 6;
            else if (shipMass >= MassTier5.Value) count = 5;
            else if (shipMass >= MassTier4.Value) count = 4;
            else if (shipMass >= MassTier3.Value) count = 3;
            else if (shipMass >= MassTier2.Value) count = 2;
            else if (shipMass >= MassTier1.Value) count = 1;
            else if (shipMass >= MassTier0.Value) count = 0; // Trigger logic check only
            else count = 0;

            if (count == 0) yield break;

            var allDefs = Resources.FindObjectsOfTypeAll<UnitDefinition>();
            UnitDefinition palletDef = allDefs.FirstOrDefault(d => d.name == "NavalPallet1");
            UnitDefinition containerDef = allDefs.FirstOrDefault(d => d.name == "NavalSupplyContainer1");

            // Fallbacks if exact names aren't found
            if (palletDef == null) palletDef = allDefs.FirstOrDefault(d => d.unitName.Contains("Pallet"));
            if (containerDef == null) containerDef = allDefs.FirstOrDefault(d => d.unitName.Contains("Container") && d.unitName.Contains("Naval"));

            if (palletDef == null && containerDef == null) yield break;

            // Get ratio for current tier
            float ratio = 0f;
            if (count == 6) ratio = RatioTier6.Value;
            else if (count == 5) ratio = RatioTier5.Value;
            else if (count == 4) ratio = RatioTier4.Value;
            else if (count == 3) ratio = RatioTier3.Value;
            else if (count == 2) ratio = RatioTier2.Value;
            else if (count == 1) ratio = RatioTier1.Value;

            System.Random rnd = new System.Random(Guid.NewGuid().GetHashCode());

            for (int i = 0; i < count; i++)
            {
                if (ship == null) yield break;

                // Select prefab based on ratio
                UnitDefinition selectedDef = (rnd.NextDouble() < ratio) ? containerDef : palletDef;
                if (selectedDef == null) selectedDef = (containerDef ?? palletDef); // Final safety fallback
                if (selectedDef == null || selectedDef.unitPrefab == null) continue;

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

    // Standard BepInEx ConfigurationManager class to support custom buttons
    public class ConfigurationManagerAttributes
    {
        public bool? HideDefaultButton;
        public Action<ConfigEntryBase> CustomDrawer;
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