using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.InputSystem;

namespace MonMP.Plugin.UI
{
    // Restore captured actions and native enables observed while the modal holds.
    // The caller validates both the original owner and its current input locks.
    internal sealed class TimeSkipInputLease : IDisposable
    {
        private readonly List<InputActionAsset> assets = new List<InputActionAsset>();
        private readonly List<InputAction> restore = new List<InputAction>();
        private Func<InputAction, bool>? canRestore;
        private Func<bool>? ownerIsCurrent;
        private Func<InputAction, bool>? actionOwnerIsCurrent;
        internal bool Active { get; private set; }
        internal void Begin(IEnumerable<InputActionAsset> sources, Func<bool> restoreAllowed)
            => Begin(sources, _ => restoreAllowed());
        internal void Begin(IEnumerable<InputActionAsset> sources, Func<InputAction, bool> restoreAllowed)
            => Begin(sources, restoreAllowed, () => true);
        internal void Begin(IEnumerable<InputActionAsset> sources, Func<InputAction, bool> restoreAllowed, Func<bool> currentOwner,
            Func<InputAction, bool>? currentActionOwner = null)
        {
            if (Active) return;
            TryRestorePending();
            assets.Clear();
            assets.AddRange(sources.Where(a => a != null).Distinct());
            foreach (var asset in assets)
                foreach (var action in asset.Where(a => a.enabled))
                    if (!restore.Contains(action)) restore.Add(action);
            canRestore = restoreAllowed;
            ownerIsCurrent = currentOwner;
            actionOwnerIsCurrent = currentActionOwner;
            Active = true;
            InputSystem.onBeforeUpdate += Hold;
            Hold();
        }
        internal void Hold()
        {
            if (!Active) return;
            foreach (var asset in assets)
                if (asset != null)
                {
                    // Native completion/resume may enable an action while this
                    // modal still owns input. Preserve that restoration intent.
                    foreach (var action in asset)
                        if (action.enabled && !restore.Contains(action)) restore.Add(action);
                    asset.Disable();
                }
        }
        internal void End()
        {
            if (Active)
            {
                Active = false;
                InputSystem.onBeforeUpdate -= Hold;
            }
            TryRestorePending();
        }
        internal void TryRestorePending()
        {
            if (Active) return;
            if (restore.Count == 0) { Clear(); return; }
            if (ownerIsCurrent?.Invoke() != true) { Clear(); return; }
            // Pause, death and another modal can outlast the skip. Native resume
            // enables Player only, so dropping Vehicle Controls here strands it.
            for (int i = restore.Count - 1; i >= 0; i--)
                if (actionOwnerIsCurrent != null && !actionOwnerIsCurrent(restore[i])) restore.RemoveAt(i);
                else if (canRestore?.Invoke(restore[i]) == true)
                {
                    if (!restore[i].enabled) restore[i].Enable();
                    restore.RemoveAt(i);
                }
            if (restore.Count == 0) Clear();
        }
        private void Clear()
        {
            restore.Clear(); assets.Clear(); canRestore = null; ownerIsCurrent = null; actionOwnerIsCurrent = null;
        }
        public void Dispose()
        {
            try { End(); }
            finally { Clear(); }
        }
    }
}
