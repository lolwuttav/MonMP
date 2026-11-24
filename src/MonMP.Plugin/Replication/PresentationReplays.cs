using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // The receiving side of the presentation events owned by no single adapter (garden tools, the
    // small-engine pull cord, road-crossing animals, bolt work and install clicks), and the local
    // posts that feed them. Every replay runs inside SharedPresentationEvents.Replaying, so the
    // native sound calls it makes are not reported again.
    internal static class PresentationReplays
    {
        internal static SharedPresentationEvents? Events => MonMPPlugin.Instance?.World?.Events;

        internal static void Post(PresentationKind kind, byte variant, ulong target, Vector3 at, float value = 0)
        {
            if (SharedPresentationEvents.Replaying) return;
            Events?.Post(kind, variant, target, at, value);
        }
        internal static void VehicleWindowClick(PresentationEvent e) => MonMPPlugin.Instance?.World?.Vehicles.Windows.PlayClick(e);

        private static Vector3 At(PresentationEvent e) => new Vector3(e.X, e.Y, e.Z);
        private static SOUND_Manager? Sound => Singleton<SOUND_Manager>.i;
        internal static void TirePump(PresentationEvent e) => TirePumpPresentation.Play(e);

        // ---- Garden (#9) --------------------------------------------------------------------------
        private static GameObject? fertilizerPuff;
        private static readonly System.Reflection.FieldInfo? PuffField = AccessTools.Field(typeof(Tool_FertilizerPack), "VFX_FertilizerPack");

        internal static void Fertilized(PresentationEvent e)
        {
            var at = At(e);
            using (SpatialSounds.At(at)) Sound?.PlayOneShot2D(Sound.PartInstalledInDirt, _randomPitch: true);
            if (fertilizerPuff == null)
                foreach (var pack in Resources.FindObjectsOfTypeAll<Tool_FertilizerPack>())
                    if (PuffField?.GetValue(pack) is GameObject puff && puff != null) { fertilizerPuff = puff; break; }
            if (fertilizerPuff == null) return;
            var patch = Object.FindObjectsOfType<Garden_DirtPatch>().Where(p => p != null && (p.transform.position - at).sqrMagnitude < 0.5f * 0.5f)
                .OrderBy(p => (p.transform.position - at).sqrMagnitude).FirstOrDefault();
            if (patch != null) Object.Instantiate(fertilizerPuff, patch.transform);
            else Object.Instantiate(fertilizerPuff, at, Quaternion.identity);
        }

        internal static void Planted(PresentationEvent e)
        {
            using (SpatialSounds.At(At(e))) Sound?.PlaySound_PartInstalledInDirt();
        }

        internal static void Shoveled(PresentationEvent e)
        {
            using (SpatialSounds.At(At(e))) Sound?.PlayOneShot2D(Sound.Tool_DirtShovel, _randomPitch: true);
        }

        // ---- Generator and wood-splitter pull cord (#16) -------------------------------------------
        internal static void PullStart(PresentationEvent e)
        {
            if (MachineControlPresentation.PlayPull(e)) return;
            var at = At(e);
            var sound = Object.FindObjectsOfType<SOUND_WoodSplitter>().Where(s => s != null && (s.transform.position - at).sqrMagnitude < 3f * 3f)
                .OrderBy(s => (s.transform.position - at).sqrMagnitude).FirstOrDefault();
            if (sound != null) sound.PullStart();
        }

        // ---- Install clicks the host heard alone (the fryer basket, #20) ----------------------------
        internal static void PartInstalled(PresentationEvent e)
        {
            using (SpatialSounds.At(At(e))) Sound?.PlaySound_PartInstalled();
        }

        // ---- Road-crossing animals (#19) -------------------------------------------------------------
        private static readonly System.Reflection.FieldInfo? AnimalRunning = AccessTools.Field(typeof(AnimalRunAcrossTheRoad), "Coroutine_AnimalRunning");
        private static readonly System.Reflection.FieldInfo? AnimalLastRun = AccessTools.Field(typeof(AnimalRunAcrossTheRoad), "LastTimeItRun");
        private static readonly System.Reflection.FieldInfo? AnimalMesh = AccessTools.Field(typeof(AnimalRunAcrossTheRoad), "mesh");
        private static readonly System.Reflection.FieldInfo? AnimalFurs = AccessTools.Field(typeof(AnimalRunAcrossTheRoad), "AnimalFurs");
        private static readonly System.Reflection.MethodInfo? AnimalRun = AccessTools.Method(typeof(AnimalRunAcrossTheRoad), "AnimalRunning");
        private static Dictionary<ulong, AnimalRunAcrossTheRoad>? animals;

        internal static bool IsAnimalRunning(AnimalRunAcrossTheRoad animal) => AnimalRunning?.GetValue(animal) is true;

        private static ulong AnimalId(AnimalRunAcrossTheRoad animal) => StaticId.Hash(SharedBuildingDoors.StablePath(animal.transform) + "/animal-crossing-v1");

        // This machine's driver set an animal running: everyone else sees the same one, same fur.
        internal static void AnimalStarted(AnimalRunAcrossTheRoad animal)
        {
            byte fur = 0;
            if (AnimalMesh?.GetValue(animal) is SkinnedMeshRenderer mesh && mesh != null && AnimalFurs?.GetValue(animal) is Material[] furs)
            {
                string name = mesh.sharedMaterial != null ? mesh.sharedMaterial.name : "";
                int index = Array.FindIndex(furs, m => m != null && name.StartsWith(m.name, StringComparison.Ordinal));
                if (index > 0 && index < 256) fur = (byte)index;
            }
            Post(PresentationKind.AnimalCrossing, fur, AnimalId(animal), animal.transform.position);
        }

        internal static void AnimalCrossing(PresentationEvent e)
        {
            if (animals == null || !animals.ContainsKey(e.Target))
            {
                animals = new Dictionary<ulong, AnimalRunAcrossTheRoad>();
                foreach (var candidate in Object.FindObjectsOfType<AnimalRunAcrossTheRoad>(true))
                    if (candidate != null && candidate.gameObject.scene.IsValid()) animals[AnimalId(candidate)] = candidate;
            }
            if (!animals.TryGetValue(e.Target, out var animal) || animal == null || !animal.isActiveAndEnabled || IsAnimalRunning(animal) || AnimalRun == null) return;
            if (AnimalMesh?.GetValue(animal) is SkinnedMeshRenderer mesh && mesh != null && AnimalFurs?.GetValue(animal) is Material[] furs && e.Variant < furs.Length && furs[e.Variant] != null)
                mesh.material = furs[e.Variant];
            AnimalLastRun?.SetValue(animal, Time.timeSinceLevelLoad);
            animal.StartCoroutine((System.Collections.IEnumerator)AnimalRun.Invoke(animal, null));
        }

        // ---- Bolt work with a spanner or impact wrench (#21) -----------------------------------------
        internal enum BoltCue : byte { ScrewStart = 1, ScrewStop, UnscrewStart, UnscrewStop, Installed, Uninstalled, Impact }
        private static bool screwing, unscrewing;
        private static float nextBoltRenewal;
        private static Vector3 boltWorkAt;

        // A native bolt cue on this machine, only while this player works with a bolt tool.
        internal static void LocalBoltCue(BoltCue cue)
        {
            if (SharedPresentationEvents.Replaying || Events?.Live != true) return;
            // The native coroutine sends its final off after UnEquipTool cleared activeTool.
            // A tracked stop still belongs to the work that started it, regardless of the new hand.
            if (cue == BoltCue.ScrewStop || cue == BoltCue.UnscrewStop)
            {
                if (cue == BoltCue.ScrewStop) { if (!screwing) return; screwing = false; }
                else { if (!unscrewing) return; unscrewing = false; }
                Post(PresentationKind.BoltWork, (byte)cue, 0, boltWorkAt);
                return;
            }
            var gameplay = MonMPPlugin.Instance?.Gameplay;
            var tool = gameplay?.Player?.Get_ActiveTool;
            if (!(tool is Tool_AdjustableSpanner) && !(tool is Tool_Air_Impact)) return;
            switch (cue)
            {
                case BoltCue.ScrewStart: if (screwing) return; screwing = true; break;
                case BoltCue.UnscrewStart: if (unscrewing) return; unscrewing = true; break;
            }
            var camera = gameplay!.Player.mainCamera;
            var at = camera != null ? camera.transform.position + camera.transform.forward * 0.6f : tool.transform.position;
            boltWorkAt = at;
            if (cue == BoltCue.ScrewStart || cue == BoltCue.UnscrewStart) nextBoltRenewal = Time.unscaledTime + 0.5f;
            Post(PresentationKind.BoltWork, (byte)cue, 0, at);
        }

        internal static void TickBoltWork()
        {
            if ((!screwing && !unscrewing) || Events?.Live != true) return;
            var tool = MonMPPlugin.Instance?.Gameplay?.Player?.Get_ActiveTool;
            if (!(tool is Tool_AdjustableSpanner) && !(tool is Tool_Air_Impact))
            {
                if (screwing) LocalBoltCue(BoltCue.ScrewStop);
                if (unscrewing) LocalBoltCue(BoltCue.UnscrewStop);
                return;
            }
            if (Time.unscaledTime < nextBoltRenewal) return;
            nextBoltRenewal = Time.unscaledTime + 0.5f;
            if (screwing) Post(PresentationKind.BoltWork, (byte)BoltCue.ScrewStart, 0, boltWorkAt);
            if (unscrewing) Post(PresentationKind.BoltWork, (byte)BoltCue.UnscrewStart, 0, boltWorkAt);
        }

        internal static void BoltWork(PresentationEvent e)
        {
            var sound = Sound;
            if (sound == null) return;
            using (SpatialSounds.At(At(e), e.Actor, expireLoop: true))
            {
                switch ((BoltCue)e.Variant)
                {
                    case BoltCue.ScrewStart: sound.PlaySound_BoltScrewing(); break;
                    case BoltCue.ScrewStop: sound.PlaySound_BoltScrewingOff(); break;
                    case BoltCue.UnscrewStart: sound.PlaySound_BoltUnScrewing(); break;
                    case BoltCue.UnscrewStop: sound.PlaySound_BoltUnScrewingOff(); break;
                    case BoltCue.Installed: sound.PlaySound_BoltScrewingInstalled(); break;
                    case BoltCue.Uninstalled: sound.PlaySound_BoltScrewingUnInstalled(); break;
                    case BoltCue.Impact: sound.PlaySound_ImpactUnScrew(); break;
                }
            }
        }

        internal static void Reset() { animals = null; screwing = unscrewing = false; boltWorkAt = default; nextBoltRenewal = 0; }
    }
}
