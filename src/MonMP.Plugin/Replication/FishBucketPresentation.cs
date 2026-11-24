using System;
using MonMP.Core.World;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Accepted actions carry their own one-shot. Restoring membership on join, repair,
    // or a late fish replica must not sound like another fish just entered the bucket.
    internal static class FishBucketPresentation
    {
        private static int nativeCollectionDepth;

        private sealed class NativeCollectionScope : IDisposable
        {
            private bool disposed;
            internal NativeCollectionScope() { nativeCollectionDepth++; }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true; nativeCollectionDepth--;
            }
        }

        // Native collection callers play the added clip after Add_Fish returns. The accepted
        // action now plays its own cue, at the bucket, including a direct host Add_Fish call.
        internal static IDisposable BeginNativeCollection() => new NativeCollectionScope();
        internal static bool AllowNativeCollectionSound(SOUND_Manager sound, AudioClip clip) =>
            nativeCollectionDepth == 0 || SharedPresentationEvents.Replaying || clip != sound.Fishing_FishBucket_FishAdded;

        internal static void Announce(FishBucketAction action, ulong bucket, Vector3 at, bool playHere)
        {
            PresentationReplays.Events?.HostAnnounce(PresentationKind.FishBucket, (byte)action,
                bucket, at, 0, 0, playHere);
        }

        internal static void Play(PresentationEvent e)
        {
            var sound = Singleton<SOUND_Manager>.i;
            if (sound == null) return;
            var clip = e.Variant == (byte)FishBucketAction.Add ? sound.Fishing_FishBucket_FishAdded :
                e.Variant == (byte)FishBucketAction.Remove ? sound.Fishing_FishBucket_FishRemoved : null;
            if (clip == null) return;
            using (SpatialSounds.At(new Vector3(e.X, e.Y, e.Z)))
                sound.PlayOneShot2D(clip, _randomPitch: true);
        }
    }
}
