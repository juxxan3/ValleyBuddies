namespace ValleyBuddies.AgencyCore;

using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.Execution;

public sealed class NpcNavigatorAdapter : INpcNavigator
{
    public void MoveTowards(NpcJob job, NPC npc, GameLocation loc, Point targetTile, ulong gameTick)
    {
        NpcActionExecutor.MoveTowardsInternal(job, npc, loc, targetTile, gameTick);
    }

    public void StopCleanly(NPC npc)
    {
        NpcActionExecutor.StopNpcCleanly(npc);
    }

    public bool IsTileWalkable(GameLocation loc, Point tile, NPC? npc = null)
    {
        return NpcActionExecutor.IsTileWalkable(loc, tile, npc);
    }

    public Point GetAdjacentWalkableTile(GameLocation loc, Point target, Point from, NPC? npc = null)
    {
        return NpcActionExecutor.GetAdjacentWalkableTile(loc, target, from, npc);
    }

    public List<Point>? FindCompleteRoute(GameLocation loc, Point start, Point goal, NPC? npc = null, int maxExpansions = 14000)
    {
        return NpcActionExecutor.FindCompleteRouteInternal(loc, start, goal, npc, maxExpansions);
    }

    public Point GetSafePorchTile(Farm farm)
    {
        return NpcActionExecutor.GetFarmSafePorchTile(farm);
    }

    public Point GetCrossMapSpawnTile(GameLocation targetLoc, Point referenceTile, int facingDirection)
    {
        return NpcActionExecutor.GetCrossMapSpawnTile(targetLoc, referenceTile, facingDirection);
    }

    public Point FindSafeWarpSpawnTile(GameLocation loc, Point referenceTile, int facingDirection)
    {
        return NpcActionExecutor.FindSafeWarpSpawnTile(loc, referenceTile, facingDirection);
    }

    public void InvalidateWalkabilityCache(string locationName)
    {
        NpcActionExecutor.InvalidateWalkabilityCache(locationName);
    }

    public void StepControllerIfLocationUnattended(NPC npc, GameLocation loc)
    {
        NpcActionExecutor.StepNpcControllerIfLocationUnattended(npc, loc);
    }

    public Point FindClosestWalkableCardinalNeighbor(GameLocation loc, Point center, NPC? npc = null, Point? preferCloserTo = null)
    {
        return NpcActionExecutor.FindClosestWalkableCardinalNeighbor(loc, center, npc, preferCloserTo);
    }

    public int DirectionToward(Vector2 from, Vector2 to)
    {
        return NpcActionExecutor.DirectionToward(from, to);
    }

    public Point GetOwnerHomeExteriorDoorTile(Farm farm, Farmer? owner)
    {
        return NpcActionExecutor.GetOwnerHomeExteriorDoorTile(farm, owner);
    }

    public void LogWarpAudit(string reason, NPC npc, GameLocation? targetLoc, Point targetTile)
    {
        NpcActionExecutor.Instance.LogWarpAudit(reason, npc, targetLoc, targetTile);
    }
}
