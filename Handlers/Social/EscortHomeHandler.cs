namespace ValleyBuddies.Handlers.Social;

using System;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class EscortHomeHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.EscortHome;

    public void Tick(JobExecutionContext ctx)
    {
        var player = ctx.Owner ?? Game1.player;
        if (player == null) { ctx.Job.Done = true; return; }

        string? playerLoc = player.currentLocation?.NameOrUniqueName;
        if (playerLoc != null &&
            (playerLoc.Contains("FarmHouse", StringComparison.OrdinalIgnoreCase)
             || playerLoc.Contains("Cabin", StringComparison.OrdinalIgnoreCase)))
        {
            ctx.Npc.doEmote(32); // wave goodbye
            ctx.Job.Done = true;
            return;
        }

        ctx.StepFollow();
    }
}
