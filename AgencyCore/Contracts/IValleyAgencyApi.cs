namespace ValleyBuddies.AgencyCore;

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;

/// <summary>
/// Public SMAPI API for Valley Buddies (Pintail-compatible).
/// Exposes companions, jobs, external tasks, human navigation, and farm world utilities.
/// </summary>
public interface IValleyAgencyApi
{
    // ── Block 1: Identity & State Queries ───────────────────────────────

    /// <summary>Semantic version of this public API contract.</summary>
    string ApiVersion { get; }

    /// <summary>Returns true if the NPC is currently an active companion (following, visiting, or night lifecycle).</summary>
    bool IsManagedCompanion(string npcName);

    /// <summary>Returns true if the NPC is currently running their spouse home routine.</summary>
    bool IsSpouseInHomeRoutine(string npcName);

    /// <summary>Returns the UniqueMultiplayerID of the farmer who recruited this companion, or 0 if unassigned.</summary>
    long GetCompanionOwnerId(string npcName);

    /// <summary>Gets the names of all currently managed companions.</summary>
    IReadOnlyList<string> GetActiveCompanionNames();

    /// <summary>Gets all registered built-in job kind names (e.g. WaterCrops, HarvestCrops, etc.).</summary>
    IReadOnlyList<string> GetRegisteredBuiltInJobKinds();

    /// <summary>Gets all registered external task IDs.</summary>
    IReadOnlyList<string> GetRegisteredExternalTasks();

    // ── Block 2: Recruitment, Control & Tasks ───────────────────────────

    /// <summary>
    /// Recruits an NPC as a companion for the given farmer.
    /// In multiplayer, if caller is farmhand, delegates to host safely.
    /// Returns false if the NPC is already accompanying another online player or cannot be recruited.
    /// </summary>
    bool TryRecruitCompanion(string npcName, Farmer owner, bool startFollowing = true);

    /// <summary>Dismisses the companion and releases them back to their native schedule.</summary>
    bool DismissCompanion(string npcName);

    /// <summary>Aborts the active job of the specified companion without dismissing them.</summary>
    void AbortCurrentJob(string npcName);

    /// <summary>
    /// Enqueues a built-in job by kind name (case-insensitive).
    /// Returns false if kind name is invalid, forbidden, or ownership validation fails.
    /// </summary>
    bool TryEnqueueBuiltInJob(string npcName, string jobKindName, Farmer? owner = null, string? extraParam = null);

    /// <summary>Registers a custom external task handler by task ID.</summary>
    bool RegisterExternalTask(string taskId, Func<NPC, Farmer?, ulong, bool> onTick, Action<NPC, Farmer?>? onAbort = null);

    /// <summary>Unregisters an external task by task ID.</summary>
    bool UnregisterExternalTask(string taskId);

    /// <summary>Starts an external task on a companion by task ID.</summary>
    bool TryStartExternalTask(string npcName, string taskId, Farmer? owner = null);

    // ── Block 3: Human Navigation ───────────────────────────────────────

    /// <summary>
    /// Moves an NPC towards a target tile using Valley Buddies A* pathfinding.
    /// Uses an internal lightweight navigation job. Returns true if moving or successfully processing.
    /// </summary>
    bool MoveNpcTowardsTile(NPC npc, GameLocation location, Point targetTile, ulong gameTick);

    /// <summary>Halts and stops the NPC cleanly, clearing paths and controllers.</summary>
    void StopNpcCleanly(NPC npc);

    /// <summary>Checks whether a tile is walkable, safe, and unblocked by gates, water, or impassable objects.</summary>
    bool IsTileWalkable(GameLocation location, Point tile, NPC? npc = null);

    /// <summary>Finds the best adjacent walkable tile to stand next to target.</summary>
    Point GetAdjacentWalkableTile(GameLocation location, Point target, Point from, NPC? npc = null);

    /// <summary>Finds the closest walkable cardinal neighbor (N/S/E/W) of center.</summary>
    Point FindClosestWalkableCardinalNeighbor(GameLocation location, Point center, NPC? npc = null);

    /// <summary>Finds a safe spawn tile near referenceTile when warping characters.</summary>
    Point FindSafeWarpSpawnTile(GameLocation location, Point referenceTile, int facingDirection = 2);

    /// <summary>Invalidates cached tile walkability data for a location (or all locations if empty).</summary>
    void InvalidateWalkabilityCache(string locationName);

    // ── Block 4: World, Chest & Dialogue Utilities ───────────────────────

    /// <summary>Finds the nearest chest in a location within maxRadius tiles.</summary>
    Chest? FindNearestChest(GameLocation location, Vector2 fromTile, float maxRadius = 250f);

    /// <summary>Adds an item to a chest, respecting capacity and stacking.</summary>
    bool AddItemToChest(Chest chest, Item item);

    /// <summary>Gives an item directly to the farmer inventory (or drops on ground if full).</summary>
    void GiveItemToFarmerDirectly(Farmer player, Item item);

    /// <summary>Checks whether a tile is a playable, safe farm tile.</summary>
    bool IsSafePlayableFarmTile(GameLocation location, Vector2 tile, NPC? npc = null);

    /// <summary>Displays an immersive dialogue/reaction bubble above the companion's head.</summary>
    void ShowCompanionBubble(NPC npc, string text, int durationMs = 3800);
}
