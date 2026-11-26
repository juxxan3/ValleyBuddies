namespace ValleyBuddies.Handlers.Social;

using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class DoAnimationHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.DoAnimation;

    public void Tick(JobExecutionContext ctx)
    {
        var state = ctx.Job.GetOrCreateState<DoAnimationState>();
        if (ctx.Job.Data.ContainsKey("played") || state.Played)
        {
            ctx.Job.Done = true;
            return;
        }

        string? animName = state.AnimationName;
        if (ctx.Job.Data.TryGetValue("anim", out var a) && a != null)
        {
            animName = a.ToString();
            state.AnimationName = animName;
        }

        int emote = animName?.ToLower() switch
        {
            "laugh"     => 32,
            "dance"     => 56,
            "surprised" => 16,
            "music"     => 56,
            "question"  => 8,
            "sleep"     => 24,
            "drink"     => 32,
            "pause"     => 40,
            _           => 32
        };

        ctx.Npc.doEmote(emote);
        state.Played = true;
        ctx.Job.Data["played"] = true;
        ctx.Job.Done = true;
    }
}
