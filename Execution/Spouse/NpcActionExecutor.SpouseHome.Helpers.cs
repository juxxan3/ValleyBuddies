using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using ValleyBuddies.Systems;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    /// <summary>
    /// Suma minutos al formato decimal militar de Stardew Valley (ej. 650 + 30 = 720).
    /// </summary>
    public static int AdvanceGameTime(int time, int minutesToAdd)
    {
        int hours = time / 100;
        int minutes = time % 100;
        int totalMinutes = hours * 60 + minutes + minutesToAdd;
        int nextHours = totalMinutes / 60;
        int nextMinutes = totalMinutes % 60;
        return nextHours * 100 + nextMinutes;
    }

    public static FarmHouse? GetOwnerHouse(Farmer? owner)
    {
        if (owner == null) return Game1.getLocationFromName("FarmHouse") as FarmHouse;
        try
        {
            var home = Utility.getHomeOfFarmer(owner);
            if (home is FarmHouse fh) return fh;
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(owner.homeLocation?.Value))
        {
            var loc = Game1.getLocationFromName(owner.homeLocation.Value);
            if (loc is FarmHouse fhLoc) return fhLoc;
        }

        return Game1.getLocationFromName("FarmHouse") as FarmHouse;
    }

    public static GameLocation? GetOwnerHomeLocation(Farmer? owner)
    {
        if (owner == null) return Game1.getLocationFromName("FarmHouse");
        try
        {
            var home = Utility.getHomeOfFarmer(owner);
            if (home != null) return home;
        }
        catch { }

        if (!string.IsNullOrWhiteSpace(owner.homeLocation?.Value))
        {
            var loc = Game1.getLocationFromName(owner.homeLocation.Value);
            if (loc != null) return loc;
        }

        return Game1.getLocationFromName("FarmHouse");
    }

    public static Point GetOwnerHomeExteriorDoorTile(Farm farm, Farmer? owner)
    {
        if (farm == null) return new Point(64, 15);
        if (owner == null || owner.IsMainPlayer)
            return farm.GetMainFarmHouseEntry();

        var homeLoc = GetOwnerHomeLocation(owner);
        if (homeLoc == null || (homeLoc is FarmHouse && string.Equals(homeLoc.NameOrUniqueName, "FarmHouse", StringComparison.OrdinalIgnoreCase)))
            return farm.GetMainFarmHouseEntry();

        if (farm.buildings != null)
        {
            foreach (var b in farm.buildings)
            {
                if (b == null) continue;
                if (b.indoors?.Value != null && (b.indoors.Value == homeLoc || string.Equals(b.indoors.Value.NameOrUniqueName, homeLoc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase)))
                {
                    return b.getPointForHumanDoor();
                }
            }
        }

        return farm.GetMainFarmHouseEntry();
    }

    /// <summary>
    /// Encuentra el vecino cardinal libre más cercano a una casilla objetivo (para cofres, puertas o muebles sólidos).
    /// </summary>
    public static Point FindClosestWalkableCardinalNeighbor(GameLocation loc, Point targetTile, NPC? npc = null, Point? preferCloserTo = null)
    {
        if (loc == null) return Point.Zero;

        Point from = preferCloserTo ?? (npc?.TilePoint ?? targetTile);
        Point[] cardinals = new[]
        {
            new Point(targetTile.X, targetTile.Y + 1), // Sur
            new Point(targetTile.X, targetTile.Y - 1), // Norte
            new Point(targetTile.X + 1, targetTile.Y), // Este
            new Point(targetTile.X - 1, targetTile.Y)  // Oeste
        };

        bool isHouse = loc is FarmHouse || (loc != null && loc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase));
        Point best = Point.Zero;
        float bestDist = float.MaxValue;

        foreach (var cand in cardinals)
        {
            if (isHouse && cand.Y < 4) continue;
            if (IsTileWalkable(loc!, cand, npc, ignoreDynamicCharacters: true, allowWarpTile: false))
            {
                float d = Vector2.Distance(cand.ToVector2(), from.ToVector2());
                if (d < bestDist)
                {
                    bestDist = d;
                    best = cand;
                }
            }
        }

        if (best != Point.Zero)
            return best;

        Point fallback = GetAdjacentWalkableTile(loc!, targetTile, from, npc, maxRadius: 2);
        if (isHouse && fallback.Y < 4)
            return Point.Zero;

        return fallback;
    }

    /// <summary>
    /// Inicia una ruta personalizada hacia el destino usando FindCustomPath y asigna el PathFindController sin destructividad.
    /// </summary>
    public static bool TryStartPathTo(NPC npc, GameLocation loc, Point destination, bool allowWarpExit = false)
    {
        if (npc == null || loc == null) return false;

        if (!IsTileWalkable(loc, destination, npc, ignoreDynamicCharacters: true, allowWarpTile: allowWarpExit))
            destination = GetAdjacentWalkableTile(loc, destination, npc.TilePoint, npc);

        if (npc.TilePoint == destination)
        {
            StopNpcCleanly(npc);
            return true;
        }

        var path = FindCustomPath(loc, npc.TilePoint, destination, npc, 4500, allowWarpExit);
        if (path != null && path.Count > 0)
        {
            npc.Halt();
            npc.controller = new StardewValley.Pathfinding.PathFindController(path, loc, npc, destination)
            {
                nonDestructivePathing = true
            };
            return true;
        }

        StopNpcCleanly(npc);
        return false;
    }

    /// <summary>
    /// Calcula un waypoint intermedio a 6-12 casillas en abanico angular hacia la meta para no exceder el presupuesto A*.
    /// </summary>
    private static Point? CalculateIntermediateWaypoint(GameLocation loc, Point start, Point goal, NPC? npc, int retryCount = 0)
    {
        float dx = goal.X - start.X;
        float dy = goal.Y - start.Y;
        float dist = (float)Math.Sqrt(dx * dx + dy * dy);
        if (dist <= 1.0f) return null;

        double baseAngle = Math.Atan2(dy, dx);
        int[] radii = dist > 16f ? new[] { 12, 9, 6 } : new[] { (int)Math.Max(4, dist * 0.7f), (int)Math.Max(3, dist * 0.5f) };

        double[] angleOffsets = (retryCount % 2 == 1)
            ? new double[] { 50, -50, 25, -25, 75, -75, 0 }
            : new double[] { 0, 25, -25, 50, -50, 75, -75 };

        int mapW = loc.map?.Layers?[0]?.LayerWidth ?? 100;
        int mapH = loc.map?.Layers?[0]?.LayerHeight ?? 100;

        foreach (int r in radii)
        {
            foreach (double deg in angleOffsets)
            {
                double rad = baseAngle + (deg * Math.PI / 180.0);
                int cx = (int)Math.Round(start.X + r * Math.Cos(rad));
                int cy = (int)Math.Round(start.Y + r * Math.Sin(rad));

                if (cx < 1 || cx >= mapW - 1 || cy < 1 || cy >= mapH - 1)
                    continue;

                Point cand = new Point(cx, cy);
                if (!IsTileWalkable(loc, cand, npc, ignoreDynamicCharacters: true, allowWarpTile: false))
                    continue;

                var testPath = FindCustomPath(loc, start, cand, npc, maxExpansions: 1800, allowWarpExit: false);
                if (testPath != null && testPath.Count > 0)
                {
                    return cand;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Avanza manualmente el controlador de un NPC cuando se encuentra en un edificio o mapa sin granjeros presentes,
    /// evitando que su navegación física se congele debido a que SDV no ejecuta updateCharacters() en ubicaciones desatendidas.
    /// </summary>
    private static readonly Dictionary<string, ulong> _lastUnattendedStepTick = new(StringComparer.OrdinalIgnoreCase);

    internal static void ClearUnattendedStepCache() => _lastUnattendedStepTick.Clear();

    internal static void StepNpcControllerIfLocationUnattended(NPC npc, GameLocation loc)
    {
        if (npc == null || loc == null || npc.controller == null)
            return;

        ulong curTick = ModEntry.CurrentTick;
        if (_lastUnattendedStepTick.TryGetValue(npc.Name, out ulong lastT) && lastT == curTick)
            return;

        bool anyFarmerHere = Game1.getOnlineFarmers().Any(f => f?.currentLocation == loc);
        if (!anyFarmerHere)
        {
            _lastUnattendedStepTick[npc.Name] = curTick;
            try
            {
                npc.controller.update(Game1.currentGameTime);
            }
            catch { }
        }
    }

    /// <summary>
    /// Motor de caminata 100% física con paciencia basada en movimiento en píxeles y waypoints intermedios.
    /// Reemplaza cualquier atajo o teletransporte dentro del mismo mapa.
    /// </summary>
    public WalkResult WalkPhysicallyTo(
        NPC spouse,
        GameLocation loc,
        Point finalStandTile,
        SpouseHomeState state,
        ulong gameTick,
        float arrivalRadiusTiles = 1.65f)
    {
        if (spouse == null || loc == null) return WalkResult.Unreachable;

        // Avanzar el controlador si la ubicación no tiene jugadores presentes
        StepNpcControllerIfLocationUnattended(spouse, loc);

        // Mantener paso tranquilo en todo momento
        spouse.speed = 2;
        spouse.addedSpeed = 0;

        // Asegurar casilla destino transitable
        if (!IsTileWalkable(loc, finalStandTile, spouse, ignoreDynamicCharacters: true, allowWarpTile: false))
        {
            finalStandTile = FindClosestWalkableCardinalNeighbor(loc, finalStandTile, spouse);
            if (finalStandTile == Point.Zero)
            {
                StopNpcCleanly(spouse);
                state.CurrentWaypoint = null;
                state.CurrentNavTarget = null;
                return WalkResult.Unreachable;
            }
        }

        // 1. Verificación de llegada
        float distToFinal = Vector2.Distance(spouse.TilePoint.ToVector2(), finalStandTile.ToVector2());
        if (distToFinal <= arrivalRadiusTiles)
        {
            StopNpcCleanly(spouse);
            state.CurrentWaypoint = null;
            state.CurrentNavTarget = null;
            state.RealStuckTicks = 0;
            state.StuckRetryCount = 0;
            return WalkResult.Arrived;
        }

        // 2. Medición de avance real en píxeles cada 60 ticks (1 segundo)
        if (state.LastPixelCheckTick == 0)
        {
            state.LastPixelPos = spouse.Position;
            state.LastPixelCheckTick = gameTick;
            state.RealStuckTicks = 0;
        }
        else if (gameTick >= state.LastPixelCheckTick + 60)
        {
            float pxMoved = Vector2.Distance(spouse.Position, state.LastPixelPos);
            state.LastPixelPos = spouse.Position;
            state.LastPixelCheckTick = gameTick;

            if (pxMoved >= 6.0f)
            {
                state.RealStuckTicks = 0;
            }
            else
            {
                state.RealStuckTicks += 60;
            }
        }

        // 3. Manejo de estancamiento (>= 180 ticks = 3s continuos sin avanzar >= 6px)
        if (state.RealStuckTicks >= 180)
        {
            state.RealStuckTicks = 0;
            state.StuckRetryCount++;

            // En FarmHouse, comprobar si la propia baldosa del cónyuge está superpuesta con un mueble sólido y deslizarla suavemente
            if (loc is FarmHouse fh)
            {
                Rectangle spouseBox = spouse.GetBoundingBox();
                var collidingFurniture = fh.furniture?.FirstOrDefault(f => f != null && f.furniture_type.Value != Furniture.rug && !f.isPassable() && f.GetBoundingBox().Intersects(spouseBox));
                if (collidingFurniture != null)
                {
                    Point freeTile = FindClosestWalkableCardinalNeighbor(fh, spouse.TilePoint, spouse);
                    if (freeTile != Point.Zero && freeTile != spouse.TilePoint)
                    {
                        spouse.setTilePosition(freeTile);
                        spouse.Position = new Vector2(freeTile.X * 64f, freeTile.Y * 64f);
                        spouse.Halt();
                        spouse.controller = null;
                        ModEntry.ModMonitor?.Log($"[SpouseHome] Shifted {spouse.Name} off interior furniture from {spouse.TilePoint} to {freeTile}", LogLevel.Debug);
                    }
                }
            }

            if (state.StuckRetryCount > 3)
            {
                StopNpcCleanly(spouse);
                state.CurrentWaypoint = null;
                state.CurrentNavTarget = null;
                state.RealStuckTicks = 0;
                state.StuckRetryCount = 0;
                ModEntry.ModMonitor?.Log($"[SpouseHome] {spouse.Name} declared Unreachable from={spouse.TilePoint} to={finalStandTile} after 3 retries (stuck >= 10s)", LogLevel.Debug);
                return WalkResult.Unreachable;
            }

            // Limpiar ruta para recalcular con desvío lateral
            StopNpcCleanly(spouse);
            state.CurrentWaypoint = null;
        }

        // 4. Si cambió el objetivo de navegación
        if (!state.CurrentNavTarget.HasValue)
        {
            state.CurrentNavTarget = finalStandTile;
            state.CurrentWaypoint = null;
            state.RealStuckTicks = 0;
            state.StuckRetryCount = 0;
            state.LastPixelPos = spouse.Position;
            state.LastPixelCheckTick = gameTick;
        }
        else if (state.CurrentNavTarget.Value != finalStandTile)
        {
            int manhattan = Math.Abs(state.CurrentNavTarget.Value.X - finalStandTile.X)
                          + Math.Abs(state.CurrentNavTarget.Value.Y - finalStandTile.Y);
            state.CurrentNavTarget = finalStandTile;
            state.CurrentWaypoint = null;
            if (manhattan > 3)
            {
                state.RealStuckTicks = 0;
                state.StuckRetryCount = 0;
                state.LastPixelPos = spouse.Position;
                state.LastPixelCheckTick = gameTick;
            }
        }

        // 5. Comprobar llegada al waypoint intermedio
        if (state.CurrentWaypoint.HasValue)
        {
            float distToWp = Vector2.Distance(spouse.TilePoint.ToVector2(), state.CurrentWaypoint.Value.ToVector2());
            if (distToWp <= 2.0f)
            {
                state.CurrentWaypoint = null;
                StopNpcCleanly(spouse);
            }
        }

        // 6. Determinar tramo activo
        Point currentLegTarget;
        bool isLargeExterior = loc is Farm;
        if (state.CurrentWaypoint.HasValue)
        {
            currentLegTarget = state.CurrentWaypoint.Value;
        }
        else
        {
            if (isLargeExterior && distToFinal > 14f)
            {
                var wp = CalculateIntermediateWaypoint(loc, spouse.TilePoint, finalStandTile, spouse, state.StuckRetryCount);
                if (wp.HasValue && wp.Value != spouse.TilePoint)
                {
                    state.CurrentWaypoint = wp.Value;
                    currentLegTarget = wp.Value;
                }
                else
                {
                    currentLegTarget = finalStandTile;
                }
            }
            else
            {
                currentLegTarget = finalStandTile;
            }
        }

        // 7. Iniciar o mantener PathFindController activo hacia currentLegTarget
        bool hasActivePath = spouse.controller?.pathToEndPoint != null && spouse.controller.pathToEndPoint.Count > 0;
        if (!hasActivePath)
        {
            bool started = TryStartPathTo(spouse, loc, currentLegTarget, allowWarpExit: false);
            if (!started && loc is AnimalHouse)
            {
                TryStepTowardsTarget(spouse, loc, currentLegTarget);
            }
            else if (!started && isLargeExterior)
            {
                if (!state.CurrentWaypoint.HasValue && currentLegTarget == finalStandTile)
                {
                    var wp = CalculateIntermediateWaypoint(loc, spouse.TilePoint, finalStandTile, spouse, state.StuckRetryCount);
                    if (wp.HasValue && wp.Value != spouse.TilePoint)
                    {
                        state.CurrentWaypoint = wp.Value;
                        TryStartPathTo(spouse, loc, wp.Value, allowWarpExit: false);
                    }
                }
            }
        }

        return WalkResult.Walking;
    }

    /// <summary>
    /// Transición limpia e inmediata a la vida doméstica interior (IndoorLiving).
    /// </summary>
    public static bool HasAnyFarmChores(Farm farm, SpouseHomeState state, Config.ActionsConfig? config, Farmer? owner = null)
    {
        if (farm == null) return false;

        Point baseDoor = GetOwnerHomeExteriorDoorTile(farm, owner);

        if (!Game1.IsRainingHere(farm) && state.WateredCount < 150)
        {
            Point dryCrop = FindUnwateredCropTile(farm, baseDoor, 40, null, state.SpouseName);
            if (dryCrop != Point.Zero) return true;
        }

        if (!Game1.IsRainingHere(farm) && state.PettedCount < 100)
        {
            if (farm.animals.Values.Any(a => a != null && !a.wasPet.Value && (!_claimedAnimalIds.TryGetValue(a.myID.Value, out var c) || string.Equals(c, state.SpouseName, StringComparison.OrdinalIgnoreCase))))
                return true;
        }

        if (farm.Objects.Pairs.Any(kv => ValleyBuddies.AgencyCore.FarmTaskHelper.IsAnimalProductObject(farm, kv.Value)))
            return true;

        if (farm.animals.Values.Any(a => ValleyBuddies.AgencyCore.FarmTaskHelper.IsReadyForToolHarvest(a) && (!_claimedAnimalIds.TryGetValue(a.myID.Value, out var hc) || string.Equals(hc, state.SpouseName, StringComparison.OrdinalIgnoreCase))))
            return true;

        if (state.PettedCount < 100)
        {
            if (farm.buildings.Any(b => b != null && b.indoors.Value is AnimalHouse ah && !state.VisitedAnimalHousesToday.Contains(ah.NameOrUniqueName) && (!_claimedAnimalHouses.TryGetValue(ah.NameOrUniqueName, out var hc) || string.Equals(hc, state.SpouseName, StringComparison.OrdinalIgnoreCase)) && (
                ah.Objects.Pairs.Any(kv => ValleyBuddies.AgencyCore.FarmTaskHelper.IsAnimalProductObject(ah, kv.Value))
                || ah.animals.Values.Any(a => a != null && (!a.wasPet.Value || ValleyBuddies.AgencyCore.FarmTaskHelper.IsReadyForToolHarvest(a)) && (!_claimedAnimalIds.TryGetValue(a.myID.Value, out var ac) || string.Equals(ac, state.SpouseName, StringComparison.OrdinalIgnoreCase))))))
                return true;
        }

        // Mascota (Pet) sin acariciar en la granja o cuenco de agua (PetBowl) seco
        if (farm.characters.OfType<Pet>().Any(p => p != null && !state.PettedPetNamesToday.Contains(p.Name)))
            return true;

        if (!state.FilledPetBowlToday && farm.buildings.OfType<PetBowl>().Any(b => b != null && !b.watered.Value))
            return true;

        bool isGhUnlocked = farm.greenhouseUnlocked.Value || Game1.MasterPlayer.hasOrWillReceiveMail("ccPantry");
        var gh = Game1.getLocationFromName("Greenhouse");
        if (isGhUnlocked && gh != null && !state.GreenhouseVisitedToday)
        {
            bool ghNeedsWater = FindUnwateredCropTile(gh, new Point(10, 15), 25, null, state.SpouseName) != Point.Zero;
            bool ghNeedsHarvest = (config?.EnableSpouseAutoHarvest == true) && FindHarvestableCropTile(gh, new Point(10, 15), 25, null, state.SpouseName) != Point.Zero;
            if (ghNeedsWater || ghNeedsHarvest)
                return true;
        }

        if (config?.EnableSpouseAutoHarvest == true && state.HarvestedCount < 120)
        {
            Point ripeCrop = FindHarvestableCropTile(farm, baseDoor, 40, null, state.SpouseName);
            if (ripeCrop != Point.Zero) return true;
        }

        if (state.HarvestBasket.Count > 0) return true;

        return false;
    }

    public static Point FindBestWalkableIndoorStandTile(FarmHouse house, Point anchorTile, int maxRadius = 3)
    {
        if (house == null) return anchorTile;

        bool IsValidStandTile(int x, int y)
        {
            Point entry = house.getEntryLocation();
            if (y < 4 || y >= entry.Y - 1) return false;

            if (!house.isTileOnMap(x, y) || !house.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport))
                return false;

            Rectangle tileBox = new Rectangle(x * 64 + 8, y * 64 + 8, 48, 48);

            if (house.furniture != null)
            {
                for (int i = 0; i < house.furniture.Count; i++)
                {
                    var f = house.furniture[i];
                    if (f == null || f.furniture_type.Value == Furniture.rug || f.isPassable()) continue;
                    if (f.GetBoundingBox().Intersects(tileBox)) return false;
                }

                foreach (var bed in house.furniture.OfType<BedFurniture>())
                {
                    int tilesW = Math.Max(bed.getTilesWide(), 2);
                    int tilesH = Math.Max(bed.getTilesHigh(), 3);
                    Rectangle fullBedRect = new Rectangle((int)bed.TileLocation.X * 64, (int)bed.TileLocation.Y * 64 - 32, tilesW * 64, tilesH * 64 + 32);
                    if (fullBedRect.Intersects(tileBox)) return false;
                }
            }

            Vector2 tVec = new Vector2(x, y);
            if (house.Objects != null && house.Objects.TryGetValue(tVec, out var obj) && obj != null && !obj.isPassable())
                return false;

            return true;
        }

        bool HasAtLeastTwoPassableCardinals(int x, int y)
        {
            int count = 0;
            Point[] cardinals = new Point[] { new(x, y - 1), new(x, y + 1), new(x - 1, y), new(x + 1, y) };
            foreach (var c in cardinals)
            {
                if (IsValidStandTile(c.X, c.Y))
                    count++;
            }
            return count >= 2;
        }

        if (IsValidStandTile(anchorTile.X, anchorTile.Y) && HasAtLeastTwoPassableCardinals(anchorTile.X, anchorTile.Y))
        {
            return anchorTile;
        }

        for (int r = 1; r <= maxRadius; r++)
        {
            List<Point> candidates = new();
            // Prioridad Y+ (hacia el interior, alejándose de la pared norte)
            for (int dy = 1; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) == r || Math.Abs(dy) == r)
                        candidates.Add(new Point(anchorTile.X + dx, anchorTile.Y + dy));
                }
            }
            // Laterales Y=0
            for (int dx = -r; dx <= r; dx++)
            {
                if (Math.Abs(dx) == r)
                    candidates.Add(new Point(anchorTile.X + dx, anchorTile.Y));
            }
            // Hacia arriba dy < 0
            for (int dy = -1; dy >= -r; dy--)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) == r || Math.Abs(dy) == r)
                        candidates.Add(new Point(anchorTile.X + dx, anchorTile.Y + dy));
                }
            }

            foreach (var cand in candidates)
            {
                if (IsValidStandTile(cand.X, cand.Y) && HasAtLeastTwoPassableCardinals(cand.X, cand.Y))
                {
                    return cand;
                }
            }
        }

        // Fallback en radio extendido sin exigir 2 vecinos si la habitación es muy compacta
        for (int r = 1; r <= maxRadius + 2; r++)
        {
            for (int dy = 0; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    Point cand = new Point(anchorTile.X + dx, anchorTile.Y + dy);
                    if (IsValidStandTile(cand.X, cand.Y))
                        return cand;
                }
            }
        }

        return anchorTile;
    }

    public static Point GetSpouseRoomTile(FarmHouse house, NPC spouse)
    {
        Point entry = house.getEntryLocation();

        // 1. SDV 1.6 API: GetSpouseRoomSpot()
        Point roomSpot = house.GetSpouseRoomSpot();
        if (roomSpot != Point.Zero)
        {
            Point best = FindBestWalkableIndoorStandTile(house, roomSpot, 3);
            if (best != Point.Zero && best.Y >= 4) return best;
        }

        // 2. SDV 1.6 API: GetSpouseRoomCorner()
        Point corner = house.GetSpouseRoomCorner();
        if (corner != Point.Zero)
        {
            Point candidate = new Point(corner.X + 1, Math.Max(4, corner.Y + 2));
            Point best = FindBestWalkableIndoorStandTile(house, candidate, 3);
            if (best != Point.Zero && best.Y >= 4) return best;
        }

        // 3. Fallback en esquina tranquila si no hay habitación de cónyuge (ej. Casa nivel 1)
        Point fallback = new Point(Math.Max(3, entry.X - 3), Math.Max(4, entry.Y - 4));
        return FindBestWalkableIndoorStandTile(house, fallback, 3);
    }

    public static string ResolveSpouseRoomActivityKey(NPC spouse)
    {
        return NpcPersonalityDialogueService.GetSpouseRoomActivityKey(spouse);
    }

    public static Point GetRelaxFurnitureSpot(FarmHouse house, NPC spouse)
    {
        // 1. Buscar TV
        var tv = house.furniture?.OfType<TV>().FirstOrDefault();
        if (tv != null)
        {
            Point tvFront = new Point((int)tv.TileLocation.X + 1, (int)tv.TileLocation.Y + 2);
            Point best = FindBestWalkableIndoorStandTile(house, tvFront, 3);
            if (best != Point.Zero && best.Y >= 4) return best;
        }

        // 2. Buscar chimenea
        var fireplace = house.furniture?.FirstOrDefault(f => f.furniture_type.Value == Furniture.fireplace || f.Name.Contains("Fireplace", StringComparison.OrdinalIgnoreCase));
        if (fireplace != null)
        {
            Point fpFront = new Point((int)fireplace.TileLocation.X + 1, (int)fireplace.TileLocation.Y + 2);
            Point best = FindBestWalkableIndoorStandTile(house, fpFront, 3);
            if (best != Point.Zero && best.Y >= 4) return best;
        }

        // 3. Fallback sala de estar
        Point entry = house.getEntryLocation();
        Point livingSpot = new Point(Math.Max(3, entry.X - 2), Math.Max(4, entry.Y - 3));
        return FindBestWalkableIndoorStandTile(house, livingSpot, 3);
    }

    public static Point GetGreenhouseDoor(Farm farm)
    {
        var ghBuilding = farm.buildings.FirstOrDefault(b => b != null && (b.buildingType.Value.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase) || (b.indoors.Value != null && b.indoors.Value.Name.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase))));
        if (ghBuilding != null)
        {
            return ghBuilding.getPointForHumanDoor();
        }
        if (farm.warps != null)
        {
            var warp = farm.warps.FirstOrDefault(w => w.TargetName.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase));
            if (warp != null)
            {
                return new Point(warp.X, warp.Y);
            }
        }
        return new Point(28, 14);
    }

    private static Point FindUnwateredCropTile(GameLocation farm, Point center, int radius, HashSet<Point>? excluded = null, string? spouseName = null)
    {
        if (farm?.terrainFeatures == null) return Point.Zero;

        Point best = Point.Zero;
        int bestChebyshev = int.MaxValue;
        long bestDistSq = long.MaxValue;

        foreach (var kv in farm.terrainFeatures.Pairs)
        {
            if (kv.Value is not HoeDirt dirt || dirt.crop == null) continue;
            if (dirt.state.Value != HoeDirt.dry || dirt.crop.dead.Value) continue;

            Point p = new Point((int)kv.Key.X, (int)kv.Key.Y);
            int dx = Math.Abs(p.X - center.X);
            int dy = Math.Abs(p.Y - center.Y);
            int cheb = Math.Max(dx, dy);
            if (cheb > radius) continue;
            if (excluded != null && excluded.Contains(p)) continue;
            if (farm is Farm && _claimedWaterTiles.TryGetValue(p, out var claimer) && !string.Equals(claimer, spouseName, StringComparison.OrdinalIgnoreCase)) continue;

            long distSq = (long)dx * dx + (long)dy * dy;
            if (cheb < bestChebyshev || (cheb == bestChebyshev && distSq < bestDistSq))
            {
                bestChebyshev = cheb;
                bestDistSq = distSq;
                best = p;
            }
        }
        return best;
    }

    private static List<Point> CollectBeeHousePoints(GameLocation farm)
    {
        var list = new List<Point>();
        if (farm?.objects == null) return list;
        foreach (var kv in farm.objects.Pairs)
        {
            if (kv.Value != null && (kv.Value.QualifiedItemId == "(BC)10" || kv.Value.Name.Contains("Bee House", StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new Point((int)kv.Key.X, (int)kv.Key.Y));
            }
        }
        return list;
    }

    private static Point FindHarvestableCropTile(GameLocation farm, Point center, int radius, HashSet<Point>? excluded = null, string? spouseName = null)
    {
        if (farm?.terrainFeatures == null) return Point.Zero;

        List<Point>? beeHouses = null;
        HashSet<Vector2>? giantCropTiles = null;

        Point best = Point.Zero;
        int bestChebyshev = int.MaxValue;
        long bestDistSq = long.MaxValue;

        foreach (var kv in farm.terrainFeatures.Pairs)
        {
            if (kv.Value is not HoeDirt dirt || dirt.crop == null) continue;
            if (dirt.crop.dead.Value) continue;
            if (!dirt.readyForHarvest()) continue;

            Point p = new Point((int)kv.Key.X, (int)kv.Key.Y);
            int dx = Math.Abs(p.X - center.X);
            int dy = Math.Abs(p.Y - center.Y);
            int cheb = Math.Max(dx, dy);
            if (cheb > radius) continue;
            if (excluded != null && excluded.Contains(p)) continue;
            if (farm is Farm && _claimedHarvestTiles.TryGetValue(p, out var claimer) && !string.Equals(claimer, spouseName, StringComparison.OrdinalIgnoreCase)) continue;

            // ESCUDO ANTI-COLMENAS: No cosechar flores a 5 casillas de colmenas
            bool isFlower = dirt.crop.isWildSeedCrop() ||
                            (ItemRegistry.GetData(dirt.crop.indexOfHarvest.Value)?.Category == StardewValley.Object.flowersCategory);
            if (isFlower)
            {
                beeHouses ??= CollectBeeHousePoints(farm);
                bool nearBee = false;
                for (int i = 0; i < beeHouses.Count; i++)
                {
                    if (Math.Max(Math.Abs(beeHouses[i].X - p.X), Math.Abs(beeHouses[i].Y - p.Y)) <= 5)
                    {
                        nearBee = true;
                        break;
                    }
                }
                if (nearBee) continue;
            }

            // Escudo contra cultivos gigantes
            if (giantCropTiles == null)
            {
                giantCropTiles = new HashSet<Vector2>();
                if (farm.resourceClumps != null)
                {
                    foreach (var g in farm.resourceClumps.OfType<GiantCrop>())
                        giantCropTiles.Add(g.Tile);
                }
            }
            if (giantCropTiles.Contains(kv.Key)) continue;
            if (!HasWalkableInteractionTile(farm, p, null, allowSelfOrDiagonal: true)) continue;

            long distSq = (long)dx * dx + (long)dy * dy;
            if (cheb < bestChebyshev || (cheb == bestChebyshev && distSq < bestDistSq))
            {
                bestChebyshev = cheb;
                bestDistSq = distSq;
                best = p;
            }
        }
        return best;
    }

    public static Vector2 GetSafeFarmPorchTile(Farm farm, GameLocation? house, Farmer? owner = null)
    {
        Point farmDoor = GetOwnerHomeExteriorDoorTile(farm, owner);
        Vector2[] candidates = new[]
        {
            new Vector2(farmDoor.X, farmDoor.Y + 1),
            new Vector2(farmDoor.X, farmDoor.Y + 2),
            new Vector2(farmDoor.X + 1, farmDoor.Y + 1),
            new Vector2(farmDoor.X - 1, farmDoor.Y + 1),
            new Vector2(farmDoor.X + 2, farmDoor.Y + 1),
            new Vector2(farmDoor.X - 2, farmDoor.Y + 1)
        };

        foreach (var cand in candidates)
        {
            int cx = (int)cand.X;
            int cy = (int)cand.Y;
            if (farm.isTilePassable(new xTile.Dimensions.Location(cx, cy), Game1.viewport))
            {
                if (farm.Objects != null && farm.Objects.ContainsKey(cand))
                    continue;
                return cand;
            }
        }

        return new Vector2(farmDoor.X, farmDoor.Y + 1);
    }

}
