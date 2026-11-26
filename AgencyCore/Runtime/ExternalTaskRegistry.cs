namespace ValleyBuddies.AgencyCore;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using StardewValley;

public sealed class ExternalTaskRegistration
{
    public string TaskId { get; }
    public Func<NPC, Farmer?, ulong, bool> OnTick { get; }
    public Action<NPC, Farmer?>? OnAbort { get; }

    public ExternalTaskRegistration(string taskId, Func<NPC, Farmer?, ulong, bool> onTick, Action<NPC, Farmer?>? onAbort = null)
    {
        TaskId = taskId;
        OnTick = onTick;
        OnAbort = onAbort;
    }
}

public sealed class ExternalTaskRegistry
{
    public static readonly ExternalTaskRegistry Instance = new();

    private readonly ConcurrentDictionary<string, ExternalTaskRegistration> _tasks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> RegisteredTaskIds => (IReadOnlyCollection<string>)_tasks.Keys;

    public bool Register(string taskId, Func<NPC, Farmer?, ulong, bool> onTick, Action<NPC, Farmer?>? onAbort = null)
    {
        if (string.IsNullOrWhiteSpace(taskId) || onTick == null)
            return false;

        var reg = new ExternalTaskRegistration(taskId, onTick, onAbort);
        _tasks[taskId] = reg;
        return true;
    }

    public bool Unregister(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return false;

        return _tasks.TryRemove(taskId, out _);
    }

    public bool TryGet(string taskId, [NotNullWhen(true)] out ExternalTaskRegistration? registration)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            registration = null;
            return false;
        }

        return _tasks.TryGetValue(taskId, out registration);
    }

    public bool TryGet(string taskId, [NotNullWhen(true)] out Func<NPC, Farmer?, ulong, bool>? onTick, out Action<NPC, Farmer?>? onAbort)
    {
        if (TryGet(taskId, out var reg) && reg != null)
        {
            onTick = reg.OnTick;
            onAbort = reg.OnAbort;
            return true;
        }

        onTick = null;
        onAbort = null;
        return false;
    }

    public void Clear()
    {
        _tasks.Clear();
    }
}
