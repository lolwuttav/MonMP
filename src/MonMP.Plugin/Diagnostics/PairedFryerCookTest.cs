using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.World;
using MonMP.Plugin.Replication;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Diagnostics
{
    // Isolated native cook: no synthetic Working/steam/audio phases. Commands below only
    // coordinate observations; native PowerOnButton owns cooking and output creation.
    internal sealed class PairedFryerCookTest
    {
        private const byte Marker = 243;
        private readonly MonMPPlugin plugin;
        private readonly Gameplay gameplay;
        private readonly Furniture_Fryer fryer;
        private readonly HomeGenerator generator;
        private readonly SOUND_Furniture_Fryer sound;
        private readonly Animator animator;
        private readonly ParticleSystem steam;
        private readonly AudioSource boiling;
        private readonly AudioSource oneShot;
        private readonly Part_FuelTank fuel;
        private readonly Store_HydroSaintClin hydro;
        private readonly Furniture_SwitchButton killSwitch;
        private readonly bool host;
        private readonly int originalFilled, originalCurds, originalFriendship, originalHydroDay;
        private readonly bool originalBasket, originalSteam, originalBoil, originalRunning, originalSwitch, originalHydroCut;
        private readonly float originalFuel;
        private readonly AnimatorStateInfo originalAnimation;
        private readonly AnimatorStateInfo[] originalLayers;
        private readonly float[] originalLayerWeights;
        private readonly int cookingLayer;
        private readonly AnimatorCullingMode originalCullingMode;
        private readonly bool[] originalLights;
        private readonly List<GameObject> outputs = new List<GameObject>();
        private WorldChannel? subscribed;
        private Harmony? guard;
        private IEnumerator? ownCook;
        private FieldInfo? cookOwnerField;
        private static PairedFryerCookTest? nativeOwner;
        private int phase, acknowledgment, paritySamples, outputBeforeCut, scoopBeforeCut, trackedCookSteps;
        private byte pendingStage;
        private bool stageAcknowledged;
        private float retryStageAt;
        private uint guestPeer, initialPeer;
        private uint initialHostGuest;
        private float next, deadline, mismatchSince = -1, refillUntil;
        private float secondBoilAt = -1;
        private HashSet<int>? guestOutputsBeforeCut;
        private bool staged, cleaned, hostSawDisconnect, nativeCookPoseObserved;
        private bool animationSamplingStaged;
        private int lastNativeHash, lastCanonicalHash;
        private float lastNativeTime, lastCanonicalTime, nextAnimationLog;
        private string animationTrace = "none";
        private int[] lastLayerHashes = Array.Empty<int>();
        private string layerTelemetry = "not sampled";
        internal string Status => "fryer cook phase=" + phase + "; ack=" + acknowledgment +
            "; working=" + fryer.Get_MachineWorking + "; steam=" + steam.isEmitting + "; boil=" + boiling.isPlaying +
            "; outputs=" + outputs.Count + "; trackedSteps=" + trackedCookSteps + "; cookBound=" + (ownCook != null) +
            "; parity=" + paritySamples + "; cookPose=" + nativeCookPoseObserved +
            "; animator=" + lastNativeHash + "/" + lastNativeTime.ToString("F3") +
            "; canonical=" + lastCanonicalHash + "/" + lastCanonicalTime.ToString("F3") +
            "; originalHash=" + originalAnimation.fullPathHash + "; enabled=" + animator.isActiveAndEnabled +
            "; initialized=" + animator.isInitialized + "; culling=" + animator.cullingMode +
            "; originalCulling=" + originalCullingMode + "; transition=" + animator.IsInTransition(0) +
            "; currentHash=" + animator.GetCurrentAnimatorStateInfo(0).fullPathHash +
            "; nextHash=" + animator.GetNextAnimatorStateInfo(0).fullPathHash + "; updateMode=" + animator.updateMode +
            "; visible=" + animator.GetComponentsInChildren<Renderer>(true).Any(renderer => renderer.isVisible) +
            "; controller=" + (animator.runtimeAnimatorController == null ? "none" : animator.runtimeAnimatorController.name) +
            "; animationTrace=" + animationTrace + "; layers=" + layerTelemetry;

        internal PairedFryerCookTest(MonMPPlugin plugin, Gameplay gameplay)
        {
            this.plugin = plugin; this.gameplay = gameplay; host = plugin.TestOptions.IsHost;
            fryer = plugin.World!.FoodPrep.TestFryer ?? throw new InvalidOperationException("Fryer cook fixture missing.");
            generator = fryer.Get_HomeGenerator ?? plugin.World.Generator.TestGenerator ?? throw new InvalidOperationException("Fryer generator missing.");
            sound = Field<SOUND_Furniture_Fryer>(fryer, "SOUND_Furniture_Fryer");
            animator = Field<Animator>(fryer, "animator"); steam = Field<ParticleSystem>(fryer, "vapeur");
            Require(animator.isInitialized && animator.runtimeAnimatorController != null && animator.layerCount > 0,
                "Native fryer animator is not initialized; animation acceptance cannot be established.");
            boiling = Field<AudioSource>(sound, "BoilingOil"); fuel = Field<Part_FuelTank>(generator, "FuelTank");
            oneShot = Field<AudioSource>(sound, "OneShot");
            Require(!oneShot.isPlaying, "Fixture requires no existing native fryer one-shot playback.");
            hydro = Field<Store_HydroSaintClin>(generator, "store_Hydro"); killSwitch = Field<Furniture_SwitchButton>(generator, "KillSwitchButton");
            Require(!fryer.Get_MachineWorking && !Field<bool>(generator, "EngineRunningCoroutine"), "Fixture requires idle native fryer and generator cord.");
            originalFilled = fryer.Get_PercentFiller; originalCurds = fryer.Get_FromageCurdsInMachine;
            originalFriendship = GameManager.Get_Friendship[12]; originalBasket = SharedFoodPrep.TestBasketVisible(fryer);
            originalSteam = steam.isEmitting; originalBoil = boiling.isPlaying;
            originalAnimation = animator.GetCurrentAnimatorStateInfo(0);
            originalLayers = Enumerable.Range(0, animator.layerCount).Select(layer => animator.GetCurrentAnimatorStateInfo(layer)).ToArray();
            originalLayerWeights = Enumerable.Range(0, animator.layerCount).Select(animator.GetLayerWeight).ToArray();
            cookingLayer = animator.GetLayerIndex("Fryer");
            Require(cookingLayer >= 0, "Native Fryer animation layer is missing.");
            originalCullingMode = animator.cullingMode;
            originalRunning = generator.Get_IsCombustionEngineRunning; originalSwitch = Field<bool>(generator, "isKillSwitchOff");
            originalFuel = fuel.Get_FuelLevel; originalHydroDay = hydro.Get_DaySinceBuildStarted; originalHydroCut = hydro.Get_HydroCut;
            originalLights = Field<Furniture_Light[]>(generator, "LightsConnected").Select(light => light != null && light.Get_IsOn).ToArray();
            deadline = Time.unscaledTime + 160;
            if (host) InstallGuards();
        }

        internal bool Tick()
        {
            Require(Time.unscaledTime < deadline, "Native cook/rejoin exceeded bounded fixture time. " + Status);
            if (cleaned) return true;
            if (!host && phase == 2)
            {
                if (Time.unscaledTime < next) return false;
                AssertOfflineRestoration();
                plugin.BeginPairedTestSession(); phase = 3; return false;
            }
            var world = plugin.World;
            if (world?.Channel.FullySynchronized != true || !world.PersonalProfileReady) return false;
            Attach(world.Channel);
            RetryStage();
            if (!animationSamplingStaged)
            {
                Require(animator.isActiveAndEnabled, "Native fryer animator is disabled/inactive; cannot sample actual native cooking.");
                animationSamplingStaged = true;
                // Both native fryer animators were confirmed AlwaysAnimate. Observe the actual
                // authored mode rather than changing animation visibility behavior in the fixture.
                Debug.Log("MonMP FryerCook native animator sampling enabled; original culling=" + originalCullingMode +
                    "; original hash=" + originalAnimation.fullPathHash + "; camera=" +
                    (Camera.main == null ? "none" : Vector3.Distance(Camera.main.transform.position, fryer.transform.position).ToString("F1")) + " m.");
            }
            if (host) return Host(world);
            var prep = world.FoodPrep;
            if (phase == 0)
            {
                initialPeer = plugin.Session!.LocalPeerId; Signal(1); phase = 1;
            }
            else if (phase == 1)
            {
                CheckParity(prep);
                if (prep.TestCueCount(3) > 0 && !boiling.isPlaying && steam.isEmitting)
                {
                    Require(prep.TestCueCount(1) > 0 && prep.TestCueCount(2) > 0 && prep.TestCueSoundObserved(1) &&
                        prep.TestCueSoundObserved(2) && prep.TestCueSoundObserved(3), "Live native close/open/scoop sounds were not observed.");
                    Require(paritySamples >= 5 && nativeCookPoseObserved, "Insufficient live native animation/effect samples or no native cook pose observed. " + Status);
                    Signal(2); phase = 10;
                }
            }
            else if (phase == 10 && stageAcknowledged)
            {
                plugin.EndPairedTestSession(); Detach();
                Require(!boiling.isPlaying && !steam.isEmitting, "Disconnect retained fixture steam or boiling loop.");
                phase = 2; next = Time.unscaledTime + 1;
            }
            else if (phase == 3)
            {
                Require(plugin.Session!.LocalPeerId != initialPeer, "Guest did not perform a real peer reconnect.");
                // Snapshot history is not replayed: the first cook's close/open are old events.
                Require(prep.TestCueCount(1) == 0 && prep.TestCueCount(2) == 0, "Reconnect replayed historical close/open cues.");
                var canonical = prep.TestCanonicalState(fryer);
                Require(canonical.Working && canonical.SteamPlaying && !canonical.BoilingPlaying,
                    "Reconnect snapshot did not describe the host's actual scoop phase.");
                CheckParity(prep); phase = 4; next = Time.unscaledTime + 1;
            }
            else if (phase == 4)
            {
                CheckParity(prep);
                if (Time.unscaledTime >= next && prep.TestCueCount(3) > 0)
                {
                    Require(prep.TestCueSoundObserved(3), "Future native scoop after reconnect did not play.");
                    Require(prep.TestCueCount(1) == 0 && prep.TestCueCount(2) == 0, "Scoop phase fabricated close/open after joining.");
                    Signal(3); phase = 5;
                }
            }
            else if (phase == 5)
            {
                CheckParity(prep);
                if (!prep.TestCanonicalState(fryer).Working && !steam.isEmitting && !boiling.isPlaying)
                { Signal(4); phase = 6; }
            }
            else if (phase == 6)
            {
                CheckParity(prep);
                if (prep.TestCanonicalState(fryer).BoilingPlaying && steam.isEmitting && boiling.isPlaying && prep.TestCueCount(1) > 0)
                {
                    if (secondBoilAt < 0) secondBoilAt = Time.unscaledTime;
                    if (Time.unscaledTime - secondBoilAt >= 1)
                    {
                        guestOutputsBeforeCut = new HashSet<int>(Object.FindObjectsOfType<Item_Poutine>().Select(item => item.GetInstanceID()));
                        scoopBeforeCut = prep.TestCueCount(3); Signal(5); phase = 7;
                    }
                }
            }
            else if (phase == 7)
            {
                CheckParity(prep);
                if (!prep.TestCanonicalState(fryer).Working && !steam.isEmitting && !boiling.isPlaying && !generator.Get_isEngineRunning)
                {
                    Require(prep.TestCueCount(3) == scoopBeforeCut, "Power cut during boiling fabricated a scoop cue.");
                    Require(guestOutputsBeforeCut!.SetEquals(Object.FindObjectsOfType<Item_Poutine>().Select(item => item.GetInstanceID())),
                        "Guest output objects changed after power cut during boiling.");
                    Signal(6); phase = 8;
                }
            }
            else if (phase == 8 && stageAcknowledged) { Cleanup(); return true; }
            return false;
        }

        private bool Host(SharedWorld world)
        {
            SampleAnimation(null);
            if (phase == 0 && acknowledgment >= 1)
            {
                staged = true;
                initialHostGuest = guestPeer;
                world.Generator.TestSetHydroAvailability(false);
                generator.KillSwitchOff(); fuel.Get_FuelLevel = Math.Max(10, originalFuel); generator.KillSwitchOn(); generator.StartEngine();
                GameManager.Get_Friendship[12] = Math.Max(50, originalFriendship); fryer.BasketUnlocked();
                phase = 1;
            }
            else if (phase == 1 && generator.Get_IsCombustionEngineRunning)
            {
                SeedCook(); fryer.PowerOnButton(); Require(fryer.Get_MachineWorking, "Native PowerOnButton did not start cooking.");
                refillUntil = Time.unscaledTime + 90; phase = 2;
            }
            else if (phase == 2)
            {
                hostSawDisconnect |= plugin.Session!.Peers.Count == 1;
                Require(Time.unscaledTime < refillUntil, "Guest reconnect did not finish within the bounded native scoop refill window.");
                // Bounded real ingredients keep the native coroutine alive through the scene rejoin.
                if (fryer.Get_PercentFiller < 60) fryer.Get_PercentFiller = 100;
                if (fryer.Get_FromageCurdsInMachine < 30) Set(fryer, "FromageCurdsInMachine", 50);
                if (acknowledgment >= 3)
                {
                    Require(hostSawDisconnect && guestPeer != initialHostGuest, "Host did not observe guest departure and a new peer identity.");
                    Require(trackedCookSteps > 0 && outputs.Count > 0, "Native cooking produced no tracked fixture output. " + Status);
                    fryer.Get_PercentFiller = 20; Set(fryer, "FromageCurdsInMachine", 10); phase = 3;
                }
            }
            else if (phase == 3 && acknowledgment >= 4 && !fryer.Get_MachineWorking)
            { SeedCook(); fryer.PowerOnButton(); phase = 4; }
            else if (phase == 4 && acknowledgment >= 5)
            {
                Require(steam.isEmitting && boiling.isPlaying, "Guest power-cut acknowledgment missed the native boiling phase.");
                outputBeforeCut = outputs.Count; generator.KillSwitchOff();
                Require(!generator.Get_isEngineRunning, "Native kill switch failed to cut fixture power."); phase = 5;
            }
            else if (phase == 5 && acknowledgment >= 6 && !fryer.Get_MachineWorking)
            {
                Require(outputs.Count == outputBeforeCut, "Power cut during native boiling fabricated output.");
                Require(!steam.isEmitting && !boiling.isPlaying, "Native power-cut cleanup retained steam or boiling.");
                Cleanup(); return true;
            }
            return false;
        }

        private void SeedCook() { fryer.Get_PercentFiller = 100; Set(fryer, "FromageCurdsInMachine", 50); }
        private void CheckParity(SharedFoodPrep prep)
        {
            var state = prep.TestCanonicalState(fryer);
            SampleAnimation(state);
            bool layersMatch = state.AnimatorLayers.Count == animator.layerCount;
            bool cookPose = false;
            foreach (var carried in state.AnimatorLayers)
            {
                int layer = carried.LayerIndex;
                if (layer >= animator.layerCount) { layersMatch = false; continue; }
                var native = animator.GetCurrentAnimatorStateInfo(layer);
                bool useNext = false;
                if (animator.IsInTransition(layer))
                {
                    var nextState = animator.GetNextAnimatorStateInfo(layer);
                    if (nextState.fullPathHash != 0) { native = nextState; useNext = true; }
                }
                var clips = useNext ? animator.GetNextAnimatorClipInfo(layer) : animator.GetCurrentAnimatorClipInfo(layer);
                bool hasMotion = carried.Weight > 0.01f && animator.GetLayerWeight(layer) > 0.01f &&
                    clips.Any(clip => clip.clip != null && clip.weight > 0.01f);
                float nativeTime = carried.Loop ? Mathf.Repeat(native.normalizedTime, 1) : Mathf.Clamp01(native.normalizedTime);
                float difference = Math.Abs(nativeTime - carried.NormalizedTime);
                if (carried.Loop) difference = Math.Min(difference, 1 - difference);
                // This controller's blank idle states have advancing normalized clocks but no
                // clips contributing visible motion. Keep their hash/weight checks; time parity
                // is meaningful for the actual selected animation clips, including cooking.
                bool layerMatch = carried.StateHash != 0 && native.fullPathHash == carried.StateHash && (!hasMotion || difference < 0.25f) &&
                    Math.Abs(animator.GetLayerWeight(layer) - carried.Weight) < 0.01f;
                bool actualCookClip = clips.Any(clip => clip.clip != null && clip.weight > 0.01f && clip.clip.name == "Fryer_MakePoutine");
                if (layer == cookingLayer && carried.StateHash != originalLayers[layer].fullPathHash)
                    layerMatch &= hasMotion && actualCookClip && difference < 0.25f;
                layersMatch &= layerMatch;
                cookPose |= layerMatch && hasMotion && difference < 0.25f && layer == cookingLayer &&
                    actualCookClip &&
                    carried.StateHash != originalLayers[layer].fullPathHash;
            }
            bool match = steam.isEmitting == state.SteamPlaying && boiling.isPlaying == state.BoilingPlaying && layersMatch;
            if (match)
            {
                mismatchSince = -1; paritySamples++;
                nativeCookPoseObserved |= state.Working && cookPose;
            }
            else
            {
                if (mismatchSince < 0) mismatchSince = Time.unscaledTime;
                Require(Time.unscaledTime - mismatchSince < 1,
                    "Guest presentation did not converge to host animation/steam/boil within 1 s. " + Status);
            }
        }

        private void SampleAnimation(FoodPrepState? canonical)
        {
            var info = animator.GetCurrentAnimatorStateInfo(0);
            if (animator.IsInTransition(0))
            {
                var nextState = animator.GetNextAnimatorStateInfo(0);
                if (nextState.fullPathHash != 0) info = nextState;
            }
            bool changed = info.fullPathHash != lastNativeHash;
            if (lastLayerHashes.Length != animator.layerCount) lastLayerHashes = new int[animator.layerCount];
            var layerDescriptions = new List<string>();
            for (int layer = 0; layer < animator.layerCount; layer++)
            {
                var currentState = animator.GetCurrentAnimatorStateInfo(layer);
                var nextState = animator.GetNextAnimatorStateInfo(layer);
                bool transition = animator.IsInTransition(layer);
                int effectiveHash = transition && nextState.fullPathHash != 0 ? nextState.fullPathHash : currentState.fullPathHash;
                changed |= lastLayerHashes[layer] != effectiveHash; lastLayerHashes[layer] = effectiveHash;
                layerDescriptions.Add(layer + ":" + animator.GetLayerName(layer) + ",weight=" + animator.GetLayerWeight(layer).ToString("F2") +
                    ",current=" + currentState.fullPathHash + "@" + currentState.normalizedTime.ToString("F3") + ",loop=" + currentState.loop +
                    ",next=" + nextState.fullPathHash + "@" + nextState.normalizedTime.ToString("F3") + ",nextLoop=" + nextState.loop +
                    ",transition=" + transition + ",baseline=" + (layer < originalLayers.Length ? originalLayers[layer].fullPathHash : 0) +
                    ",clips=" + string.Join(",", animator.GetCurrentAnimatorClipInfo(layer).Select(clip => clip.clip.name + ":" + clip.weight.ToString("F2"))));
            }
            layerTelemetry = "count=" + animator.layerCount + " [" + string.Join(" | ", layerDescriptions) + "] params=" +
                string.Join(",", animator.parameters.Where(parameter => parameter.name == "MakePoutine" || parameter.name == "PressPotato")
                    .Select(parameter => parameter.name + ":" + parameter.type));
            if (canonical.HasValue)
                layerTelemetry += " carried=[" + string.Join(" | ", canonical.Value.AnimatorLayers.Select(layer =>
                    layer.LayerIndex + ":" + layer.StateHash + "@" + layer.NormalizedTime.ToString("F3") +
                    ",loop=" + layer.Loop + ",weight=" + layer.Weight.ToString("F2"))) + "]";
            lastNativeHash = info.fullPathHash; lastNativeTime = info.normalizedTime;
            if (canonical.HasValue)
            {
                changed |= canonical.Value.AnimatorStateHash != lastCanonicalHash;
                lastCanonicalHash = canonical.Value.AnimatorStateHash; lastCanonicalTime = canonical.Value.AnimatorNormalizedTime;
            }
            if (changed)
            {
                animationTrace += " -> " + info.fullPathHash + "@" + info.normalizedTime.ToString("F2");
                if (animationTrace.Length > 350) animationTrace = animationTrace.Substring(animationTrace.Length - 350);
            }
            if (changed || fryer.Get_MachineWorking && Time.unscaledTime >= nextAnimationLog)
            {
                Debug.Log("MonMP FryerCook " + (host ? "host" : "guest") + " " + Status);
                nextAnimationLog = Time.unscaledTime + 1;
            }
        }

        private void Attach(WorldChannel channel)
        {
            if (subscribed == channel) return;
            Detach(); subscribed = channel;
            if (host) channel.CommandRequested += Command;
            else channel.ReplyReceived += Reply;
        }
        private void Detach()
        {
            if (subscribed != null)
            {
                subscribed.CommandRequested -= Command;
                subscribed.ReplyReceived -= Reply;
            }
            subscribed = null;
        }
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (peer == 0 || entity != ClockState.Entity || payload.Length != 2 || payload[0] != Marker) return;
            if (payload[1] < 1 || payload[1] > 6) return;
            guestPeer = peer; acknowledgment = Math.Max(acknowledgment, payload[1]);
            subscribed!.Reply(peer, entity, new[] { Marker, payload[1] });
        }
        private void Reply(ulong entity, byte[] payload)
        {
            if (entity == ClockState.Entity && payload.Length == 2 && payload[0] == Marker && payload[1] == pendingStage)
                stageAcknowledged = true;
        }
        private void Signal(byte stage)
        {
            pendingStage = stage; stageAcknowledged = false; retryStageAt = 0; RetryStage();
        }
        private void RetryStage()
        {
            if (host || pendingStage == 0 || stageAcknowledged || subscribed == null || Time.unscaledTime < retryStageAt) return;
            // Clock records advance while native cooking runs. Retry this idempotent diagnostic
            // marker with the current revision until the host explicitly acknowledges it.
            subscribed.Request(ClockState.Entity, new[] { Marker, pendingStage });
            retryStageAt = Time.unscaledTime + 0.5f;
        }

        private void InstallGuards()
        {
            Require(nativeOwner == null, "A native fryer fixture is already active."); nativeOwner = this;
            guard = new Harmony("MonMP.LocalTest.FryerCook");
            var achievement = AccessTools.Method(typeof(Steam_AchievementManager), nameof(Steam_AchievementManager.AchievementDone), new[] { typeof(AchievementType) });
            var factory = AccessTools.Method(typeof(Furniture_Fryer), "BuildPoutine", Type.EmptyTypes);
            var cookStep = factory == null ? null : AccessTools.EnumeratorMoveNext(factory);
            try
            {
                Require(cookStep != null && cookStep.ReturnType == typeof(bool) && cookStep.GetParameters().Length == 0,
                    "Native BuildPoutine MoveNext target is missing or has an unsupported shape.");
                cookOwnerField = cookStep!.DeclaringType!.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SingleOrDefault(field => field.FieldType == typeof(Furniture_Fryer));
                Require(cookOwnerField != null, "Native BuildPoutine iterator owner field is missing or ambiguous.");
                guard.Patch(achievement, prefix: new HarmonyMethod(typeof(PairedFryerCookTest), nameof(GuardAchievement)));
                // Patch the actual native step, not its tiny iterator factory: Mono may inline
                // BuildPoutine at the native PowerOnButton caller before a factory postfix runs.
                guard.Patch(cookStep, prefix: new HarmonyMethod(typeof(PairedFryerCookTest), nameof(BeforeCookStep)),
                    postfix: new HarmonyMethod(typeof(PairedFryerCookTest), nameof(AfterCookStep)));
                PatchHealth.VerifyManualPatchInstalled(guard, achievement); PatchHealth.VerifyManualPatchInstalled(guard, cookStep);
                Debug.Log("MonMP FryerCook tracking native iterator " + cookStep.DeclaringType.FullName + "." + cookStep.Name +
                    "; owner field=" + cookOwnerField!.Name);
            }
            catch { guard.UnpatchSelf(); guard = null; nativeOwner = null; throw; }
        }
        private static bool GuardAchievement(AchievementType __0) => nativeOwner == null || __0 != AchievementType.MakeHomemadePoutine;
        private static void BeforeCookStep(object __instance, out HashSet<int>? __state)
        {
            __state = null;
            var owner = nativeOwner;
            if (owner == null || !owner.staged || (owner.cookOwnerField?.GetValue(__instance) as Furniture_Fryer) != owner.fryer) return;
            owner.ownCook = (IEnumerator)__instance; owner.trackedCookSteps++;
            __state = new HashSet<int>(Object.FindObjectsOfType<Item_Poutine>(true)
                .Where(item => item.gameObject.scene.IsValid()).Select(item => item.GetInstanceID()));
        }
        private static void AfterCookStep(object __instance, HashSet<int>? __state)
        {
            var owner = nativeOwner;
            if (__state == null || owner == null || owner.ownCook != __instance) return;
            foreach (var item in Object.FindObjectsOfType<Item_Poutine>(true).Where(item => item.gameObject.scene.IsValid()))
                if (!__state.Contains(item.GetInstanceID())) owner.outputs.Add(item.gameObject);
        }

        private void AssertOfflineRestoration()
        {
            Require(animator.layerCount == originalLayers.Length, "Disconnect changed native animator layer count.");
            for (int layer = 0; layer < originalLayers.Length; layer++)
            {
                var currentState = animator.GetCurrentAnimatorStateInfo(layer);
                var nextState = animator.GetNextAnimatorStateInfo(layer);
                Require(currentState.fullPathHash == originalLayers[layer].fullPathHash &&
                    (!animator.IsInTransition(layer) || nextState.fullPathHash == originalLayers[layer].fullPathHash) &&
                    Math.Abs(animator.GetLayerWeight(layer) - originalLayerWeights[layer]) < 0.01f,
                    "Disconnect did not restore native layer " + layer + " (" + animator.GetLayerName(layer) + ") after 1 s: current=" +
                    currentState.fullPathHash + "; next=" + nextState.fullPathHash + "; expected=" + originalLayers[layer].fullPathHash);
            }
            Require(steam.isEmitting == originalSteam && boiling.isPlaying == originalBoil,
                "Disconnect did not restore original native steam/boil after 1 s.");
            Debug.Log("MonMP FryerCook guest verified original native hashes/weights on all layers and steam/boil after disconnect, before rejoining.");
        }

        internal void Cleanup()
        {
            if (cleaned) return;
            var failures = new List<Exception>();
            void Restore(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
            try
            {
                if (host && staged && fryer != null)
                {
                    Restore(() => { if (ownCook != null) fryer.StopCoroutine(ownCook); });
                    Restore(() => Set(fryer, "Coroutine_BuildingRunning", false));
                    Restore(() => { fryer.Get_PercentFiller = originalFilled; Set(fryer, "FromageCurdsInMachine", originalCurds); });
                    Restore(() => GameManager.Get_Friendship[12] = originalFriendship);
                    Restore(() => SharedFoodPrep.TestSetBasketVisible(fryer, originalBasket));
                    Restore(() => { Set(hydro, "DaySinceBuildStarted", originalHydroDay); Set(hydro, "HydroCut", originalHydroCut); });
                    Restore(() => generator.LoadFromSave(originalFuel, originalRunning, originalSwitch, originalLights));
                    Restore(() => { killSwitch.isOn = originalSwitch; killSwitch.UpdateVisual(); });
                    foreach (var output in outputs) if (output != null) Restore(() => Object.Destroy(output));
                    Restore(() => plugin.Items?.DiscoverSoon());
                }
                if (fryer != null)
                {
                    Restore(() => { if (originalSteam) steam.Play(); else steam.Stop(); });
                    Restore(() => { if (originalBoil) sound.BoilingStart(); else sound.BoilingStop(); });
                    Restore(() => oneShot.Stop());
                    Restore(() =>
                    {
                        animator.ResetTrigger("MakePoutine"); animator.ResetTrigger("PressPotato");
                        for (int layer = 0; layer < originalLayers.Length && layer < animator.layerCount; layer++)
                        {
                            animator.SetLayerWeight(layer, originalLayerWeights[layer]);
                            if (originalLayers[layer].fullPathHash != 0 && animator.HasState(layer, originalLayers[layer].fullPathHash))
                                animator.Play(originalLayers[layer].fullPathHash, layer, originalLayers[layer].normalizedTime);
                        }
                        animator.Update(0);
                    });
                }
            }
            finally
            {
                Detach(); guard?.UnpatchSelf(); guard = null;
                if (nativeOwner == this) nativeOwner = null;
                cleaned = true;
            }
            if (failures.Count > 0) throw new AggregateException("Fryer cook fixture restoration failed.", failures);
        }
        private static T Field<T>(object target, string name) => (T)AccessTools.Field(target.GetType(), name).GetValue(target);
        private static void Set(object target, string name, object value) => AccessTools.Field(target.GetType(), name).SetValue(target, value);
        private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException("Fryer cook test: " + message); }
    }
}
