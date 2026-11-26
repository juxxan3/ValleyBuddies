namespace ValleyBuddies.Handlers.Social;

using System;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class GiveItemHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.GiveItem;

    public void Tick(JobExecutionContext ctx)
    {
        var state = ctx.Job.GetOrCreateState<GiveItemState>();
        if (ctx.Job.Data.ContainsKey("played") || state.Played)
        {
            ctx.Job.Done = true;
            return;
        }

        string? itemName = state.ItemName;
        if (ctx.Job.Data.TryGetValue("itemName", out var i) && i != null)
        {
            itemName = i.ToString();
            state.ItemName = itemName;
        }

        int qty = state.Quantity;
        if (ctx.Job.Data.TryGetValue("qty", out var q) && q is int qVal)
        {
            qty = qVal;
            state.Quantity = qty;
        }

        var player = ctx.Owner ?? Game1.player;
        if (player == null)
        {
            ctx.Job.Done = true;
            return;
        }

        if (!string.IsNullOrWhiteSpace(itemName))
        {
            string? qualifiedId = ctx.Job.Data.TryGetValue("qualifiedId", out var qidObj) ? qidObj?.ToString() : null;
            Item? item = !string.IsNullOrWhiteSpace(qualifiedId)
                ? ItemRegistry.Create(qualifiedId, qty, allowNull: true)
                : null;
            item ??= ItemRegistry.Create(itemName, qty, allowNull: true)
                     ?? ctx.World.TryCreateItemByName(itemName, qty);
            if (item != null)
            {
                if (player.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
                    ctx.World.GiveItemToFarmerDirectly(player, item);
                else if (item.GetType() == typeof(StardewValley.Object) && item.Quality == 0 && ModEntry.MultiplayerSync != null)
                    ModEntry.MultiplayerSync.SendSpouseGift(player.UniqueMultiplayerID, item.QualifiedItemId, item.Stack, ctx.Npc.Name);
                else
                    (player.currentLocation ?? ctx.Npc.currentLocation)?.debris.Add(new Debris(item, player.Position));
                ctx.Npc.doEmote(20);
                try { player.changeFriendship(15, ctx.Npc); } catch { }
            }
        }

        state.Played = true;
        ctx.Job.Data["played"] = true;
        ctx.Job.Done = true;
    }
}
