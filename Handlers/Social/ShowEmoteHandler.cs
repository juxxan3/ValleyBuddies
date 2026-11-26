namespace ValleyBuddies.Handlers.Social;

using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class ShowEmoteHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.ShowEmote;

    public void Tick(JobExecutionContext ctx)
    {
        var state = ctx.Job.GetOrCreateState<ShowEmoteState>();
        if (ctx.Job.Data.ContainsKey("played") || state.Played)
        {
            ctx.Job.Done = true;
            return;
        }

        int emoteId = state.EmoteId;
        if (ctx.Job.Data.TryGetValue("emoteId", out var e) && e is int id)
        {
            emoteId = id;
            state.EmoteId = id;
        }

        ctx.Npc.doEmote(emoteId);
        state.Played = true;
        ctx.Job.Data["played"] = true;
        ctx.Job.Done = true;
    }
}
