namespace ValleyBuddies.Handlers.Social;

using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class TakePhotoHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.TakePhoto;

    public void Tick(JobExecutionContext ctx)
    {
        var player = ctx.Owner ?? Game1.player;
        if (player == null) { ctx.Job.Done = true; return; }

        ctx.Npc.controller = null;
        ctx.Npc.Halt();
        ctx.Npc.faceTowardFarmerForPeriod(4000, 3, false, player);

        var loc = ctx.Npc.currentLocation;
        if (loc != null)
        {
            loc.playSound("cameraNoise");
            Vector2 center = (ctx.Npc.Position + player.Position) / 2f;
            NpcActionExecutor.BroadcastTemporarySprite(loc, new TemporaryAnimatedSprite(28, center, Color.White) { interval = 40f });
        }

        ctx.Npc.shake(200);
        ctx.Npc.doEmote(52); // Blush!
        player.doEmote(20); // Heart

        try
        {
            NpcActionExecutor.TryAwardDailyActivityFriendship(player, ctx.Npc, "take_photo", 15, 15);
        }
        catch { }

        string photoPhrase = Utilities.I18n.Get($"bubble.activity.photo_{Game1.random.Next(1, 4)}");
        ctx.Npc.showTextAboveHead(photoPhrase, duration: 4000);
        ctx.RecordMemory(ctx.Npc.Name, "We posed close together and took a commemorative photo on the farm to remember this moment.");
        ctx.Job.Done = true;
    }
}
