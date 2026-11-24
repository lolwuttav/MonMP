using System;
using System.Collections.Generic;
using HarmonyLib;
using MonMP.Core.Protocol;
using UnityEngine;

namespace MonMP.Plugin.Patches
{
    // Every native player voice line ("Oups" from Sorry, "Sacre" from Slur, a locked item, a spat-on
    // fryer, ...) plays through SOUND_General.PlayerVoice once its cooldown allowed it, and only
    // SpitDown plays the spit sound, so these two calls are exactly the emotes that happened.
    [HarmonyPatch(typeof(SOUND_General), nameof(SOUND_General.PlayerVoice))]
    internal static class PlayerVoiceEmotePatch
    {
        private static void Prefix(SOUND_General __instance, AudioClip audioClip)
        {
            var sounds = __instance.GetComponent<SOUND_Manager>();
            if (sounds == null || audioClip == null) return;
            int oups = Array.FindIndex(sounds.PlayerVoice_Oups ?? Array.Empty<PlacerVoiceDetails>(), v => v != null && v.VoiceClip == audioClip);
            if (oups >= 0) { LocalEmotes.Record(PlayerEmoteKind.Oups, oups); return; }
            int sacre = Array.FindIndex(sounds.PlayerVoice_Sacre ?? Array.Empty<PlacerVoiceDetails>(), v => v != null && v.VoiceClip == audioClip);
            if (sacre >= 0) LocalEmotes.Record(PlayerEmoteKind.Sacre, sacre);
        }
    }

    [HarmonyPatch(typeof(SOUND_Manager), nameof(SOUND_Manager.PlaySound_Player_Spit))]
    internal static class PlayerSpitEmotePatch
    {
        private static void Postfix() => LocalEmotes.Record(PlayerEmoteKind.Spit, 0);
    }

    // Drunk vomiting (BasicNeeds_Manager decides; DrunkEffect plays the sound and the particles).
    [HarmonyPatch(typeof(DrunkEffect), nameof(DrunkEffect.Vomit))]
    internal static class PlayerVomitEmotePatch
    {
        private static void Postfix() => LocalEmotes.Record(PlayerEmoteKind.Vomit, 0);
    }

    // The hands' short interaction poke (every click on something) and the one-second Leo pet.
    [HarmonyPatch(typeof(PlayerVisualHands), nameof(PlayerVisualHands.Interaction))]
    internal static class PlayerInteractEmotePatch
    {
        private static void Postfix() => LocalEmotes.Record(PlayerEmoteKind.Interact, 0);
    }

    [HarmonyPatch(typeof(PlayerVisualHands), nameof(PlayerVisualHands.InteractionPetLeo))]
    internal static class PlayerPetLeoEmotePatch
    {
        private static void Postfix() => LocalEmotes.Record(PlayerEmoteKind.PetLeo, 0);
    }

    // Queues the local player's emotes for the pose stream. Each one is repeated in a few poses
    // under one sequence number, so a dropped pose packet does not lose it.
    internal static class LocalEmotes
    {
        private const int PosesPerEmote = 4;
        private const int MaxPending = 8;
        private static readonly Queue<(PlayerEmoteKind Kind, byte Variant)> pending = new Queue<(PlayerEmoteKind, byte)>();
        private static byte sequence, variant;
        private static PlayerEmoteKind current;
        private static int posesLeft;

#if MONMP_DEVELOPMENT
        internal static int TestRecorded { get; private set; }
#endif


        internal static bool RecordGesture(PlayerEmoteKind kind)
        {
            if (!PlayerEmotePolicy.IsCosmeticGesture(kind)) return false;
            return Record(kind, 0);
        }

        internal static bool Record(PlayerEmoteKind kind, int voiceVariant)
        {
            if (kind == PlayerEmoteKind.None || kind > PlayerEmoteKind.ThumbsUp ||
                MonMPPlugin.Instance?.Session?.IsRunning != true || pending.Count >= MaxPending) return false;
            if (PlayerEmotePolicy.IsCosmeticGesture(kind) && voiceVariant != 0)
                return false;
            // Clicks come fast; one queued poke is enough and must not hold back the voice lines.
            if (kind == PlayerEmoteKind.Interact && (current == kind && posesLeft > 0 || pending.Contains((kind, 0)))) return false;
            pending.Enqueue((kind, (byte)Mathf.Clamp(voiceVariant, 0, PlayerPose.MaxEmoteVariant)));

#if MONMP_DEVELOPMENT
            TestRecorded++;
#endif

            MonMPPlugin.Instance?.Activity.RecordAction();
            return true;
        }

        internal static (byte Sequence, PlayerEmoteKind Kind, byte Variant) NextPose()
        {
            if (posesLeft <= 0 && pending.Count > 0)
            {
                (current, variant) = pending.Dequeue();
                sequence++;
                posesLeft = PosesPerEmote;
            }
            if (posesLeft > 0) posesLeft--;
            return (sequence, current, variant);
        }

        internal static void Reset()
        {
            pending.Clear();
            posesLeft = 0; current = PlayerEmoteKind.None; variant = 0;
        }
    }
}
