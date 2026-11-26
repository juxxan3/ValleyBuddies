namespace ValleyBuddies.AgencyCore;

using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.Execution;

public interface INpcNavigator
{
    void MoveTowards(NpcJob job, NPC npc, GameLocation loc, Point targetTile, ulong gameTick);
    void StopCleanly(NPC npc);
    bool IsTileWalkable(GameLocation loc, Point tile, NPC? npc = null);
    Point GetAdjacentWalkableTile(GameLocation loc, Point target, Point from, NPC? npc = null);
    List<Point>? FindCompleteRoute(GameLocation loc, Point start, Point goal, NPC? npc = null, int maxExpansions = 14000);
    Point GetSafePorchTile(Farm farm);
    Point GetCrossMapSpawnTile(GameLocation targetLoc, Point referenceTile, int facingDirection);
    Point FindSafeWarpSpawnTile(GameLocation loc, Point referenceTile, int facingDirection);
    void InvalidateWalkabilityCache(string locationName);
    void StepControllerIfLocationUnattended(NPC npc, GameLocation loc);
    Point FindClosestWalkableCardinalNeighbor(GameLocation loc, Point center, NPC? npc = null, Point? preferCloserTo = null);
    int DirectionToward(Vector2 from, Vector2 to);
    Point GetOwnerHomeExteriorDoorTile(Farm farm, Farmer? owner);
    void LogWarpAudit(string reason, NPC npc, GameLocation? targetLoc, Point targetTile);
}
