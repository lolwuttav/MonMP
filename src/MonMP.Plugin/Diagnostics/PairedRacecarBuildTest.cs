using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using MonMP.Plugin.Testing;
using NWH.VehiclePhysics2;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Diagnostics
{
    // Uses isolated local-test saves to exercise the three Speedway reward callbacks and the
    // native RacecarBuild callback on the host after the carried-pack lease is released in range.
    // Invoke that callback explicitly: this fixture does not verify physics trigger delivery.
    // No native save is requested; every temporarily staged in-memory value is restored.
    internal sealed class PairedRacecarBuildTest
    {
        private static readonly ToDoList_Line[] RewardLines =
        {
            ToDoList_Line.Speedway_Racecar_Controls,
            ToDoList_Line.Speedway_Racecar_Wheels,
            ToDoList_Line.Speedway_Racecar_BodyKits
        };
        private static readonly string[] RewardFields = { "DragReward", "TrackReward", "OvalReward" };
        private static readonly string[] RewardTextFields = { "DragTitle", "DragBest", "TrackTitle", "TrackBest", "OvalTitle", "OvalBest" };
        private static readonly string[] BestTimeFields = { "Player_Best_Drag", "Player_Best_Track", "Player_Best_Oval" };
        private static readonly FieldInfo PacksField = AccessTools.Field(typeof(RacecarBuild), "PackInstalled");
        private static readonly FieldInfo VehicleField = AccessTools.Field(typeof(RacecarBuild), "vc");
        private static readonly FieldInfo EnableField = AccessTools.Field(typeof(RacecarBuild), "ToEnableWhenCompleted");
        private static readonly FieldInfo DisableField = AccessTools.Field(typeof(RacecarBuild), "ToDisableWhenCompleted");
        private static readonly FieldInfo PackTypeField = AccessTools.Field(typeof(Event_RacecarPack), "Pack_Type");
        private static readonly FieldInfo GearIndexField = AccessTools.Field(typeof(NWH.VehiclePhysics2.Powertrain.TransmissionComponent), "_currentGearIndex");
        private static readonly FieldInfo[] BestTimeFieldsInfo = BestTimeFields.Select(name => AccessTools.Field(typeof(Speedway), name)).ToArray();
        private static FixtureSnapshot? fixture;
        private static Harmony? achievementGuard;

        private sealed class FixtureSnapshot
        {
            public RacecarBuild Build = null!;
            public bool[] Installed = Array.Empty<bool>();
            public VehicleController Vehicle = null!;
            public Rigidbody Body = null!;
            public bool VehicleEnabled, VehicleObjectActive, BodyKinematic, RootActive;
            public int VehicleGear, IgnitionPosition;
            public CollisionDetectionMode Collision;
            public Vector3 Position, Velocity, AngularVelocity;
            public Quaternion Rotation;
            public Dictionary<GameObject, bool> Visuals = new Dictionary<GameObject, bool>();
            public Dictionary<GameObject, bool> Rewards = new Dictionary<GameObject, bool>();
            public Dictionary<Component, string> BoardTexts = new Dictionary<Component, string>();
            public Dictionary<GameObject, bool> PackObjects = new Dictionary<GameObject, bool>();
            public Speedway Speedway = null!;
            public float[][] BestTimes = Array.Empty<float[]>();
            public ToDoList_Manager Todo = null!;
            public Dictionary<ToDoList_Line, bool> Lines = new Dictionary<ToDoList_Line, bool>();
            public List<ToDoList_Object> Groups = new List<ToDoList_Object>();
            public bool[] GroupActive = Array.Empty<bool>();
            public GameObject? TodoPopup;
            public bool TodoPopupActive;
            public Transform Player = null!;
            public Rigidbody PlayerBody = null!;
            public Vector3 PlayerPosition, PlayerVelocity;
            public Quaternion PlayerRotation;
            public HashSet<int> OriginalPackIds = new HashSet<int>();
        }

        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly RacecarBuild build;
        private readonly Speedway_RacecarToDo rewardBoard;
        private readonly ToDoList_Manager todo;
        private readonly SharedItems items;
        private readonly ushort packPrefab;
        private readonly ulong[] packIds = new ulong[3];
        private readonly Vector3 triggerCenter;
        private readonly Vector3 packBase;
        private int phase, pack;
        private bool openedGarage;
        private string guestWait = "";
        private Vehicle_Nitrous_Controls? nitrous;
        private bool nitrousArmed;
        private int nitrousPower = -1;
        private float nitrousWait;
        private bool nosInputStaged, nosAutoInput;
        private VehicleController? nosStagedVehicle;
        private float nextNosEntry;
        private float nosLeaseSettledAfter;
        private bool nosHostObserved;
        private int nosIgnition;
        private float nosThrottle, nosRatio, nosCharge;
        private Vehicle_Nitrous_Controls? BoundNitrous() => UnityEngine.Object.FindObjectsOfType<Vehicle_Nitrous_Controls>(true)
            .FirstOrDefault(c => c.gameObject.scene.IsValid() && plugin.World!.Nitrous.TestBound(c) && c.isActiveAndEnabled);
        private string AllNitrous() => string.Join(" | ", UnityEngine.Object.FindObjectsOfType<Vehicle_Nitrous_Controls>(true)
            .Where(c => c.gameObject.scene.IsValid()).Select(c => plugin.World!.Nitrous.TestWhy(c)));
        private uint guest;
        private bool guestReady, guestAttemptedReward, cleaned;
        private float next, deadline, packDropAt;
        private Vector3 guestReadyPoint;

        internal string Status
        {
            get
            {
                var shared = SharedRacecarBuild.Current;
                byte todoMask = 0;
                for (int i = 0; i < RewardLines.Length; i++) if (todo.isDone(RewardLines[i])) todoMask |= (byte)(1 << i);
                var packState = pack < packIds.Length && packIds[pack] != 0 ? items.TestState(packIds[pack]) : null;
                var packItem = packState != null ? items.TestItem(packIds[pack]) : null;
                return "racecar phase=" + phase + "; pack=" + pack + "; shared=" +
                    (shared?.TestState.Installed.ToString("X1") ?? "none") + "; local=" +
                    (shared?.TestLocal.Installed.ToString("X1") ?? "none") + "; todo=" + todoMask.ToString("X1") +
                    "; guestReady=" + guestReady + "; rewardNoOp=" + guestAttemptedReward +
                    "; vehicleActive=" + (shared?.TestVehicleActive.ToString() ?? "none") +
                    "; peerCount=" + (plugin.Session?.Peers.Count.ToString() ?? "none") +
                    "; item=" + (packState == null ? "unbound" : packState.Exists + "/" + packState.Lease.Owner + "/" + packState.Lease.Generation) +
                    "; itemPosition=" + (packItem == null ? "none" : packItem.transform.position.ToString("F2")) +
                    "; receiver=" + triggerCenter.ToString("F2") + (phase >= 10 ? "; " + NosStatus : "");
            }
        }

        private string NosStatus
        {
            get
            {
                var world = plugin.World;
                if (world == null) return "Konig NOS world=disposed";
                var vc = world.Vehicles.TestKonigVehicle;
                var bottle = vc.vehicle_PartsManager.NitrousBottle;
                var mount = vc.vehicle_PartsManager.NitrousMount;
                var seat = vc.vehicle_PartsManager.Seat_Left;
                var seatControl = vc.GetComponentsInChildren<Konig_Seat>(true).FirstOrDefault(value => value.Get_isDriverSeat);
                var seatPart = seat.partInstalled == null ? null : seat.partInstalled.GetComponent<Part_Seat>();
                return "Konig NOS bottle=" + bottle.isInstalled + ",charge=" + (bottle.partInstalled == null ? "none" : bottle.partInstalled.Value1 + "/" + bottle.partInstalled.Value1_Max) +
                    ",armed=" + (nitrous == null ? "none" : SharedNitrous.TestModuleArmed(nitrous).ToString()) + ",power=" + (nitrous == null ? -1 : SharedNitrous.TestPower(nitrous)) +
                    ",mountSolid=" + mount.CheckIfAllBoltAre100Percent() + ",bottleSolid=" + bottle.CheckIfAllBoltAre100Percent() + ",chargeSeeded=" + world.VehicleTune.TestChargeSeeded(vc) +
                    ",seat=" + seat.isInstalled + "/" + seat.CheckIfAllBoltAre100Percent() + ",driver=" + world.Vehicles.DrivenBy(vc) +
                    ",player=" + gameplay.Get_PlayerState + ",holding=" + gameplay.Player.IsHoldingAnObject + ",boost=" + vc.input.Boost +
                    ",seatActive=" + (seatControl?.seatPosition?.gameObject.activeInHierarchy == true) + ",seatFolded=" + (seatPart == null ? "missing" : seatPart.Get_isFolded.ToString()) +
                    ",CanSeat=" + (seatControl == null ? "missing" : seatControl.CanSeat().ToString()) + ",seatSolidityGate=" + (!seat.isInstalled || seat.CheckIfSolidlyAttached()) +
                    ",doorsClosed=" + vc.isDriverDoorClosed + "/" + vc.isPassengerDoorClosed +
                    ",currentVehicle=" + (gameplay.Get_CurrentVehicleController == null ? "none" : gameplay.Get_CurrentVehicleController.name);
            }
        }

        internal PairedRacecarBuildTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            build = fixture?.Build ?? throw new InvalidOperationException("Racecar fixture state was not staged before session setup.");
            rewardBoard = Object.FindObjectsOfType<Speedway_RacecarToDo>(true)
                .SingleOrDefault(value => value.gameObject.scene.IsValid())
                ?? throw new InvalidOperationException("Racecar fixture cannot find the native Speedway reward board.");
            todo = Object.FindObjectOfType<ToDoList_Manager>() ?? throw new InvalidOperationException("Racecar fixture ToDo manager is missing.");
            items = plugin.World?.Items ?? throw new InvalidOperationException("Racecar fixture shared item service is missing.");
            var shared = SharedRacecarBuild.Current ?? throw new InvalidOperationException("Racecar build adapter is absent.");
            if (shared.TestNative != build || shared.TestLocal.Installed != 0 || shared.TestState.Installed != 0 ||
                shared.TestVehicleActive || !plugin.World!.Channel.FullySynchronized)
                throw new InvalidOperationException("Racecar fixture did not enter the expected unbuilt shared baseline: " + Status);
            var buildTrigger = build.GetComponents<Collider>().FirstOrDefault(collider => collider != null && collider.enabled && collider.isTrigger)
                ?? throw new InvalidOperationException("Racecar build trigger collider is missing.");
            triggerCenter = buildTrigger.bounds.center;
            packBase = triggerCenter + Vector3.right * Mathf.Max(4f, buildTrigger.bounds.extents.x + 2f);
            guestReadyPoint = packBase;
            var prefabCandidates = items.TestPrefabs(typeof(Event_RacecarPack));
            if (prefabCandidates.Length == 0 || PackTypeField == null)
                throw new InvalidOperationException("Racecar pack prefab or native pack-type field is missing.");
            packPrefab = prefabCandidates[0];
            deadline = Time.unscaledTime + 240;
            plugin.World!.Vehicles.TestEnableDriverEntryDiagnostics();
            if (plugin.TestOptions.IsHost)
                plugin.Session!.PoseReceived += OnPose;
        }

        // Runs independently in both isolated copies before SharedWorld captures its initial
        // snapshot. It normalizes any earlier Racecar completion and disables saved loose packs.
        internal static void PrepareIsolatedFixture()
        {
            if (!LocalTestIsolation.Installed || MonMPPlugin.Instance?.TestOptions.Enabled != true)
                throw new InvalidOperationException("Racecar fixture preparation requires isolated local test copies.");
            if (fixture != null) return;

            var build = Object.FindObjectsOfType<RacecarBuild>(true)
                .SingleOrDefault(value => value.gameObject.scene.IsValid());
            var vehicle = build == null ? null : VehicleField?.GetValue(build) as VehicleController;
            var body = vehicle?.vehicleRigidbody;
            var speedway = Object.FindObjectOfType<Speedway>();
            var board = Object.FindObjectsOfType<Speedway_RacecarToDo>(true)
                .SingleOrDefault(value => value.gameObject.scene.IsValid());
            var todo = Object.FindObjectOfType<ToDoList_Manager>();
            var player = Singleton<Gameplay>.i?.PlayerWalking;
            var playerBody = Singleton<Gameplay>.i?.Get_Player_Rigidbody;
            if (build == null || vehicle == null || body == null || speedway == null || board == null || todo == null ||
                player == null || playerBody == null || PacksField == null || EnableField == null || DisableField == null ||
                BestTimeFieldsInfo.Any(field => field == null) || GearIndexField == null)
                throw new InvalidOperationException("Racecar fixture is missing a native build, Speedway board/data, ToDo manager, player or expected field.");
            var installed = PacksField.GetValue(build) as bool[];
            if (installed == null || installed.Length != 3) throw new InvalidOperationException("Racecar native pack array is not three entries.");

            var snapshot = new FixtureSnapshot
            {
                Build = build, Installed = (bool[])installed.Clone(), Vehicle = vehicle, Body = body,
                VehicleEnabled = vehicle.enabled, VehicleObjectActive = vehicle.gameObject.activeSelf,
                // Gear's getter mutates an uninitialized inactive Racecar's index; preserve the backing value.
                VehicleGear = (int)GearIndexField.GetValue(vehicle.powertrain.transmission), IgnitionPosition = vehicle.Get_IgnitionPosition,
                BodyKinematic = body.isKinematic,
                Collision = body.collisionDetectionMode, RootActive = build.gameObject.activeSelf,
                Position = vehicle.transform.position, Rotation = vehicle.transform.rotation,
                Velocity = body.velocity, AngularVelocity = body.angularVelocity,
                Speedway = speedway, BestTimes = BestTimeFieldsInfo.Select(field => (float[])((float[])field!.GetValue(speedway)).Clone()).ToArray(),
                Todo = todo, Player = player.transform, PlayerBody = playerBody,
                PlayerPosition = player.transform.position, PlayerRotation = player.transform.rotation, PlayerVelocity = playerBody.velocity
            };
            foreach (var lineId in RewardLines)
            {
                if (!todo.Get_Lines.TryGetValue(lineId, out var line)) throw new InvalidOperationException("Racecar claim ToDo line is missing: " + lineId);
                snapshot.Lines.Add(lineId, line.value);
            }
            foreach (var fieldName in RewardFields)
            {
                var field = AccessTools.Field(typeof(Speedway_RacecarToDo), fieldName);
                var reward = field?.GetValue(board) as GameObject;
                if (reward == null) throw new InvalidOperationException("Racecar reward object is missing: " + fieldName);
                snapshot.Rewards[reward] = reward.activeSelf;
            }
            foreach (var fieldName in RewardTextFields)
            {
                var text = AccessTools.Field(typeof(Speedway_RacecarToDo), fieldName)?.GetValue(board) as Component;
                var textProperty = text == null ? null : AccessTools.Property(text.GetType(), "text");
                if (text == null || textProperty == null) throw new InvalidOperationException("Speedway reward board text reference is missing: " + fieldName);
                snapshot.BoardTexts[text] = textProperty.GetValue(text) as string
                    ?? throw new InvalidOperationException("Speedway reward board text is null: " + fieldName);
            }
            snapshot.Visuals = VisualObjects(build).Distinct().ToDictionary(value => value, value => value.activeSelf);
            var scenePacks = Object.FindObjectsOfType<Event_RacecarPack>(true)
                .Where(value => value != null && value.gameObject.scene.IsValid()).ToArray();
            foreach (var racecarPack in scenePacks)
            {
                snapshot.OriginalPackIds.Add(racecarPack.GetInstanceID());
                snapshot.PackObjects[racecarPack.gameObject] = racecarPack.gameObject.activeSelf;
            }
            var groups = (List<ToDoList_Object>)AccessTools.Field(typeof(ToDoList_Manager), "ToDoListsPerType").GetValue(todo);
            snapshot.Groups = groups.ToList(); snapshot.GroupActive = groups.Select(group => group.gameObject.activeSelf).ToArray();
            var popup = Object.FindObjectOfType<UI_FrontLeftPopup>(true);
            snapshot.TodoPopup = popup == null ? null : AccessTools.Field(typeof(UI_FrontLeftPopup), "ToDoList")?.GetValue(popup) as GameObject;
            snapshot.TodoPopupActive = snapshot.TodoPopup?.activeSelf == true;
            fixture = snapshot;

            InstallAchievementGuard();
            build.gameObject.SetActive(true);
            build.CancelInvoke("BuildRacecarNextFrame"); build.CancelInvoke("ActivateRacecar");
            body.isKinematic = true; body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.velocity = Vector3.zero; body.angularVelocity = Vector3.zero;
            vehicle.SetInputToPlayer(false); vehicle.enabled = false;
            foreach (var go in (GameObject[])EnableField.GetValue(build)) if (go != null) go.SetActive(false);
            foreach (var go in (GameObject[])DisableField.GetValue(build)) if (go != null) go.SetActive(true);
            foreach (var racecarPack in scenePacks) racecarPack.gameObject.SetActive(false);
            foreach (var reward in snapshot.Rewards.Keys) reward.SetActive(false);
            foreach (var lineId in RewardLines) { var line = todo.Get_Lines[lineId]; line.value = false; line.UpdateStrike(); }
            build.LoadFromSaveSystem(new bool[3]);
            foreach (var go in (GameObject[])EnableField.GetValue(build)) if (go != null) go.SetActive(false);
            foreach (var go in (GameObject[])DisableField.GetValue(build)) if (go != null) go.SetActive(true);
            SetEligibleBestTimes(speedway);

            var stagedInstalled = PacksField.GetValue(build) as bool[];
            if (build.gameObject.activeSelf != true || vehicle.enabled || !body.isKinematic ||
                stagedInstalled == null || stagedInstalled.Length != 3 || stagedInstalled.Any(value => value) ||
                RewardFields.Select(name => (GameObject)AccessTools.Field(typeof(Speedway_RacecarToDo), name).GetValue(board)).Any(value => value.activeSelf) ||
                RewardLines.Any(todo.isDone))
                throw new InvalidOperationException("Racecar fixture failed to stage an unbuilt, unclaimed baseline.");
        }

        private static IEnumerable<GameObject> VisualObjects(RacecarBuild racecar)
        {
            var details = racecar.PacksDetails ?? throw new InvalidOperationException("Racecar pack detail array is missing.");
            if (details.Length != 3) throw new InvalidOperationException("Racecar pack detail layout changed.");
            var enableField = AccessTools.Field(typeof(RacecarBuild.RaceCar_PacksDetails), "ToEnableOnceInstalled");
            var disableField = AccessTools.Field(typeof(RacecarBuild.RaceCar_PacksDetails), "ToDisableOnceInstalled");
            if (enableField == null || disableField == null) throw new InvalidOperationException("Racecar pack visual fields are missing.");
            foreach (var detail in details)
            {
                foreach (var value in (GameObject[]?)enableField.GetValue(detail) ?? Array.Empty<GameObject>()) if (value != null) yield return value;
                foreach (var value in (GameObject[]?)disableField.GetValue(detail) ?? Array.Empty<GameObject>()) if (value != null) yield return value;
            }
            foreach (var value in (GameObject[])EnableField.GetValue(racecar)) if (value != null) yield return value;
            foreach (var value in (GameObject[])DisableField.GetValue(racecar)) if (value != null) yield return value;
        }

        private static void SetEligibleBestTimes(Speedway speedway)
        {
            foreach (var field in BestTimeFieldsInfo)
            {
                var times = (float[])field!.GetValue(speedway);
                if (times.Length <= 13) throw new InvalidOperationException("Speedway best-time table no longer includes Konig index 13.");
                times[0] = 1f; times[13] = 1f;
            }
        }

        private static void InstallAchievementGuard()
        {
            if (achievementGuard != null) return;
            var target = AccessTools.Method(typeof(Steam_AchievementManager), nameof(Steam_AchievementManager.AchievementDone), new[] { typeof(AchievementType) });
            var prefix = typeof(PairedRacecarBuildTest).GetMethod(nameof(SuppressAchievement), BindingFlags.Static | BindingFlags.NonPublic);
            if (target == null || prefix == null) throw new InvalidOperationException("Could not guard Steam achievements in the isolated Racecar test.");
            achievementGuard = new Harmony("MonMP.LocalTest.RacecarAchievementGuard");
            achievementGuard.Patch(target, prefix: new HarmonyMethod(prefix));
            PatchHealth.VerifyManualPatchInstalled(achievementGuard, target);
        }

        private static bool SuppressAchievement(AchievementType __0) => false;

        internal static void RestoreIsolatedFixture()
        {
            var saved = fixture;
            if (saved == null) { achievementGuard?.UnpatchSelf(); achievementGuard = null; return; }
            try
            {
                saved.Build.CancelInvoke("BuildRacecarNextFrame"); saved.Build.CancelInvoke("ActivateRacecar");
                saved.Build.gameObject.SetActive(true);
                PacksField!.SetValue(saved.Build, (bool[])saved.Installed.Clone());
                foreach (var pair in saved.Visuals) if (pair.Key != null) pair.Key.SetActive(pair.Value);
                foreach (var pair in saved.Rewards) if (pair.Key != null) pair.Key.SetActive(pair.Value);
                foreach (var pair in saved.BoardTexts)
                    if (pair.Key != null) AccessTools.Property(pair.Key.GetType(), "text")?.SetValue(pair.Key, pair.Value);
                foreach (var pair in saved.PackObjects) if (pair.Key != null) pair.Key.SetActive(pair.Value);
                for (int i = 0; i < RewardLines.Length; i++)
                { var line = saved.Todo.Get_Lines[RewardLines[i]]; line.value = saved.Lines[RewardLines[i]]; line.UpdateStrike(); }
                for (int i = 0; i < saved.Groups.Count; i++) if (saved.Groups[i] != null) saved.Groups[i].gameObject.SetActive(saved.GroupActive[i]);
                if (saved.TodoPopup != null) saved.TodoPopup.SetActive(saved.TodoPopupActive);
                for (int i = 0; i < BestTimeFieldsInfo.Length; i++)
                {
                    var current = (float[])BestTimeFieldsInfo[i]!.GetValue(saved.Speedway);
                    Array.Copy(saved.BestTimes[i], current, Math.Min(current.Length, saved.BestTimes[i].Length));
                }
                var newPacks = Object.FindObjectsOfType<Event_RacecarPack>(true)
                    .Where(value => value != null && value.gameObject.scene.IsValid() && !saved.OriginalPackIds.Contains(value.GetInstanceID())).ToArray();
                foreach (var racecarPack in newPacks) { racecarPack.gameObject.SetActive(false); Object.Destroy(racecarPack.gameObject); }
                saved.Vehicle.transform.SetPositionAndRotation(saved.Position, saved.Rotation);
                saved.Body.isKinematic = saved.BodyKinematic;
                saved.Body.collisionDetectionMode = saved.Collision;
                saved.Body.velocity = saved.Velocity; saved.Body.angularVelocity = saved.AngularVelocity;
                saved.Vehicle.gameObject.SetActive(true);
                saved.Vehicle.Get_IgnitionPosition = saved.IgnitionPosition;
                GearIndexField.SetValue(saved.Vehicle.powertrain.transmission, saved.VehicleGear);
                saved.Vehicle.enabled = saved.VehicleEnabled; saved.Vehicle.SetInputToPlayer(saved.VehicleEnabled);
                saved.Vehicle.gameObject.SetActive(saved.VehicleObjectActive);
                saved.Build.gameObject.SetActive(saved.RootActive);
                saved.Player.SetPositionAndRotation(saved.PlayerPosition, saved.PlayerRotation);
                // CMF destroys the walking rigidbody on driver entry and creates a replacement on
                // exit. Restore the replacement rather than the snapshot's destroyed native handle.
                saved.PlayerBody = saved.Player.GetComponent<Rigidbody>();
                if (saved.PlayerBody == null)
                    throw new InvalidOperationException("Walking player rigidbody was not recreated before fixture restore.");
                saved.PlayerBody.position = saved.PlayerPosition; saved.PlayerBody.velocity = saved.PlayerVelocity;

                var restoredPacks = PacksField.GetValue(saved.Build) as bool[];
                var checks = new Dictionary<string, bool>
                {
                    ["packs"] = restoredPacks != null && restoredPacks.SequenceEqual(saved.Installed),
                    ["vehicle enabled"] = saved.Vehicle.enabled == saved.VehicleEnabled,
                    ["kinematic"] = saved.Body.isKinematic == saved.BodyKinematic,
                    ["vehicle active"] = saved.Vehicle.gameObject.activeSelf == saved.VehicleObjectActive,
                    ["gear"] = (int)GearIndexField.GetValue(saved.Vehicle.powertrain.transmission) == saved.VehicleGear,
                    ["ignition"] = saved.Vehicle.Get_IgnitionPosition == saved.IgnitionPosition,
                    ["collision"] = saved.Body.collisionDetectionMode == saved.Collision,
                    ["build active"] = saved.Build.gameObject.activeSelf == saved.RootActive,
                    ["vehicle position"] = (saved.Vehicle.transform.position - saved.Position).sqrMagnitude < 0.0001f,
                    ["vehicle rotation"] = Quaternion.Angle(saved.Vehicle.transform.rotation, saved.Rotation) < 0.1f,
                    ["vehicle velocity"] = saved.BodyKinematic || ((saved.Body.velocity - saved.Velocity).sqrMagnitude < 0.0001f &&
                        (saved.Body.angularVelocity - saved.AngularVelocity).sqrMagnitude < 0.0001f),
                    ["todo"] = RewardLines.All(line => saved.Todo.Get_Lines[line].value == saved.Lines[line]),
                    ["rewards"] = saved.Rewards.All(pair => pair.Key == null || pair.Key.activeSelf == pair.Value),
                    ["board text"] = saved.BoardTexts.All(pair => pair.Key == null || (AccessTools.Property(pair.Key.GetType(), "text")!.GetValue(pair.Key) as string) == pair.Value),
                    ["original packs"] = saved.PackObjects.All(pair => pair.Key == null || pair.Key.activeSelf == pair.Value),
                    ["visuals"] = saved.Visuals.All(pair => pair.Key == null || pair.Key.activeSelf == pair.Value),
                    ["groups"] = saved.Groups.Select((group, index) => group == null || group.gameObject.activeSelf == saved.GroupActive[index]).All(value => value),
                    ["popup"] = saved.TodoPopup == null || saved.TodoPopup.activeSelf == saved.TodoPopupActive,
                    ["player position"] = (saved.Player.position - saved.PlayerPosition).sqrMagnitude < 0.0001f,
                    ["player rotation"] = Quaternion.Angle(saved.Player.rotation, saved.PlayerRotation) < 0.1f,
                    ["player body position"] = (saved.PlayerBody.position - saved.PlayerPosition).sqrMagnitude < 0.0001f,
                    ["player velocity"] = (saved.PlayerBody.velocity - saved.PlayerVelocity).sqrMagnitude < 0.0001f,
                    ["best times"] = BestTimeFieldsInfo.Select((field, index) => ((float[])field!.GetValue(saved.Speedway)).SequenceEqual(saved.BestTimes[index])).All(value => value)
                };
                if (checks.Any(pair => !pair.Value))
                    throw new InvalidOperationException("Racecar fixture restore check failed: " + string.Join(", ", checks.Where(pair => !pair.Value).Select(pair => pair.Key)));
            }
            finally
            {
                fixture = null;
                achievementGuard?.UnpatchSelf(); achievementGuard = null;
            }
        }

        private void OnPose(uint peer, PlayerPose pose)
        {
            if (peer != guest || pose.State != (byte)PlayerState.Walking) return;
            var remote = new Vector3(pose.X, pose.Y, pose.Z);
            if ((remote - guestReadyPoint).sqrMagnitude <= 9f) guestReady = true;
        }

        internal bool Tick()
        {
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Racecar paired fixture timed out: " + Status);
            var world = plugin.World;
            if (world == null) throw new InvalidOperationException("Racecar/Konig NOS fixture lost its shared world at phase=" + phase);
            if (!world.Channel.FullySynchronized || !items.Ready || Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                guest = plugin.Session.Peers.Keys.Single(id => id != 0);
                if (!guestReady) return false;
                // Invoke the game's exact Speedway click handler in the same order it uses for
                // Drag, Track and Oval; the best-time fields were staged in the isolated copy.
                for (int i = 0; i < 3; i++)
                {
                    rewardBoard.MouseDown();
                    Require(todo.isDone(RewardLines[i]), "native Speedway click did not claim " + RewardLines[i] + ".");
                    var reward = AccessTools.Field(typeof(Speedway_RacecarToDo), RewardFields[i]).GetValue(rewardBoard) as GameObject;
                    Require(reward != null && reward.activeSelf, "native Speedway click did not enable " + RewardFields[i] + ".");
                }
                Require(RewardLines.All(todo.isDone), "native Speedway reward sequence did not finish all three claims.");
                for (int i = 0; i < 3; i++)
                {
                    float scale = 0.86f + i * 0.01f;
                    var item = items.TestSpawn(packBase + Vector3.forward * (i * 1.1f) + Vector3.up * 1.2f, packPrefab);
                    Require(item is Event_RacecarPack, "shared prefab factory did not create a Racecar pack.");
                    PackTypeField!.SetValue(item, i);
                    item.transform.localScale = Vector3.one * scale;
                    item.isSpawnFromTheSaveLoadSystem = true; item.CreateBasics();
                    item.IntValue1 = i;
                    if (item.rb != null) item.rb.isKinematic = true;
                }
                phase = 1; next = Time.unscaledTime + 1.5f;
                return false;
            }
            if (phase == 1)
            {
                for (int i = 0; i < 3; i++) packIds[i] = items.TestTaggedItem(packPrefab, 0.86f + i * 0.01f);
                if (packIds.Any(id => id == 0)) return false;
                for (int i = 0; i < 3; i++)
                    Require(items.TestState(packIds[i]).Values.IntValue1 == i, "shared pack item values lost native pack index " + i + ".");
                phase = 2;
            }
            if (phase == 2)
            {
                if (pack < 3 && !SharedRacecarBuild.Current!.TestState.Has(pack))
                {
                    var drop = items.TestState(packIds[pack]);
                    // A completed grab/drop can arrive between fixture samples; its two lease advances persist.
                    if (drop.Lease.Generation >= 3 && drop.Exists && drop.Lease.Owner == ItemLease.Unowned)
                    {
                        var input = items.TestItem(packIds[pack]);
                        Require(input is Event_RacecarPack && input.itemCollider != null &&
                            Vector3.Distance(input.transform.position, triggerCenter) < 0.5f,
                            "accepted guest pack drop did not reach the native receiver: " + Status);
                        // Exercise the native completion/achievement callback with a validated dropped input (#144).
                        AccessTools.Method(typeof(RacecarBuild), "OnTriggerEnter", new[] { typeof(Collider) })!
                            .Invoke(build, new object[] { input!.itemCollider! });
                        Require(SharedRacecarBuild.Current.TestLocal.Has(pack), "native callback did not install the released pack.");
                    }
                }
                if (pack < 3 && SharedRacecarBuild.Current!.TestState.Has(pack) && !items.TestState(packIds[pack]).Exists)
                {
                    Require(SharedRacecarBuild.Current.TestLocal.Has(pack), "host native pack callback did not set pack bit " + pack + ".");
                    Require(SharedRacecarBuild.Current.TestState.Installed == (1 << (pack + 1)) - 1,
                        "host pack installation was duplicated or arrived out of order.");
                    pack++;
                }
                if (pack < 3) return false;
                if (!SharedRacecarBuild.Current!.TestVehicleActive) return false;
                // The built racecar is a shared vehicle: one driver, seated players posed on its seat.
                // It starts stored behind the Speedway garage door, bound dormant; opening the door
                // takes it out natively, and only then does the binding go live on both sides.
                if (plugin.World!.Vehicles.TestRacecarLive != true)
                {
                    Require(plugin.World.Vehicles.TestRacecarBound, "host did not bind the built racecar.");
                    if (!openedGarage && !SharedRacecarBuild.Current.TestVehicle!.gameObject.activeInHierarchy)
                    {
                        var garages = plugin.World.StorageGarages;
                        ulong racecar = plugin.World.Vehicles.EntityOf(SharedRacecarBuild.Current.TestVehicle);
                        ulong door = garages.TestDoors.FirstOrDefault(id => garages.TestRecord(id) is { Stored: true } r && r.StoredVehicle == racecar);
                        Require(racecar != 0 && door != 0, "host found no storage door holding the built racecar.");
                        if (garages.TestMoving(door)) return false;
                        garages.TestDoor(door).Door_Open();
                        openedGarage = true;
                        Debug.Log("MonMP racecar test opened the Speedway garage holding the racecar");
                    }
                    return false;
                }
                Debug.Log("MonMP racecar bound as a shared vehicle on the host");
                Require(SharedRacecarBuild.Current.TestState.Complete && SharedRacecarBuild.Current.TestLocal.Complete,
                    "host Racecar activation preceded the three-pack native state.");
                // Nitrous controls: the host flips the native arm switch and power button, then waits
                // for the guest's power-button click to come back through the host.
                // The controls ride on the nitrous mount part. The fixture racecar has none, and its
                // parts are not shared, so the Konig's shared mount socket carries them instead.
                var raceControls = SharedRacecarBuild.Current.TestVehicle!.GetComponentInChildren<Vehicle_Nitrous_Controls>(true);
                if (raceControls == null && nitrousWait == 0)
                {
                    var (socket, already) = plugin.World.VehicleParts.TestEnsureInstalled(Car_PartsType.NitrousMount);
                    Debug.Log("MonMP racecar test host uses the Konig nitrous mount " + socket.ToString("X") + " (already installed=" + already + ")");
                    // The controls live on the Konig and wake with the mount; their native Start finds the NOS module.
                    nitrousWait = Time.unscaledTime + 10; next = Time.unscaledTime + 0.5f;
                    return false;
                }
                nitrous = BoundNitrous();
                if (nitrous == null)
                { if (nitrousWait == 0) nitrousWait = Time.unscaledTime + 10; Require(Time.unscaledTime < nitrousWait, "host has no shared nitrous controls: racecar=" + (raceControls == null ? "none" : plugin.World.Nitrous.TestWhy(raceControls)) + " all=" + AllNitrous()); return false; }
                nitrousArmed = !SharedNitrous.TestSwitch(nitrous!);
                SharedNitrous.TestSwitchButton(nitrous!).MouseDown();
                Require(SharedNitrous.TestSwitch(nitrous!) == nitrousArmed && SharedNitrous.TestModuleArmed(nitrous!) == nitrousArmed,
                    "host native nitrous arm switch did not change the NOS module.");
                nitrous!.Power_Button_Toggle();
                nitrousPower = SharedNitrous.TestPower(nitrous);
                Debug.Log("MonMP racecar test host set nitrous armed=" + nitrousArmed + " power=" + nitrousPower);
                phase = 10;
                return false;
            }
            if (phase == 10)
            {
                if (SharedNitrous.TestPower(nitrous!) != (nitrousPower + 1) % 3) return false;
                Require(SharedNitrous.TestSwitch(nitrous!) == nitrousArmed, "guest nitrous power click changed the host arm switch.");
                Debug.Log("MonMP racecar test host applied the guest's nitrous power click");
                phase = 18; next = Time.unscaledTime + 1f;
                return false;
            }
            if (phase == 18)
            {
                plugin.World!.VehicleParts.TestEnsureInstalled(Car_PartsType.NitrousBottle);
                var vc = plugin.World.Vehicles.TestKonigVehicle;
                TightenNosSocket(vc.vehicle_PartsManager.NitrousMount);
                TightenNosSocket(vc.vehicle_PartsManager.NitrousBottle);
                EnsureKonigNosSeat(vc);
                var bottle = vc.vehicle_PartsManager.NitrousBottle.partInstalled;
                bottle.Value1 = bottle.Value1_Max;
                // Full power makes the native modifier's >1 assertion independent of button level.
                while (SharedNitrous.TestPower(nitrous!) != 2) nitrous!.Power_Button_Toggle();
                if (!SharedNitrous.TestSwitch(nitrous!)) SharedNitrous.TestSwitchButton(nitrous!).MouseDown();
                var doors = plugin.World.Vehicles.Doors;
                MoveNear(doors.TestKonigPosition());
                if (!doors.TestKonigState().Open) doors.TestKonigClick();
                phase = 20; nitrousWait = Time.unscaledTime + 20; next = Time.unscaledTime + 0.7f;
                return false;
            }
            if (phase == 20)
            {
                var vc = plugin.World!.Vehicles.TestKonigVehicle;
                if (!plugin.World.Vehicles.Doors.TestKonigState().Open || vc.isDriverDoorClosed)
                {
                    Require(Time.unscaledTime < nitrousWait, "Konig NOS native driver door did not open: " + NosStatus);
                    return false;
                }
                Debug.Log("MonMP Konig NOS host staged native entry and filled bottle: " + NosStatus);
                phase = 11;
                return false;
            }
            if (phase == 11)
            {
                Require(plugin.Session?.Peers.ContainsKey(guest) == true, "Konig NOS guest departed before usage/consumption acceptance: " + NosStatus);
                var vc = plugin.World!.Vehicles.TestKonigVehicle;
                var module = vc.GetComponent<NWH.VehiclePhysics2.Modules.NOS.NOSModuleWrapper>().module;
                if (plugin.World.Vehicles.DrivenBy(vc) != guest || !vc.input.Boost || module.soundComponent.Source?.isPlaying != true) return false;
                var bottle = vc.vehicle_PartsManager.NitrousBottle.partInstalled;
                if (bottle.Value1 >= bottle.Value1_Max - 0.01f) return false;
                Debug.Log("MonMP Konig NOS host observed guest native consumption and remote loop; charge=" + bottle.Value1);
                // Native power level 1 is the explicit observer acknowledgement. The guest must see
                // this before level 2 can mean that the host really performed the refill below.
                while (SharedNitrous.TestPower(nitrous!) != 1) nitrous!.Power_Button_Toggle();
                // Let the drained host record reach the guest before the independent refill.
                phase = 19; next = Time.unscaledTime + 0.7f;
                return false;
            }
            if (phase == 19)
            {
                var vc = plugin.World!.Vehicles.TestKonigVehicle;
                Require(plugin.Session?.Peers.ContainsKey(guest) == true && plugin.World.Vehicles.DrivenBy(vc) == guest,
                    "Konig NOS driver left before the host refill handshake.");
                if (vc.input.Boost || vc.GetComponent<NWH.VehiclePhysics2.Modules.NOS.NOSModuleWrapper>().module.soundComponent.Source?.isPlaying == true) return false;
                var bottle = vc.vehicle_PartsManager.NitrousBottle.partInstalled;
                bottle.Value1 = bottle.Value1_Max;
                plugin.World.VehicleTune.TestStaleDriverDebitAfterRefill(vc, guest);
                Debug.Log("MonMP Konig NOS host rejected stale driver debit against unpublished native refill and published full charge=" + bottle.Value1);
                while (SharedNitrous.TestPower(nitrous!) != 2) nitrous!.Power_Button_Toggle();
                Debug.Log("MonMP Konig NOS host actually refilled after observing guest usage/debit; charge=" + bottle.Value1);
                phase = 12;
                return false;
            }
            if (phase == 12)
            {
                var vc = plugin.World!.Vehicles.TestKonigVehicle;
                if (plugin.World.Vehicles.DrivenBy(vc) != ItemLease.Unowned || vc.input.Boost) return false;
                Require(vc.GetComponent<NWH.VehiclePhysics2.Modules.NOS.NOSModuleWrapper>().module.soundComponent.Source?.isPlaying != true,
                    "remote NOS loop did not stop after guest released boost and exited.");
                phase = 3;
            }
            if (phase == 3)
            {
                // Guest has to observe the host's final build snapshot before disconnecting.
                if (plugin.Session!.Peers.Count > 1) return false;
                plugin.Session.PoseReceived -= OnPose;
                plugin.EndPairedTestSession();
                RestoreIsolatedFixture(); cleaned = true;
                return true;
            }
            return false;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                SetEligibleBestTimes(fixture!.Speedway);
                Require(RewardLines.All(line => !todo.isDone(line)), "guest fixture has a previously claimed reward line.");
                rewardBoard.MouseDown();
                Require(RewardLines.All(line => !todo.isDone(line)), "guest Speedway click changed a host-owned reward line.");
                Require(RewardFields.All(name => !(AccessTools.Field(typeof(Speedway_RacecarToDo), name).GetValue(rewardBoard) as GameObject)!.activeSelf),
                    "guest Speedway click enabled a local race reward.");
                guestAttemptedReward = true;
                MoveNear(guestReadyPoint);
                phase = 1;
            }
            if (phase == 1)
            {
                if (!RewardLines.All(todo.isDone)) return false;
                Require(guestAttemptedReward, "guest did not exercise the guarded Speedway callback.");
                phase = 2;
            }
            if (phase == 2)
            {
                for (int i = 0; i < 3; i++)
                    if (packIds[i] == 0) packIds[i] = items.TestTaggedItem(packPrefab, 0.86f + i * 0.01f);
                if (pack == 3)
                {
                    var built = SharedRacecarBuild.Current!;
                    string wait = "complete=" + built.TestState.Complete + "/" + built.TestLocal.Complete + " synced=" + plugin.World!.Channel.FullySynchronized +
                        " active=" + built.TestVehicleActive + " stored=" + (built.TestVehicle != null && !built.TestVehicle.gameObject.activeInHierarchy) +
                        " bound=" + plugin.World.Vehicles.TestRacecarBound + " live=" + plugin.World.Vehicles.TestRacecarLive;
                    if (wait != guestWait) { guestWait = wait; Debug.Log("MonMP racecar guest waits: " + wait); }
                    // Live on the guest, its copy follows the host's physics (a kinematic proxy), so the
                    // native "vehicle active" state is the host's to check.
                    if (!built.TestState.Complete || !built.TestLocal.Complete || !plugin.World!.Channel.FullySynchronized)
                        return false;
                    if (plugin.World.Vehicles.TestRacecarLive != true) return false;
                    Debug.Log("MonMP racecar bound as a shared vehicle on the guest");
                    for (int i = 0; i < 3; i++)
                        Require(packIds[i] != 0 && !items.TestState(packIds[i]).Exists,
                            "guest still has a loose pack after host installation, index " + i + ".");
                    // The host picks the racecar's mount, or the Konig's shared one; the controls bind on the next scan.
                    nitrous = BoundNitrous();
                    if (nitrous == null)
                    { if (nitrousWait == 0) nitrousWait = Time.unscaledTime + 15; Require(Time.unscaledTime < nitrousWait, "guest has no shared nitrous controls: " + AllNitrous()); return false; }
                    // Both peers start from the same fixture; the host flips the arm switch and steps the power.
                    nitrousArmed = !SharedNitrous.TestSwitch(nitrous!);
                    nitrousPower = (SharedNitrous.TestPower(nitrous!) + 1) % 3;
                    phase = 10;
                    return false;
                }
                if (packIds[pack] == 0 || items.TestItem(packIds[pack]) == null) return false;
                var loose = items.TestItem(packIds[pack])!;
                Require(items.TestState(packIds[pack]).Values.IntValue1 == pack,
                    "replicated pack record has the wrong native index " + pack + ".");
                MoveNear(loose.transform.position + Vector3.forward * 0.5f);
                phase = 3; next = Time.unscaledTime + 0.4f;
                return false;
            }
            if (phase == 10)
            {
                if (SharedNitrous.TestSwitch(nitrous!) != nitrousArmed || SharedNitrous.TestPower(nitrous!) != nitrousPower) return false;
                Require(SharedNitrous.TestModuleArmed(nitrous!) == nitrousArmed, "guest NOS module did not follow the host's arm switch.");
                Debug.Log("MonMP racecar test guest mirrored nitrous armed=" + nitrousArmed + " power=" + nitrousPower);
                MoveNear(nitrous!.transform.position + Vector3.up * 0.5f);
                phase = 11; next = Time.unscaledTime + 0.5f;
                return false;
            }
            if (phase == 11)
            {
                nitrous!.Power_Button_Toggle();
                Require(SharedNitrous.TestPower(nitrous) == nitrousPower, "guest nitrous power click ran natively instead of going to the host.");
                nitrousPower = (nitrousPower + 1) % 3;
                phase = 12;
                return false;
            }
            if (phase == 12)
            {
                if (SharedNitrous.TestPower(nitrous!) != nitrousPower) return false;
                Require(SharedNitrous.TestSwitch(nitrous!) == nitrousArmed, "guest nitrous arm switch drifted after the power click.");
                Debug.Log("MonMP racecar test guest saw its nitrous power click applied by the host");
                phase = 13;
                return false;
            }
            if (phase == 13)
            {
                var vc = plugin.World!.Vehicles.TestKonigVehicle;
                var bottle = vc.vehicle_PartsManager.NitrousBottle;
                var seatControl = vc.GetComponentsInChildren<Konig_Seat>(true).FirstOrDefault(value => value.Get_isDriverSeat);
                if (!bottle.isInstalled || bottle.partInstalled == null || bottle.partInstalled.Value1 < bottle.partInstalled.Value1_Max - 0.001f ||
                    !SharedNitrous.TestModuleArmed(nitrous!).GetValueOrDefault() || SharedNitrous.TestPower(nitrous!) != 2 ||
                    !vc.vehicle_PartsManager.Seat_Left.CheckIfAllBoltAre100Percent() || !vc.vehicle_PartsManager.NitrousMount.CheckIfAllBoltAre100Percent() ||
                    !bottle.CheckIfAllBoltAre100Percent() || seatControl == null || !seatControl.CanSeat() ||
                    !seatControl.seatPosition.gameObject.activeInHierarchy || vc.isDriverDoorClosed) return false;
                MoveNear(plugin.World.Vehicles.TestKonigDriverPosition + Vector3.right * 1.5f);
                Debug.Log("MonMP Konig NOS guest requests native driver entry: " + NosStatus);
                plugin.World.Vehicles.TestKonigEnterDriver();
                nitrousWait = Time.unscaledTime + 20; nextNosEntry = Time.unscaledTime + 0.7f;
                phase = 14;
                return false;
            }
            if (phase == 14)
            {
                var vc = plugin.World!.Vehicles.TestKonigVehicle;
                if (plugin.World.Vehicles.DrivenBy(vc) != plugin.Session!.LocalPeerId || gameplay.Get_CurrentVehicleController != vc)
                {
                    Require(Time.unscaledTime < nitrousWait, "Konig NOS native driver entry did not complete: " + NosStatus);
                    if (Time.unscaledTime >= nextNosEntry && gameplay.Get_PlayerState == PlayerState.Walking)
                    {
                        MoveNear(plugin.World.Vehicles.TestKonigDriverPosition + Vector3.right * 1.5f);
                        plugin.World.Vehicles.TestKonigEnterDriver(); nextNosEntry = Time.unscaledTime + 0.7f;
                    }
                    return false;
                }
                if (nosLeaseSettledAfter == 0) { nosLeaseSettledAfter = Time.unscaledTime + 0.7f; return false; }
                if (Time.unscaledTime < nosLeaseSettledAfter || !plugin.World.VehicleTune.TestChargeSeeded(vc)) return false;
                var module = vc.GetComponent<NWH.VehiclePhysics2.Modules.NOS.NOSModuleWrapper>().module;
                nosAutoInput = vc.input.autoSetInput; nosIgnition = vc.IgnitionPosition;
                nosThrottle = vc.powertrain.engine.ThrottlePosition; nosRatio = vc.powertrain.transmission.ratio;
                nosInputStaged = true;
                nosStagedVehicle = vc;
                vc.input.autoSetInput = false; vc.IgnitionPosition = 1; vc.input.Boost = true;
                vc.powertrain.engine.ThrottlePosition = 0.5f; vc.powertrain.transmission.ratio = 1f;
                nosCharge = module.charge;
                float factor = 1;
                // Exercise the native modifier directly with a parked engine: validates its gates,
                // charge debit and multiplier, while avoiding a driving/acceleration fixture.
                for (int i = 0; i < 50; i++) factor = module.NOSPowerModifier();
                Require(module.IsBeingUsed && factor > 1f && module.charge < nosCharge - 0.01f,
                    "Konig guest native NOS modifier did not increase power and consume charge: factor=" + factor +
                    ",moduleActive=" + module.Active + ",armed=" + module.isArmed + ",using=" + module.IsBeingUsed +
                    ",charge=" + nosCharge + "->" + module.charge + ",bottle=" + vc.vehicle_PartsManager.NitrousBottle.isInstalled +
                    ",mount=" + vc.vehicle_PartsManager.NitrousMount.isInstalled + ",boost=" + vc.input.Boost +
                    ",ignition=" + vc.IgnitionPosition + ",ratio=" + vc.powertrain.transmission.Ratio + ",throttle=" + vc.powertrain.engine.ThrottlePosition +
                    ",powerCoefficient=" + module.powerCoefficient + ",buttonPower=" + SharedNitrous.TestPower(nitrous!));
                // Reproduce the critical interleave synchronously: a host tune echo arrives after
                // this one-shot debit but before the next guest reporting cadence. The adapter must
                // retain both the local charge and the outstanding debit until actual host receipt.
                plugin.World.VehicleTune.TestReapplyHostRecord(vc);
                Require(module.charge < nosCharge - 0.01f, "Konig NOS one-shot debit was overwritten by the current host tune record.");
                Debug.Log("MonMP Konig NOS guest native factor=" + factor + " charge=" + nosCharge + "->" + module.charge);
                phase = 15; next = Time.unscaledTime + 0.5f;
                return false;
            }
            if (phase == 15)
            {
                var vc = plugin.World!.Vehicles.TestKonigVehicle;
                var module = vc.GetComponent<NWH.VehiclePhysics2.Modules.NOS.NOSModuleWrapper>().module;
                int power = SharedNitrous.TestPower(nitrous!);
                if (!nosHostObserved)
                {
                    Require(module.soundComponent.Source?.isPlaying == true, "guest local NOS loop did not play.");
                    if (power != 1) return false;
                    nosHostObserved = true;
                    Debug.Log("MonMP Konig NOS guest received host acknowledgement of the one-shot debit and remote loop.");
                    // Native engine simulation can continue consuming while Boost is held. Release
                    // it after observation, keeping the real driver lease through the host refill.
                    RestoreNosInput();
                    return false;
                }
                if (power != 2) return false;
                if (module.charge < nosCharge - 0.001f) return false;
                Require(!vc.input.Boost && module.soundComponent.Source?.isPlaying != true,
                    "Konig NOS local loop did not stop before accepting the host refill.");
                Debug.Log("MonMP Konig NOS guest accepted host refill while holding driver lease; charge=" + module.charge);
                phase = 16; next = Time.unscaledTime + 0.5f;
                return false;
            }
            if (phase == 16)
            {
                var vc = plugin.World!.Vehicles.TestKonigVehicle;
                Require(vc.GetComponent<NWH.VehiclePhysics2.Modules.NOS.NOSModuleWrapper>().module.soundComponent.Source?.isPlaying != true,
                    "guest local NOS loop did not stop on boost release.");
                plugin.World.Vehicles.TestKonigExitDriver();
                phase = 17; next = Time.unscaledTime + 0.5f;
                return false;
            }
            if (phase == 17)
            {
                if (plugin.World!.Vehicles.DrivenBy(plugin.World.Vehicles.TestKonigVehicle) == plugin.Session!.LocalPeerId) return false;
                plugin.EndPairedTestSession();
                RestoreIsolatedFixture(); cleaned = true;
                return true;
            }
            if (phase == 3)
            {
                items.TestGrab(packIds[pack]); phase = 4;
            }
            else if (phase == 4 && items.TestState(packIds[pack]).Lease.Owner == plugin.Session!.LocalPeerId)
            {
                Require(gameplay.Player.Get_HoldingItem == items.TestItem(packIds[pack]), "guest did not carry Racecar pack " + pack + ".");
                MoveNear(triggerCenter + Vector3.forward * 1.5f);
                phase = 5; next = Time.unscaledTime + 0.4f;
            }
            else if (phase == 5)
            {
                var item = items.TestItem(packIds[pack]);
                Require(item is Event_RacecarPack, "guest carried Racecar pack disappeared before drop.");
                item!.transform.position = triggerCenter;
                items.TestDrop(false);
                phase = 6; packDropAt = Time.unscaledTime;
            }
            else if (phase == 6 && SharedRacecarBuild.Current!.TestState.Has(pack) && !items.TestState(packIds[pack]).Exists)
            {
                Require(SharedRacecarBuild.Current.TestLocal.Has(pack), "guest build presentation did not follow pack " + pack + ".");
                pack++; phase = 2; next = Time.unscaledTime + 0.25f;
            }
            else if (phase == 6 && Time.unscaledTime - packDropAt > 8f)
                throw new InvalidOperationException("Racecar fixture dropped pack did not reach its native receiver: " + Status);
            if (phase == 2)
            {
                var shared = SharedRacecarBuild.Current!;
                if (!RewardLines.All(todo.isDone) || shared.TestState.Installed > RacecarBuildState.CompleteMask)
                    throw new InvalidOperationException("Guest Racecar test left the expected replicated state: " + Status);
            }
            return false;
        }

        private void RestoreNosInput()
        {
            if (!nosInputStaged) return;
            var vc = nosStagedVehicle;
            nosInputStaged = false;
            nosStagedVehicle = null;
            if (vc == null) return;
            try
            {
                vc.input.Boost = false; vc.input.autoSetInput = nosAutoInput; vc.IgnitionPosition = nosIgnition;
                vc.powertrain.engine.ThrottlePosition = nosThrottle; vc.powertrain.transmission.ratio = nosRatio;
            }
            catch (Exception ex) { Debug.LogWarning("MonMP NOS fixture input restore: " + ex.Message); }
        }

        private void EnsureKonigNosSeat(VehicleController vc)
        {
            var seat = vc.vehicle_PartsManager.Seat_Left;
            if (!seat.isInstalled)
            {
                var binding = plugin.World!.VehicleParts.PaintSockets.Single(entry => entry.Socket == seat);
                var prefabs = items.TestPrefabs(typeof(Part_Seat));
                Require(prefabs.Length > 0, "Konig NOS fixture has no native driver seat prefab.");
                plugin.World.VehicleParts.TestInstall(binding.Id, prefabs[0]);
            }
            TightenNosSocket(seat);
        }

        private static void TightenNosSocket(Vehicle_PartSocket socket)
        {
            Require(socket.isInstalled, "Konig NOS fixture cannot tighten absent " + socket.car_PartsType);
            foreach (var hole in socket.boltHolesDetails)
            {
                if (!hole.isBoltInstalled)
                {
                    var bolt = Object.Instantiate(Singleton<GameAssets>.i.Hardware_Bolt(hole.boltSize), hole.HoleLocation).GetComponent<Hardware_Bolt>();
                    hole.Attach(bolt, 1f);
                }
                hole.BoltedPercent = 1f;
            }
            socket.OneBoltFullyTighten();
            Require(socket.CheckIfAllBoltAre100Percent() && socket.CheckIfSolidlyAttached(), "Konig NOS fixture " + socket.car_PartsType + " is not solid.");
        }

        private void MoveNear(Vector3 point)
        {
            gameplay.PlayerWalking.transform.position = point;
            gameplay.Get_Player_Rigidbody.position = point;
            gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }

        internal void Cleanup()
        {
            if (cleaned) return;
            var connected = plugin.Session;
            RestoreNosInput();
            try { if (plugin.Session?.IsRunning == true) plugin.EndPairedTestSession(); }
            finally
            {
                if (plugin.TestOptions.IsHost && connected != null) connected.PoseReceived -= OnPose;
                RestoreIsolatedFixture(); cleaned = true;
            }
        }

        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Racecar paired test: " + message); }
    }
}
