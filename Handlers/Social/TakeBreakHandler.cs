namespace ValleyBuddies.Handlers.Social;

using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class TakeBreakHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.TakeBreak;

    public void Tick(JobExecutionContext ctx)
    {
        var player = ctx.Owner ?? Game1.player;
        if (player == null) { ctx.Job.Done = true; return; }

        ctx.Npc.controller = null;
        ctx.Npc.Halt();
        ctx.Npc.faceTowardFarmerForPeriod(4000, 3, false, player);

        ctx.Npc.currentLocation?.playSound("gulp");
        ctx.Npc.shake(250);
        ctx.Npc.doEmote(Game1.random.NextDouble() > 0.5 ? 32 : 20); // Laugh or Heart
        player.doEmote(32); // Smile

        try
        {
            NpcActionExecutor.TryAwardDailyActivityFriendship(player, ctx.Npc, "take_break", 15, 15);
        }
        catch { }

        if (Game1.timeOfDay >= 2300)
        {
            ctx.Npc.showTextAboveHead(Utilities.I18n.Get("bubble.night_coffee_late"), duration: 4200);
            ctx.RecordMemory(ctx.Npc.Name, "We shared a late-night cup of warm coffee past midnight under the stars, laughing about being night owls together.");
            ctx.Job.Done = true;
            return;
        }

        string breakPhrase = Utilities.I18n.Get("bubble.activity.coffee_day", new { farmerName = player.displayName });
        ctx.Npc.showTextAboveHead(breakPhrase, duration: 4000);
        ctx.RecordMemory(ctx.Npc.Name, $"We shared a relaxing coffee/tea break together on the farm, chatting and enjoying the peaceful atmosphere with {player.displayName}.");
        ctx.Job.Done = true;
    }
}
