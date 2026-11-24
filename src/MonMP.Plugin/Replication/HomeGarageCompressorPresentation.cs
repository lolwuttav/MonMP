using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MonMP.Plugin.Replication
{
    // Guest-only projection of Francis's shared unlock. Native dialogue plays rewards on the host;
    // changing this root's visibility is the complete native AirCompressor_Unlocked side effect.
    internal sealed class HomeGarageCompressorPresentation : IDisposable
    {
        private readonly GameObject root;
        private readonly Scene scene;
        private readonly bool originalActive;
        private bool applied, disposed;
        internal HomeGarageCompressorPresentation(GameObject root)
        { this.root = root; scene = root.scene; originalActive = root.activeSelf; }
        private bool Available => root != null && scene.IsValid() && scene.isLoaded && root.scene == scene;
        internal void Apply(int francisFriendship, bool garageCompleted)
        {
            if (disposed || !Available) return;
            bool unlocked = garageCompleted && francisFriendship >= 50;
            if (root.activeSelf != unlocked) root.SetActive(unlocked);
            applied = true;
        }
        public void Dispose()
        {
            if (disposed) return;
            if (applied && Available && root.activeSelf != originalActive) root.SetActive(originalActive);
            disposed = true;
        }
    }
}
