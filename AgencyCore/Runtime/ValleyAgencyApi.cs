namespace ValleyBuddies.AgencyCore;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Objects;
using ValleyBuddies.Execution;

public sealed class ValleyAgencyApi : IValleyAgencyApi
{
    private readonly ConcurrentDictionary<string, NpcJob> _apiNavJobs = new(StringComparer.OrdinalIgnoreCase);

    // ── Block 1: Identity & State Queries ───────────────────────────────

    public string ApiVersion => "1.0.0";

    public bool IsManagedCompanion(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName) || !Context.IsWorldReady)
            return false;

        return NpcActionExecutor.Instance.IsManagedCompanion(npcName);
    }

    public bool IsSpouseInHomeRoutine(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName) || !Context.IsWorldReady)
            return false;

        return NpcActionExecutor.Instance.IsSpouseInHomeRoutine(npcName);
    }

    public long GetCompanionOwnerId(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName) || !Context.IsWorldReady)
            return 0L;

        return NpcActionExecutor.Instance.GetCompanionOwner(npcName)?.UniqueMultiplayerID ?? 0L;
    }

    public IReadOnlyList<string> GetActiveCompanionNames()
    {
        if (!Context.IsWorldReady)
            return Array.Empty<string>();

        return NpcActionExecutor.Instance.GetActiveCompanionNames();
    }

    public IReadOnlyList<string> GetRegisteredBuiltInJobKinds()
    {
        return Enum.GetNames<JobKind>()
            .Where(n => !n.Equals(nameof(JobKind.Done), StringComparison.OrdinalIgnoreCase) && !n.Equals(nameof(JobKind.ExternalTask), StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetRegisteredExternalTasks()
    {
        return ExternalTaskRegistry.Instance.RegisteredTaskIds
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Block 2: Recruitment, Control & Tasks ───────────────────────────

    public bool TryRecruitCompanion(string npcName, Farmer owner, bool startFollowing = true)
    {
        if (string.IsNullOrWhiteSpace(npcName) || owner == null || !Context.IsWorldReady)
            return false;

        try
        {
            var currentOwner = NpcActionExecutor.Instance.GetCompanionOwner(npcName);
            if (currentOwner != null && currentOwner.UniqueMultiplayerID != owner.UniqueMultiplayerID)
            {
                if (Game1.GetPlayer(currentOwner.UniqueMultiplayerID, onlyOnline: true) != null)
                {
                    return false; // Busy with another active player online
                }
            }

            var npc = Game1.getCharacterFromName(npcName);
            if (npc == null)
                return false;

            JobKind initialKind = startFollowing ? JobKind.Follow : JobKind.SitOrRest;

            if (Game1.IsMultiplayer && !Context.IsMainPlayer)
            {
                ModEntry.BroadcastJob(npcName, initialKind, owner.UniqueMultiplayerID, owner.Name, "silent_resume");
                return true;
            }

            NpcActionExecutor.Instance.SetCompanion(npcName, owner.UniqueMultiplayerID, isVisitingFarm: false, isFollowing: startFollowing, farmerName: owner.Name);

            if (!startFollowing && NpcActionExecutor.Instance.TryGetCompanionState(npcName, out var comp))
            {
                comp.IsRestingInPlace = true;
            }

            NpcActionExecutor.Instance.Enqueue(new NpcJob
            {
                NpcName = npcName,
                Kind = initialKind,
                FarmerId = owner.UniqueMultiplayerID,
                FarmerName = owner.Name,
                CreatedTick = Game1.ticks
            });

            if (Game1.IsMultiplayer)
            {
                ModEntry.BroadcastJob(npcName, initialKind, owner.UniqueMultiplayerID, owner.Name, "silent_resume");
            }

            return true;
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[ValleyAgencyApi] Error recruiting {npcName}: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    public bool DismissCompanion(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName) || !Context.IsWorldReady)
            return false;

        try
        {
            long ownerId = 0;
            string ownerName = string.Empty;
            if (NpcActionExecutor.Instance.TryGetCompanionInfo(npcName, out var info))
            {
                ownerId = info.FarmerId;
                ownerName = info.FarmerName ?? string.Empty;
            }

            NpcActionExecutor.Instance.DismissCompanion(npcName);

            if (Game1.IsMultiplayer)
            {
                ModEntry.BroadcastJob(npcName, JobKind.Done, ownerId, ownerName);
            }

            return true;
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[ValleyAgencyApi] Error dismissing {npcName}: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    public void AbortCurrentJob(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName) || !Context.IsWorldReady)
            return;

        try
        {
            NpcActionExecutor.Instance.AbortCurrentJob(npcName);
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[ValleyAgencyApi] Error aborting job for {npcName}: {ex.Message}", LogLevel.Error);
        }
    }

    public bool TryEnqueueBuiltInJob(string npcName, string jobKindName, Farmer? owner = null, string? extraParam = null)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(jobKindName) || !Context.IsWorldReady)
            return false;

        try
        {
            if (!Enum.TryParse<JobKind>(jobKindName, ignoreCase: true, out var kind))
                return false;

            if (kind == JobKind.Done || kind == JobKind.ExternalTask)
                return false;

            var companionOwner = NpcActionExecutor.Instance.GetCompanionOwner(npcName);
            var targetOwner = owner ?? companionOwner ?? Game1.player;

            if (companionOwner != null && targetOwner != null && companionOwner.UniqueMultiplayerID != targetOwner.UniqueMultiplayerID)
            {
                if (Game1.GetPlayer(companionOwner.UniqueMultiplayerID, onlyOnline: true) != null)
                {
                    return false;
                }
            }

            if (Game1.IsMultiplayer && !Context.IsMainPlayer)
            {
                ModEntry.BroadcastJob(npcName, kind, targetOwner?.UniqueMultiplayerID ?? 0L, targetOwner?.Name ?? string.Empty, extraParam ?? string.Empty);
                return true;
            }

            var job = new NpcJob
            {
                NpcName = npcName,
                Kind = kind,
                FarmerId = targetOwner?.UniqueMultiplayerID ?? 0L,
                FarmerName = targetOwner?.Name ?? string.Empty,
                CreatedTick = Game1.ticks
            };

            if (!string.IsNullOrWhiteSpace(extraParam))
            {
                job.Data["extraParam"] = extraParam;
                job.Data["extra"] = extraParam;

                if (kind == JobKind.ChopTrees) job.Data["treeFilter"] = extraParam;
                else if (kind == JobKind.FetchFromChest) job.Data["fetchCategory"] = extraParam;
                else if (kind == JobKind.TakeFromChest) job.Data["item"] = extraParam;
                else if (kind == JobKind.GiveItem) job.Data["itemName"] = extraParam;
                else if (kind == JobKind.HangOut || kind == JobKind.GoToLocation || kind == JobKind.VisitFarm || kind == JobKind.MeetAtTime) job.Data["locationName"] = extraParam;
                else if (kind == JobKind.DoAnimation) job.Data["anim"] = extraParam;
            }

            NpcActionExecutor.Instance.Enqueue(job);

            if (Game1.IsMultiplayer)
            {
                ModEntry.BroadcastJob(npcName, kind, targetOwner?.UniqueMultiplayerID ?? 0L, targetOwner?.Name ?? string.Empty, extraParam ?? string.Empty);
            }

            return true;
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[ValleyAgencyApi] Error enqueuing built-in job '{jobKindName}' for {npcName}: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    public bool RegisterExternalTask(string taskId, Func<NPC, Farmer?, ulong, bool> onTick, Action<NPC, Farmer?>? onAbort = null)
    {
        return ExternalTaskRegistry.Instance.Register(taskId, onTick, onAbort);
    }

    public bool UnregisterExternalTask(string taskId)
    {
        return ExternalTaskRegistry.Instance.Unregister(taskId);
    }

    public bool TryStartExternalTask(string npcName, string taskId, Farmer? owner = null)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(taskId) || !Context.IsWorldReady)
            return false;

        try
        {
            if (!ExternalTaskRegistry.Instance.TryGet(taskId, out _))
                return false;

            var companionOwner = NpcActionExecutor.Instance.GetCompanionOwner(npcName);
            var targetOwner = owner ?? companionOwner ?? Game1.player;

            if (companionOwner != null && targetOwner != null && companionOwner.UniqueMultiplayerID != targetOwner.UniqueMultiplayerID)
            {
                if (Game1.GetPlayer(companionOwner.UniqueMultiplayerID, onlyOnline: true) != null)
                {
                    return false;
                }
            }

            if (Game1.IsMultiplayer && !Context.IsMainPlayer)
            {
                ModEntry.BroadcastJob(npcName, JobKind.ExternalTask, targetOwner?.UniqueMultiplayerID ?? 0L, targetOwner?.Name ?? string.Empty, taskId);
                return true;
            }

            var job = new NpcJob
            {
                NpcName = npcName,
                Kind = JobKind.ExternalTask,
                FarmerId = targetOwner?.UniqueMultiplayerID ?? 0L,
                FarmerName = targetOwner?.Name ?? string.Empty,
                CreatedTick = Game1.ticks
            };

            job.Data["externalTaskId"] = taskId;
            job.Data["taskId"] = taskId;

            NpcActionExecutor.Instance.Enqueue(job);
            return true;
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[ValleyAgencyApi] Error starting external task '{taskId}' for {npcName}: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    // ── Block 3: Human Navigation ───────────────────────────────────────

    public void ResetNavJobs()
    {
        _apiNavJobs.Clear();
    }

    public bool MoveNpcTowardsTile(NPC npc, GameLocation location, Point targetTile, ulong gameTick)
    {
        if (npc == null || location == null || !Context.IsWorldReady)
            return false;

        try
        {
            if (npc.TilePoint == targetTile)
            {
                StopNpcCleanly(npc);
                _apiNavJobs.TryRemove(npc.Name, out _);
                return false;
            }

            var job = _apiNavJobs.GetOrAdd(npc.Name, name => new NpcJob
            {
                NpcName = name,
                Kind = JobKind.ComeHere,
                CreatedTick = (int)gameTick
            });

            NpcActionExecutor.MoveTowardsInternal(job, npc, location, targetTile, gameTick);
            return true;
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[ValleyAgencyApi] Error in MoveNpcTowardsTile for {npc.Name}: {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    public void StopNpcCleanly(NPC npc)
    {
        if (npc == null) return;
        try
        {
            _apiNavJobs.TryRemove(npc.Name, out _);
            NpcActionExecutor.StopNpcCleanly(npc);
        }
        catch { }
    }

    public bool IsTileWalkable(GameLocation location, Point tile, NPC? npc = null)
    {
        if (location == null) return false;
        return NpcActionExecutor.IsTileWalkable(location, tile, npc);
    }

    public Point GetAdjacentWalkableTile(GameLocation location, Point target, Point from, NPC? npc = null)
    {
        if (location == null) return target;
        return NpcActionExecutor.GetAdjacentWalkableTile(location, target, from, npc);
    }

    public Point FindClosestWalkableCardinalNeighbor(GameLocation location, Point center, NPC? npc = null)
    {
        if (location == null) return center;
        return NpcActionExecutor.FindClosestWalkableCardinalNeighbor(location, center, npc);
    }

    public Point FindSafeWarpSpawnTile(GameLocation location, Point referenceTile, int facingDirection = 2)
    {
        if (location == null) return referenceTile;
        return NpcActionExecutor.FindSafeWarpSpawnTile(location, referenceTile, facingDirection);
    }

    public void InvalidateWalkabilityCache(string locationName)
    {
        NpcActionExecutor.InvalidateWalkabilityCache(locationName);
    }

    // ── Block 4: World, Chest & Dialogue Utilities ───────────────────────

    public Chest? FindNearestChest(GameLocation location, Vector2 fromTile, float maxRadius = 250f)
    {
        if (location == null) return null;
        return NpcActionExecutor.Instance.FarmWorld.FindNearestChest(location, fromTile, maxRadius);
    }

    public bool AddItemToChest(Chest chest, Item item)
    {
        if (chest == null || item == null) return false;
        return NpcActionExecutor.Instance.FarmWorld.AddItemToChest(chest, item);
    }

    public void GiveItemToFarmerDirectly(Farmer player, Item item)
    {
        if (player == null || item == null) return;
        NpcActionExecutor.Instance.FarmWorld.GiveItemToFarmerDirectly(player, item);
    }

    public bool IsSafePlayableFarmTile(GameLocation location, Vector2 tile, NPC? npc = null)
    {
        if (location == null) return false;
        return NpcActionExecutor.Instance.FarmWorld.IsSafePlayableFarmTile(location, tile, npc);
    }

    public void ShowCompanionBubble(NPC npc, string text, int durationMs = 3800)
    {
        if (npc == null || string.IsNullOrWhiteSpace(text)) return;
        try
        {
            ModEntry.ConversationBridge?.ShowCompanionBubble(npc, text, durationMs);
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[ValleyAgencyApi] Error showing companion bubble for {npc.Name}: {ex.Message}", LogLevel.Error);
        }
    }
}
