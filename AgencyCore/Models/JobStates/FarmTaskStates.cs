namespace ValleyBuddies.AgencyCore;

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;

public abstract class FarmWorkerStateBase
{
    public string Phase { get; set; } = string.Empty;
    public ulong TaskPauseUntil { get; set; } = 0;
    public int WorkTimer { get; set; } = 0;
    public string CompletionMsg { get; set; } = string.Empty;
    public bool DeliveringAnnounced { get; set; } = false;
    public bool TaskDispatchedLogged { get; set; } = false;
    public bool CheckedFreeGround { get; set; } = false;
    public int ReturnTicks { get; set; } = 0;
    public Vector2 LastPixel { get; set; } = Vector2.Zero;
    public int StuckTicks { get; set; } = 0;
    public Vector2 TrackedTarget { get; set; } = Vector2.Zero;
    public Point StuckLastTile { get; set; } = Point.Zero;
    public Point StuckPrevTile { get; set; } = Point.Zero;
    public List<Point>? ActiveRoute { get; set; } = null;
    public Vector2? ActiveRouteGoal { get; set; } = null;
    public int ActiveRouteIndex { get; set; } = 0;
}

public sealed class HarvestCropsState : FarmWorkerStateBase, IPendingItemCarrierState
{
    public int HarvestCount { get; set; } = 0;
    public bool? HarvestFlowers { get; set; } = null;
    public bool RetriedFailedCrops { get; set; } = false;
    public bool GreenhouseVisited { get; set; } = false;
    public bool InsideGreenhouse { get; set; } = false;
    public string? TargetWorkLocationName { get; set; } = null;
    public int GhStuckTicks { get; set; } = 0;
    public Vector2 GhLastPos { get; set; } = Vector2.Zero;
    public List<Vector2> FailedCrops { get; } = new();
    public Dictionary<string, int> PendingItemsByQid { get; } = new();
    public List<Item> PendingItems { get; } = new();
    public bool DeliverToPlayerOnArrival { get; set; } = false;

    public void AddPendingItem(Item? item) => FarmTaskHelper.AddMergedPendingItem(PendingItems, item, PendingItemsByQid);

    public bool DeliverPendingItems(NPC npc, Farmer player, IFarmWorldService world)
    {
        bool gaveAny = false;
        foreach (var kv in PendingItemsByQid)
        {
            int remaining = kv.Value;
            while (remaining > 0)
            {
                int chunk = Math.Min(remaining, 999);
                var item = ItemRegistry.Create(kv.Key, chunk);
                if (item == null) break;
                int maxStack = Math.Max(1, item.maximumStackSize());
                if (item.Stack > maxStack)
                    item.Stack = maxStack;
                int taken = item.Stack;
                world.GiveItemToFarmerDirectly(player, item);
                gaveAny = true;
                remaining -= taken;
            }
        }
        PendingItemsByQid.Clear();

        foreach (var item in PendingItems)
        {
            if (item != null && item.Stack > 0)
            {
                world.GiveItemToFarmerDirectly(player, item);
                gaveAny = true;
            }
        }
        PendingItems.Clear();

        return gaveAny;
    }
}

public sealed class ClearDebrisState : FarmWorkerStateBase, IPendingItemCarrierState
{
    public int ClearedCount { get; set; } = 0;
    public Vector2? TargetTile { get; set; } = null;
    public List<Vector2> FailedDebris { get; } = new();
    public int StoredWood { get; set; } = 0;
    public int StoredStone { get; set; } = 0;
    public int StoredFiber { get; set; } = 0;
    public bool DeliverToPlayerOnArrival { get; set; } = false;

    public int TotalItems => StoredWood + StoredStone + StoredFiber;

    public bool DeliverPendingItems(NPC npc, Farmer player, IFarmWorldService world)
    {
        bool gaveAny = false;
        if (StoredWood > 0)
        {
            world.GiveItemToFarmerDirectly(player, ItemRegistry.Create("(O)388", StoredWood));
            StoredWood = 0;
            gaveAny = true;
        }
        if (StoredStone > 0)
        {
            world.GiveItemToFarmerDirectly(player, ItemRegistry.Create("(O)390", StoredStone));
            StoredStone = 0;
            gaveAny = true;
        }
        if (StoredFiber > 0)
        {
            world.GiveItemToFarmerDirectly(player, ItemRegistry.Create("(O)771", StoredFiber));
            StoredFiber = 0;
            gaveAny = true;
        }
        return gaveAny;
    }
}

