using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Leo;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Focused paired fixture for the Leo item adapters. Each source must first receive a real
    // SharedItems lease; dry food and bunker fertilizer enter through their native MouseDown
    // entry points, while watering runs its actual iterator and returns its native amount.
    internal sealed class PairedLeoSuppliesTest
    {
        private static readonly System.Reflection.FieldInfo ReservoirAmount = AccessTools.Field(typeof(Home_Bunker), "FertilizerAmounInReservoir");
        private static readonly System.Reflection.MethodInfo ReservoirUpdateVisual = AccessTools.Method(typeof(Home_Bunker_FertilizerReservoir), "UpdateVisual");
        private static readonly System.Reflection.MethodInfo BowlUpdateVisual = AccessTools.Method(typeof(FoodBowl), "UpdateVisual");
        private static float hostReservoirBaseline;

        private const float BowlDryStart = 0.4f, DryPackStart = 0.5f, WaterCanStart = 0.8f, FertilizerPackStart = 0.8f;
        private const float ReservoirFixtureStart = 0.1f, Near = 1.6f;
        private static bool hostBaselineCaptured;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly SharedItems items;
        private readonly SharedLeoSupplies supplies;
        private readonly Vector3 originalPlayerPosition;
        private readonly Quaternion originalCameraRotation;
        private readonly bool originalPlayerKinematic;
        private readonly Transform cameraTransform;
        private int phase;
        private float next, deadline, wateringStart;
        private ulong bowlId, dryId, canId, fertilizerId;
        private int appliedBefore, rejectedBefore, duplicateBefore;
        private bool distantRequestPositioned, canPositioned, fertilizerPositioned;
        private int bunkerPlacements;
        private readonly List<GameObject> activatedReservoirObjects = new List<GameObject>();
        private Collider? stagedReservoirCollider;
        private uint dryGeneration;

        internal string Status => "Leo supplies phase=" + phase + "; ids=" + bowlId.ToString("X") + "/" + dryId.ToString("X") + "/" + canId.ToString("X") + "/" + fertilizerId.ToString("X") +
            "; ack=" + supplies.TestApplied + "/" + supplies.TestRejected + "/" + supplies.TestDuplicates + "; pending=" + supplies.PendingCount +
            "; reservoir=" + supplies.TestNativeState.Reservoir.ToString("F3") + "; fertilizerGate=" + supplies.TestFertilizerGate +
            "; localRange=" + (supplies.TestBunkerReservoir is Home_Bunker_FertilizerReservoir visual
                ? Vector3.Distance(gameplay.PlayerWalking.transform.position, visual.transform.position).ToString("F2") + "/" +
                  (visual.GetComponent<Collider>() == null ? "no collider" : Vector3.Distance(visual.transform.position, visual.GetComponent<Collider>().bounds.center).ToString("F2"))
                : "missing") +
            "; water=" + (bowlId != 0 && supplies.TestBowl(bowlId, 1) is FoodBowl bowl ? bowl.WhatsIn + "/" + bowl.amount.ToString("F3") + "/" + bowl.isDiscarded : "missing") +
            "; can=" + (canId != 0 && items.TestItem(canId) is Tool_WateringCan can ? can.Value1.ToString("F3") : "missing") +
            "; canStart=" + wateringStart.ToString("F3") + "; waterTx=" + supplies.TestWaterCalls + "/" + supplies.TestWaterTargetedCalls + "/" +
            supplies.TestWaterDrained.ToString("F3") + "/" + supplies.TestWaterAdded.ToString("F3");

        internal static void Prepare(Gameplay gameplay, bool host)
        {
            if (!host || hostBaselineCaptured) return;
            var bunker = UnityEngine.Object.FindObjectsOfType<Home_Bunker>(true)
                .FirstOrDefault(candidate => candidate != null && candidate.gameObject.scene.IsValid()) ??
                throw new InvalidOperationException("Leo supplies fixture: home bunker is missing.");
            hostReservoirBaseline = bunker.Get_FertilizerAmounInReservoir;
            hostBaselineCaptured = true;
            ReservoirAmount.SetValue(bunker, ReservoirFixtureStart);
            if (AccessTools.Field(typeof(Home_Bunker), "FertilizerReservoir").GetValue(bunker) is Home_Bunker_FertilizerReservoir visual)
                ReservoirUpdateVisual.Invoke(visual, null);
        }

        internal PairedLeoSuppliesTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay;
            items = plugin.World!.Items; supplies = plugin.World.LeoSupplies;
            originalPlayerPosition = gameplay.PlayerWalking.transform.position;
            originalPlayerKinematic = gameplay.Get_Player_Rigidbody?.isKinematic ?? false;
            cameraTransform = gameplay.Player.mainCamera.transform;
            originalCameraRotation = cameraTransform.rotation;
            appliedBefore = supplies.TestApplied; rejectedBefore = supplies.TestRejected; duplicateBefore = supplies.TestDuplicates;
        }

        internal bool Tick()
        {
            if (deadline == 0) deadline = Time.unscaledTime + 120;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Leo supplies test stalled: " + Status);
            if (!plugin.TestOptions.IsHost && phase == 8 && supplies.TestBowl(bowlId, 1) is FoodBowl waterBowl)
                AimAt(waterBowl.GetComponent<Collider>()?.bounds.center ?? waterBowl.transform.position);
            if (Time.unscaledTime < next || plugin.World?.Channel.FullySynchronized != true) return false;
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private ushort Prefab(Type type) => items.TestPrefabs(type).First();
        private ushort BowlPrefab() => items.TestPrefabs(typeof(Item_FoodBowl)).First(id => items.TestPrefabChildCount<FoodBowl>(id) >= 2);

        private bool Host()
        {
            if (phase == 0 && plugin.Session!.Peers.Count >= 2)
            {
                var origin = gameplay.PlayerWalking.transform.position;
                ushort bowlPrefab = BowlPrefab();
                ushort dryPrefab = Prefab(typeof(Tool_DryFoodPack));
                ushort canPrefab = Prefab(typeof(Tool_WateringCan));
                ushort fertilizerPrefab = Prefab(typeof(Tool_FertilizerPack));
                var bowl = (Item_FoodBowl)items.TestSpawn(origin + gameplay.PlayerWalking.transform.forward * 2f + Vector3.up * 0.25f, bowlPrefab);
                // Keep native Leo from drinking the small test fill between replicated pulses.
                foreach (FoodBowl child in bowl.GetComponentsInChildren<FoodBowl>(true)) child.isDiscarded = true;
                var dry = (Tool_DryFoodPack)items.TestSpawn(bowl.transform.position + Vector3.right * 0.7f + Vector3.up * 0.15f, dryPrefab);
                var can = (Tool_WateringCan)items.TestSpawn(bowl.transform.position + Vector3.left * 0.8f + Vector3.up * 0.15f, canPrefab);
                var bunker = supplies.TestBunker ?? throw new InvalidOperationException("Leo supplies fixture: shared bunker was not discovered.");
                var fertilizer = (Tool_FertilizerPack)items.TestSpawn(bunker.transform.position + bunker.transform.forward * 1.5f + Vector3.up * 0.3f, fertilizerPrefab);
                bowl.IntValue1 = 0; bowl.Value1 = BowlDryStart;
                bowl.IntValue2 = 0; bowl.Value2 = 0;
                foreach (var child in bowl.GetComponentsInChildren<FoodBowl>(true)) BowlUpdateVisual.Invoke(child, null);
                dry.Value1 = DryPackStart; can.Value1 = WaterCanStart; fertilizer.Value1 = FertilizerPackStart;
                phase = 1; next = Time.unscaledTime + 1.5f;
                return false;
            }
            if (phase == 1)
            {
                // Prefer the freshly spawned top ids; all four fixture entries are the newest of their prefab type.
                bowlId = items.TestNewestPrefab(BowlPrefab());
                dryId = items.TestNewestPrefab(Prefab(typeof(Tool_DryFoodPack)));
                canId = items.TestNewestPrefab(Prefab(typeof(Tool_WateringCan)));
                fertilizerId = items.TestNewestPrefab(Prefab(typeof(Tool_FertilizerPack)));
                if (new[] { bowlId, dryId, canId, fertilizerId }.Any(id => id == 0 || items.TestItem(id) == null)) return false;
                foreach (ulong id in new[] { bowlId, dryId, canId, fertilizerId }) items.PublishHostItem(id);
                Require(Math.Abs(supplies.TestNativeState.Reservoir - ReservoirFixtureStart) < 0.001f,
                    "Host bunker reservoir did not begin at the isolated fixture value.");
                phase = 2;
            }
            else if (phase == 2 && supplies.TestApplied > appliedBefore && supplies.TestDuplicates > duplicateBefore && supplies.TestRejected > rejectedBefore &&
                HasFinalNativeValues()) phase = 3;
            else if (phase == 3 && plugin.Session!.Peers.Count == 1)
            {
                Require(HasFinalNativeValues(), "Host Leo supply item or reservoir values did not match the guest's three native requests.");
                supplies.TestSetReservoir(hostReservoirBaseline);
                foreach (ulong id in new[] { bowlId, dryId, canId, fertilizerId })
                    if (items.TestState(id).Exists && items.TestItem(id) != null) items.TestDestroy(id);
                phase = 4; next = Time.unscaledTime + 1.2f;
            }
            else if (phase == 4 && Math.Abs(supplies.TestNativeState.Reservoir - hostReservoirBaseline) < 0.001f &&
                new[] { bowlId, dryId, canId, fertilizerId }.All(id => !items.TestState(id).Exists))
            return true;
            return false;
        }

        private bool HasFinalNativeValues()
        {
            if (bowlId == 0 || dryId == 0 || canId == 0 || fertilizerId == 0) return false;
            if (items.TestItem(bowlId) is not Item_FoodBowl bowl || items.TestItem(dryId) is not Tool_DryFoodPack dry ||
                items.TestItem(canId) is not Tool_WateringCan can) return false;
            FoodBowl? dryBowl = supplies.TestBowl(bowlId, 0), waterBowl = supplies.TestBowl(bowlId, 1);
            return dryBowl != null && waterBowl != null && dryBowl.WhatsIn == 2 && Math.Abs(dryBowl.amount - 1f) < 0.01f &&
                Math.Abs(dry.Value1 - (DryPackStart - (1 - BowlDryStart) / 5f)) < 0.02f &&
                waterBowl.WhatsIn == 1 && waterBowl.amount > 0.02f && can.Value1 < WaterCanStart - 0.05f &&
                !items.TestState(fertilizerId).Exists && Math.Abs(supplies.TestNativeState.Reservoir - (ReservoirFixtureStart + FertilizerPackStart * 0.25f)) < 0.02f &&
                supplies.TestApplied >= appliedBefore + 3 && supplies.TestDuplicates >= duplicateBefore + 1 && supplies.TestRejected >= rejectedBefore + 1;
        }

        private bool Guest()
        {
            if (phase == 0)
            {
                bowlId = items.TestNewestPrefab(BowlPrefab());
                dryId = items.TestNewestPrefab(Prefab(typeof(Tool_DryFoodPack)));
                canId = items.TestNewestPrefab(Prefab(typeof(Tool_WateringCan)));
                fertilizerId = items.TestNewestPrefab(Prefab(typeof(Tool_FertilizerPack)));
                if (new[] { bowlId, dryId, canId, fertilizerId }.Any(id => id == 0 || items.TestItem(id) == null)) return false;
                Require(Math.Abs(supplies.TestNativeState.Reservoir - ReservoirFixtureStart) < 0.001f, "Host bunker reservoir did not reach the guest snapshot.");
                var dryBowl = supplies.TestBowl(bowlId, 0); var waterBowl = supplies.TestBowl(bowlId, 1);
                if (dryBowl == null || waterBowl == null || dryBowl.WhatsIn != 0 ||
                    Math.Abs(dryBowl.amount - BowlDryStart) >= 0.01f || waterBowl.WhatsIn != 0) return false;
                dryBowl.isDiscarded = true; waterBowl.isDiscarded = true;
                phase = 1; next = Time.unscaledTime + 0.5f; return false;
            }
            if (phase == 1)
            {
                MoveNear(items.TestItem(dryId)!.transform.position);
                items.TestUse(dryId); phase = 2; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 2)
            {
                Require(HasLocalToolLease(dryId, out dryGeneration), "Dry food pack did not receive the local item lease.");
                var bowl = supplies.TestBowl(bowlId, 0)!;
                AimAtBowl(bowl);
                ((Tool_DryFoodPack)items.TestItem(dryId)!).MouseDown();
                phase = 3; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 3 && DrySucceeded())
            {
                supplies.TestRequest(supplies.TestCommand(LeoSuppliesAction.FillDryBowl, dryId, dryGeneration, 1, bowlId, 0));
                phase = 4; next = Time.unscaledTime + 0.5f; return false;
            }
            if (phase == 4 && supplies.TestDuplicates > duplicateBefore)
            {
                if (!distantRequestPositioned)
                {
                    MovePlayer(items.TestItem(bowlId)!.transform.position + Vector3.right * 30);
                    distantRequestPositioned = true;
                    next = Time.unscaledTime + 0.6f;
                    return false;
                }
                supplies.TestRequest(supplies.TestCommand(LeoSuppliesAction.FillDryBowl, dryId, dryGeneration, 2, bowlId, 0));
                phase = 5; next = Time.unscaledTime + 0.5f; return false;
            }
            if (phase == 5 && supplies.TestRejected > rejectedBefore)
            {
                Require(DrySucceeded(), "Duplicate or distant dry-food request changed the host bowl or pack.");
                items.TestPutAwayTool(); phase = 6; next = Time.unscaledTime + 0.6f; return false;
            }
            if (phase == 6)
            {
                if (!canPositioned)
                {
                    MoveNear(items.TestItem(canId)!.transform.position);
                    canPositioned = true; next = Time.unscaledTime + 0.6f; return false;
                }
                items.TestUse(canId); phase = 7; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 7)
            {
                Require(HasLocalToolLease(canId, out _), "Watering can did not receive the local item lease.");
                var bowl = supplies.TestBowl(bowlId, 1)!;
                AimAtBowl(bowl);
                var can = (Tool_WateringCan)items.TestItem(canId)!; wateringStart = can.Value1; can.MouseDown();
                Replication.RemotePlayers.TestLocalToolCleanup(can);
                phase = 8; next = Time.unscaledTime + 0.4f; return false;
            }
            if (phase == 8)
            {
                ((Tool_WateringCan)items.TestItem(canId)!).MouseUp();
                phase = 9; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 9 && WateringSucceeded())
            {
                items.TestPutAwayTool(); phase = 10; next = Time.unscaledTime + 0.6f; return false;
            }
            if (phase == 10)
            {
                if (!fertilizerPositioned)
                {
                    MoveNear(items.TestItem(fertilizerId)!.transform.position);
                    fertilizerPositioned = true; next = Time.unscaledTime + 0.6f; return false;
                }
                items.TestUse(fertilizerId); phase = 11; next = Time.unscaledTime + 0.8f; return false;
            }
            if (phase == 11)
            {
                Require(HasLocalToolLease(fertilizerId, out uint generation), "Fertilizer pack did not receive the local item lease.");
                var bunker = supplies.TestBunker ?? throw new InvalidOperationException("Guest Leo bunker replica is unavailable.");
                if (bunkerPlacements < 2)
                {
                    PositionForBunker(bunker); bunkerPlacements++; next = Time.unscaledTime + 0.6f; return false;
                }
                var visual = supplies.TestBunkerReservoir ?? throw new InvalidOperationException("Guest Leo bunker reservoir visual is unavailable.");
                AimAt(visual.GetComponent<Collider>().bounds.center);
                Require(supplies.TestTraceBunker(), "The equipped fertilizer pack's native camera ray lost the bunker target.");
                ((Tool_FertilizerPack)items.TestItem(fertilizerId)!).MouseDown();
                phase = 12; next = Time.unscaledTime + 1.0f; return false;
            }
            if (phase == 12 && FertilizerSucceeded())
            {
                Require(supplies.TestApplied >= appliedBefore + 3 && supplies.TestDuplicates >= duplicateBefore + 1 && supplies.TestRejected >= rejectedBefore + 1,
                    "Leo supply requests did not exercise success, duplicate replay and range rejection.");
                RestoreLocalScene(); plugin.EndPairedTestSession();
                Require(items.TestRestored && supplies.TestRestored, "Leo supplies disconnect did not restore guest items or local bunker save data.");
                return true;
            }
            return false;
        }

        private bool DrySucceeded()
        {
            var bowl = supplies.TestBowl(bowlId, 0); var pack = items.TestItem(dryId) as Tool_DryFoodPack;
            return bowl != null && bowl.WhatsIn == 2 && Math.Abs(bowl.amount - 1f) < 0.01f && pack != null &&
                Math.Abs(pack.Value1 - (DryPackStart - (1 - BowlDryStart) / 5f)) < 0.02f;
        }

        private bool WateringSucceeded()
        {
            var bowl = supplies.TestBowl(bowlId, 1); var can = items.TestItem(canId) as Tool_WateringCan;
            float drained = wateringStart - (can?.Value1 ?? wateringStart);
            return bowl != null && bowl.WhatsIn == 1 && bowl.amount > 0.02f && drained > 0.05f &&
                Math.Abs(bowl.amount - drained * 0.4f) < 0.06f && supplies.TestApplied > appliedBefore + 1;
        }

        private bool FertilizerSucceeded() => items.TestItem(fertilizerId) == null && !items.TestState(fertilizerId).Exists &&
            Math.Abs(supplies.TestNativeState.Reservoir - (ReservoirFixtureStart + FertilizerPackStart * 0.25f)) < 0.02f;

        private bool HasLocalToolLease(ulong id, out uint generation)
        {
            var state = items.TestState(id); generation = state.Lease.Generation;
            return state.Exists && state.Lease.Owner == plugin.Session!.LocalPeerId && generation != 0 &&
                items.TestItem(id) is InteractableTools tool && gameplay.Player.Get_ActiveTool == tool && items.TestActiveToolInputAllowed(id);
        }

        private void MoveNear(Vector3 target) => MovePlayer(target - gameplay.PlayerWalking.transform.forward * Near);
        private void AimAtBowl(FoodBowl bowl)
        {
            Vector3 target = bowl.GetComponent<Collider>()?.bounds.center ?? bowl.transform.position;
            MoveNear(target); AimAt(target);
            var ray = gameplay.Player.mainCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
            Require(Physics.Raycast(ray, out var hit, 2.4f) && hit.collider.GetComponent<FoodBowl>() == bowl,
                "The equipped tool's native camera ray did not hit the selected food bowl.");
        }
        private void PositionForBunker(Home_Bunker bunker)
        {
            // The isolated save has no constructed bunker floor. Hold the walking body while
            // its camera ray and host-relayed walking pose settle at the staged reservoir.
            if (gameplay.Get_Player_Rigidbody != null) gameplay.Get_Player_Rigidbody.isKinematic = true;
            var visual = supplies.TestBunkerReservoir ?? throw new InvalidOperationException("Leo supplies test: the native bunker reservoir visual is missing.");
            var collider = visual.GetComponent<Collider>() ?? throw new InvalidOperationException("Leo supplies test: the native bunker reservoir has no collider.");
            for (Transform? node = visual.transform; node != null; node = node.parent)
                if (!node.gameObject.activeSelf) { activatedReservoirObjects.Add(node.gameObject); node.gameObject.SetActive(true); }
            if (!collider.enabled) { stagedReservoirCollider = collider; collider.enabled = true; }
            supplies.TestReapplyNative();
            Require(collider.enabled && collider.gameObject.activeInHierarchy, "Native bunker reservoir collider is disabled.");
            Vector3 cameraOffset = cameraTransform.position - gameplay.PlayerWalking.transform.position;
            Vector3 center = collider.bounds.center;
            float radius = Mathf.Max(collider.bounds.extents.x, collider.bounds.extents.z) + 0.5f;
            foreach (Vector3 direction in new[] { Vector3.forward, Vector3.back, Vector3.right, Vector3.left })
            {
                MovePlayer(center + direction * radius - cameraOffset);
                AimAt(center);
                if ((gameplay.PlayerWalking.transform.position - visual.transform.position).sqrMagnitude > SharedLeoSupplies.Reach * SharedLeoSupplies.Reach) continue;
                var ray = gameplay.Player.mainCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
                if (Physics.Raycast(ray, out var hit, SharedLeoSupplies.InputRange) && hit.collider.GetComponent<Home_Bunker_FertilizerReservoir>() == visual)
                    return;
            }
            throw new InvalidOperationException("Leo supplies test: native bunker reservoir collider could not be targeted by the equipped fertilizer pack.");
        }
        private void MovePlayer(Vector3 point)
        {
            gameplay.PlayerWalking.transform.position = point;
            if (gameplay.Get_Player_Rigidbody != null)
            { gameplay.Get_Player_Rigidbody.position = point; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
        }
        private void AimAt(Vector3 point)
        {
            Vector3 delta = point - cameraTransform.position;
            if (delta.sqrMagnitude > 0.001f) cameraTransform.rotation = Quaternion.LookRotation(delta.normalized, Vector3.up);
        }
        private void RestoreLocalScene()
        {
            if (gameplay.Get_Player_Rigidbody != null) gameplay.Get_Player_Rigidbody.isKinematic = originalPlayerKinematic;
            MovePlayer(originalPlayerPosition);
            cameraTransform.rotation = originalCameraRotation;
            if (stagedReservoirCollider != null) stagedReservoirCollider.enabled = false;
            foreach (GameObject go in activatedReservoirObjects) if (go != null) go.SetActive(false);
            activatedReservoirObjects.Clear(); stagedReservoirCollider = null;
        }
        private static void Require(bool ok, string reason)
        { if (!ok) throw new InvalidOperationException("Leo supplies test: " + reason); }
    }
}
