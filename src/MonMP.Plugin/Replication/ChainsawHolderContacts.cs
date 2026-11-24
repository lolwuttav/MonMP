using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace MonMP.Plugin.Replication
{
    // Native racks react to a new physical entry, not to time passing inside
    // the rack. Rebuilding a replica body can emit a second entry at the same
    // position; keep that contact until the saw really leaves the trigger.
    internal sealed class ChainsawHolderContacts : MonoBehaviour
    {
        private SharedItems? owner;
        private Vehicle_Chainsaw_Holder? holder;
        private Collider[] triggers = Array.Empty<Collider>();
        private readonly Dictionary<Tool_Chainsaw, Collider> inside = new Dictionary<Tool_Chainsaw, Collider>();
        private readonly List<Tool_Chainsaw> departed = new List<Tool_Chainsaw>();

        internal void Initialize(SharedItems items, Vehicle_Chainsaw_Holder native)
        {
            owner = items; holder = native;
            triggers = native.GetComponentsInChildren<Collider>(true).Where(c => c != null && c.isTrigger &&
                c.GetComponentInParent<Vehicle_Chainsaw_Holder>() == native && c.GetComponentInParent<Tool_Chainsaw>() == null).ToArray();
        }
        internal bool Enter(Tool_Chainsaw saw, Collider other)
        {
            if (inside.ContainsKey(saw)) return false;
            inside.Add(saw, other); return true;
        }
        internal void Hold(Tool_Chainsaw saw)
        {
            if (saw.itemCollider != null) inside[saw] = saw.itemCollider;
        }
        private bool Overlaps(Collider other)
        {
            if (other == null) return false;
            foreach (var trigger in triggers)
                if (trigger != null && Physics.ComputePenetration(trigger, trigger.transform.position, trigger.transform.rotation,
                    other, other.transform.position, other.transform.rotation, out _, out _)) return true;
            return false;
        }
        private void OnTriggerExit(Collider other)
        {
            var saw = other == null ? null : other.GetComponent<Tool_Chainsaw>();
            if (saw != null && !Overlaps(other!)) Leave(saw);
        }
        private void Update()
        {
            // A collider rebuild can also synthesize an exit before its next
            // entry. Geometry, including disabled saw colliders, decides when
            // the retained native contact has actually ended.
            departed.Clear();
            foreach (var pair in inside)
                if (pair.Key == null || !Overlaps(pair.Value)) departed.Add(pair.Key!);
            foreach (var saw in departed) Leave(saw);
        }
        private void Leave(Tool_Chainsaw saw)
        {
            if (inside.Remove(saw) && owner != null && holder != null)
                owner.ChainsawHolderExit(holder, saw);
        }
        internal void Detach()
        {
            owner = null; holder = null; inside.Clear(); departed.Clear(); triggers = Array.Empty<Collider>();
        }
    }
}
