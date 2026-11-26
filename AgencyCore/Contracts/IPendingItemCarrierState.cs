namespace ValleyBuddies.AgencyCore;

using StardewValley;

public interface IPendingItemCarrierState
{
    bool DeliverToPlayerOnArrival { get; set; }
    bool DeliverPendingItems(NPC npc, Farmer player, IFarmWorldService world);
}
