using ValleyBuddies.Config;
using ValleyBuddies.Memory;
using StardewModdingAPI;

namespace ValleyBuddies.Actions;

/// <summary>Runtime dependencies passed to every action during Validate/Execute.</summary>
public sealed class ActionContext
{
    public ActionsConfig Config   { get; }
    public IMonitor      Monitor  { get; }
    public ActionMemory  Memory   { get; }
    public IModHelper    Helper   { get; }

    public ActionContext(ActionsConfig config, IMonitor monitor, ActionMemory memory, IModHelper helper)
    {
        Config  = config;
        Monitor = monitor;
        Memory  = memory;
        Helper  = helper;
    }
}