public sealed class PetAnimalsState : FarmWorkerStateBase, IPendingItemCarrierState
{
    public int PettedCount { get; set; } = 0;
    public int CollectedProductsCount { get; set; } = 0;
    public HashSet<string> PettedIds { get; } = new();
    public HashSet<string> VisitedAnimalHouses { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Vector2> FailedFloorProducts { get; } = new();
    public bool InsideAnimalBuilding { get; set; } = false;
    public Point? SubBuildingFarmDoorTile { get; set; } = null;
    public List<Item> PendingItems { get; } = new();
    public bool DeliverToPlayerOnArrival { get; set; } = false;

    public void AddPendingItem(Item? item) => FarmTaskHelper.AddMergedPendingItem(PendingItems, item);

    public bool DeliverPendingItems(NPC npc, Farmer player, IFarmWorldService world)
    {
        bool gaveAny = false;
        foreach (var item in PendingItems)
        {
            if (item != null && item.Stack > 0)
            {
                world.GiveItemToFarmerDirectly(player, item);
                gaveAny = true;
            }
        }
        PendingItems.Clear();
        return gaveAny;
    }
}

public sealed class MineNearbyRockState : FarmWorkerStateBase, IPendingItemCarrierState
{
    public int MinedCount { get; set; } = 0;
    public int StoredStone { get; set; } = 0;
    public Vector2? TargetTile { get; set; } = null;
    public List<Vector2> FailedRocks { get; } = new();
    public bool DeliverToPlayerOnArrival { get; set; } = false;

    public bool DeliverPendingItems(NPC npc, Farmer player, IFarmWorldService world)
    {
        bool gaveAny = false;
        if (StoredStone > 0)
        {
            world.GiveItemToFarmerDirectly(player, ItemRegistry.Create("(O)390", StoredStone));
            StoredStone = 0;
            gaveAny = true;
        }
        return gaveAny;
    }
}

public sealed class ChopWoodState : FarmWorkerStateBase, IPendingItemCarrierState
{
    public int WoodCount { get; set; } = 0;
    public int GatheredWood { get; set; } = 0;
    public Vector2? WoodTile { get; set; } = null;
    public List<Vector2> FailedWood { get; } = new();
    public bool DeliverToPlayerOnArrival { get; set; } = false;

    public bool DeliverPendingItems(NPC npc, Farmer player, IFarmWorldService world)
    {
        bool gaveAny = false;
        if (GatheredWood > 0)
        {
            world.GiveItemToFarmerDirectly(player, ItemRegistry.Create("(O)388", GatheredWood));
            GatheredWood = 0;
            gaveAny = true;
        }
        return gaveAny;
    }
}

public sealed class ChopTreesState : FarmWorkerStateBase, IPendingItemCarrierState
{
    public int ChoppedCount { get; set; } = 0;
    public int GatheredWood { get; set; } = 0;
    public int GatheredHardwood { get; set; } = 0;
    public Vector2? TreeTile { get; set; } = null;
    public List<Vector2> FailedTrees { get; } = new();
    public string TreeFilter { get; set; } = string.Empty;
    public bool DeliverToPlayerOnArrival { get; set; } = false;

    public bool DeliverPendingItems(NPC npc, Farmer player, IFarmWorldService world)
    {
        bool gaveAny = false;
        if (GatheredWood > 0)
        {
            world.GiveItemToFarmerDirectly(player, ItemRegistry.Create("(O)388", GatheredWood));
            GatheredWood = 0;
            gaveAny = true;
        }
        if (GatheredHardwood > 0)
        {
            world.GiveItemToFarmerDirectly(player, ItemRegistry.Create("(O)709", GatheredHardwood));
            GatheredHardwood = 0;
            gaveAny = true;
        }
        return gaveAny;
    }
}

public sealed class FetchFromChestState : FarmWorkerStateBase
{
    public string FetchCategory { get; set; } = string.Empty;
    public HashSet<Vector2> CheckedChests { get; } = new();
    public Vector2? TargetChestTile { get; set; } = null;
    public string? SourceChestLocationName { get; set; } = null;
    public Vector2? SourceChestTile { get; set; } = null;
    public bool Announced { get; set; } = false;
    public List<Item>? RetrievedItems { get; set; } = null;
}

public sealed class HarvestForageState : FarmWorkerStateBase
{
    public int CollectedCount { get; set; } = 0;
    public List<Vector2> FailedForage { get; } = new();
}
