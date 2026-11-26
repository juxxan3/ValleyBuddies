namespace ValleyBuddies.AgencyCore;

using System;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.Execution;

public sealed class JobExecutionContext
{
    public NpcJob Job { get; }
    public NPC Npc { get; }
    public Farmer? Owner { get; }
    public CompanionState? Companion { get; }
    public ulong GameTick { get; }
    public INpcNavigator Navigator { get; }
    public IFarmWorldService World { get; }
    public Action<string, string> RecordMemory { get; }
    public Action<string, string, string, int, Point> RecordDepositedItem { get; }
    public Action<string> DebugLog { get; }

    public void DismissCompanion(string? npcName = null)
    {
        NpcActionExecutor.Instance.DismissCompanion(npcName ?? Npc.Name);
    }

    public void ReleaseNpc(string? npcName = null)
    {
        NpcActionExecutor.Instance.ReleaseNpc(npcName ?? Npc.Name);
    }

    public GameLocation? FindLocationByNameOrPlayer(string? locName, Farmer? farmer = null)
    {
        return NpcActionExecutor.FindLocationByNameOrPlayer(locName, farmer ?? Owner);
    }

    public void StepFollow()
    {
        NpcActionExecutor.StepFollowInternal(Job, Npc, GameTick);
    }

    public JobExecutionContext(
        NpcJob job,
        NPC npc,
        Farmer? owner,
        CompanionState? companion,
        ulong gameTick,
        INpcNavigator navigator,
        IFarmWorldService world,
        Action<string, string> recordMemory,
        Action<string, string, string, int, Point> recordDepositedItem,
        Action<string> debugLog)
    {
        Job = job;
        Npc = npc;
        Owner = owner;
        Companion = companion;
        GameTick = gameTick;
        Navigator = navigator;
        World = world;
        RecordMemory = recordMemory;
        RecordDepositedItem = recordDepositedItem;
        DebugLog = debugLog;
    }
}
