using System.Collections.Generic;

namespace MonMP.Plugin.Replication
{
    internal sealed partial class SharedItems
    {
        private readonly ActiveItemWorkIndex<Binding> itemWork = new ActiveItemWorkIndex<Binding>();
        private readonly ActiveItemWorkIndex<Binding> chainsawWork = new ActiveItemWorkIndex<Binding>();
        private readonly ActiveItemWorkIndex<Binding> trayBoltWork = new ActiveItemWorkIndex<Binding>();
        private readonly ActiveItemWorkIndex<Binding> outdoorPlanterWork = new ActiveItemWorkIndex<Binding>();
        private readonly List<Binding> chainsawWorkScratch = new List<Binding>();
        private readonly List<Binding> outdoorPlanterWorkScratch = new List<Binding>();
        private void RefreshItemWork(Binding binding)
        {
            var item = binding.Item;
            bool native = item != null;
            itemWork.Set(binding.Id, binding,
                native || host && binding.State?.Exists == true ||
                binding.SeedPrediction?.HasPending == true && binding.State?.Exists == true);
            // State/native replacement already pass this boundary. Protocol tombstones and
            // seed recovery stay in the general index; these narrow scans need live types only.
            chainsawWork.Set(binding.Id, binding, native && item is Tool_Chainsaw);
            trayBoltWork.Set(binding.Id, binding, native && item is Hardware_Bolt);
            outdoorPlanterWork.Set(binding.Id, binding, native && item is Garden_OutdoorPlanter && binding.State?.Exists == true);
        }
        private ActiveItemWorkIndex<Binding>.View ItemWorkSnapshot() => itemWork.ReadSnapshot();
        internal void SnapshotOutdoorPlanterIds(List<ulong> destination)
        {
            outdoorPlanterWork.Snapshot(outdoorPlanterWorkScratch);
            destination.Clear();
            foreach (var binding in outdoorPlanterWorkScratch) destination.Add(binding.Id);
            outdoorPlanterWorkScratch.Clear();
        }
        private void ClearTypedItemWork()
        { chainsawWork.Clear(); trayBoltWork.Clear(); outdoorPlanterWork.Clear(); chainsawWorkScratch.Clear(); outdoorPlanterWorkScratch.Clear(); }
        private void ClearRetiredNativeReference(Binding binding)
        {
            // Unity's destroyed object compares equal to null while retaining its managed key.
            if (!ReferenceEquals(binding.Item, null)) byItem.Remove(binding.Item!);
            binding.Item = null;
        }
        private static void RefreshPlanterLocation(InteractableItems item)
        {
            if (item is Garden_OutdoorPlanter planter && planter.DirtPatch != null)
                planter.DirtPatch.UpdateLocation();
        }
        private void StopLiftOperators()
        {
            if (host)
                foreach (var id in new List<ulong>(liftOperators.Keys))
                    if (bindings.TryGetValue(id, out var binding) && binding.Item is Furniture_CarLift lift && lift != null)
                    {
                        try { CarLiftState.Stop(lift); }
                        catch (System.Exception ex) { UnityEngine.Debug.LogException(ex); }
                    }
            liftOperators.Clear();
        }
    }
}
