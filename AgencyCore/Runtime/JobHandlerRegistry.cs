namespace ValleyBuddies.AgencyCore;

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using ValleyBuddies.Execution;

public sealed class JobHandlerRegistry
{
    private readonly Dictionary<JobKind, INpcJobHandler> _handlers = new();

    public IReadOnlyCollection<JobKind> RegisteredKinds => _handlers.Keys;

    public void Register(INpcJobHandler handler)
    {
        _handlers[handler.Kind] = handler;
    }

    public bool TryGetHandler(JobKind kind, [NotNullWhen(true)] out INpcJobHandler? handler)
    {
        return _handlers.TryGetValue(kind, out handler);
    }

    public bool TryTick(JobExecutionContext ctx)
    {
        if (TryGetHandler(ctx.Job.Kind, out var handler))
        {
            handler.Tick(ctx);
            return true;
        }
        return false;
    }

    public void NotifyAbort(JobExecutionContext ctx)
    {
        if (TryGetHandler(ctx.Job.Kind, out var handler))
        {
            handler.OnAbort(ctx);
        }
    }
}
