namespace ValleyBuddies.AgencyCore;

using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;

public interface IFarmWorldService
{
    Chest? FindNearestChest(GameLocation loc, Vector2 fromTile, float maxRadius);
    (Chest chest, Vector2 tile)? FindNearestChestWithTile(GameLocation loc, Vector2 fromTile, float maxRadius);
    List<(Chest chest, Vector2 tile)> FindAllChests(GameLocation loc);
    bool AddItemToChest(Chest chest, Item item);
    void GiveItemToFarmerDirectly(Farmer? player, Item? item);
    bool IsSafePlayableFarmTile(GameLocation loc, Vector2 tile, NPC? npc = null);
    bool TryBreakAdjacentBlockingDebris(NPC npc, GameLocation loc, Farmer? owner, Vector2 desiredTargetTile);
    Item? TryCreateItemByName(string name, int quantity = 1);
}
