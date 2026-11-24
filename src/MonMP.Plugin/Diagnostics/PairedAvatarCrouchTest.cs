using System;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace MonMP.Plugin.Diagnostics
{
    // Sends diagnostic shirt/crouch values through the real paired pose stream and checks the
    // remote shirt material and head presentation. Native input binding is inspected separately.
    // Then the guest runs the native emote handlers (Sorry held, Slur, Spit, vomit, the interaction
    // poke and petting Leo) and hand states (map, flashlight, joint and a puff), then walks; the host
    // checks the raised hand, both voice lines with their text, the presentation-only spit drop,
    // every emote, the hand states with their prop copies, and native footsteps.
    internal sealed class PairedAvatarCrouchTest
    {
        private const byte TestShirt = 7; // Purple
        private readonly MonMPPlugin plugin;
        private int phase;
        private uint guest;
        private float next, deadline;
        private string preview = "pending";
        private bool sawSorry, sawVoiceText, sawMap, sawFlashlight, sawJoint, sawSmoking;
        private int recordedBeforeEmotes, footstepsBefore = -1;
        private Vector3 walkFrom;

        internal PairedAvatarCrouchTest(MonMPPlugin plugin) { this.plugin = plugin; }
        internal string Status => $"avatar pose/shirt phase={phase}; guest={guest}; head={plugin.RemoteAvatarHeadHeight(guest):0.00}; shirt={plugin.RemoteAvatarShirt(guest)}; preview={preview}; body={plugin.RemoteAvatarBody(guest)}; emotes={plugin.RemoteAvatarEmotes(guest)}; sorry={sawSorry}; text={sawVoiceText}; spit={plugin.RemoteSpitDrops}; hands={plugin.RemoteAvatarHands(guest)}; props={plugin.RemoteAvatarHandProps(guest)}; map={sawMap}; flashlight={sawFlashlight}; joint={sawJoint}; smoking={sawSmoking}; footsteps={plugin.RemoteFootsteps - Math.Max(0, footstepsBefore)}";
        private bool Stock => plugin.RemoteAvatarStockBody(guest);
        private bool Crouched(float head) => Stock ? head is > 0.6f and < 1.3f : head is > 0.95f and < 1.15f;
        private bool Standing(float head) => head > (Stock ? 1.4f : 1.6f);

        internal bool Tick()
        {
            if (plugin.Session?.IsReady != true || plugin.World?.Channel.FullySynchronized != true) return false;
            if (deadline == 0) deadline = Time.unscaledTime + 100;
            if (Time.unscaledTime > deadline) throw new InvalidOperationException("Avatar crouch test stalled: " + Status);
            return plugin.TestOptions.IsHost ? Host() : Guest();
        }

        private bool Host()
        {
            if (phase == 0)
            {
                if (plugin.Session!.Peers.Count < 2) return false;
                guest = plugin.Session.Peers.Keys.First(p => p != 0);
                // Remote players must wear the stock NPC body, not the fallback mannequin.
                if (!Stock) return false;
                phase = 1;
            }
            else if (phase == 1 && Crouched(plugin.RemoteAvatarHeadHeight(guest)))
            {
                if (plugin.RemoteAvatarShirt(guest) != TestShirt)
                    throw new InvalidOperationException("Remote avatar did not apply the guest's streamed purple shirt: " + Status);
                phase = 2;
            }
            else if (phase == 2 && Standing(plugin.RemoteAvatarHeadHeight(guest)))
            {
                if (plugin.RemoteAvatarShirt(guest) != TestShirt)
                    throw new InvalidOperationException("Remote avatar lost the guest's shirt while standing: " + Status);
                CapturePreview();
                phase = 3;
            }
            else if (phase == 3)
            {
                if (footstepsBefore < 0) footstepsBefore = plugin.RemoteFootsteps;
                var hands = plugin.RemoteAvatarHands(guest);
                if (!sawMap && (hands & Core.Protocol.PlayerHands.Map) != 0) { sawMap = true; LiveShot("map", AvatarPreviewCapture.View.Back); }
                if (!sawFlashlight && plugin.RemoteAvatarFlashlightInHand(guest)) { sawFlashlight = true; LiveShot("flashlight", AvatarPreviewCapture.View.Side); }
                if ((hands & Core.Protocol.PlayerHands.Joint) != 0) sawJoint = true;
                if (!sawSmoking && (hands & Core.Protocol.PlayerHands.Smoking) != 0) { sawSmoking = true; LiveShot("smoking", AvatarPreviewCapture.View.Side); }
                if (!sawSorry && plugin.RemoteAvatarSorry(guest) > 0.95f && plugin.RemoteAvatarHandRaised(guest))
                {
                    sawSorry = true;
                    try { AvatarPreviewCapture.SaveBitmap(plugin.RemoteAvatarRoot(guest)!, Path.Combine(Paths.BepInExRootPath, "MonMP", "diagnostics", "avatar-live-sorry.bmp")); }
                    catch (Exception ex) { preview += "; live sorry unavailable (" + ex.GetType().Name + ")"; }
                }
                if (plugin.RemoteAvatarVoiceText(guest).Length > 0) sawVoiceText = true;
                string emotes = plugin.RemoteAvatarEmotes(guest);
                if (sawSorry && sawVoiceText && emotes.Contains("Oups") && emotes.Contains("Sacre") && emotes.Contains("Spit") &&
                    plugin.RemoteSpitDrops.Spawned > 0 && plugin.RemoteSpitDrops.Landed > 0 &&
                    emotes.Contains("Vomit") && emotes.Contains("Interact") && emotes.Contains("PetLeo") &&
                    sawMap && sawFlashlight && sawJoint && sawSmoking && plugin.RemoteFootsteps > footstepsBefore)
                {
                    string props = plugin.RemoteAvatarHandProps(guest);
                    if (!props.Contains("map") || !props.Contains("flashlight") || !props.Contains("joint"))
                        throw new InvalidOperationException("Remote avatar is missing native hand prop copies: " + Status);
                    phase = 4;
                }
            }
            else if (phase == 4 && plugin.Session!.Peers.Count == 1 && plugin.RemoteAvatarCount == 0)
                return true;
            return false;
        }

        private bool Guest()
        {
            if (phase == 0 && plugin.RemoteAvatarCount > 0)
            {
                plugin.TestShirtOverride = TestShirt;
                plugin.TestCrouchOverride = true;
                next = Time.unscaledTime + 2;
                phase = 1;
            }
            else if (phase == 1 && Time.unscaledTime >= next)
            {
                plugin.TestCrouchOverride = false;
                next = Time.unscaledTime + 2;
                phase = 2;
            }
            else if (phase == 2 && Time.unscaledTime >= next)
            {
                plugin.TestCrouchOverride = null;
                recordedBeforeEmotes = Patches.LocalEmotes.TestRecorded;
                Native("SorryDown");
                next = Time.unscaledTime + 1.5f;
                phase = 3;
            }
            else if (phase == 3 && Time.unscaledTime >= next)
            {
                Native("SorryUp");
                // The native player voice has a 2 s cooldown after the Sorry "Oups".
                next = Time.unscaledTime + 1.5f;
                phase = 4;
            }
            else if (phase == 4 && Time.unscaledTime >= next)
            {
                Native("SlurDown");
                next = Time.unscaledTime + 1f;
                phase = 5;
            }
            else if (phase == 5 && Time.unscaledTime >= next)
            {
                Native("SpitDown");
                next = Time.unscaledTime + 1f;
                phase = 6;
            }
            else if (phase == 6 && Time.unscaledTime >= next)
            {
                var drunk = UnityEngine.Object.FindObjectOfType<DrunkEffect>() ?? throw new InvalidOperationException("The guest has no DrunkEffect to vomit.");
                drunk.Vomit();
                next = Time.unscaledTime + 1f;
                phase = 7;
            }
            else if (phase == 7 && Time.unscaledTime >= next)
            {
                Native("Interaction");
                Native("InteractionPetLeo");
                next = Time.unscaledTime + 1.5f;
                phase = 8;
            }
            else if (phase == 8 && Time.unscaledTime >= next)
            {
                Native("Map_Toggle");
                next = Time.unscaledTime + 1.5f;
                phase = 9;
            }
            else if (phase == 9 && Time.unscaledTime >= next)
            {
                Native("Map_Toggle");
                Native("FlashLight_Toggle");
                next = Time.unscaledTime + 1.5f;
                phase = 10;
            }
            else if (phase == 10 && Time.unscaledTime >= next)
            {
                Native("FlashLight_Toggle");
                // The joint's own animator states, without spending an inventory joint.
                Native("JointInHand_Start");
                Native("SmokingJoint_Start");
                next = Time.unscaledTime + 1.5f;
                phase = 11;
            }
            else if (phase == 11 && Time.unscaledTime >= next)
            {
                Native("SmokingJoint_Stop");
                Native("JointInHand_Stop");
                var walker = UnityEngine.Object.FindObjectOfType<Gameplay>()?.PlayerWalking;
                walkFrom = walker != null ? walker.transform.position : Vector3.zero;
                next = Time.unscaledTime + 3f;
                phase = 12;
            }
            else if (phase == 12)
            {
                // Walk a metre and a half out and back so the host hears footsteps.
                var gameplay = UnityEngine.Object.FindObjectOfType<Gameplay>();
                var walker = gameplay?.PlayerWalking;
                if (walker != null)
                {
                    float left = next - Time.unscaledTime;
                    var target = walkFrom + walker.transform.forward * Mathf.PingPong((3f - left) * 1.4f, 1.5f);
                    walker.transform.position = new Vector3(target.x, walker.transform.position.y, target.z);
                    if (gameplay!.Get_Player_Rigidbody != null) gameplay.Get_Player_Rigidbody.position = walker.transform.position;
                }
                if (Time.unscaledTime >= next) { next = Time.unscaledTime + 3f; phase = 13; }
            }
            else if (phase == 13 && Time.unscaledTime >= next)
            {
                if (Patches.LocalEmotes.TestRecorded - recordedBeforeEmotes < 6)
                    throw new InvalidOperationException("The guest's native emote handlers recorded " + (Patches.LocalEmotes.TestRecorded - recordedBeforeEmotes) + " of 6 emotes (Oups, Sacre, Spit, Vomit, Interact, PetLeo).");
                plugin.TestShirtOverride = null;
                plugin.EndPairedTestSession();
                return true;
            }
            return false;
        }

        // The native key handlers, as the Sorry/Slur/Spit input actions call them.
        private static void Native(string handler)
        {
            var hands = UnityEngine.Object.FindObjectOfType<PlayerVisualHands>();
            if (hands == null) throw new InvalidOperationException("The guest has no PlayerVisualHands for " + handler + ".");
            var method = AccessTools.Method(typeof(PlayerVisualHands), handler) ?? throw new MissingMethodException(nameof(PlayerVisualHands), handler);
            method.Invoke(hands, null);
        }

        private void LiveShot(string name, AvatarPreviewCapture.View view)
        {
            try { AvatarPreviewCapture.SaveBitmap(plugin.RemoteAvatarRoot(guest)!, Path.Combine(Paths.BepInExRootPath, "MonMP", "diagnostics", "avatar-live-" + name + ".bmp"), view); }
            catch (Exception ex) { preview += "; live " + name + " unavailable (" + ex.GetType().Name + ")"; }
        }

        private void CapturePreview()
        {
            string directory = Path.Combine(Paths.BepInExRootPath, "MonMP", "diagnostics");
            string path = Path.Combine(directory, "avatar-shirt.bmp");
            try
            {
                var root = plugin.RemoteAvatarRoot(guest);
                if (root == null) throw new InvalidOperationException("Remote avatar root is unavailable.");
                AvatarPreviewCapture.SaveBitmap(root, path);
                preview = path + "; poses=" + plugin.RemoteAvatarPreviewPoses(guest, directory);
            }
            catch (Exception ex) { preview = "unavailable (" + ex.GetType().Name + ")"; }
            var holdEditorEvidence = plugin.TestHeldGripEditor(directory);
            if (!holdEditorEvidence.Contains("MONMP_HELD_EDITOR_SMOKE_PASS"))
                throw new InvalidOperationException("Held-grip editor smoke failed: " + holdEditorEvidence);
            preview += "; heldEditor=passed (renderer-only previews and temporary persistence)";
        }
    }
}
