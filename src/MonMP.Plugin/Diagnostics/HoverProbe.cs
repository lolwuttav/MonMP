using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // #164: holds the crosshair still on one control and counts how often its hover drops. Each
    // MouseOut and ResetCurrentInteractable during the hold logs its caller.
    internal sealed class HoverProbe : IDisposable
    {
        private static HoverProbe? active;
        private readonly Gameplay gameplay;
        private readonly Interactable target;
        private readonly Harmony harmony = new Harmony("MonMP.LocalTest.HoverTrace");
        private readonly List<string> traces = new List<string>();
        private readonly float endsAt;
        private int frames, onTarget, drops, lastFrame;
        private bool wasOn;
        private string lastMiss = "";
        internal string Label { get; }

        private readonly Vector3? eye;
        // eye: where to hold the walking camera (world space), re-pinned every frame so gravity and
        // pushes from the vehicle's collider don't move it; null leaves the player where it is (seated).
        internal HoverProbe(Gameplay gameplay, Interactable target, string label, Vector3? eye = null, float seconds = 3f)
        {
            this.gameplay = gameplay; this.target = target; Label = label; this.eye = eye;
            endsAt = Time.unscaledTime + seconds;
            harmony.Patch(AccessTools.Method(typeof(Interactable), nameof(Interactable.MouseOut)), prefix: new HarmonyMethod(typeof(HoverProbe), nameof(OutPrefix)));
            harmony.Patch(AccessTools.Method(target.GetType(), nameof(Interactable.MouseOut)), prefix: new HarmonyMethod(typeof(HoverProbe), nameof(OutPrefix)));
            harmony.Patch(AccessTools.Method(typeof(PlayerInteractable), nameof(PlayerInteractable.ResetCurrentInteractable)), prefix: new HarmonyMethod(typeof(HoverProbe), nameof(ResetPrefix)));
            active = this;
        }

        private static void OutPrefix(Interactable __instance)
        {
            var probe = active;
            if (probe == null || __instance != probe.target || probe.traces.Count >= 6) return;
            probe.traces.Add("MouseOut frame " + Time.frameCount + ":\n" + Environment.StackTrace);
        }
        private static void ResetPrefix()
        {
            var probe = active;
            if (probe == null || probe.traces.Count >= 6) return;
            probe.traces.Add("ResetCurrentInteractable frame " + Time.frameCount + ":\n" + Environment.StackTrace);
        }

        // Aims at the control; returns true once the hold is over.
        internal bool Tick(out string report)
        {
            report = "";
            var controller = gameplay.PlayerWalking.playerCameraController;
            var camera = gameplay.Player.mainCamera;
            if (eye.HasValue)
            {
                var walker = gameplay.PlayerWalking.transform;
                var p = walker.position + (eye.Value - camera.transform.position);
                walker.position = p;
                if (gameplay.Get_Player_Rigidbody != null) { gameplay.Get_Player_Rigidbody.position = p; gameplay.Get_Player_Rigidbody.velocity = Vector3.zero; }
            }
            var point = target.GetComponent<Collider>() is Collider c && c != null ? c.bounds.center : target.transform.position;
            var world = point - camera.transform.position;
            var parent = controller.transform.parent;
            var local = parent != null ? parent.InverseTransformDirection(world) : world;
            var look = Quaternion.LookRotation(local).eulerAngles;
            controller.SetRotationAngles(Mathf.DeltaAngle(0, look.x), look.y);
            if (Time.frameCount != lastFrame)
            {
                lastFrame = Time.frameCount;
                frames++;
                bool on = gameplay.Player.Get_CurrentInteractable == target;
                if (on) onTarget++;
                else if (Physics.Raycast(camera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)), out var hit, 2.4f))
                    lastMiss = hit.collider.name + "@" + hit.distance.ToString("F2") + " (" + gameplay.Player.Get_CurrentInteractable?.name + ")";
                else lastMiss = "nothing";
                if (wasOn && !on) drops++;
                wasOn = on;
            }
            if (Time.unscaledTime < endsAt) return false;
            report = Label + ": frames=" + frames + " onTarget=" + onTarget + " drops=" + drops + " lastMiss=" + lastMiss +
                " distance=" + world.magnitude.ToString("F2") + (traces.Count > 0 ? "\n" + string.Join("\n", traces) : "");
            return true;
        }

        internal int Drops => drops;
        internal int OnTarget => onTarget;

        public void Dispose()
        {
            if (active == this) active = null;
            harmony.UnpatchSelf();
        }
    }
}
