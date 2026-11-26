namespace ValleyBuddies.AgencyCore;

using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;
using ValleyBuddies.Execution;

public sealed class FarmWorldService : IFarmWorldService
{
    public Chest? FindNearestChest(GameLocation loc, Vector2 fromTile, float maxRadius)
    {
        return NpcActionExecutor.FindNearestChest(loc, fromTile, maxRadius);
    }

    public (Chest chest, Vector2 tile)? FindNearestChestWithTile(GameLocation loc, Vector2 fromTile, float maxRadius)
    {
        return NpcActionExecutor.FindNearestChestWithTile(loc, fromTile, maxRadius);
    }

    public List<(Chest chest, Vector2 tile)> FindAllChests(GameLocation loc)
    {
        return NpcActionExecutor.FindAllChests(loc);
    }

    public bool AddItemToChest(Chest chest, Item item)
    {
        return NpcActionExecutor.AddItemToChest(chest, item);
    }

    public void GiveItemToFarmerDirectly(Farmer? player, Item? item)
    {
        NpcActionExecutor.GiveItemToFarmerDirectly(player, item);
    }

    public bool IsSafePlayableFarmTile(GameLocation loc, Vector2 tile, NPC? npc = null)
    {
        return NpcActionExecutor.IsSafePlayableFarmTile(loc, tile, npc);
    }

    public bool TryBreakAdjacentBlockingDebris(NPC npc, GameLocation loc, Farmer? owner, Vector2 desiredTargetTile)
    {
        return NpcActionExecutor.TryBreakAdjacentBlockingDebris(npc, loc, owner, desiredTargetTile);
    }

    public Item? TryCreateItemByName(string name, int quantity = 1)
    {
        return NpcActionExecutor.TryCreateByName(name, quantity);
    }
}
