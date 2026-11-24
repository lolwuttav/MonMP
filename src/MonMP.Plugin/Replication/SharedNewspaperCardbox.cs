using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MonMP.Core.Protocol;
using MonMP.Core.Session;
using MonMP.Core.World;
using MonMP.Plugin.Diagnostics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MonMP.Plugin.Replication
{
    // Host-owned cardbox transactions. A successful click creates one catalogued,
    // leased Newspaper_Crumpled through SharedItems, then advances the shared journal.
    internal sealed class SharedNewspaperCardbox : IDisposable
    {
        private const float InteractionRange = 3.5f;
        private const float PoseMaxAge = 2f;
        private const float CaptureInterval = 0.5f;
        private const float RequestTimeout = 8f;
        private const float BudgetWindow = 2f;
        private const int RequestsPerWindow = 8;
        private const int MaxPending = 16;
        private const int MaxReplayResults = 4096;

        private static readonly FieldInfo JournalsField = AccessTools.Field(typeof(Newspaper_Cardbox), "Journals");
        private static readonly FieldInfo JournalIndexField = AccessTools.Field(typeof(Newspaper_Cardbox), "activeJournal");
        private static readonly MethodInfo UpdateFirstJournalMethod = AccessTools.Method(typeof(Newspaper_Cardbox), "updateFirstJournal");

        private sealed class BoxBinding
        {
            public readonly Newspaper_Cardbox Box;
            public readonly ulong Id;
            public readonly GameObject[] Journals;
            public readonly bool[] OriginalJournalActive;
            public readonly int OriginalJournalIndex;

            public BoxBinding(Newspaper_Cardbox box, ulong id, GameObject[] journals, int originalJournalIndex)
            {
                Box = box; Id = id; Journals = journals;
                OriginalJournalActive = journals.Select(journal => journal.activeSelf).ToArray();
                OriginalJournalIndex = originalJournalIndex;
            }
        }

        private readonly Gameplay gameplay;
        private readonly bool host;
        private readonly SharedItems items;
        private readonly BoxBinding[] boxes;
        private readonly Dictionary<Newspaper_Cardbox, BoxBinding> boxesByInstance = new Dictionary<Newspaper_Cardbox, BoxBinding>();
        private readonly Dictionary<ulong, BoxBinding> boxesById = new Dictionary<ulong, BoxBinding>();
        private readonly Dictionary<uint, (PlayerPose Pose, float Time)> presence = new Dictionary<uint, (PlayerPose, float)>();
        private readonly Dictionary<Guid, float> pending = new Dictionary<Guid, float>();
        private readonly Dictionary<(uint Peer, Guid Operation), NewspaperCardboxStatus> replayResults =
            new Dictionary<(uint, Guid), NewspaperCardboxStatus>();
        private readonly Queue<(uint Peer, Guid Operation)> replayOrder = new Queue<(uint, Guid)>();
        private readonly Dictionary<uint, (float Started, int Count)> requestBudgets =
            new Dictionary<uint, (float, int)>();

        private NetworkSession? session;
        private WorldChannel? channel;
        private NewspaperCardboxState state;
        private float nextCapture;
        private bool disposed;
        private bool applied;

        internal static SharedNewspaperCardbox? Active { get; private set; }
        internal bool NewActionsBlocked { get; set; }
        internal bool TransactionsSettled => pending.Count == 0;

        internal SharedNewspaperCardbox(Gameplay gameplay, bool host, SharedItems items)
        {
            this.gameplay = gameplay ?? throw new ArgumentNullException(nameof(gameplay));
            this.host = host;
            this.items = items ?? throw new ArgumentNullException(nameof(items));

            if (JournalsField?.FieldType != typeof(GameObject[]) || JournalIndexField?.FieldType != typeof(int) ||
                UpdateFirstJournalMethod == null)
                throw new InvalidOperationException("Newspaper cardbox native field or rotation method layout is unsupported.");

            var sceneBoxes = Object.FindObjectsOfType<Newspaper_Cardbox>(true)
                .Where(candidate => candidate != null && candidate.gameObject.scene.IsValid()).ToArray();
            // Inactive boxes count too: the Sugar Shack's box only appears once the shack is built, and
            // host and guest must agree on the set regardless of who has built what locally. The host
            // checks that the target is active when the command arrives.
            var activeBoxes = sceneBoxes
                .OrderBy(candidate => SharedBuildingDoors.StablePath(candidate.transform), StringComparer.Ordinal).ToArray();
            if (activeBoxes.Length < 1 || activeBoxes.Length > NewspaperCardboxState.MaxBoxes)
            {
                string detail = string.Join(";", sceneBoxes.Select(candidate => RuntimeProbe.CanonicalPath(candidate.transform) +
                    ":active=" + candidate.gameObject.activeInHierarchy + ",enabled=" + candidate.enabled));
                throw new InvalidOperationException("Expected 1 to " + NewspaperCardboxState.MaxBoxes + " scene newspaper cardboxes; found " +
                    sceneBoxes.Length + " (" + detail + ").");
            }
            boxes = new BoxBinding[activeBoxes.Length];
            for (int i = 0; i < activeBoxes.Length; i++)
            {
                var box = activeBoxes[i];
                var journals = JournalsField.GetValue(box) as GameObject[] ??
                    throw new InvalidOperationException("Newspaper cardbox journal presentation is missing at " + RuntimeProbe.CanonicalPath(box.transform) + ".");
                if (journals.Length < 1 || journals.Length > NewspaperCardboxState.MaxJournals || journals.Any(journal => journal == null) ||
                    journals.Distinct().Count() != journals.Length)
                    throw new InvalidOperationException("Newspaper cardbox journal presentation has an unsupported layout at " + RuntimeProbe.CanonicalPath(box.transform) + ".");
                // Same-name ordinals, not raw sibling indices: a played save reorders ENVIRONEMENT
                // children relative to the fresh world a menu-joining guest registers from.
                string identity = "newspaper/cardbox/v3/" + SharedBuildingDoors.StablePath(box.transform);
                ulong id = StaticId.Hash(identity);
                if (id == 0 || boxesById.ContainsKey(id)) throw new InvalidOperationException("Duplicate newspaper cardbox identity: " + identity);
                var binding = new BoxBinding(box, id, journals, ReadJournalIndex(box, journals));
                boxes[i] = binding;
                boxesByInstance.Add(box, binding);
                boxesById.Add(id, binding);
            }
            state = Capture();
            Active = this;
        }

        internal IEnumerable<WorldRecord> InitialRecords() => new[]
        {
            new WorldRecord(NewspaperCardboxState.Entity, 1, NewspaperCardboxState.Adapter, state.Encode())
        };

        internal void Attach(NetworkSession session, WorldChannel channel)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            session.PoseReceived += OnPose;
            session.PeerLeft += OnPeerLeft;
            channel.SnapshotApplied += Snapshot;
            channel.Changed += Changed;
            channel.CommandRequested += Command;
            channel.CommandRejected += Rejected;
            channel.ReplyReceived += Reply;
        }

        private bool Running => !disposed && session?.IsRunning == true;
        private bool Ready => Running && channel?.FullySynchronized == true;

        // Returns true whenever online play owns this native interaction and the stock
        // client-side newspaper spawn must be suppressed.
        internal bool Intercept(Newspaper_Cardbox candidate)
        {
            if (!Running) return false;
            if (candidate == null || !boxesByInstance.TryGetValue(candidate, out var target) ||
                candidate.gameObject.scene != target.Box.gameObject.scene)
            {
                Report("This newspaper box is not registered in the shared world.");
                return true;
            }
            if (!Ready || NewActionsBlocked)
            {
                Report("Wait for the shared world and save to finish before taking a newspaper.");
                return true;
            }
            if (!host && (gameplay.Player.IsHoldingAnObject || pending.Count >= MaxPending))
            {
                Report(gameplay.Player.IsHoldingAnObject
                    ? "Put down the item in your hand before taking a newspaper."
                    : "Wait for the pending newspaper request to finish.");
                return true;
            }

            var request = new NewspaperCardboxRequest(Guid.NewGuid(), target.Id);
            if (!host) pending[request.Operation] = Time.unscaledTime + RequestTimeout;
            channel!.Request(NewspaperCardboxState.Entity, request.Encode());
            candidate.MouseOut();
            return true;
        }

        private void OnPose(uint peer, PlayerPose pose) => presence[peer] = (pose, Time.unscaledTime);

        private void OnPeerLeft(uint peer)
        {
            presence.Remove(peer);
            requestBudgets.Remove(peer);
            foreach (var key in replayResults.Keys.Where(key => key.Peer == peer).ToArray()) replayResults.Remove(key);
        }

        private bool TryGetPeerPosition(uint peer, out Vector3 position, out bool freshWalkingPose)
        {
            if (peer == 0)
            {
                position = gameplay.PlayerWalking.transform.position;
                freshWalkingPose = gameplay.Get_PlayerState == PlayerState.Walking;
                return true;
            }
            if (!presence.TryGetValue(peer, out var observed))
            {
                position = default;
                freshWalkingPose = false;
                return false;
            }
            float age = Time.unscaledTime - observed.Time;
            position = new Vector3(observed.Pose.X, observed.Pose.Y, observed.Pose.Z);
            freshWalkingPose = age >= 0 && age <= PoseMaxAge && observed.Pose.State == (byte)PlayerState.Walking;
            return age >= 0 && age <= PoseMaxAge;
        }

        private bool TakeRequestBudget(uint peer)
        {
            float now = Time.unscaledTime;
            if (!requestBudgets.TryGetValue(peer, out var budget) || now - budget.Started >= BudgetWindow || now < budget.Started)
                budget = (now, 0);
            if (budget.Count >= RequestsPerWindow) { requestBudgets[peer] = budget; return false; }
            requestBudgets[peer] = (budget.Started, budget.Count + 1);
            return true;
        }

        [MonMP.Core.World.WorldCommandAdapters(NewspaperCardboxState.Adapter)]
        private void Command(uint peer, ulong entity, byte[] payload)
        {
            if (!host || session?.IsHost != true || entity != NewspaperCardboxState.Entity || !NewspaperCardboxRequest.Is(payload)) return;
            NewspaperCardboxRequest request;
            try { request = NewspaperCardboxRequest.Decode(payload); }
            catch (InvalidDataException) { return; }

            BoxBinding? target = boxesById.TryGetValue(request.BoxId, out var knownTarget) ? knownTarget : null;
            Newspaper_Cardbox? targetBox = target?.Box;
            bool targetRegistered = targetBox != null;
            bool targetActive = targetBox != null && targetBox.isActiveAndEnabled && targetBox.gameObject.activeInHierarchy;

            var key = (peer, request.Operation);
            if (replayResults.TryGetValue(key, out var previous))
            {
                SendReply(peer, request.Operation, previous);
                return;
            }

            bool peerKnown = peer == 0 || session.Peers.ContainsKey(peer);
            bool poseKnown = TryGetPeerPosition(peer, out Vector3 playerPosition, out bool freshWalkingPose);
            int count = Object.FindObjectsOfType<Newspaper_Crumpled>().Length;
            bool inRange = poseKnown && targetBox != null && Vector3.Distance(playerPosition, targetBox.transform.position) <= InteractionRange;
            bool handsFree = peer == 0 ? !gameplay.Player.IsHoldingAnObject : peerKnown && !items.PeerHolding(peer);
            var status = NewspaperCardboxPolicy.Validate(
                Ready && peerKnown,
                NewActionsBlocked,
                peerKnown && TakeRequestBudget(peer),
                targetRegistered,
                targetActive,
                freshWalkingPose,
                inRange,
                handsFree,
                count);

            if (status == NewspaperCardboxStatus.Accepted && target != null && targetBox != null)
            {
                try
                {
                    var assets = Singleton<GameAssets>.i;
                    GameObject? asset = assets == null ? null : assets.Newspaper_Crumpled;
                    Vector3 position = peer == 0 ? gameplay.Player.HandLocation.position : targetBox.transform.position + Vector3.up * 0.35f;
                    Quaternion rotation = targetBox.transform.rotation;
                    ulong itemId = asset == null ? 0 : items.HostSpawnLeasedItem(asset, position, rotation, peer);
                    if (itemId == 0)
                    {
                        status = NewspaperCardboxStatus.SpawnFailed;
                    }
                    else
                    {
                        crumpledCount = -1; AdvanceJournalPresentation(target);
                        CaptureAndPublish();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    status = NewspaperCardboxStatus.SpawnFailed;
                }
            }

            Remember(key, status);
            if (peer == 0) ReportStatus(status);
            else SendReply(peer, request.Operation, status);
        }

        private void Remember((uint Peer, Guid Operation) key, NewspaperCardboxStatus status)
        {
            replayResults[key] = status;
            replayOrder.Enqueue(key);
            while (replayOrder.Count > MaxReplayResults)
                replayResults.Remove(replayOrder.Dequeue());
        }

        private void SendReply(uint peer, Guid operation, NewspaperCardboxStatus status)
        {
            if (peer != 0 && channel?.Ready == true && session?.Peers.ContainsKey(peer) == true)
                channel.Reply(peer, NewspaperCardboxState.Entity, new NewspaperCardboxReply(operation, status).Encode());
        }

        private void Rejected(uint peer, ulong entity, byte[] payload)
        {
            if (!host || entity != NewspaperCardboxState.Entity || !NewspaperCardboxRequest.Is(payload)) return;
            try { SendReply(peer, NewspaperCardboxRequest.Decode(payload).Operation, NewspaperCardboxStatus.NotReady); }
            catch (InvalidDataException) { }
        }

        private void Reply(ulong entity, byte[] payload)
        {
            if (host || entity != NewspaperCardboxState.Entity || !NewspaperCardboxReply.Is(payload)) return;
            NewspaperCardboxReply reply;
            try { reply = NewspaperCardboxReply.Decode(payload); }
            catch (InvalidDataException) { return; }
            if (!pending.Remove(reply.Operation)) return;
            ReportStatus(reply.Status);
        }

        private void Snapshot(IReadOnlyDictionary<ulong, WorldRecord> records)
        {
            if (host || !records.TryGetValue(NewspaperCardboxState.Entity, out var record) ||
                record.Adapter != NewspaperCardboxState.Adapter) return;
            Apply(NewspaperCardboxState.Decode(record.Payload));
        }

        private void Changed(WorldRecord record)
        {
            if (host || record.Id != NewspaperCardboxState.Entity || record.Adapter != NewspaperCardboxState.Adapter) return;
            Apply(NewspaperCardboxState.Decode(record.Payload));
        }

        private void Apply(NewspaperCardboxState next)
        {
            if (next.Boxes.Count != boxes.Length)
                throw new InvalidDataException("Host and guest newspaper box counts differ.");
            foreach (var binding in boxes)
                if (!next.TryGetBox(binding.Id, out var journal) || journal.JournalCount != binding.Journals.Length)
                    throw new InvalidDataException("Host and guest newspaper box identities or journal layouts differ.");
            state = next; applied = true;
            foreach (var binding in boxes)
            {
                next.TryGetBox(binding.Id, out var journal);
                SetJournalPresentation(binding, journal.JournalIndex);
            }
        }

        private static int ReadJournalIndex(Newspaper_Cardbox box, GameObject[] journals)
        {
            int index = (int)JournalIndexField.GetValue(box);
            if ((uint)index < (uint)journals.Length) return index;
            index = Array.FindIndex(journals, journal => journal.activeSelf);
            return index >= 0 ? index : 0;
        }

        private NewspaperCardboxState Capture() => new NewspaperCardboxState(
            boxes.Select(binding => new NewspaperJournalState(binding.Id,
                ReadJournalIndex(binding.Box, binding.Journals), binding.Journals.Length)),
            CrumpledCount());

        // The crumpled count is informational and needs a whole-scene search; refresh it every few seconds.
        private int crumpledCount = -1;
        private float nextCrumpledCount;
        private int CrumpledCount()
        {
            if (crumpledCount < 0 || Time.unscaledTime >= nextCrumpledCount && WorldScans.TryClaim())
            {
                crumpledCount = Object.FindObjectsOfType<Newspaper_Crumpled>().Length;
                nextCrumpledCount = Time.unscaledTime + 3f;
            }
            return crumpledCount;
        }

        private static void AdvanceJournalPresentation(BoxBinding binding)
        {
            // Preserve the exact native wraparound and activeJournal progression.
            UpdateFirstJournalMethod.Invoke(binding.Box, null);
        }

        private void CaptureAndPublish()
        {
            if (!host || channel?.Ready != true) return;
            var captured = Capture();
            if (captured.Equals(state)) return;
            state = captured;
            channel.Publish(NewspaperCardboxState.Entity, state.Encode());
        }

        private static void SetJournalPresentation(BoxBinding binding, int index)
        {
            if ((uint)index >= (uint)binding.Journals.Length) throw new InvalidDataException("Invalid newspaper journal index.");
            JournalIndexField.SetValue(binding.Box, index);
            for (int i = 0; i < binding.Journals.Length; i++) binding.Journals[i].SetActive(i == index);
        }

        internal void Update()
        {
            if (!Running) return;
            float now = Time.unscaledTime;
            if (pending.Count != 0)
            foreach (var operation in pending.Where(pair => now >= pair.Value).Select(pair => pair.Key).ToArray())
            {
                pending.Remove(operation);
                Report("The newspaper request timed out; check whether the shared world is still connected.");
            }
            if (now >= nextCapture)
            {
                nextCapture = now + CaptureInterval;
                if (host) CaptureAndPublish();
                else if (applied)
                    foreach (var binding in boxes)
                        if (binding.Box != null && state.TryGetBox(binding.Id, out var journal) &&
                            ReadJournalIndex(binding.Box, binding.Journals) != journal.JournalIndex)
                            SetJournalPresentation(binding, journal.JournalIndex);
            }
        }

        private static void ReportStatus(NewspaperCardboxStatus status)
        {
            if (status == NewspaperCardboxStatus.Accepted) return;
            if (status == NewspaperCardboxStatus.CapReached)
            {
                try
                {
                    var error = Singleton<UI_ErrorMessage>.i;
                    if (error != null) { error.Show(TEXT.GetText("EVENT_ENOUGHNEWSPAPER")); return; }
                }
                catch { }
            }
            string message = status switch
            {
                NewspaperCardboxStatus.NotReady => "The shared world is not ready for newspaper pickup.",
                NewspaperCardboxStatus.ActionsBlocked => "Wait for the shared save to finish before taking a newspaper.",
                NewspaperCardboxStatus.TargetUnavailable => "The newspaper box is unavailable in the shared world.",
                NewspaperCardboxStatus.OutOfRange => "Move closer to the newspaper box and try again.",
                NewspaperCardboxStatus.HandsBusy => "Put down the item in your hand before taking a newspaper.",
                NewspaperCardboxStatus.CapReached => "The world already has enough newspapers.",
                _ => "The host could not create the shared newspaper."
            };
            Report(message);
        }

        private static void Report(string message) => MonMP.Plugin.MonMPPlugin.Instance?.ReportItemUseUnavailable(message);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (channel != null)
            {
                channel.SnapshotApplied -= Snapshot;
                channel.Changed -= Changed;
                channel.CommandRequested -= Command;
                channel.CommandRejected -= Rejected;
                channel.ReplyReceived -= Reply;
            }
            if (session != null)
            {
                session.PoseReceived -= OnPose;
                session.PeerLeft -= OnPeerLeft;
            }
            if (!host)
            {
                foreach (var binding in boxes)
                {
                    for (int i = 0; i < binding.Journals.Length; i++)
                        if (binding.Journals[i] != null) binding.Journals[i].SetActive(binding.OriginalJournalActive[i]);
                    JournalIndexField.SetValue(binding.Box, binding.OriginalJournalIndex);
                }
            }
            presence.Clear();
            pending.Clear();
            requestBudgets.Clear();
            replayResults.Clear();
            replayOrder.Clear();
            session = null;
            channel = null;
            if (ReferenceEquals(Active, this)) Active = null;
        }
    }
}
