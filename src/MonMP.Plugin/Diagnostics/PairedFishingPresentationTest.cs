using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Real shared rod/native Equip and serialized TubeRenderer/attachment fixture.
    // Cast endpoint and FishCaugh are staged presentation inputs; this does not
    // exercise the native Fishing coroutine, bite timing, fish selection or rewards.
    internal sealed class PairedFishingPresentationTest
    {
        private static readonly FieldInfo Line = AccessTools.Field(typeof(Tool_FishingRod), "Tube_FishingLine");
        private static readonly FieldInfo Attachment = AccessTools.Field(typeof(Tool_FishingRod), "Transform_LineAttachment");
        private static readonly FieldInfo FishLocation = AccessTools.Field(typeof(Tool_FishingRod), "Fish_Location");
        private static readonly FieldInfo Caught = AccessTools.Field(typeof(Tool_FishingRod), "FishCaugh");
        private static readonly FieldInfo Folded = AccessTools.Field(typeof(Tool_FishingRod), "FishingRod_Folded");
        private static readonly FieldInfo Unfolded = AccessTools.Field(typeof(Tool_FishingRod), "FishingRod_Unfolded");
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly ushort prefab;
        private readonly Vector3 originalWalkingPosition, originalBodyPosition, originalVelocity, originalAngularVelocity;
        private readonly Quaternion originalWalkingRotation, originalBodyRotation;
        private Tool_FishingRod? rod;
        private GameObject? stagedCatch;
        private ulong entity;
        private int phase, castCues;
        private float next, deadline;
        private bool equippedSeen, endSeen, rodCaptured;
        private float equipSeenAt;
        private Vector3[]? originalPoints;
        private bool originalLine, originalFolded, originalUnfolded, capturedRod, aborted;
        private const float Marker = 0.743f;
        internal string Status => "fishing presentation phase=" + phase + "; rod=" + entity.ToString("X16") +
            "; current=" + SharedFishingPresentation.Active?.TestCurrent(entity)?.Phase + "; staged cast/catch";
        internal PairedFishingPresentationTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; prefab = plugin.Items!.TestTypePrefab(typeof(Tool_FishingRod));
            originalWalkingPosition = gameplay.PlayerWalking.transform.position; originalWalkingRotation = gameplay.PlayerWalking.transform.rotation;
            var body = gameplay.Get_Player_Rigidbody;
            originalBodyPosition = body.position; originalBodyRotation = body.rotation;
            originalVelocity = body.velocity; originalAngularVelocity = body.angularVelocity;
        }
        internal bool Tick()
        {
            var state = SharedFishingPresentation.Active;
            if (plugin.Items?.Ready != true || state?.Ready != true) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 100f;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Fishing presentation test stalled: " + Status);
            if (Time.unscaledTime < next) return false;
            return plugin.TestOptions.IsHost ? Host(state) : Guest(state);
        }
        private bool Host(SharedFishingPresentation state)
        {
            var items = plugin.Items!;
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                rod = (Tool_FishingRod)items.TestSpawn(gameplay.PlayerWalking.transform.position + new Vector3(2, 0.8f, 2), prefab);
                rod.transform.localScale = Vector3.one * Marker;
                rod.CreateBasics(); if (rod.rb != null) rod.rb.isKinematic = true;
                phase = 1; next = Time.unscaledTime + 1f;
            }
            else if (phase == 1)
            {
                entity = items.EntityOf(rod!); if (entity == 0) return false;
                CaptureRod();
                items.TestGrab(entity); gameplay.Player.EquipThisTool(rod!);
                phase = 2; next = Time.unscaledTime + 4f;
            }
            else if (phase == 2)
            {
                Require(rod!.isEquiped && items.OwnedBy(entity, 0), "host rod was not natively equipped and leased.");
                CaptureRodViews(rod, null);
                var before = state.TestRejected;
                uint guest = plugin.Session!.Peers.Keys.First(id => id != 0);
                var lease = items.StateOf(entity)!.Lease;
                var at = rod.transform.position;
                state.TestReportAs(guest, new FishingPresentationState(entity, guest, lease.Generation, FishingPresentationPhase.Cast, at.x, at.y, at.z));
                state.TestReportAs(0, new FishingPresentationState(entity, 0, lease.Generation + 1, FishingPresentationPhase.Cast, at.x, at.y, at.z));
                Require(state.TestRejected == before + 2, "host accepted another owner's rod or a stale lease generation.");
                StageCast(rod); phase = 3; next = Time.unscaledTime + 5f;
            }
            else if (phase == 3)
            {
                Require(state.TestCurrent(entity)?.Phase == FishingPresentationPhase.Cast, "host did not capture staged settled cast.");
                var current = state.TestCurrent(entity)!.Value; int cues = state.TestCues;
                state.PlayCue(new PresentationEvent(1, (byte)PresentationKind.Fishing, 0, current.Owner, entity, current.X, current.Y, current.Z,
                    current.Generation == 1 ? 2 : current.Generation - 1));
                Require(state.TestCues == cues, "stale-generation cast emitted a one-shot.");
                stagedCatch = new GameObject("MonMP staged fishing attachment sentinel");
                stagedCatch.transform.SetParent((Transform)FishLocation.GetValue(rod!), false);
                Caught.SetValue(rod!, stagedCatch);
                phase = 4; next = Time.unscaledTime + 5f;
            }
            else if (phase == 4)
            {
                Require(state.TestCurrent(entity)?.Phase == FishingPresentationPhase.Caught, "host did not capture staged caught attachment.");
                ClearStage(); phase = 5; next = Time.unscaledTime + 5f;
            }
            else if (phase == 5)
            {
                Require(state.TestCurrent(entity)?.Phase == FishingPresentationPhase.End, "end folded or lost the held rod phase.");
                items.TestPutAwayTool(); phase = 6;
            }
            else if (phase == 6)
            {
                var current = state.TestCurrent(entity);
                if (!current.HasValue || current.Value.Owner == 0 || current.Value.Phase != FishingPresentationPhase.Cast ||
                    !RemoteFishing.TestPresented(entity, FishingPresentationPhase.Cast)) return false;
                RemoteFishing.Update();
                AssertLine(rod!, new Vector3(current.Value.X, current.Value.Y, current.Value.Z));
                phase = 7;
            }
            else if (phase == 7 && plugin.Session!.Peers.Count == 1)
            {
                Require(!state.TestCurrent(entity).HasValue && !RemoteFishing.TestPresented(entity, FishingPresentationPhase.Cast), "departed owner retained a durable or drawn cast.");
                Require(items.StateOf(entity)!.Lease.Owner == ItemLease.Unowned, "departed angler retained the shared rod lease.");
                Require(!((TubeRenderer)Line.GetValue(rod!)).gameObject.activeSelf, "departed angler's tube baseline was not restored.");
                return true;
            }
            return false;
        }
        private bool Guest(SharedFishingPresentation state)
        {
            var items = plugin.Items!;
            if (entity == 0)
            {
                entity = items.TestTaggedItem(prefab, Marker); if (entity == 0) return false;
                rod = (Tool_FishingRod)items.ItemOf(entity)!;
                MoveNear(rod.transform.position);
            }
            var current = state.TestCurrent(entity);
            if (phase == 0)
            {
                if (current?.Phase == FishingPresentationPhase.Equip)
                {
                    AssertUnfolded(rod!);
                    if (!equippedSeen) equipSeenAt = Time.unscaledTime;
                    equippedSeen = true;
                    if (!rodCaptured && Time.unscaledTime - equipSeenAt >= 1.5f) CaptureRodViews(rod!, plugin.RemoteAvatarRoot(current.Value.Owner));
                }
                if (current?.Phase != FishingPresentationPhase.Cast || state.TestCues == 0) return false;
                Require(equippedSeen, "observer never saw native equipped idle presentation.");
                RemoteFishing.Update();
                AssertLine(rod!, new Vector3(current.Value.X, current.Value.Y, current.Value.Z));
                castCues = state.TestCues;
                int events = plugin.World!.Events!.TestPlayed;
                RemoteFishing.RemoveCurrent(entity);
                state.TestReplaySnapshot(); state.TestReplaySnapshot(); RemoteFishing.Update();
                Require(RemoteFishing.TestPresented(entity, FishingPresentationPhase.Cast), "repair snapshot did not seed current cast.");
                AssertLine(rod!, new Vector3(current.Value.X, current.Value.Y, current.Value.Z));
                Require(state.TestCues == castCues && plugin.World.Events.TestPlayed == events, "snapshot replay emitted historical cast cues.");
                phase = 1;
            }
            else if (phase == 1)
            {
                if (current?.Phase != FishingPresentationPhase.Caught) return false;
                RemoteFishing.Update(); AssertLine(rod!, ((Transform)FishLocation.GetValue(rod!)).position);
                Require(state.TestCues == castCues, "caught phase emitted another swing/splash.");
                state.TestReplaySnapshot(); state.TestReplaySnapshot(); RemoteFishing.Update();
                AssertLine(rod!, ((Transform)FishLocation.GetValue(rod!)).position);
                phase = 2;
            }
            else if (phase == 2)
            {
                if (current?.Phase == FishingPresentationPhase.End)
                {
                    AssertUnfolded(rod!);
                    Require(!((TubeRenderer)Line.GetValue(rod!)).gameObject.activeSelf, "end retained the tube."); endSeen = true;
                }
                if (items.StateOf(entity)!.Lease.Owner != ItemLease.Unowned || current.HasValue) return false;
                Require(endSeen, "observer never saw end with held unfolded rod.");
                Require(!((TubeRenderer)Line.GetValue(rod!)).gameObject.activeSelf && ((GameObject)Folded.GetValue(rod!)).activeSelf,
                    "released owner did not restore rod baseline; " + RemoteFishing.TestNativePresentation(rod!));
                MoveNear(rod!.transform.position); items.TestGrab(entity); phase = 3;
            }
            else if (phase == 3)
            {
                if (!items.OwnedBy(entity, plugin.Session!.LocalPeerId) || !items.TestLocalHeld(entity)) return false;
                CaptureRod();
                gameplay.Player.EquipThisTool(rod!);
                Require(rod!.isEquiped && !((TubeRenderer)Line.GetValue(rod)).gameObject.activeSelf, "local takeover kept the remote tube.");
                AssertUnfolded(rod); StageCast(rod); phase = 4; next = Time.unscaledTime + 5f;
            }
            else if (phase == 4)
            {
                Require(state.TestCurrent(entity)?.Owner == plugin.Session!.LocalPeerId && state.TestCurrent(entity)?.Phase == FishingPresentationPhase.Cast,
                    "guest's own leased rod phase was not host-arbitrated.");
                // Leave while a real native tube is displaying the staged cast endpoint.
                plugin.EndPairedTestSession(); return true;
            }
            return false;
        }
        // Pictures of the same rod from behind and beside its holder: the host's own first-person
        // rod, and the guest's copy of it in the host avatar's hands. Same framing in both, so a
        // flipped presentation shows as a flipped picture. Logs only; never fails the suite.
        private void CaptureRodViews(Tool_FishingRod target, Transform? avatar)
        {
            rodCaptured = true;
            string directory = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "MonMP", "diagnostics");
            var frame = new GameObject("MonMP rod capture frame");
            try
            {
                Transform subject;
                Quaternion look;
                if (avatar == null)
                {
                    var camera = gameplay.Player.mainCamera.transform;
                    look = camera.rotation; subject = target.transform;
                    frame.transform.rotation = Quaternion.Euler(0, camera.eulerAngles.y, 0);
                    AvatarPreviewCapture.SaveView(gameplay.Player.mainCamera, System.IO.Path.Combine(directory, "fishing-rod-first-person.bmp"));
                }
                else
                {
                    var copy = avatar.Find("MonMP Remote Held Presentation");
                    if (copy == null) { Debug.LogWarning("MONMP_ROD_VIEW no remote rod presentation under the host avatar"); return; }
                    subject = copy; look = avatar.rotation;
                    frame.transform.rotation = Quaternion.Euler(0, avatar.eulerAngles.y, 0);
                    AvatarPreviewCapture.SaveBitmap(avatar, System.IO.Path.Combine(directory, "fishing-rod-remote-avatar-back.bmp"), AvatarPreviewCapture.View.Back);
                    AvatarPreviewCapture.SaveBitmap(avatar, System.IO.Path.Combine(directory, "fishing-rod-remote-avatar-side.bmp"), AvatarPreviewCapture.View.Side);
                }
                string role = avatar == null ? "native" : "remote";
                AvatarPreviewCapture.SaveBitmap(subject, System.IO.Path.Combine(directory, "fishing-rod-" + role + "-back.bmp"), AvatarPreviewCapture.View.Back, frame.transform);
                AvatarPreviewCapture.SaveBitmap(subject, System.IO.Path.Combine(directory, "fishing-rod-" + role + "-side.bmp"), AvatarPreviewCapture.View.Side, frame.transform);
                var relative = Quaternion.Inverse(frame.transform.rotation) * subject.rotation;
                var location = (Transform)FishLocation.GetValue(target);
                var hang = subject.rotation * (Quaternion.Inverse(target.transform.rotation) * location.rotation) * Vector3.back;
                Debug.Log("MONMP_ROD_VIEW " + role + " forward=" + (relative * Vector3.forward).ToString("F2") + " up=" + (relative * Vector3.up).ToString("F2") +
                    " right=" + (relative * Vector3.right).ToString("F2") + " fishHang=" + hang.ToString("F2") + " lookPitch=" + Mathf.DeltaAngle(0, look.eulerAngles.x).ToString("F1") +
                    " dir=" + directory);
            }
            catch (Exception ex) { Debug.LogWarning("MONMP_ROD_VIEW capture failed: " + ex); }
            finally { UnityEngine.Object.Destroy(frame); }
        }
        private static void StageCast(Tool_FishingRod target)
        {
            var line = (TubeRenderer)Line.GetValue(target); var attach = (Transform)Attachment.GetValue(target);
            Require(line.points != null && line.points.Length >= 2, "native rod does not have two serialized tube points.");
            line.points![0] = target.transform.InverseTransformPoint(attach.position);
            line.points[1] = target.transform.InverseTransformPoint(attach.position + Vector3.forward * 10f);
            line.gameObject.SetActive(true); line.Redraw();
        }
        private static void AssertUnfolded(Tool_FishingRod target) => Require(!((GameObject)Folded.GetValue(target)).activeSelf &&
            ((GameObject)Unfolded.GetValue(target)).activeSelf, "equipped/end rod was folded.");
        private static void AssertLine(Tool_FishingRod target, Vector3 end)
        {
            AssertUnfolded(target); var line = (TubeRenderer)Line.GetValue(target);
            Require(line.gameObject.activeSelf && line.points != null && line.points.Length >= 2, "observer native tube is absent.");
            Require(Vector3.Distance(target.transform.TransformPoint(line.points![1]), end) < 0.03f, "observer tube endpoint missed its native attachment/current water endpoint.");
        }
        private void MoveNear(Vector3 at)
        {
            var location = at + new Vector3(0, -0.8f, 1);
            gameplay.PlayerWalking.transform.position = location; gameplay.Get_Player_Rigidbody.position = location; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero;
        }
        private void ClearStage()
        {
            if (rod != null) { Caught.SetValue(rod, null); ((TubeRenderer)Line.GetValue(rod)).gameObject.SetActive(false); }
            if (stagedCatch != null) UnityEngine.Object.Destroy(stagedCatch);
            stagedCatch = null;
        }
        private void CaptureRod()
        {
            if (capturedRod || rod == null) return;
            var line = (TubeRenderer)Line.GetValue(rod);
            originalLine = line.gameObject.activeSelf;
            originalPoints = line.points == null ? null : (Vector3[])line.points.Clone();
            originalFolded = ((GameObject)Folded.GetValue(rod)).activeSelf;
            originalUnfolded = ((GameObject)Unfolded.GetValue(rod)).activeSelf;
            capturedRod = true;
        }
        internal void Abort()
        {
            if (aborted) return;
            aborted = true;
            try
            {
                ClearStage();
                if (rod != null && (gameplay.Player.Get_ActiveTool == rod || gameplay.Player.Get_HoldingItem == rod)) plugin.Items?.TestPutAwayTool();
                if (capturedRod && rod != null)
                {
                    var line = (TubeRenderer)Line.GetValue(rod);
                    if (originalPoints != null) { line.points = (Vector3[])originalPoints.Clone(); line.Redraw(); }
                    line.gameObject.SetActive(originalLine);
                    ((GameObject)Folded.GetValue(rod)).SetActive(originalFolded);
                    ((GameObject)Unfolded.GetValue(rod)).SetActive(originalUnfolded);
                }
                // Only the host creates the fixture rod. Destroy its exact object even
                // if discovery has not assigned an entity yet; guest replicas are owned
                // by SharedItems and restored/destroyed through session cleanup.
                if (plugin.TestOptions.IsHost && rod != null) UnityEngine.Object.Destroy(rod.gameObject);
            }
            finally
            {
                gameplay.PlayerWalking.transform.SetPositionAndRotation(originalWalkingPosition, originalWalkingRotation);
                var body = gameplay.Get_Player_Rigidbody;
                if (body != null)
                {
                    body.position = originalBodyPosition; body.rotation = originalBodyRotation;
                    body.velocity = originalVelocity; body.angularVelocity = originalAngularVelocity;
                }
            }
        }
        private static void Require(bool condition, string reason)
        { if (!condition) throw new InvalidOperationException("Fishing presentation: " + reason); }
    }
}
