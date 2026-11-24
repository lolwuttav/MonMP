using System;
using System.IO;
using MonMP.Core.World;

var cases = new (string Name, Action Run)[]
{
    ("state round-trip and flags", StateRoundTrip),
    ("request round-trip and identity validation", RequestRoundTrip),
    ("reply round-trip and item validation", ReplyRoundTrip),
    ("host friendship threshold", FriendshipThreshold),
    ("host identity and transaction gates", HostGates)
};

foreach (var test in cases)
{
    test.Run();
    Console.WriteLine("PASS " + test.Name);
}
Console.WriteLine($"PASS {cases.Length} focused keg protocol cases");

static void StateRoundTrip()
{
    var state = new KegPurchaseState(90, 12500, 50, true, false);
    var decoded = KegPurchaseState.Decode(state.Encode());
    Check(decoded.ItemPrefab == 90 && decoded.PriceCents == 12500 && decoded.CaroleFriendship == 50 && decoded.Available && !decoded.StoreOpen,
        "state fields differ");
    var malformed = state.Encode(); malformed[^1] = 0x80;
    Throws<InvalidDataException>(() => KegPurchaseState.Decode(malformed));
}

static void RequestRoundTrip()
{
    var request = new KegPurchaseRequest(Guid.NewGuid(), 0x123456UL, 90, 12500);
    var decoded = KegPurchaseRequest.Decode(request.Encode());
    Check(decoded.Operation == request.Operation && decoded.Listing == request.Listing &&
        decoded.ItemPrefab == request.ItemPrefab && decoded.PriceCents == request.PriceCents, "request fields differ");
    Check(KegPurchaseRequest.Is(request.Encode()) && KegPurchaseRequest.HasMarker(request.Encode()), "request marker was lost");
    Throws<InvalidDataException>(() => new KegPurchaseRequest(Guid.Empty, request.Listing, 90, 12500));
    Throws<InvalidDataException>(() => new KegPurchaseRequest(request.Operation, 0, 90, 12500));
    Throws<InvalidDataException>(() => KegPurchaseRequest.Decode(new byte[KegPurchaseRequest.Size]));
}

static void ReplyRoundTrip()
{
    Guid operation = Guid.NewGuid();
    var reply = new KegPurchaseReply(operation, PurchaseStatus.Held, WorldRecord.DynamicIdStart + 7);
    var decoded = KegPurchaseReply.Decode(reply.Encode());
    Check(decoded.Operation == operation && decoded.Status == PurchaseStatus.Held && decoded.Item == reply.Item, "reply fields differ");
    Throws<InvalidDataException>(() => new KegPurchaseReply(operation, PurchaseStatus.Placed));
    Throws<InvalidDataException>(() => new KegPurchaseReply(operation, PurchaseStatus.Funds, WorldRecord.DynamicIdStart + 7));
}

static void FriendshipThreshold()
{
    Check(Validate(friendship: 49) == PurchaseStatus.Unavailable, "friendship 49 must remain locked");
    Check(Validate(friendship: 50) == PurchaseStatus.Placed, "friendship 50 must unlock the listing");
    Check(Validate(friendship: 51) == PurchaseStatus.Placed, "friendship above 50 must unlock the listing");
}

static void HostGates()
{
    Check(Validate(exactTarget: false) == PurchaseStatus.Unavailable, "wrong target was accepted");
    Check(Validate(exactListing: false) == PurchaseStatus.Unavailable, "wrong listing was accepted");
    Check(Validate(exactItem: false) == PurchaseStatus.Unavailable, "wrong item was accepted");
    Check(Validate(requestedPriceCents: 12499) == PurchaseStatus.PriceChanged, "changed price was accepted");
    Check(Validate(stale: true) == PurchaseStatus.PriceChanged, "stale request was accepted");
    Check(Validate(storeOpen: false) == PurchaseStatus.Closed, "closed store was accepted");
    Check(Validate(inRange: false) == PurchaseStatus.TooFar, "out-of-range request was accepted");
    Check(Validate(canAfford: false) == PurchaseStatus.Funds, "unaffordable request was accepted");
    Check(Validate(actionsBlocked: true) == PurchaseStatus.NativeRejected, "save-blocked request was accepted");
    Check(Validate(budget: false) == PurchaseStatus.Capacity, "request over budget was accepted");
}

static PurchaseStatus Validate(bool stale = false, bool synchronized = true, bool actionsBlocked = false,
    bool budget = true, bool exactTarget = true, bool exactListing = true, bool exactItem = true,
    int requestedPriceCents = 12500, int registeredPriceCents = 12500, int friendship = 50,
    bool available = true, bool storeOpen = true, bool inRange = true, bool canAfford = true) =>
    KegPurchasePolicy.ValidateHostRequest(stale, synchronized, actionsBlocked, budget,
        exactTarget, exactListing, exactItem, requestedPriceCents, registeredPriceCents, friendship,
        available, storeOpen, inRange, canAfford);

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
