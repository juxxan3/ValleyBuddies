namespace ValleyBuddies.Handlers.Social;

using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class MeetAtTimeHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.MeetAtTime;

    public void Tick(JobExecutionContext ctx)
    {
        var state = ctx.Job.GetOrCreateState<MeetAtTimeState>();

        int meetTime = state.MeetTime;
        if (ctx.Job.Data.TryGetValue("meetTime", out var mt) && mt is int mVal)
        {
            meetTime = mVal;
            state.MeetTime = meetTime;
        }

        if (meetTime == 0 && !ctx.Job.Data.ContainsKey("meetTime"))
        {
            ctx.Job.Done = true;
            return;
        }

        if (Game1.timeOfDay < meetTime) return;

        string? locName = state.LocationName;
        if (ctx.Job.Data.TryGetValue("locationName", out var ln) && ln != null)
        {
            locName = ln.ToString();
            state.LocationName = locName;
        }

        if (!string.IsNullOrWhiteSpace(locName))
        {
            var loc = Game1.getLocationFromName(locName);
            if (loc != null && ctx.Npc.currentLocation?.NameOrUniqueName != loc.NameOrUniqueName)
            {
                ctx.Navigator.StopCleanly(ctx.Npc);
                Point entry = NpcActionExecutor.GetDefaultLocationEntry(loc, ctx.Npc);
                Game1.warpCharacter(ctx.Npc, loc, new Vector2(entry.X, entry.Y));
            }
        }

        ctx.Job.Done = true;
    }
}
