using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using ValleyBuddies.Systems;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    private enum WaterTargetKind { HoeDirt, IndoorPot, PetBowl }
    private readonly record struct WaterTarget(Point Tile, WaterTargetKind Kind, Point StandTile);

    internal static bool IsGreenhouseUnlocked(Farm? farm) =>
        farm != null && (farm.greenhouseUnlocked.Value || Game1.MasterPlayer.hasOrWillReceiveMail("ccPantry"));

    private void TickWaterCrops(NpcJob job, NPC npc, ulong gameTick)
    {
        var player = GetJobFarmer(job);
        if (player == null) { CompleteJob(job, npc); return; }

        string waterPhase = job.Data.TryGetValue("water_phase", out var wp) ? wp.ToString()! : "watering";

        // Fase "returning": camina físicamente hasta el jugador que lo invitó
        if (waterPhase == "returning")
        {
            if (gameTick % 15 != 0) return;

            int watered = job.Data.TryGetValue("watered_count", out var rwc) ? (int)rwc : 0;
            string msg = job.Data.TryGetValue("completionMsg", out var cm)
                ? cm.ToString()!
                : Utilities.I18n.Get("bubble.water_done_all", new { count = watered });

            bool stillReturning = ReturnToPlayerAfterTask(job, npc, gameTick, msg);
            if (!stillReturning)
            {
                var farmLoc = Game1.getFarm();
                if (_config?.EnableEasterEggs == true &&
                    (watered >= 30 || (farmLoc != null && farmLoc.buildings != null && farmLoc.buildings.Any(b => b is JunimoHut))))
                {
                    (npc.currentLocation ?? farmLoc)?.playSound("junimoMeep1");
                    (npc.currentLocation ?? farmLoc)?.playSound("getNewSpecialItem");
                }
                CompleteJob(job, npc);
            }
            return;
        }

        // Fase "exiting_greenhouse": caminar hacia la puerta interior y salir a la granja
        if (waterPhase == "exiting_greenhouse")
        {
            if (gameTick % 6 != 0) return;

            if (npc.currentLocation?.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase) == true)
            {
                var curLoc = npc.currentLocation;
                var ghWarpBack = curLoc.warps?.FirstOrDefault(w => w.TargetName.Equals("Farm", StringComparison.OrdinalIgnoreCase)) ?? curLoc.warps?.FirstOrDefault();
                Point exitTile = ghWarpBack != null ? new Point(ghWarpBack.X, Math.Max(1, ghWarpBack.Y - 1)) : new Point(10, 22);

                float distToExit = Vector2.Distance(npc.Tile, exitTile.ToVector2());

                int ghExitStuck = job.Data.TryGetValue("gh_exit_stuck_ticks", out var ges) ? (int)ges : 0;
                Vector2 lastExitPos = job.Data.TryGetValue("gh_exit_last_pos", out var gelp) && gelp is Vector2 gv ? gv : npc.Position;
                if (Vector2.DistanceSquared(npc.Position, lastExitPos) < 0.25f)
                {
                    ghExitStuck += 6;
                }
                else
                {
                    ghExitStuck = Math.Max(0, ghExitStuck - 12);
                    job.Data["gh_exit_last_pos"] = npc.Position;
                }
                job.Data["gh_exit_stuck_ticks"] = ghExitStuck;

                if (distToExit <= 1.8f || (ghExitStuck >= 300 && distToExit <= 4.5f) || ghExitStuck >= 600)
                {
                    job.Data.Remove("gh_exit_stuck_ticks");
                    job.Data.Remove("gh_exit_last_pos");

                    if (Game1.currentLocation is Farm || Game1.currentLocation?.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        Game1.currentLocation.playSound("doorClose");
                    }

                    var outFarm = Game1.getFarm();
                    Point ghDoor = outFarm != null ? GetGreenhouseDoor(outFarm) : new Point(28, 14);
                    Point outDoor = new Point(ghDoor.X, ghDoor.Y + 1);
                    if (outFarm != null && !IsTileWalkable(outFarm, outDoor, npc))
                    {
                        outDoor = GetAdjacentWalkableTile(outFarm, ghDoor, ghDoor, npc);
                    }

                    Game1.warpCharacter(npc, "Farm", new Vector2(outDoor.X, outDoor.Y));
                    npc.controller = null;
                    npc.Halt();

                    job.Data["water_phase"] = "returning";
                    job.Data["water_pause_until"] = gameTick + 15;
                    return;
                }

                MoveNpcTowards(job, npc, curLoc, exitTile, gameTick);
                return;
            }
            else
            {
                job.Data["water_phase"] = "returning";
            }
        }

        if (gameTick % 6 != 0) return;

        var unreachable = job.Data.TryGetValue("unreachable_water", out var uObj) && uObj is HashSet<Point> hs
            ? hs : (HashSet<Point>)(job.Data["unreachable_water"] = new HashSet<Point>());

        GameLocation workLoc = ResolveWateringLocation(job, npc, player, unreachable);
        if (workLoc == null) { CompleteJob(job, npc); return; }
        npc.speed = 2;
        npc.addedSpeed = 0;
        EnsureNpcOnFreeGround(job, npc, workLoc, player);

        // Si el NPC está en la granja y debe pasar al invernadero, caminar físicamente hasta su puerta
        if (npc.currentLocation is Farm farm && workLoc.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase))
        {
            Point doorTarget;
            if (job.Data.TryGetValue("gh_door_target", out var dtObj) && dtObj is Point pt)
            {
                doorTarget = pt;
            }
            else
            {
                // Localizar el warp dinámico hacia Greenhouse usando GetGreenhouseDoor(farm)
                Point ghDoor = GetGreenhouseDoor(farm);
                doorTarget = new Point(ghDoor.X, ghDoor.Y + 1);
                if (!IsTileWalkable(farm, doorTarget, npc))
                {
                    doorTarget = GetAdjacentWalkableTile(farm, ghDoor, npc.TilePoint, npc);
                }

                job.Data["gh_door_target"] = doorTarget;
                job.Data["gh_stuck_ticks"] = 0;
                job.Data["gh_last_pos"] = npc.Position;

                SpeakNonRepeating(npc, "bubble.water_going_greenhouse");
            }

            float distToDoor = Vector2.Distance(npc.Tile, doorTarget.ToVector2());

            // Anti-atasco camino a la puerta
            int ghStuck = job.Data.TryGetValue("gh_stuck_ticks", out var gs) ? (int)gs : 0;
            Vector2 ghLastPos = job.Data.TryGetValue("gh_last_pos", out var glp) && glp is Vector2 gv ? gv : npc.Position;
            if (Vector2.DistanceSquared(npc.Position, ghLastPos) < 0.25f)
            {
                ghStuck += 6;
            }
            else
            {
                ghStuck = Math.Max(0, ghStuck - 12);
                job.Data["gh_last_pos"] = npc.Position;
            }
            job.Data["gh_stuck_ticks"] = ghStuck;

            // Llegada (<= 1.8 tiles), rescate medio (>= 300 ticks cerca), o respaldo absoluto si queda bloqueado lejos (>= 450 ticks)
            if (ghStuck >= 450 && distToDoor > 4.5f)
            {
                job.Data.Remove("gh_door_target");
                job.Data.Remove("gh_stuck_ticks");
                job.Data.Remove("gh_last_pos");
                job.Data.Remove("water_target_loc");
                job.Data["gh_watered_done"] = true;
                job.Data["water_phase"] = "returning";
                SpeakNonRepeating(npc, "chore.path_blocked");
                return;
            }
            if (distToDoor <= 1.8f || (ghStuck >= 300 && distToDoor <= 4.5f))
            {
                job.Data.Remove("gh_door_target");
                job.Data.Remove("gh_stuck_ticks");
                job.Data.Remove("gh_last_pos");

                // Calcula insideTile a 2 casillas adentro desde el warp de salida
                var ghWarpBack = workLoc.warps?.FirstOrDefault(w => w.TargetName.Equals("Farm", StringComparison.OrdinalIgnoreCase));
                Point entry;
                if (ghWarpBack != null)
                {
                    Point candidate = new(ghWarpBack.X, Math.Max(2, ghWarpBack.Y - 2));
                    if (IsTileWalkable(workLoc, candidate, npc))
                        entry = candidate;
                    else
                        entry = FindClosestWalkableCardinalNeighbor(workLoc, candidate, npc);
                }
                else
                {
                    entry = GetDefaultLocationEntry(workLoc, npc);
                }

                Game1.warpCharacter(npc, workLoc, new Vector2(entry.X, entry.Y));
                npc.controller = null;
                npc.Halt();
                unreachable.Clear();
                job.Data.Remove("water_retried_unreachable");

                if (Game1.currentLocation is Farm || Game1.currentLocation?.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase) == true)
                {
                    Game1.currentLocation.playSound("doorClose");
                }

                job.Data["water_pause_until"] = gameTick + 20;
                return;
            }

            MoveNpcTowards(job, npc, farm, doorTarget, gameTick);
            return;
        }

        // Solo cruzar de mapa si el NPC está realmente en una ubicación diferente (nunca warp en el mismo mapa)
        if (!string.Equals(npc.currentLocation?.NameOrUniqueName, workLoc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
        {
            Point entry;
            if (workLoc is Farm fWork)
            {
                Point door = GetOwnerHomeExteriorDoorTile(fWork, player);
                Point outsideDoor = new Point(door.X, door.Y + 1);
                entry = IsTileWalkable(fWork, outsideDoor, npc)
                    ? outsideDoor
                    : GetAdjacentWalkableTile(fWork, door, door, npc);
            }
            else
            {
                entry = GetDefaultLocationEntry(workLoc, npc);
            }

            LogWarpAudit("TickWaterCrops_EnterWorkLoc", npc, workLoc, entry);
            Game1.warpCharacter(npc, workLoc, new Vector2(entry.X, entry.Y));
            npc.controller = null;
            npc.Halt();
            job.Data["water_pause_until"] = gameTick + 20;
            return;
        }

        if (job.Data.TryGetValue("water_pause_until", out var pu) && pu is ulong pauseUntil && gameTick < pauseUntil)
            return;

        int wateredCount = job.Data.TryGetValue("watered_count", out var wc) ? (int)wc : 0;
        int maxPerJob = Math.Clamp(_config?.MaxCropsPerWaterJob ?? 200, 10, 500);

        if (wateredCount >= maxPerJob)
        {
            job.Data["completionMsg"] = Utilities.I18n.Get("bubble.water_done_part", new { count = wateredCount });
            if (workLoc.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase))
            {
                job.Data["gh_watered_done"] = true;
                job.Data.Remove("water_target_loc");
                if (player.currentLocation?.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase) == true)
                {
                    job.Data["water_phase"] = "returning";
                }
                else
                {
                    job.Data["water_phase"] = "exiting_greenhouse";
                }
                return;
            }

            job.Data["water_phase"] = "returning";
            return;
        }

        WaterTarget? current = job.Data.TryGetValue("current_water_target", out var ct) && ct is WaterTarget wt ? wt : null;
        if (current.HasValue && !NeedsWateringNow(workLoc, current.Value))
        {
            current = null;
            job.Data.Remove("current_water_target");
        }

        if (!current.HasValue)
        {
            current = FindNextWaterTarget(workLoc, npc, player, unreachable);
            if (!current.HasValue)
            {
                if (unreachable.Count > 0 && !job.Data.ContainsKey("water_retried_unreachable"))
                {
                    job.Data["water_retried_unreachable"] = true;
                    unreachable.Clear();
                    return;
                }

                if (workLoc is Farm fDone && IsGreenhouseUnlocked(fDone) && !job.Data.ContainsKey("gh_watered_done"))
                {
                    var gh = Game1.getLocationFromName("Greenhouse");
                    if (gh != null && HasAnyDryTarget(gh))
                    {
                        job.Data["water_target_loc"] = "Greenhouse";
                        unreachable.Clear();
                        job.Data.Remove("water_retried_unreachable");
                        return;
                    }
                }

                if (workLoc.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase))
                {
                    job.Data["gh_watered_done"] = true;
                    job.Data.Remove("water_target_loc");

                    string completionMsg = wateredCount > 0
                        ? Utilities.I18n.Get("bubble.water_done_all", new { count = wateredCount })
                        : Utilities.I18n.Get("bubble.water_already_watered");
                    job.Data["completionMsg"] = completionMsg;

                    if (player.currentLocation?.NameOrUniqueName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        job.Data["water_phase"] = "returning";
                    }
                    else
                    {
                        job.Data["water_phase"] = "exiting_greenhouse";
                    }
                    return;
                }

                // Terminó en Farm
                if (wateredCount > 0)
                {
                    job.Data["completionMsg"] = Utilities.I18n.Get("bubble.water_done_all", new { count = wateredCount });
                    job.Data["water_phase"] = "returning";
                    return;
                }
                else
                {
                    FinishWateringJob(job, npc, workLoc, 0, finishedAll: true);
                    return;
                }
            }
            job.Data["current_water_target"] = current.Value;
            job.Data["water_stuck_ticks"] = 0;
            job.Data["water_last_pixel"] = npc.Position;
        }

        WaterTarget target = current.Value;

        // Rango de riego: distancia Chebyshev <= 1 (permite regar espalderas, macetas y cuencos sin pisarlos)
        int chebDist = Math.Max(Math.Abs(npc.TilePoint.X - target.Tile.X), Math.Abs(npc.TilePoint.Y - target.Tile.Y));
        if (chebDist <= 1)
        {
            npc.controller = null;
            npc.Halt();
            npc.faceGeneralDirection(new Vector2(target.Tile.X * 64 + 32, target.Tile.Y * 64 + 32));

            int newlyWatered = ExecuteWaterAction(workLoc, npc, target);
            wateredCount += newlyWatered;
            job.Data["watered_count"] = wateredCount;
            job.Data.Remove("current_water_target");
            job.Data["water_stuck_ticks"] = 0;
            job.Data["water_pause_until"] = gameTick + 24;
            return;
        }

        // Detector anti-atasco por píxeles reales (6 ticks por evaluación)
        int stuck = job.Data.TryGetValue("water_stuck_ticks", out var ws) ? (int)ws : 0;
        Vector2 lastPx = job.Data.TryGetValue("water_last_pixel", out var lp) && lp is Vector2 v ? v : npc.Position;
        if (Vector2.DistanceSquared(npc.Position, lastPx) < 0.25f)
            stuck += 6;
        else
        {
            stuck = Math.Max(0, stuck - 12);
            job.Data["water_last_pixel"] = npc.Position;
        }
        job.Data["water_stuck_ticks"] = stuck;

        if (stuck == 120)
        {
            npc.controller = null;
            npc.Halt();
            job.Data["lastPathTick"] = 0UL;
        }
        else if (stuck == 180)
        {
            Point? altStand = FindAlternativeWateringStandTile(workLoc, target.Tile, target.StandTile, npc.TilePoint, npc);
            if (altStand.HasValue)
            {
                target = target with { StandTile = altStand.Value };
                job.Data["current_water_target"] = target;
                npc.controller = null;
                npc.Halt();
                job.Data["lastPathTick"] = 0UL;
            }
        }
        else if (stuck >= 270)
        {
            if (workLoc is Farm && TryBreakAdjacentBlockingDebris(npc, workLoc, player, new Vector2(target.Tile.X, target.Tile.Y)))
            {
                job.Data["water_stuck_ticks"] = 0;
                job.Data["lastPathTick"] = 0UL;
                job.Data["water_pause_until"] = gameTick + 35;
                return;
            }

            unreachable.Add(target.Tile);
            job.Data.Remove("current_water_target");
            job.Data["water_stuck_ticks"] = 0;
            npc.controller = null;
            npc.Halt();

            if (unreachable.Count >= 20)
            {
                var unreachableList = unreachable.Select(p => new Vector2(p.X, p.Y)).ToList();
                if (HandleUnreachableTaskTargets(job, npc, workLoc, player, unreachableList, "WaterCrops", new Vector2(target.Tile.X, target.Tile.Y)))
                {
                    FinishWateringJob(job, npc, workLoc, wateredCount, finishedAll: false);
                    return;
                }
            }
            return;
        }

        MoveNpcTowards(job, npc, workLoc, target.StandTile, gameTick);
    }

    private GameLocation ResolveWateringLocation(NpcJob job, NPC npc, Farmer player, HashSet<Point> unreachable)
    {
        if (job.Data.TryGetValue("water_target_loc", out var forcedObj) && forcedObj is string forcedName && !string.IsNullOrWhiteSpace(forcedName))
        {
            var forcedLoc = Game1.getLocationFromName(forcedName);
            if (forcedLoc != null && HasAnyDryTarget(forcedLoc, unreachable))
            {
                return forcedLoc;
            }
            job.Data.Remove("water_target_loc");
        }

        var pLoc = player.currentLocation;
        if (pLoc != null && HasAnyDryTarget(pLoc, unreachable)) return pLoc;
        if (npc.currentLocation != null && HasAnyDryTarget(npc.currentLocation, unreachable)) return npc.currentLocation;
        var farm = Game1.getFarm();
        if (farm != null && HasAnyDryTarget(farm, unreachable)) return farm;
        if (IsGreenhouseUnlocked(farm))
        {
            var gh = Game1.getLocationFromName("Greenhouse");
            if (gh != null && !job.Data.ContainsKey("gh_watered_done") && HasAnyDryTarget(gh, unreachable))
            {
                job.Data["water_target_loc"] = "Greenhouse";
                return gh;
            }
        }
        return farm ?? pLoc!;
    }

    private static bool NeedsWateringNow(GameLocation loc, WaterTarget t)
    {
        return t.Kind switch
        {
            WaterTargetKind.HoeDirt =>
                loc.terrainFeatures.TryGetValue(new Vector2(t.Tile.X, t.Tile.Y), out var tf) &&
                tf is HoeDirt hd && IsHoeDirtDry(loc, hd),
            WaterTargetKind.IndoorPot =>
                loc.Objects.TryGetValue(new Vector2(t.Tile.X, t.Tile.Y), out var obj) &&
                obj is IndoorPot pot && pot.hoeDirt.Value != null && IsHoeDirtDry(loc, pot.hoeDirt.Value, ignoreRain: true),
            WaterTargetKind.PetBowl =>
                loc.buildings != null && loc.buildings.OfType<PetBowl>().Any(b => b.tileX.Value == t.Tile.X && b.tileY.Value == t.Tile.Y && !b.watered.Value),
            _ => false
        };
    }

    private static bool IsHoeDirtDry(GameLocation loc, HoeDirt hd, bool ignoreRain = false)
    {
        if (hd == null) return false;
        if (hd.crop == null || hd.crop.dead.Value) return false; // NUNCA regar tierra vacía sin planta ni cultivos muertos
        if (!ignoreRain && loc.IsOutdoors && Game1.IsRainingHere(loc)) return false;
        if (hd.isWatered() || hd.state.Value == HoeDirt.watered) return false;
        return hd.state.Value == 0;
    }

    private bool HasAnyDryTarget(GameLocation loc, HashSet<Point>? excluded = null)
    {
        if (loc.terrainFeatures.Pairs.Any(kvp => kvp.Value is HoeDirt hd && (excluded == null || !excluded.Contains(new Point((int)kvp.Key.X, (int)kvp.Key.Y))) && IsHoeDirtDry(loc, hd))) return true;
        if (loc.Objects.Pairs.Any(kvp => kvp.Value is IndoorPot p && (excluded == null || !excluded.Contains(new Point((int)kvp.Key.X, (int)kvp.Key.Y))) && p.hoeDirt.Value != null && IsHoeDirtDry(loc, p.hoeDirt.Value, ignoreRain: true))) return true;
        if (loc.buildings != null && loc.buildings.OfType<PetBowl>().Any(b => (excluded == null || !excluded.Contains(new Point(b.tileX.Value, b.tileY.Value))) && !b.watered.Value)) return true;
        return false;
    }

    private WaterTarget? FindNextWaterTarget(GameLocation loc, NPC npc, Farmer player, HashSet<Point> unreachable)
    {
        var candidates = new List<(Point Tile, WaterTargetKind Kind)>();

        foreach (var kvp in loc.terrainFeatures.Pairs)
        {
            if (kvp.Value is HoeDirt hd && IsHoeDirtDry(loc, hd))
            {
                Point pt = new((int)kvp.Key.X, (int)kvp.Key.Y);
                if (!unreachable.Contains(pt)) candidates.Add((pt, WaterTargetKind.HoeDirt));
            }
        }
        foreach (var kvp in loc.Objects.Pairs)
        {
            if (kvp.Value is IndoorPot pot && pot.hoeDirt.Value != null && IsHoeDirtDry(loc, pot.hoeDirt.Value, ignoreRain: true))
            {
                Point pt = new((int)kvp.Key.X, (int)kvp.Key.Y);
                if (!unreachable.Contains(pt)) candidates.Add((pt, WaterTargetKind.IndoorPot));
            }
        }
        if (loc.buildings != null)
        {
            foreach (var bowl in loc.buildings.OfType<PetBowl>())
            {
                if (!bowl.watered.Value)
                {
                    Point pt = new(bowl.tileX.Value, bowl.tileY.Value);
                    if (!unreachable.Contains(pt)) candidates.Add((pt, WaterTargetKind.PetBowl));
                }
            }
        }

        NpcJob? activeJob = null;
        if (_active.TryGetValue(npc.Name, out var aj)) activeJob = aj;
        if (activeJob != null)
        {
            if (!activeJob.Data.ContainsKey("task_dispatched_logged"))
            {
                activeJob.Data["task_dispatched_logged"] = true;
                ModEntry.ModMonitor?.Log($"[TaskDispatch] {npc.Name} job=WaterCrops loc={loc.NameOrUniqueName} candidates={candidates.Count} reason={(candidates.Count > 0 ? "found_targets" : "all_crops_watered")}", LogLevel.Debug);
            }
        }
        else if (TryGetSpouseHomeState(npc.Name, out var sState))
        {
            if (!sState.WateringDispatchLogged)
            {
                sState.WateringDispatchLogged = true;
                ModEntry.ModMonitor?.Log($"[TaskDispatch] {npc.Name} job=WaterCrops loc={loc.NameOrUniqueName} candidates={candidates.Count} reason={(candidates.Count > 0 ? "found_targets" : "all_crops_watered")}", LogLevel.Debug);
            }
        }
        if (candidates.Count == 0) return null;

        // Búsqueda en dos niveles: radio configurado + respaldo de mapa completo
        int scanRadius = _config?.FarmWorkRadius ?? 35;
        int radiusSq = scanRadius * scanRadius;
        Point npcTile = npc.TilePoint;
        var localPool = candidates.Where(c => DistSq(c.Tile, npcTile) <= radiusSq)
            .OrderBy(c => DistSq(c.Tile, npcTile) + (Math.Abs(c.Tile.Y - npcTile.Y) * 2))
            .ToList();

        foreach (var c in localPool)
        {
            Point? stand = FindBestWateringStandTile(loc, c.Tile, npcTile, npc);
            if (stand.HasValue)
                return new WaterTarget(c.Tile, c.Kind, stand.Value);
            unreachable.Add(c.Tile);
        }

        if (_config?.AllowFullMapFarmScan ?? true)
        {
            var remainingPool = candidates.Where(c => DistSq(c.Tile, npcTile) > radiusSq && !unreachable.Contains(c.Tile))
                .OrderBy(c => DistSq(c.Tile, npcTile) + (Math.Abs(c.Tile.Y - npcTile.Y) * 2))
                .ToList();

            foreach (var c in remainingPool)
            {
                Point? stand = FindBestWateringStandTile(loc, c.Tile, npcTile, npc);
                if (stand.HasValue)
                    return new WaterTarget(c.Tile, c.Kind, stand.Value);
                unreachable.Add(c.Tile);
            }
        }

        return null;
    }

    private static Point? FindAlternativeWateringStandTile(GameLocation loc, Point targetTile, Point currentStand, Point fromTile, NPC npc)
    {
        Point[] options =
        {
            new(targetTile.X, targetTile.Y + 1),
            new(targetTile.X, targetTile.Y - 1),
            new(targetTile.X + 1, targetTile.Y),
            new(targetTile.X - 1, targetTile.Y),
            targetTile
        };
        foreach (var opt in options.Where(o => o != currentStand).OrderBy(o => DistSq(o, fromTile)))
            if (IsTileWalkable(loc, opt, npc)) return opt;

        Point[] diags =
        {
            new(targetTile.X + 1, targetTile.Y + 1), new(targetTile.X - 1, targetTile.Y + 1),
            new(targetTile.X + 1, targetTile.Y - 1), new(targetTile.X - 1, targetTile.Y - 1)
        };
        foreach (var d in diags.Where(d => d != currentStand).OrderBy(o => DistSq(o, fromTile)))
            if (IsTileWalkable(loc, d, npc)) return d;

        return null;
    }

    private static Point? FindBestWateringStandTile(GameLocation loc, Point targetTile, Point fromTile, NPC npc)
    {
        Point[] options =
        {
            new(targetTile.X, targetTile.Y + 1),
            new(targetTile.X, targetTile.Y - 1),
            new(targetTile.X + 1, targetTile.Y),
            new(targetTile.X - 1, targetTile.Y),
            targetTile
        };
        foreach (var opt in options.OrderBy(o => DistSq(o, fromTile)))
            if (IsTileWalkable(loc, opt, npc)) return opt;

        Point[] diags =
        {
            new(targetTile.X + 1, targetTile.Y + 1), new(targetTile.X - 1, targetTile.Y + 1),
            new(targetTile.X + 1, targetTile.Y - 1), new(targetTile.X - 1, targetTile.Y - 1)
        };
        foreach (var d in diags.OrderBy(o => DistSq(o, fromTile)))
            if (IsTileWalkable(loc, d, npc)) return d;

        return null;
    }

    private int ExecuteWaterAction(GameLocation loc, NPC npc, WaterTarget target)
    {
        int count = 0;

        // Easter Egg #8: Riego Arcano 3x3 del Mago (Wizard)
        if (_config?.EnableEasterEggs == true && npc.Name.Equals("Wizard", StringComparison.OrdinalIgnoreCase) &&
            target.Kind == WaterTargetKind.HoeDirt)
        {
            loc.playSound("coldSpell");
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    Point p = new(target.Tile.X + dx, target.Tile.Y + dy);
                    if (loc.terrainFeatures.TryGetValue(new Vector2(p.X, p.Y), out var tf) &&
                        tf is HoeDirt hd && IsHoeDirtDry(loc, hd))
                    {
                        hd.state.Value = HoeDirt.watered;
                        BroadcastWaterSplash(loc, p, arcane: true);
                        count++;
                    }
                }
            }
            if (count > 0) return count;
        }

        if (target.Kind == WaterTargetKind.HoeDirt &&
            loc.terrainFeatures.TryGetValue(new Vector2(target.Tile.X, target.Tile.Y), out var tf2) &&
            tf2 is HoeDirt dirt)
        {
            dirt.state.Value = HoeDirt.watered;
            ToolAnimationService.PlayWateringCanPour(loc, npc, target.Tile, playSound: false);
            loc.playSound("wateringCan");
            BroadcastWaterSplash(loc, target.Tile, arcane: false);
            count = 1;
        }
        else if (target.Kind == WaterTargetKind.IndoorPot &&
                 loc.Objects.TryGetValue(new Vector2(target.Tile.X, target.Tile.Y), out var obj) &&
                 obj is IndoorPot pot)
        {
            pot.Water();
            ToolAnimationService.PlayWateringCanPour(loc, npc, target.Tile, playSound: false);
            loc.playSound("wateringCan");
            BroadcastWaterSplash(loc, target.Tile, arcane: false);
            count = 1;
        }
        else if (target.Kind == WaterTargetKind.PetBowl && loc.buildings != null)
        {
            var bowl = loc.buildings.OfType<PetBowl>()
                .FirstOrDefault(b => b.tileX.Value == target.Tile.X && b.tileY.Value == target.Tile.Y);
            if (bowl != null && !bowl.watered.Value)
            {
                bowl.watered.Value = true;
                loc.playSound("glug");
                BroadcastWaterSplash(loc, target.Tile, arcane: false);
                npc.doEmote(20);
                count = 1;
            }
        }
        return count;
    }

    private static void BroadcastWaterSplash(GameLocation loc, Point tile, bool arcane)
    {
        var sprite = new TemporaryAnimatedSprite(
            rowInAnimationTexture: 13,
            position: new Vector2(tile.X * 64f, tile.Y * 64f),
            color: arcane ? Color.MediumPurple : Color.White,
            animationLength: 10,
            flipped: false,
            animationInterval: 65f)
        {
            layerDepth = ((tile.Y * 64f) + 32f) / 10000f
        };
        BroadcastTemporarySprite(loc, sprite);
    }

    private void FinishWateringJob(NpcJob job, NPC npc, GameLocation loc, int wateredCount, bool finishedAll)
    {
        if (wateredCount > 0)
        {
            npc.doEmote(32); // happyEmote oficial
            npc.showTextAboveHead(
                finishedAll
                    ? Utilities.I18n.Get("bubble.water_done_all", new { count = wateredCount })
                    : Utilities.I18n.Get("bubble.water_done_part", new { count = wateredCount }),
                duration: 3500);

            // Easter Egg #11: Coro Junimo al terminar la parcela
            if (_config?.EnableEasterEggs == true && finishedAll &&
                (wateredCount >= 30 || (loc is Farm f && f.buildings != null && f.buildings.Any(b => b is JunimoHut))))
            {
                loc.playSound("junimoMeep1");
                loc.playSound("getNewSpecialItem");
            }
        }
        else
        {
            npc.doEmote(8); // questionMarkEmote
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.water_already_watered"), duration: 3000);
        }
        CompleteJob(job, npc);
    }

    private static int DistSq(Point a, Point b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
}
