using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Native personal needs decide whether and when the warning happens. Only its actual
    // sound is shared; replay does not run a needs tick or change anyone's anxiety/timers.
    internal static class PlayerAnxietyWarningPresentation
    {
        private static int replayDepth;

        internal static void Native(SOUND_Manager sound, AudioClip? clip, AudioSource? playedSource)
        {
            var plugin = MonMPPlugin.Instance;
            var world = plugin?.World;
            var events = world?.Events;
            var player = plugin?.Gameplay?.PlayerWalking;
            if (replayDepth != 0 || SharedPresentationEvents.Replaying || world?.IsDisposed != false ||
                events?.Live != true || player == null || sound == null || clip == null ||
                clip != sound.Player_NervousBreathing || playedSource == null ||
                playedSource.clip != clip || !playedSource.enabled ||
                !playedSource.gameObject.activeInHierarchy || !playedSource.isPlaying) return;

            events.Post(PresentationKind.PlayerAnxietyWarning, 0, 0, player.transform.position, 0);
        }

        internal static bool AcceptCue(uint peer, PresentationEvent cue) =>
            peer != 0 && cue.Kind == (byte)PresentationKind.PlayerAnxietyWarning &&
            cue.Target == 0 && cue.Variant == 0 && cue.Value == 0;

        internal static void Play(PresentationEvent cue)
        {
            var world = MonMPPlugin.Instance?.World;
            var events = world?.Events;
            if (world?.IsDisposed != false || events?.Live != true ||
                cue.Kind != (byte)PresentationKind.PlayerAnxietyWarning ||
                cue.Target != 0 || cue.Variant != 0 || cue.Value != 0) return;
            var sound = Object.FindObjectOfType<SOUND_Manager>();
            if (sound == null || sound.Player_NervousBreathing == null) return;

            replayDepth++;
            try
            {
                using (SpatialSounds.At(new Vector3(cue.X, cue.Y, cue.Z), cue.Actor))
                    sound.PlayOneShot2D(sound.Player_NervousBreathing, _randomPitch: true);
            }
            finally { replayDepth--; }
        }
    }
}
