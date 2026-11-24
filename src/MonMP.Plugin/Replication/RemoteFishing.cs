using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Native Tool_FishingRod casts, draws its line and splashes only on the machine whose player
    // holds it: everyone else saw a folded rod in the angler's hand and nothing on the water (Astra
    // R16). The angler reports equip, settled cast, caught-line and end phases. Observers draw the
    // tube to the water or the rod's native fish attachment; only a cast plays the throw and splash.
    // Fish selection, catch inventory and the HUD stay with the angler.
    internal static class RemoteFishing
    {
        private static readonly FieldInfo? LineField = AccessTools.Field(typeof(Tool_FishingRod), "Tube_FishingLine");
        private static readonly FieldInfo? AttachField = AccessTools.Field(typeof(Tool_FishingRod), "Transform_LineAttachment");
        private static readonly FieldInfo? SplashField = AccessTools.Field(typeof(Tool_FishingRod), "VFX_FishingSplash");
        private static readonly FieldInfo? FoldedField = AccessTools.Field(typeof(Tool_FishingRod), "FishingRod_Folded");
        private static readonly FieldInfo? UnfoldedField = AccessTools.Field(typeof(Tool_FishingRod), "FishingRod_Unfolded");
        private static readonly FieldInfo? CaughtField = AccessTools.Field(typeof(Tool_FishingRod), "FishCaugh");
        private static readonly FieldInfo? FishLocationField = AccessTools.Field(typeof(Tool_FishingRod), "Fish_Location");
        private static readonly FieldInfo? AnimField = AccessTools.Field(typeof(Tool_FishingRod), "Anim");
        // Observers receive the cast only once the line has settled, so the native windup
        // (30 + force*30 degrees back) and release (throwTotal*0.2) replay as one fixed swing.
        private const float SwingWindup = 0.25f, SwingRelease = 0.08f, SwingBack = 60f, BendRelease = 0.4f, CatchSpin = 5f;
        private const byte Cast = 0, End = 1, Caught = 2, Equip = 3, Unequip = 4;

        // The angler's side: the rod this machine's player has equipped and its cast's progress.
        private static Tool_FishingRod? localRod;
        private static Vector3 lastEnd;
        private static bool lineWasOut, castPosted, caughtPosted;
        private static byte localPhase = Equip;
        private static Vector3 localEnd;
        private static FishingCatchOutcome localOutcome;
        private static ushort localTrashTemplate = FishingPresentationState.NoTrashTemplate;
        private static float nextReport;
        private static SOUND_Manager? soundManager;
        private sealed class LocalTrashOutput
        {
            public GameObject Output = null!;
            public ulong Rod;
            public uint Generation;
            public ushort Template;
            public float CapturedAt;
            public bool Dropped;
            public float DroppedAt, LastCosmeticRequestAt;
        }
        private static readonly Dictionary<int, LocalTrashOutput> localTrashOutputs = new Dictionary<int, LocalTrashOutput>();

        internal readonly struct FishingTrashOrigin
        {
            internal readonly ulong Rod;
            internal readonly uint Generation, Ticket;
            internal readonly ushort Template, RemainingMilliseconds;
            internal FishingTrashOrigin(ulong rod, uint generation, uint ticket, ushort template, ushort remainingMilliseconds)
            { Rod = rod; Generation = generation; Ticket = ticket; Template = template; RemainingMilliseconds = remainingMilliseconds; }
        }

        // Everyone else: held-rod presentation, with a baseline for session/owner cleanup.
        private sealed class Remote
        {
            public Tool_FishingRod Rod = null!;
            public Vector3 End; public uint Actor, Generation; public byte Phase;
            public FishingCatchOutcome Outcome; public uint CatchTicket; public ushort TrashTemplate = FishingPresentationState.NoTrashTemplate;
            public TubeRenderer? Line; public GameObject? Folded, Unfolded;
            public bool OriginalLine, OriginalFolded, OriginalUnfolded;
            public Vector3[]? OriginalPoints;
            // When the current phase was first received; a baseline phase counts as long settled.
            public float PhaseSince = float.NegativeInfinity;
            public Animator? Anim; public AnimatorCullingMode OriginalCulling; public bool Bent;
        }
        private static readonly Dictionary<ulong, Remote> remote = new Dictionary<ulong, Remote>();
        internal static bool IsCaught(Tool_FishingRod rod)
        {
            foreach (var state in remote.Values) if (state.Rod == rod) return state.Phase == Caught;
            return CaughtField?.GetValue(rod) is GameObject fish && fish != null;
        }

        // Degrees the observed rod is tilted back about its equip anchor's X axis, as the native
        // windup/release writes Original - right*angle into the equipped rod's localEulerAngles.
        internal static float SwingDegrees(Tool_FishingRod rod)
        {
            foreach (var state in remote.Values)
                if (state.Rod == rod) return state.Phase == Cast ? Swing(Time.unscaledTime - state.PhaseSince) : 0f;
            return 0f;
        }

        // Seconds since the observed catch began hanging; long-settled for a baseline catch.
        internal static float CaughtSeconds(Tool_FishingRod rod)
        {
            foreach (var state in remote.Values)
                if (state.Rod == rod) return state.Phase == Caught ? Time.unscaledTime - state.PhaseSince : float.PositiveInfinity;
            return float.PositiveInfinity;
        }

        // Native Caught spins Fish_Location at Lerp(110, 0, t/5) degrees per second for 5 s.
        internal static float CatchSpinDegrees(float seconds)
        {
            float t = Mathf.Clamp(seconds, 0f, CatchSpin);
            return 110f * (t - t * t / (2f * CatchSpin));
        }

        internal static float Swing(float t)
        {
            if (!(t >= 0f) || t >= SwingWindup + SwingRelease) return 0f;
            if (t < SwingWindup) return SwingBack * Mathf.SmoothStep(0f, 1f, t / SwingWindup);
            return Mathf.Lerp(SwingBack, 0f, (t - SwingWindup) / SwingRelease);
        }

        // Native VerticalBend: release 1 -> 0 over half the throw, then 0.25 -> -0.25 while the
        // line flies, -0.25 while waiting; a hooked fish adds up to -0.1 of bite tension.
        internal static float VerticalBend(byte phase, float t)
        {
            if (phase == Caught) return -0.25f - 0.1f * Mathf.Clamp01(1f - t / CatchSpin);
            if (phase != Cast || !(t >= 0f)) return 0f;
            if (t < SwingWindup) return 0f;
            float r = t - SwingWindup;
            if (r < BendRelease * .5f) return 1f - r / (BendRelease * .5f);
            if (r < BendRelease) return Mathf.Lerp(.25f, -.25f, (r - BendRelease * .5f) / (BendRelease * .5f));
            return -.25f;
        }

        internal static bool IsCatchBranch(Tool_FishingRod rod, Transform child) =>
            FishLocationField?.GetValue(rod) is Transform location && location != null &&
            child != location && child.IsChildOf(location);

        internal static bool TryGetCatchVisual(Tool_FishingRod rod, out Transform location,
            out GameObject output, out bool template)
        {
            location = null!; output = null!; template = false;
            if (rod == null || rod.isEquiped || !(FishLocationField?.GetValue(rod) is Transform attachment) || attachment == null)
                return false;
            foreach (var state in remote.Values)
            {
                if (state.Rod != rod) continue;
                if (state.Phase != Caught) return false;
                var items = MonMPPlugin.Instance?.Items;
                if (items?.IsRemoteFishingRod(rod, state.Actor, state.Generation) != true) return false;
                location = attachment;
                // Native host catches retain the exact child. Other observers receive the
                // result as a separate leased item, so resolve only that owner's unique result.
                if (CaughtField?.GetValue(rod) is GameObject native && native != null && native.transform.IsChildOf(attachment))
                { output = native; return true; }
                var item = items.RemoteFishingCatch(rod, state.Actor, state.Generation, state.Outcome, state.TrashTemplate);
                if (item != null && (item.transform.position - attachment.position).sqrMagnitude <= 8f * 8f)
                { output = item.gameObject; return true; }
                // Trash is not adopted until native drop. Its accepted array identity is
                // enough for a renderer-only copy while it hangs, with no gameplay scripts.
                if (state.Outcome != FishingCatchOutcome.Trash) return false;
                var asset = FishingTrashTemplateCatalog.TemplateAt(state.TrashTemplate);
                if (asset == null) return false;
                output = asset; template = true; return true;
            }
            return false;
        }

        // A replica may receive its phase in the same delta that instantiated it,
        // before Unity calls Start. Native Start establishes the actual idle baseline;
        // replace the earlier serialized-prefab capture before showing the phase again.
        internal static void NativeStarted(Tool_FishingRod rod)
        {
            if (rod == null || rod.isEquiped) return;
            foreach (var r in remote.Values)
                if (r.Rod == rod) { CaptureBaseline(r);
#if MONMP_DEVELOPMENT
LogBaseline("native-start", r);
#endif
 Present(r); }
        }

        internal static void Equipped(Tool_FishingRod rod)
        {
            // Native Equiped unfolds the meshes but does not clear a remote tube. Restore only
            // that tube before relinquishing remote tracking, preserving the new local hand.
            foreach (ulong id in new List<ulong>(remote.Keys))
                if (remote[id].Rod == rod) { RestoreLine(remote[id]); remote.Remove(id); }
            localRod = rod; lineWasOut = castPosted = caughtPosted = false;
            localOutcome = FishingCatchOutcome.None; localTrashTemplate = FishingPresentationState.NoTrashTemplate;
            Post(rod, Equip, rod.transform.position);
        }
        internal static void Unequipped(Tool_FishingRod rod)
        {
            if (localRod != rod) return;
            Post(rod, Unequip, rod.transform.position);
            localRod = null; lineWasOut = castPosted = caughtPosted = false;
        }

        internal static void Update()
        {
            foreach (var pair in localTrashOutputs.ToArray())
            {
                var output = pair.Value;
                if (output.Output == null) { localTrashOutputs.Remove(pair.Key); continue; }
                if (output.Output.transform.parent != null) continue;
                if (!output.Dropped) { output.Dropped = true; output.DroppedAt = Time.unscaledTime; }
                if (Time.unscaledTime - output.DroppedAt >= 6f || output.Template == FishingPresentationState.NoTrashTemplate) continue;
                var plugin = MonMPPlugin.Instance;
                var items = plugin?.Items;
                var trashItem = output.Output.GetComponent<InteractableItems>();
                // The existing catalogued item adoption path owns all supported trash prefabs.
                // Only uncatalogued native array entries use this bounded cosmetic channel.
                if (trashItem != null && items?.SupportsFishingTrashItem(trashItem, output.Template) == true) continue;
                if (plugin?.Session == null || Time.unscaledTime - output.LastCosmeticRequestAt < 0.25f ||
                    SharedFishingPresentation.Active == null ||
                    !SharedFishingPresentation.Active.TryLocalTrashTicket(output.Rod, output.Generation, output.Template, out uint ticket)) continue;
                var t = output.Output.transform;
                if (!ItemPose.TryLocal(t.position.x, t.position.y, t.position.z, t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w, out var pose)) continue;
                ushort remaining = (ushort)Mathf.Clamp(Mathf.CeilToInt((6f - (Time.unscaledTime - output.DroppedAt)) * 1000f), 1, 6000);
                var request = new FishingTrashCosmeticRequest(output.Rod, output.Generation, ticket, output.Template, pose, remaining);
                if (SharedFishingPresentation.Active.SubmitTrashCosmetic(request)) output.LastCosmeticRequestAt = Time.unscaledTime;
            }
            var equipped = MonMPPlugin.Instance?.Gameplay?.Player?.Get_ActiveTool as Tool_FishingRod;
            if (equipped != null && equipped.isEquiped && localRod != equipped) Equipped(equipped);
            if (localRod != null)
            {
                if (!localRod.isEquiped) Unequipped(localRod);
                else
                {
                    Watch(localRod);
                    if (Time.unscaledTime >= nextReport)
                    {
                        nextReport = Time.unscaledTime + 1f;
                        SharedFishingPresentation.Active?.Report(localRod, (FishingPresentationPhase)localPhase,
                            localPhase == Cast ? localEnd : localRod.transform.position, localOutcome, localTrashTemplate);
                    }
                }
            }
            else localRod = null;
            if (remote.Count > 0) Draw();
        }

        private static void Watch(Tool_FishingRod rod)
        {
            if (!(LineField?.GetValue(rod) is TubeRenderer line) || line == null) return;
            bool out_ = line.gameObject.activeSelf && line.points != null && line.points.Length >= 2;
            if (!out_)
            {
                if (castPosted) Post(rod, End, rod.transform.position);
                lineWasOut = castPosted = caughtPosted = false;
                return;
            }
            // A successful catch keeps the native tube active, now attached to Fish_Location rather
            // than the water. Report that phase once without another throw or splash.
            if (CaughtField?.GetValue(rod) is GameObject fish && fish != null)
            {
                if (!caughtPosted) { CaptureCatch(rod, fish); Post(rod, Caught, rod.transform.position); }
                caughtPosted = castPosted = true;
                lineWasOut = true;
                return;
            }
            // While thrown the end flies along its curve; it settles on the water for the wait.
            Vector3 end = rod.transform.TransformPoint(line.points![1]);
            // The throw's curve leaves the rod tip at rest too, so a settled end must be away from it.
            bool away = AttachField?.GetValue(rod) is Transform attach && attach != null && (end - attach.position).sqrMagnitude > 1f;
            if (lineWasOut && !castPosted && away && (end - lastEnd).sqrMagnitude < 0.0001f)
            {
                castPosted = true;
                Post(rod, Cast, end);
            }
            lineWasOut = true; lastEnd = end;
        }

        private static void Post(Tool_FishingRod rod, byte phase, Vector3 at)
        {
            localPhase = phase; localEnd = at; nextReport = Time.unscaledTime + 1f;
            var outcome = phase == Caught ? localOutcome : FishingCatchOutcome.None;
            var trashTemplate = phase == Caught ? localTrashTemplate : FishingPresentationState.NoTrashTemplate;
            SharedFishingPresentation.Active?.Report(rod, (FishingPresentationPhase)phase, at, outcome, trashTemplate);
            if (phase != Caught) { localOutcome = FishingCatchOutcome.None; localTrashTemplate = FishingPresentationState.NoTrashTemplate; }
            // Only the live cast emits a cue. Rejoin and repair replay current state silently.
            if (phase != Cast) return;
            var items = MonMPPlugin.Instance?.Items;
            ulong id = items?.EntityOf(rod) ?? 0;
            var lease = items?.StateOf(id)?.Lease;
            // PresentationEvent.Value is finite and bounded to 100000. Every generation
            // in that range is an exact float integer; higher generations retain durable
            // state but omit this optional cue rather than aliasing a stale generation.
            if (id != 0 && lease.HasValue && lease.Value.Generation <= 100000)
                PresentationReplays.Post(PresentationKind.Fishing, phase, id, at, lease.Value.Generation);
        }

        internal static void ApplyCurrent(FishingPresentationState state)
        {
            if (state.Phase > FishingPresentationPhase.Equip) return;
            if (!(MonMPPlugin.Instance?.Items?.ItemOf(state.Rod) is Tool_FishingRod rod) || rod == null || rod.isEquiped) return;
            var end = new Vector3(state.X, state.Y, state.Z);
            bool fresh = false;
            if (!remote.TryGetValue(state.Rod, out var r) || r.Rod != rod)
            {
                fresh = true;
                if (r != null) Restore(r);
                r = new Remote
                {
                    Rod = rod,
                    Line = LineField?.GetValue(rod) as TubeRenderer,
                    Folded = FoldedField?.GetValue(rod) as GameObject,
                    Unfolded = UnfoldedField?.GetValue(rod) as GameObject,
                    Anim = AnimField?.GetValue(rod) as Animator,
                };
                CaptureBaseline(r);
#if MONMP_DEVELOPMENT
LogBaseline("first-apply", r);
#endif

                remote[state.Rod] = r;
            }
            if (!fresh && r.Phase != (byte)state.Phase) r.PhaseSince = Time.unscaledTime;
            r.End = end; r.Actor = state.Owner; r.Generation = state.Generation; r.Phase = (byte)state.Phase;
            r.Outcome = state.Outcome; r.CatchTicket = state.CatchTicket; r.TrashTemplate = state.TrashTemplate;
            Present(r);
        }

        private static void CaptureCatch(Tool_FishingRod rod, GameObject output)
        {
            localOutcome = FishingCatchOutcome.Unknown;
            localTrashTemplate = FishingPresentationState.NoTrashTemplate;
            var fish = output.GetComponent<Item_Fish>();
            if (fish != null)
            {
                localOutcome = FishingCatchOutcome.Fish;
                var plugin = MonMPPlugin.Instance;
                var hostItems = plugin?.Items;
                if (plugin?.Session?.IsRunning == true && plugin.Session.IsHost && hostItems != null &&
                    fish.GetType() == typeof(Item_Fish) && output.GetComponent<Fishing_Trash>() == null)
                {
                    // FishCaugh is assigned only after native size, pose and Grab are complete.
                    // Keep its rod parent and host hand lease while publishing the exact catch.
                    try { if (hostItems.EnsureHostBinding(fish) == 0) hostItems.DiscoverSoon(); }
                    catch (System.Exception ex) { Debug.LogException(ex); hostItems.DiscoverSoon(); }
                }
            }
            else if (output.GetComponent<Fishing_Trash>() != null)
            {
                if (FishingTrashTemplateCatalog.TryIndex(output, out ushort template))
                { localOutcome = FishingCatchOutcome.Trash; localTrashTemplate = template; }
            }
            else if (output.GetComponent<InteractableItems>() != null) localOutcome = FishingCatchOutcome.Unique;

            // Some native Fishing_Trash entries are visual-only and have no item component.
            // They still need the same host-issued catch ticket for the cosmetic channel.
            if (output.GetComponent<Fishing_Trash>() == null) return;
            var items = MonMPPlugin.Instance?.Items;
            var session = MonMPPlugin.Instance?.Session;
            ulong id = items?.EntityOf(rod) ?? 0;
            var lease = items?.StateOf(id)?.Lease;
            if (id == 0 || !lease.HasValue || session == null || lease.Value.Owner != session.LocalPeerId) return;
            // FishCaugh is the native source reference. At this exact point the result is still under
            // Fish_Location; remember its identity before native drop unparents it.
            if (!(FishLocationField?.GetValue(rod) is Transform location) || location == null ||
                (output.transform != location && !output.transform.IsChildOf(location))) return;
            localTrashOutputs[output.GetInstanceID()] = new LocalTrashOutput
            { Output = output, Rod = id, Generation = lease.Value.Generation, Template = localTrashTemplate, CapturedAt = Time.unscaledTime };
            if (localTrashOutputs.Count > 32)
                foreach (var stale in localTrashOutputs.OrderBy(p => p.Value.CapturedAt).Take(localTrashOutputs.Count - 32)
                    .Select(p => p.Key).ToArray()) localTrashOutputs.Remove(stale);
        }

        // A result remains queued until the host has accepted its caught phase and returned its
        // one-use ticket. Unknown/unmapped native objects never become generic item spawns.
        internal static bool TryResolveLocalFishingTrash(InteractableItems item, out FishingTrashOrigin origin, out bool pending)
        {
            origin = default; pending = false;
            if (item == null || item.GetComponent<Fishing_Trash>() == null ||
                !localTrashOutputs.TryGetValue(item.gameObject.GetInstanceID(), out var output) || output.Output != item.gameObject) return false;
            if (output.Output == null || (output.Dropped && Time.unscaledTime - output.DroppedAt > 8f))
            { localTrashOutputs.Remove(item.gameObject.GetInstanceID()); return false; }
            if (output.Template == FishingPresentationState.NoTrashTemplate) return false;
            if (!output.Dropped) { pending = true; return false; }
            int remaining = Mathf.CeilToInt((6f - (Time.unscaledTime - output.DroppedAt)) * 1000f);
            if (remaining <= 0) return false;
            if (SharedFishingPresentation.Active == null ||
                !SharedFishingPresentation.Active.TryLocalTrashTicket(output.Rod, output.Generation, output.Template, out uint ticket))
            { pending = true; return false; }
            origin = new FishingTrashOrigin(output.Rod, output.Generation, ticket, output.Template, (ushort)Mathf.Min(remaining, 6000));
            return true;
        }

        internal static bool IsLocalFishingTrashOutput(InteractableItems item) => item != null &&
            item.GetComponent<Fishing_Trash>() != null && localTrashOutputs.TryGetValue(item.gameObject.GetInstanceID(), out var output) &&
            output.Output == item.gameObject && Time.unscaledTime - output.CapturedAt <= 30f;

        internal static bool TryLocalFishingTrashDropTime(GameObject item, out float droppedAt)
        {
            droppedAt = 0;
            return item != null && localTrashOutputs.TryGetValue(item.GetInstanceID(), out var output) &&
                output.Output == item && output.Dropped && float.IsFinite(output.DroppedAt) &&
                (droppedAt = output.DroppedAt) >= 0;
        }

        internal static void Play(PresentationEvent e)
        {
            if (e.Variant != Cast || !(MonMPPlugin.Instance?.Items?.ItemOf(e.Target) is Tool_FishingRod rod) || rod == null || rod.isEquiped) return;
            var end = new Vector3(e.X, e.Y, e.Z);
            if (soundManager == null) soundManager = Object.FindObjectOfType<SOUND_Manager>();
            var sound = soundManager;
            if (sound != null)
            {
                using (SpatialSounds.At(rod.transform.position)) sound.PlayOneShot2D(sound.Fishing_Rod_Swing, _randomPitch: true);
                var splashes = sound.Fishing_WaterSplashes;
                if (splashes != null && splashes.Length > 0)
                    using (SpatialSounds.At(end)) sound.PlayOneShot2D(splashes[Random.Range(0, splashes.Length)], _randomPitch: false);
            }
            if (SplashField?.GetValue(rod) is ParticleSystem splash && splash != null)
            {
                var copy = Object.Instantiate(splash);
                copy.transform.position = end;
                copy.Play();
            }
        }

        private static void Draw()
        {
            List<ulong>? done = null;
            foreach (var pair in remote)
            {
                var r = pair.Value;
                if (r.Rod == null || r.Rod.isEquiped || !r.Rod.gameObject.activeInHierarchy)
                { (done ??= new List<ulong>()).Add(pair.Key); continue; }
                if (r.Phase == Cast && (r.Rod.transform.position - r.End).sqrMagnitude > 60f * 60f) r.Phase = End;
                Present(r);
                if (r.Phase != Cast && r.Phase != Caught) continue;
                if (!(LineField?.GetValue(r.Rod) is TubeRenderer line) || line == null || !(AttachField?.GetValue(r.Rod) is Transform attach) || attach == null) continue;
                var points = line.points;
                if (points == null || points.Length < 2) continue;
                Vector3 end = r.End;
                if (r.Phase == Caught)
                {
                    if (!(FishLocationField?.GetValue(r.Rod) is Transform location) || location == null) continue;
                    end = location.position;
                }
                points[0] = r.Rod.transform.InverseTransformPoint(attach.position);
                points[1] = r.Rod.transform.InverseTransformPoint(end);
                line.Redraw();
            }
            if (done != null) foreach (ulong id in done) Remove(id);
        }

        private static void Remove(ulong id)
        {
            if (!remote.TryGetValue(id, out var r)) return;
            remote.Remove(id);
            Restore(r);
        }
        internal static void RemoveCurrent(ulong id) => Remove(id);

        private static void Restore(Remote r)
        {
            if (r.Rod == null || r.Rod.isEquiped) return;
            RestoreLine(r);
            if (r.Anim != null && r.Bent)
            {
                r.Anim.SetFloat("VerticalBend", 0f); r.Anim.SetFloat("HorizontalBend", 0f);
                r.Anim.cullingMode = r.OriginalCulling; r.Bent = false;
            }
            if (r.Folded != null) r.Folded.SetActive(r.OriginalFolded);
            if (r.Unfolded != null) r.Unfolded.SetActive(r.OriginalUnfolded);

#if MONMP_DEVELOPMENT
            LogBaseline("restored", r);
#endif

        }

        private static void CaptureBaseline(Remote r)
        {
            if (r.Line != null)
            {
                r.OriginalLine = r.Line.gameObject.activeSelf;
                r.OriginalPoints = r.Line.points == null ? null : (Vector3[])r.Line.points.Clone();
            }
            if (r.Folded != null) r.OriginalFolded = r.Folded.activeSelf;
            if (r.Unfolded != null) r.OriginalUnfolded = r.Unfolded.activeSelf;
        }

#if MONMP_DEVELOPMENT
        private static void LogBaseline(string reason, Remote r)
        {

#if MONMP_DEVELOPMENT
            if (MonMPPlugin.Instance?.TestOptions.Enabled != true) return;
#endif

            Debug.Log("MonMP fishing baseline " + reason + "; frame=" + Time.frameCount + "; rod=" +
                (MonMPPlugin.Instance?.Items?.EntityOf(r.Rod) ?? 0).ToString("X16") + "; original line=" + r.OriginalLine +
                ",folded=" + r.OriginalFolded + ",unfolded=" + r.OriginalUnfolded + "; " + TestNativePresentation(r.Rod));
        }
#endif


        private static void RestoreLine(Remote r)
        {
            if (r.Line != null)
            {
                if (r.OriginalPoints != null) { r.Line.points = (Vector3[])r.OriginalPoints.Clone(); r.Line.Redraw(); }
                r.Line.gameObject.SetActive(r.OriginalLine);
            }
        }

        private static void Present(Remote r)
        {
            Fold(r.Rod, r.Phase == Unequip);
            // The line leaves the tip at release, not during the replayed windup.
            bool winding = r.Phase == Cast && Time.unscaledTime - r.PhaseSince < SwingWindup;
            if (r.Line != null) r.Line.gameObject.SetActive(r.Phase == Caught || r.Phase == Cast && !winding);
            Bend(r);
        }

        // The copied held model bakes the source rod's skinned mesh, so the source Animator
        // drives the observed bend. Its renderers are hidden, so it must not be culled meanwhile.
        private static void Bend(Remote r)
        {
            if (r.Anim == null) return;
            float vertical = VerticalBend(r.Phase, Time.unscaledTime - r.PhaseSince);
            if (vertical == 0f && !r.Bent) return;
            if (!r.Bent) { r.OriginalCulling = r.Anim.cullingMode; r.Bent = true; }
            r.Anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            r.Anim.SetFloat("VerticalBend", vertical);
            r.Anim.SetFloat("HorizontalBend", 0f);
            if (vertical == 0f) { r.Anim.cullingMode = r.OriginalCulling; r.Bent = false; }
        }

        private static void Fold(Tool_FishingRod rod, bool folded)
        {
            if (FoldedField?.GetValue(rod) is GameObject f && f != null) f.SetActive(folded);
            if (UnfoldedField?.GetValue(rod) is GameObject u && u != null) u.SetActive(!folded);
        }

        internal static void PeerLeft(uint peer)
        {
            List<ulong>? gone = null;
            foreach (var pair in remote) if (pair.Value.Actor == peer) (gone ??= new List<ulong>()).Add(pair.Key);
            if (gone != null) foreach (ulong id in gone) Remove(id);
        }

        internal static void Clear()
        {
            foreach (ulong id in new List<ulong>(remote.Keys)) Remove(id);
            localRod = null; lineWasOut = castPosted = caughtPosted = false;
            localOutcome = FishingCatchOutcome.None; localTrashTemplate = FishingPresentationState.NoTrashTemplate; localTrashOutputs.Clear();
            soundManager = null;
        }


#if MONMP_DEVELOPMENT
        internal static bool TestPresented(ulong id, FishingPresentationPhase phase) => remote.TryGetValue(id, out var r) && r.Phase == (byte)phase;


        internal static string TestNativePresentation(Tool_FishingRod rod) => "native equipped=" + rod.isEquiped +
            ",line=" + (LineField?.GetValue(rod) is TubeRenderer line && line != null && line.gameObject.activeSelf) +
            ",folded=" + (FoldedField?.GetValue(rod) is GameObject folded && folded != null && folded.activeSelf) +
            ",unfolded=" + (UnfoldedField?.GetValue(rod) is GameObject unfolded && unfolded != null && unfolded.activeSelf);
#endif

    }
}
