using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Objects;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    private enum HousePoiKind { Fireplace, Bookcase, Window, Tv, Kitchen, Seat, Pet, FishTank, Decor }
    private readonly record struct HousePoi(HousePoiKind Kind, Point TargetTile, Point StandTile, string Label);

    // Registro de huéspedes que efectivamente pasan la noche: NpcName -> OwnerPlayerId
    private readonly Dictionary<string, long> _activeSleepoverGuests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _speechHistory = new();

    public IReadOnlyDictionary<string, long> GetActiveSleepoverGuests() => _activeSleepoverGuests;
    public void ClearSleepoverGuests() => _activeSleepoverGuests.Clear();

    public void WakeUpSleepoverGuests()
    {
        ClearSleepoverGuests();
        foreach (var job in GetAllActiveJobsSnapshot())
        {
            var npc = Game1.getCharacterFromName(job.NpcName);
            if (npc == null) continue;

            npc.isSleeping.Value = false;
            npc.drawOffset = Vector2.Zero;
            try { npc.Sprite?.StopAnimation(); } catch { }

            if (_companions.TryGetValue(job.NpcName, out var comp))
            {
                comp.NightState = NightLifecycleState.DaytimeActive;
                comp.EveningResolution = EveningResolutionState.Undecided;
                comp.HasEmotedAtCurrentPoi = false;
                comp.LastIndoorPoi = null;
                comp.LockedBedPixelPosition = null;
                comp.AssignedBedSpot = null;
                comp.IsRestingInPlace = false;
            }

            if (npc.currentLocation != null)
            {
                Point stand = GetAdjacentWalkableTile(npc.currentLocation, npc.TilePoint, npc.TilePoint, npc, maxRadius: 2);
                if (stand != npc.TilePoint)
                    npc.setTileLocation(new Vector2(stand.X, stand.Y));
            }

            BroadcastNightStateSync(job.NpcName, null, npc);
        }
    }

    private void SpeakNonRepeating(NPC npc, string key, object? tokens = null)
    {
        if (npc == null) return;
        try
        {
            if (tokens == null)
            {
                var owner = GetCompanionOwner(npc.Name) ?? Game1.player;
                string pName = owner?.Name ?? "Player";
                tokens = new { playerName = pName, npcName = npc.displayName, farmerName = pName, ownerName = pName };
            }
            string text = tokens != null ? I18n.Get(key, tokens) : I18n.Get(key);
            if (string.IsNullOrWhiteSpace(text)) return;
            if (_speechHistory.Contains(text)) return;
            _speechHistory.Enqueue(text);
            while (_speechHistory.Count > 5) _speechHistory.Dequeue();
            ModEntry.ConversationBridge.ShowCompanionBubble(npc, text, 3500);
        }
        catch { }
    }

    /// <summary>
    /// Detecta si el NPC se encuentra dentro de la huella rectangular completa de una cama (incluyendo almohadas y cabecera).
    /// JAMÁS exige isSleeping.Value == true para evitar falsos negativos a la luz del día.
    /// </summary>
    public static bool IsNpcInsideBedFootprint(NPC npc, GameLocation loc, out BedFurniture? occupiedBed)
    {
        occupiedBed = null;
        if (npc == null || loc?.furniture == null) return false;

        Point standing = npc.StandingPixel;
        Rectangle npcBox = npc.GetBoundingBox();
        Point centerPoint = new Point((int)npc.Position.X + 32, (int)npc.Position.Y + 32);

        foreach (var furniture in loc.furniture)
        {
            if (furniture is BedFurniture bed)
            {
                int tilesW = Math.Max(bed.getTilesWide(), 2);
                int tilesH = Math.Max(bed.getTilesHigh(), 3);
                Rectangle fullBedRect = new Rectangle(
                    (int)bed.TileLocation.X * 64,
                    (int)bed.TileLocation.Y * 64 - 32,
                    tilesW * 64,
                    tilesH * 64 + 32
                );

                if (fullBedRect.Contains(standing) ||
                    fullBedRect.Intersects(npcBox) ||
                    fullBedRect.Contains(centerPoint))
                {
                    occupiedBed = bed;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Escanea en anillo alrededor de fullBedRect buscando una casilla de suelo libre garantizada.
    /// Empieza 1 casilla debajo del pie de la cama y costados izquierdo/derecho, expandiendo hasta radio 3 tiles.
    /// </summary>
    public static Point FindGuaranteedFloorTileOutsideBed(GameLocation loc, BedFurniture bed, Point preferNearTile = default)
    {
        if (loc == null || bed == null) return preferNearTile != Point.Zero ? preferNearTile : new Point(10, 10);

        int tilesW = Math.Max(bed.getTilesWide(), 2);
        int tilesH = Math.Max(bed.getTilesHigh(), 3);
        Rectangle fullBedRect = new Rectangle(
            (int)bed.TileLocation.X * 64,
            (int)bed.TileLocation.Y * 64 - 32,
            tilesW * 64,
            tilesH * 64 + 32
        );

        int bx = (int)bed.TileLocation.X;
        int by = (int)bed.TileLocation.Y;

        List<Point> candidates = new List<Point>();

        // Escanear en anillos desde radio 1 hasta 3
        for (int r = 1; r <= 3; r++)
        {
            // Pie de cama (abajo)
            for (int dy = tilesH; dy <= tilesH + r; dy++)
            {
                for (int dx = -r; dx < tilesW + r; dx++)
                {
                    candidates.Add(new Point(bx + dx, by + dy));
                }
            }
            // Costados izquierdo y derecho
            for (int dy = 0; dy < tilesH; dy++)
            {
                for (int d = 1; d <= r; d++)
                {
                    candidates.Add(new Point(bx - d, by + dy));
                    candidates.Add(new Point(bx + tilesW - 1 + d, by + dy));
                }
            }
        }

        Point bestTile = Point.Zero;
        float bestDist = float.MaxValue;
        Point targetRef = preferNearTile != Point.Zero ? preferNearTile : new Point(bx + 1, by + tilesH + 1);

        foreach (var tile in candidates.Distinct())
        {
            // 1. Fuera de fullBedRect
            if (fullBedRect.Contains(new Point(tile.X * 64 + 32, tile.Y * 64 + 32)))
                continue;

            // 2. tile.Y >= 4
            if (tile.Y < 4)
                continue;

            // 3. Casilla transitable
            if (!IsBedPerimeterTileWalkable(loc, tile))
                continue;

            // 4. Ningún Furniture intersecta tileBox ni objeto bloqueante
            Rectangle tileBox = new Rectangle(tile.X * 64 + 8, tile.Y * 64 + 8, 48, 48);
            bool furnitureCollision = false;
            if (loc.furniture != null)
            {
                foreach (var f in loc.furniture)
                {
                    if (f == null || f is BedFurniture || f.furniture_type.Value == Furniture.rug || f.isPassable())
                        continue;
                    if (f.GetBoundingBox().Intersects(tileBox))
                    {
                        furnitureCollision = true;
                        break;
                    }
                }
            }
            if (furnitureCollision)
                continue;

            if (loc.Objects != null && loc.Objects.TryGetValue(new Vector2(tile.X, tile.Y), out var obj) && obj != null && !obj.isPassable())
                continue;

            float d = (tile.X - targetRef.X) * (tile.X - targetRef.X) + (tile.Y - targetRef.Y) * (tile.Y - targetRef.Y);
            if (d < bestDist)
            {
                bestDist = d;
                bestTile = tile;
            }
        }

        if (bestTile != Point.Zero)
            return bestTile;

        // Fallbacks seguros si la habitación está abarrotada
        if (loc is FarmHouse fh)
        {
            Point k = fh.getKitchenStandingSpot();
            if (k != Point.Zero && k.Y >= 4 && IsBedPerimeterTileWalkable(loc, k))
                return k;

            Point entry = fh.getEntryLocation();
            Point beforeEntry = new Point(entry.X, Math.Max(4, entry.Y - 2));
            if (beforeEntry.Y >= 4 && IsBedPerimeterTileWalkable(loc, beforeEntry))
                return beforeEntry;
        }

        return new Point(bx + 1, by + tilesH + 1);
    }

    /// <summary>
    /// Despierta al NPC y lo extrae físicamente fuera del rectángulo de la cama a una casilla caminable contigua,
    /// activando el BedExitNoClip temporal para permitirle caminar sobre el colchón y muebles contiguos hacia suelo libre.
    /// </summary>
    public static void WakeAndExtractNpcFromBed(NPC npc, GameLocation? loc, CompanionState? comp = null, Farmer? nearPlayer = null)
    {
        if (npc == null) return;
        if (loc == null) loc = npc.currentLocation ?? Game1.currentLocation;
        if (nearPlayer == null) nearPlayer = Game1.player;

        // 1. Activar paso libre de colisión sobre la cama
        Bridge.PathfindingSafetyPatches.EnableBedExitNoClip(npc.Name, 240);

        // 2. Despertar y limpiar flags de sueño y movimiento
        npc.isSleeping.Value = false;
        try { npc.Sprite?.StopAnimation(); } catch { }
        try { npc.Sprite?.UpdateSourceRect(); } catch { }
        npc.drawOffset = Vector2.Zero;
        npc.movementPause = 0;
        npc.Halt();
        npc.controller = null;
        npc.temporaryController = null;
        npc.addedSpeed = 0;

        if (comp == null && Instance != null)
        {
            Instance._companions.TryGetValue(npc.Name, out comp);
        }

        if (comp != null)
        {
            comp.LockedBedPixelPosition = null;
            comp.AssignedBedSpot = null;
            comp.IsPausedWhileOwnerOutsideHouse = false;
            comp.FrozenHousePosition = null;
            comp.IsSettledInsideHouseForNight = false;
            comp.SettledHouseName = null;
            comp.WokeUpByPlayerTonight = true;
            comp.NightState = comp.IsFollowing ? NightLifecycleState.DaytimeActive : NightLifecycleState.IndoorRoaming;
            comp.EveningResolution = EveningResolutionState.Undecided;
        }

        // 3. Comprobar colisión con camas o casilla no transitable
        if (loc == null) return;

        bool insideBed = IsNpcInsideBedFootprint(npc, loc, out var occupiedBed);
        Point currentTile = npc.TilePoint;
        bool isCurrentPassable = IsBedPerimeterTileWalkable(loc, currentTile, npc) && (loc is not FarmHouse || currentTile.Y >= 4);

        if (insideBed || !isCurrentPassable)
        {
            Point targetRef = nearPlayer != null ? nearPlayer.TilePoint : currentTile;
            Point bestTile = Point.Zero;

            if (occupiedBed != null)
            {
                bestTile = FindGuaranteedFloorTileOutsideBed(loc, occupiedBed, targetRef);
            }
            else
            {
                var beds = loc.furniture?.OfType<BedFurniture>().ToList() ?? new List<BedFurniture>();
                if (beds.Count > 0)
                {
                    bestTile = FindGuaranteedFloorTileOutsideBed(loc, beds[0], targetRef);
                }
            }

            if (bestTile != Point.Zero)
            {
                ModEntry.ModMonitor?.Log($"[BedExit] WakeAndExtract {npc.Name}: BedSlideOut configured, from={currentTile} to={bestTile}", LogLevel.Debug);
                Vector2 targetPixel = new Vector2(bestTile.X * 64f, bestTile.Y * 64f);

                if (comp != null)
                {
                    comp.BedSlideOutTargetTile = bestTile;
                    comp.BedSlideOutTargetPixel = targetPixel;
                    comp.BedSlideOutTicksRemaining = 75; // ~1.25s
                }
                SpouseHomeState? sState = null;
                if (Instance != null && Instance.TryGetSpouseHomeState(npc.Name, out sState))
                {
                    sState.BedSlideOutTargetTile = bestTile;
                    sState.BedSlideOutTargetPixel = targetPixel;
                    sState.BedSlideOutTicksRemaining = 75;
                }

                float distPx = Vector2.Distance(npc.Position, targetPixel);
                if (distPx > 192f)
                {
                    npc.setTilePosition(bestTile);
                    npc.Position = targetPixel;
                    npc.Halt();
                    if (comp != null) comp.BedSlideOutTargetPixel = null;
                    if (sState != null) sState.BedSlideOutTargetPixel = null;
                }
            }
        }

        // Difundir si multijugador
        if (Game1.IsMultiplayer && Instance != null)
        {
            Instance.BroadcastNightStateSync(npc.Name, null, npc);
        }
    }

    /// <summary>
    /// Desplaza físicamente al NPC fuera de la cama hacia una casilla caminable contigua (BedSlideOut).
    /// Devuelve true mientras el deslizamiento esté activo para posponer el pathfinding A* hasta que esté en suelo libre.
    /// </summary>
    public static bool TickBedSlideOut(NPC npc, CompanionState? comp = null, SpouseHomeState? sState = null)
    {
        if (npc == null) return false;
        Vector2? targetPx = comp?.BedSlideOutTargetPixel ?? sState?.BedSlideOutTargetPixel;
        if (!targetPx.HasValue) return false;

        Vector2 target = targetPx.Value;
        Vector2 diff = target - npc.Position;
        float dist = diff.Length();

        int ticksRemaining = comp?.BedSlideOutTicksRemaining ?? sState?.BedSlideOutTicksRemaining ?? 0;

        if (dist <= 6f || ticksRemaining <= 0)
        {
            npc.Position = target;
            npc.setTilePosition(new Point((int)(target.X / 64f), (int)(target.Y / 64f)));
            npc.Halt();
            npc.controller = null;
            if (comp != null)
            {
                comp.BedSlideOutTargetPixel = null;
                comp.BedSlideOutTargetTile = null;
                comp.BedSlideOutTicksRemaining = 0;
            }
            if (sState != null)
            {
                sState.BedSlideOutTargetPixel = null;
                sState.BedSlideOutTargetTile = null;
                sState.BedSlideOutTicksRemaining = 0;
            }
            Bridge.PathfindingSafetyPatches.ClearBedExitNoClip(npc.Name);
            ModEntry.ModMonitor?.Log($"[BedExit] {npc.Name} completed BedSlideOut to {npc.TilePoint}", LogLevel.Debug);
            return false;
        }

        diff.Normalize();
        float step = 2.5f;
        npc.Position += diff * step;

        if (comp != null) comp.BedSlideOutTicksRemaining = Math.Max(0, ticksRemaining - 1);
        if (sState != null) sState.BedSlideOutTicksRemaining = Math.Max(0, ticksRemaining - 1);

        npc.faceGeneralDirection(target);
        try
        {
            npc.animateInFacingDirection(Game1.currentGameTime);
        }
        catch { }

        return true;
    }

    private void StartOrTickIndoorEvening(NpcJob job, NPC npc, FarmHouse house, Farmer owner, ulong gameTick)
    {
        if (npc == null || house == null || owner == null) return;
        if (_companions.TryGetValue(npc.Name, out var checkComp) && checkComp.IsPausedWhileOwnerOutsideHouse) return;

        npc.addedSpeed = 0;

        if (!_companions.TryGetValue(npc.Name, out var comp))
        {
            comp = new CompanionState
            {
                NpcName = npc.Name,
                FarmerId = owner.UniqueMultiplayerID,
                FarmerName = owner.Name,
                IsVisitingFarm = true,
                IsFollowing = false,
                EveningResolution = EveningResolutionState.StayInHouse
            };
            _companions[npc.Name] = comp;
        }

        // Cesión automática obligatoria hacia la cama:
        // 1. Game1.timeOfDay >= 2300 (11:00 PM) o
        // 2. El dueño se acuesta temprano (owner.isInBed.Value)
        // PERO NUNCA si el jugador lo despertó explícitamente esta noche (WokeUpByPlayerTonight)
        bool bedtimeTrigger = (Game1.timeOfDay >= 2300 || owner.isInBed.Value) && !comp.WokeUpByPlayerTonight;
        if (comp.EveningResolution == EveningResolutionState.StayInHouse && bedtimeTrigger)
        {
            if (comp.NightState != NightLifecycleState.GoingToBed && comp.NightState != NightLifecycleState.SleepingInBed)
            {
                comp.IsRestingInPlace = false;
                comp.IsFollowing = false;
                comp.NightState = NightLifecycleState.GoingToBed;

                npc.controller = null;
                npc.temporaryController = null;
                npc.Halt();
                npc.doEmote(24); // Yawn

                string bedGoingKey = Game1.random.NextDouble() < 0.5 ? "night.bed.going.v1" : "night.bed.going.v2";
                SpeakNonRepeating(npc, bedGoingKey, new { npcName = npc.displayName, playerName = owner.displayName ?? owner.Name });

                bool isNotSpouse = owner.spouse == null || !owner.spouse.Equals(npc.Name, StringComparison.OrdinalIgnoreCase);
                if (isNotSpouse && (_config == null || _config.EnableSleepoverCoffeeLetter))
                {
                    _activeSleepoverGuests[npc.Name] = owner.UniqueMultiplayerID;
                    _mailService?.ScheduleSingleSleepoverLetter(npc.Name, owner.UniqueMultiplayerID, _multiplayerSync);
                }
            }
        }

        switch (comp.NightState)
        {
            case NightLifecycleState.SleepingInBed:
                TickSleepingInBed(job, npc, comp, gameTick);
                break;

            case NightLifecycleState.GoingToBed:
                TickGoingToBed(job, npc, comp, house, owner, gameTick);
                break;

            case NightLifecycleState.IndoorResting:
                TickIndoorResting(job, npc, comp, house, owner, gameTick);
                break;

            case NightLifecycleState.IndoorRoaming:
            default:
                if (comp.NightState != NightLifecycleState.IndoorRoaming && comp.NightState != NightLifecycleState.IndoorResting)
                    comp.NightState = NightLifecycleState.IndoorRoaming;
                TickIndoorRoaming(job, npc, comp, house, owner, gameTick);
                break;
        }
    }

    /// <summary>
    /// Red de seguridad llamada en DayEnding para registrar cualquier huésped que siguiera
    /// en casa cuando el día terminó antes de medianoche.
    /// </summary>
    public void CaptureLateNightGuestsBeforeDayEnd()
    {
        if (_config != null && !_config.EnableSleepoverCoffeeLetter) return;
        foreach (var job in GetAllActiveJobsSnapshot())
        {
            var npc = Game1.getCharacterFromName(job.NpcName);
            var owner = GetJobFarmer(job);
            if (npc?.currentLocation is not FarmHouse || owner == null) continue;

            bool isNotSpouse = owner.spouse == null || !owner.spouse.Equals(npc.Name, StringComparison.OrdinalIgnoreCase);
            if (isNotSpouse && !_activeSleepoverGuests.ContainsKey(npc.Name))
                _activeSleepoverGuests[npc.Name] = owner.UniqueMultiplayerID;
        }
    }

    // ─────────────────── FASE 2.1: SELECCIÓN DE CAMA Y SLOT ───────────────────

    public Point ResolveTargetBedSlot(FarmHouse house, Farmer owner, NPC npc, CompanionState comp)
    {
        if (comp.AssignedBedSpot.HasValue && comp.AssignedBedSpot.Value != Point.Zero)
            return comp.AssignedBedSpot.Value;

        var beds = house.furniture?.OfType<BedFurniture>().ToList() ?? new List<BedFurniture>();
        BedFurniture? ownerBed = house.GetPlayerBed();

        // 1. Cama doble (BedType.Double)
        var doubleBed = beds.FirstOrDefault(b => b.bedType == BedFurniture.BedType.Double) ?? (ownerBed?.bedType == BedFurniture.BedType.Double ? ownerBed : null);
        if (doubleBed != null)
        {
            Point primarySpot = doubleBed.GetBedSpot();
            Point rightSpot = new Point((int)doubleBed.TileLocation.X + 2, (int)doubleBed.TileLocation.Y + 1);
            Point leftSpot = new Point((int)doubleBed.TileLocation.X + 1, (int)doubleBed.TileLocation.Y + 1);

            Point ownerTile = owner.TilePoint;
            bool ownerOnRight = (owner.isInBed.Value && ownerTile == rightSpot) || ownerTile == rightSpot;
            bool ownerOnLeft = (owner.isInBed.Value && ownerTile == leftSpot) || ownerTile == leftSpot;

            // Verificar si otro acompañante ya reservó un lado
            bool otherOnRight = _companions.Values.Any(c => c.NpcName != npc.Name && c.AssignedBedSpot == rightSpot);
            bool otherOnLeft = _companions.Values.Any(c => c.NpcName != npc.Name && c.AssignedBedSpot == leftSpot);

            Point chosen;
            if (ownerOnRight || otherOnRight)
                chosen = leftSpot;
            else if (ownerOnLeft || otherOnLeft)
                chosen = rightSpot;
            else
                chosen = rightSpot; // Preferencia nativa lado cónyuge/acompañante

            comp.AssignedBedSpot = chosen;
            return chosen;
        }

        // 2. Camas individuales libres (BedType.Single)
        foreach (var bed in beds.Where(b => b.bedType == BedFurniture.BedType.Single))
        {
            Point spot = bed.GetBedSpot();
            bool occupiedByOwner = (owner.isInBed.Value && owner.TilePoint == spot) || owner.TilePoint == spot;
            bool occupiedByOtherComp = _companions.Values.Any(c => c.NpcName != npc.Name && c.AssignedBedSpot == spot);
            if (!occupiedByOwner && !occupiedByOtherComp)
            {
                comp.AssignedBedSpot = spot;
                return spot;
            }
        }

        // 3. Fallback en Casa Nivel 0 (cama individual única ya ocupada):
        // Asignar casilla segura adyacente para no encimar sprites
        if (ownerBed != null)
        {
            Point rawSpot = ownerBed.GetBedSpot();
            Point[] adjCandidates = new[]
            {
                new Point(rawSpot.X - 1, rawSpot.Y),
                new Point(rawSpot.X + 1, rawSpot.Y),
                new Point(rawSpot.X, rawSpot.Y + 1),
                new Point(rawSpot.X - 1, rawSpot.Y + 1)
            };

            foreach (var adj in adjCandidates)
            {
                if (IsBedPerimeterTileWalkable(house, adj, npc) && !_companions.Values.Any(c => c.NpcName != npc.Name && c.AssignedBedSpot == adj))
                {
                    comp.AssignedBedSpot = adj;
                    return adj;
                }
            }
        }

        Point fallback = ownerBed != null ? ownerBed.GetBedSpot() : house.getEntryLocation();
        comp.AssignedBedSpot = fallback;
        return fallback;
    }

    // ─────────────────── FASE 2.2: ESCANEO PERIMETRAL COMPATIBLE CON ALFOMBRAS ───────────────────

    public static bool IsBedPerimeterTileWalkable(GameLocation house, Point p, NPC? npc = null)
    {
        if (house?.map?.Layers == null || house.map.Layers.Count == 0) return false;
        var backLayer = house.map.GetLayer("Back") ?? house.map.Layers[0];
        if (p.X < 0 || p.Y < 0 || p.X >= backLayer.LayerWidth || p.Y >= backLayer.LayerHeight)
            return false;

        // Pasable en mapa base
        if (!house.isTilePassable(new xTile.Dimensions.Location(p.X, p.Y), Game1.viewport))
            return false;

        string? bPass = house.doesTileHaveProperty(p.X, p.Y, "Passable", "Buildings");
        if (bPass != null && bPass.Equals("F", StringComparison.OrdinalIgnoreCase))
            return false;

        // No coincidir con warps de salida
        if (house.warps != null)
        {
            foreach (var w in house.warps)
            {
                if (w != null && w.X == p.X && w.Y == p.Y)
                    return false;
            }
        }

        // Objetos sólidos
        Vector2 tileVec = new(p.X, p.Y);
        if (house.Objects != null && house.Objects.TryGetValue(tileVec, out var obj) && obj != null && !obj.isPassable())
            return false;

        // Muebles: muebles sólidos bloquean, pero LAS ALFOMBRAS (Rug) SON 100% PERMITIDAS
        Rectangle fullTileRect = new(p.X * 64 + 1, p.Y * 64 + 1, 62, 62);
        if (house.furniture != null)
        {
            foreach (var f in house.furniture)
            {
                if (f == null || f is BedFurniture || f.furniture_type.Value == Furniture.rug)
                    continue;
                if (!f.isPassable() && f.GetBoundingBox().Intersects(fullTileRect))
                    return false;
            }
        }

        return true;
    }

    public static List<Point> GetBedPerimeterCandidates(FarmHouse house, BedFurniture bed, NPC npc)
    {
        Rectangle bedRect = new Rectangle(
            (int)bed.TileLocation.X,
            (int)bed.TileLocation.Y,
            bed.getTilesWide(),
            bed.getTilesHigh());

        Rectangle inflated = bedRect;
        inflated.Inflate(1, 1);

        List<Point> candidates = new List<Point>();
        for (int x = inflated.Left; x < inflated.Right; x++)
        {
            for (int y = inflated.Top; y < inflated.Bottom; y++)
            {
                if (bedRect.Contains(x, y)) continue;

                Point cand = new Point(x, y);
                if (IsBedPerimeterTileWalkable(house, cand, npc))
                {
                    candidates.Add(cand);
                }
            }
        }

        // Ordenar por distancia Manhattan al NPC
        return candidates
            .OrderBy(c => Math.Abs(c.X - npc.TilePoint.X) + Math.Abs(c.Y - npc.TilePoint.Y))
            .ToList();
    }

    // ─────────────────── FASE 2.3: LLEGADA TOLERANTE Y ANTI-ATASCO DETERMINISTA ───────────────────

    private void TickGoingToBed(NpcJob job, NPC npc, CompanionState comp, FarmHouse house, Farmer owner, ulong gameTick)
    {
        npc.addedSpeed = 0;
        Point targetBedSpot = ResolveTargetBedSlot(house, owner, npc, comp);
        var beds = house.furniture?.OfType<BedFurniture>().ToList() ?? new List<BedFurniture>();
        BedFurniture? targetBed = beds.FirstOrDefault(b =>
        {
            Rectangle r = new Rectangle((int)b.TileLocation.X, (int)b.TileLocation.Y, b.getTilesWide(), b.getTilesHigh());
            return r.Contains(targetBedSpot.X, targetBedSpot.Y);
        }) ?? house.GetPlayerBed();

        List<Point> perimeter = targetBed != null
            ? GetBedPerimeterCandidates(house, targetBed, npc)
            : new List<Point>();

        if (perimeter.Count == 0)
        {
            Point fallbackAdj = GetAdjacentWalkableTile(house, targetBedSpot, npc.TilePoint, npc, maxRadius: 2);
            perimeter.Add(fallbackAdj);
        }

        int candidateIdx = job.Data.TryGetValue("bed_cand_idx", out var ci) && ci is int i ? i : 0;
        if (candidateIdx >= perimeter.Count) candidateIdx = 0;
        Point targetPerimeter = perimeter[candidateIdx];

        Rectangle bedRect = targetBed != null
            ? new Rectangle((int)targetBed.TileLocation.X, (int)targetBed.TileLocation.Y, targetBed.getTilesWide(), targetBed.getTilesHigh())
            : new Rectangle(targetBedSpot.X - 1, targetBedSpot.Y - 1, 3, 3);

        Rectangle bedRectExpanded = bedRect;
        bedRectExpanded.Inflate(2, 2);

        Vector2 targetPerimeterPixel = new Vector2(targetPerimeter.X * 64f + 32f, targetPerimeter.Y * 64f + 32f);
        float distToPerimeterPixel = Vector2.Distance(npc.Position, targetPerimeterPixel);
        float distTileToSpot = Vector2.Distance(npc.Tile, targetBedSpot.ToVector2());

        // Detección tolerante de llegada
        bool arrived = bedRectExpanded.Contains(npc.TilePoint.X, npc.TilePoint.Y) &&
                       (distToPerimeterPixel <= 48f || npc.controller == null || distTileToSpot <= 3.0f);

        if (arrived)
        {
            EnterSleepingInBed(npc, comp, targetBed, targetBedSpot);
            return;
        }

        // Anti-atasco determinista
        ulong nextStuckCheck = job.Data.TryGetValue("bed_stuck_check", out var sc) && sc is ulong u ? u : 0UL;
        if (gameTick >= nextStuckCheck)
        {
            job.Data["bed_stuck_check"] = gameTick + 90; // cada 1.5 segundos
            Vector2 lastPos = job.Data.TryGetValue("bed_last_pos", out var lp) && lp is Vector2 v ? v : npc.Position;
            job.Data["bed_last_pos"] = npc.Position;

            float moved = Vector2.Distance(npc.Position, lastPos);
            int stuckPeriods = job.Data.TryGetValue("bed_stuck_periods", out var sp) && sp is int p ? p : 0;
            int totalStuckPeriods = job.Data.TryGetValue("bed_total_stuck_periods", out var tsp) && tsp is int tp ? tp : 0;

            if (moved < 8f)
            {
                stuckPeriods++;
                totalStuckPeriods++;
                job.Data["bed_stuck_periods"] = stuckPeriods;
                job.Data["bed_total_stuck_periods"] = totalStuckPeriods;

                // Si lleva >= 6.0 segundos (4 periodos acumulados) y ya está en la misma habitación (distancia <= 7 tiles),
                // realizar transición directa y limpia al targetBedSpot
                if (totalStuckPeriods >= 4 && distTileToSpot <= 7.0f)
                {
                    EnterSleepingInBed(npc, comp, targetBed, targetBedSpot);
                    return;
                }

                // Si lleva 3.0 segundos atascado (2 periodos de 1.5s), rotar al siguiente candidato del perímetro
                if (stuckPeriods >= 2)
                {
                    candidateIdx = (candidateIdx + 1) % perimeter.Count;
                    job.Data["bed_cand_idx"] = candidateIdx;
                    targetPerimeter = perimeter[candidateIdx];
                    job.Data["bed_stuck_periods"] = 0;
                }
            }
            else
            {
                job.Data["bed_stuck_periods"] = 0;
                job.Data["bed_total_stuck_periods"] = 0;
            }
        }

        // Desplazamiento hacia el perímetro
        HumanNavigateToTarget(job, npc, house, targetPerimeter, gameTick, arrivalChebyshev: 0, maxStuckTicks: 180, tickStep: 6);
    }

    // ─────────────────── FASE 2.4: COLOCACIÓN BAJO LA SÁBANA, ANIMACIÓN DE SUEÑO Y Zzz ───────────────────

    public void EnterSleepingInBed(NPC npc, CompanionState? comp, BedFurniture? bed, Point targetBedSpot)
    {
        // 1. Detención única y definitiva
        npc.Halt();
        npc.controller = null;
        npc.temporaryController = null;
        npc.followSchedule = false;

        // 2. Alineación exacta bajo la sábana de BedFurniture en SDV 1.6
        Vector2 bedPixelPos = new Vector2(targetBedSpot.X * 64f, targetBedSpot.Y * 64f - 16f);
        if (comp != null)
        {
            comp.LockedBedPixelPosition = bedPixelPos;
            comp.AssignedBedSpot = targetBedSpot;
            comp.NightState = NightLifecycleState.SleepingInBed;
        }
        if (TryGetSpouseHomeState(npc.Name, out var sState))
        {
            sState.Phase = SpouseHomePhase.GoingToBed;
            sState.AnnouncedBedtimeToday = true;
        }

        npc.Position = bedPixelPos;
        npc.drawOffset = Vector2.Zero;
        npc.faceDirection(2);
        npc.isSleeping.Value = true;

        // 3. Animación de dormir (Vanilla + Mods, case-insensitive)
        try
        {
            var anims = Game1.content.Load<Dictionary<string, string>>("Data\\animationDescriptions");
            string targetKey = $"{npc.Name}_sleep";
            string? matchedKey = anims?.Keys.FirstOrDefault(k => k.Equals(targetKey, StringComparison.OrdinalIgnoreCase));

            if (matchedKey != null)
            {
                npc.playSleepingAnimation();
                if (npc.Sprite?.CurrentAnimation == null && anims != null)
                {
                    string rawDesc = anims[matchedKey];
                    string[] parts = rawDesc.Split('/');
                    string[] tokens = parts[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var frames = new List<FarmerSprite.AnimationFrame>();
                    foreach (var tok in tokens)
                    {
                        if (int.TryParse(tok, out int fNum))
                            frames.Add(new FarmerSprite.AnimationFrame(fNum, 100, false, false));
                    }
                    if (frames.Count > 0)
                    {
                        npc.Sprite?.setCurrentAnimation(frames);
                        if (npc.Sprite != null) npc.Sprite.loop = true;
                    }
                }
            }
            else
            {
                npc.Sprite?.StopAnimation();
                if (npc.Sprite != null) npc.Sprite.currentFrame = 0;
                npc.faceDirection(2);
            }
        }
        catch
        {
            npc.Sprite?.StopAnimation();
            if (npc.Sprite != null) npc.Sprite.currentFrame = 0;
            npc.faceDirection(2);
        }

        // 4. Emote Zzz inmediato
        npc.doEmote(24); // Character.sleepEmote

        // 5. Difundir estado a todos los clientes en multijugador
        BroadcastNightStateSync(npc.Name, comp, npc);
    }

    private void TickSleepingInBed(NpcJob job, NPC npc, CompanionState comp, ulong gameTick)
    {
        npc.addedSpeed = 0;
        if (comp.WokeUpByPlayerTonight)
        {
            var wakeOwner = GetJobFarmer(job) ?? GetCompanionOwner(npc.Name) ?? Game1.player;
            WakeAndExtractNpcFromBed(npc, npc.currentLocation ?? Game1.currentLocation, comp, wakeOwner);
            return;
        }

        // BLOQUEO TOTAL: Ningún sistema externo puede romper el descanso
        if (npc.controller != null)
        {
            npc.controller = null;
            npc.temporaryController = null;
        }

        // Re-anclaje cada 20 ticks
        if (gameTick % 20 == 0)
        {
            if (!npc.isSleeping.Value)
                npc.isSleeping.Value = true;

            if (comp.LockedBedPixelPosition.HasValue && comp.LockedBedPixelPosition.Value != Vector2.Zero &&
                Vector2.DistanceSquared(npc.Position, comp.LockedBedPixelPosition.Value) > 4f)
            {
                npc.Position = comp.LockedBedPixelPosition.Value;
            }
        }

        // Contador regresivo aleatorio para Zzz (entre 180 y 260 ticks)
        int zzzCountdown = job.Data.TryGetValue("bed_zzz_countdown", out var zc) && zc is int c ? c : Game1.random.Next(180, 261);
        zzzCountdown--;
        if (zzzCountdown <= 0)
        {
            if (!npc.IsEmoting)
                npc.doEmote(24); // Character.sleepEmote
            zzzCountdown = Game1.random.Next(180, 261);
        }
        job.Data["bed_zzz_countdown"] = zzzCountdown;
    }

    // ─────────────────── "DESCANSA AQUÍ" EN INTERIORES ───────────────────

    private void TickIndoorResting(NpcJob job, NPC npc, CompanionState comp, FarmHouse house, Farmer owner, ulong gameTick)
    {
        npc.addedSpeed = 0;
        npc.controller = null;
        npc.temporaryController = null;
        npc.Halt();

        if (Vector2.Distance(npc.Tile, owner.Tile) <= 1.5f && gameTick % 60 == 0)
        {
            npc.faceGeneralDirection(owner.Position);
        }
        else if (gameTick % 360 == 0 && Game1.random.NextDouble() < 0.25)
        {
            npc.faceDirection(Game1.random.Next(4));
        }
    }

    // ─────────────────── FASE 3: VIDA NOCTURNA ORGÁNICA EN CASA ───────────────────

    private void TickIndoorRoaming(NpcJob job, NPC npc, CompanionState comp, FarmHouse house, Farmer owner, ulong gameTick)
    {
        npc.addedSpeed = 0;
        // Guard: pausa inicial para evitar emote inmediato al entrar desde el exterior
        if (gameTick < comp.IndoorInitPauseUntilTick)
        {
            if (Vector2.Distance(npc.Tile, owner.Tile) <= 1.5f && gameTick % 60 == 0)
                npc.faceGeneralDirection(owner.Position);
            return;
        }

        ulong dwellUntil = job.Data.TryGetValue("roam_dwell_until", out var du) && du is ulong u ? u : 0UL;
        if (gameTick < dwellUntil)
        {
            if (Vector2.Distance(npc.Tile, owner.Tile) <= 1.5f && gameTick % 60 == 0)
                npc.faceGeneralDirection(owner.Position);
            return;
        }

        HousePoi? currentPoi = job.Data.TryGetValue("roam_current_poi", out var po) && po is HousePoi hp ? hp : null;
        if (!currentPoi.HasValue)
        {
            currentPoi = PickNextOrganicHousePoi(house, npc, job);
            if (!currentPoi.HasValue)
            {
                StopNpcCleanly(npc);
                npc.faceTowardFarmerForPeriod(2000, 3, false, owner);
                job.Data["roam_dwell_until"] = gameTick + 360;
                return;
            }
            job.Data["roam_current_poi"] = currentPoi.Value;
        }

        var poi = currentPoi.Value;
        var nav = HumanNavigateToTarget(job, npc, house, poi.StandTile, gameTick, arrivalChebyshev: 0, maxStuckTicks: 180, tickStep: 6);

        if (nav == HumanNavResult.Unreachable)
        {
            job.Data.Remove("roam_current_poi");
            job.Data["roam_dwell_until"] = gameTick + 90;
            return;
        }

        if (nav != HumanNavResult.Arrived)
            return;

        // Al llegar al POI
        npc.faceGeneralDirection(new Vector2(poi.TargetTile.X * 64 + 32, poi.TargetTile.Y * 64 + 32));
        // Reset emote guard cuando el NPC llega a un POI nuevo
        if (!comp.HasEmotedAtCurrentPoi || comp.LastIndoorPoi != poi.StandTile)
        {
            comp.LastIndoorPoi = poi.StandTile;
            comp.HasEmotedAtCurrentPoi = false;
        }
        PerformOrganicPoiInteraction(job, npc, house, owner, poi, gameTick, comp);
        job.Data.Remove("roam_current_poi");

        int dwellSeconds = poi.Kind == HousePoiKind.Seat ? Game1.random.Next(10, 16) : Game1.random.Next(5, 13);
        job.Data["roam_dwell_until"] = gameTick + (ulong)(dwellSeconds * 60);
    }

    private HousePoi? PickNextOrganicHousePoi(FarmHouse house, NPC npc, NpcJob job)
    {
        var pois = new List<HousePoi>();
        Point lastStand = job.Data.TryGetValue("roam_last_stand", out var ls) && ls is Point pt ? pt : new Point(-99, -99);
        bool isRaining = house.GetWeather().IsRaining || Game1.isRaining;

        if (house.furniture != null)
        {
            foreach (var f in house.furniture)
            {
                if (f == null || f is BedFurniture || f.furniture_type.Value == Furniture.rug)
                    continue;

                HousePoiKind? kind = ClassifyOrganicFurniture(f);
                if (!kind.HasValue) continue;

                Point fTile = new((int)f.TileLocation.X, (int)f.TileLocation.Y);
                Point? stand = FindFrontOrAdjacentStandTile(house, f, npc);
                if (stand.HasValue && stand.Value != lastStand && IsSafeFromExitWarps(house, stand.Value)
                    && stand.Value.Y >= 4) // v4.3.3: blindaje anti-pared norte (Y <= 3 = pared superior de FarmHouse)
                {
                    // Prioridad extra para chimeneas encendidas y ventanas con lluvia
                    if (kind == HousePoiKind.Fireplace && f.isOn.Value)
                    {
                        pois.Add(new HousePoi(kind.Value, fTile, stand.Value, f.DisplayName ?? f.Name));
                        pois.Add(new HousePoi(kind.Value, fTile, stand.Value, f.DisplayName ?? f.Name)); // Peso doble
                    }
                    else if (kind == HousePoiKind.Window && isRaining)
                    {
                        pois.Add(new HousePoi(kind.Value, fTile, stand.Value, f.DisplayName ?? f.Name));
                        pois.Add(new HousePoi(kind.Value, fTile, stand.Value, f.DisplayName ?? f.Name)); // Peso doble
                    }
                    else
                    {
                        pois.Add(new HousePoi(kind.Value, fTile, stand.Value, f.DisplayName ?? f.Name));
                    }
                }
            }
        }

        if (house.upgradeLevel >= 1)
        {
            Point kitchen = house.getKitchenStandingSpot();
            if (kitchen != Point.Zero && kitchen != lastStand && IsBedPerimeterTileWalkable(house, kitchen, npc) && IsSafeFromExitWarps(house, kitchen) && kitchen.Y >= 4)
            {
                pois.Add(new HousePoi(HousePoiKind.Kitchen, new Point(kitchen.X, kitchen.Y - 1), kitchen, "Kitchen"));
            }
        }

        foreach (var pet in house.characters.OfType<Pet>())
        {
            Point petTile = pet.TilePoint;
            Point stand = GetAdjacentWalkableTile(house, petTile, npc.TilePoint, npc, maxRadius: 2);
            if (stand != npc.TilePoint && stand != lastStand && IsBedPerimeterTileWalkable(house, stand, npc) && IsSafeFromExitWarps(house, stand) && stand.Y >= 4)
                pois.Add(new HousePoi(HousePoiKind.Pet, petTile, stand, pet.displayName));
        }

        if (pois.Count == 0) return null;

        var chosen = pois[Game1.random.Next(pois.Count)];
        job.Data["roam_last_stand"] = chosen.StandTile;
        return chosen;
    }

    private static bool IsSafeFromExitWarps(FarmHouse house, Point tile)
    {
        if (house.warps == null) return true;
        foreach (var w in house.warps)
        {
            if (Math.Abs(w.X - tile.X) <= 1 && Math.Abs(w.Y - tile.Y) <= 1)
                return false;
        }
        return true;
    }

    private Point? FindFrontOrAdjacentStandTile(GameLocation loc, Furniture f, NPC npc)
    {
        int startX = (int)f.TileLocation.X;
        int startY = (int)f.TileLocation.Y;
        int w = Math.Max(1, f.getTilesWide());
        int h = Math.Max(1, f.getTilesHigh());

        // 1. Frente (sur de la pieza)
        int southY = startY + h;
        for (int x = startX; x < startX + w; x++)
        {
            Point p = new(x, southY);
            if (IsBedPerimeterTileWalkable(loc, p, npc))
                return p;
        }

        // 2. Laterales (izquierda y derecha)
        for (int y = startY; y < startY + h; y++)
        {
            Point pLeft = new(startX - 1, y);
            if (IsBedPerimeterTileWalkable(loc, pLeft, npc))
                return pLeft;

            Point pRight = new(startX + w, y);
            if (IsBedPerimeterTileWalkable(loc, pRight, npc))
                return pRight;
        }

        // 3. Detras (norte)
        int northY = startY - 1;
        for (int x = startX; x < startX + w; x++)
        {
            Point p = new(x, northY);
            if (IsBedPerimeterTileWalkable(loc, p, npc))
                return p;
        }

        return null;
    }

    private static HousePoiKind? ClassifyOrganicFurniture(Furniture f)
    {
        if (f is TV) return HousePoiKind.Tv;
        if (f is FishTankFurniture) return HousePoiKind.FishTank;
        return f.furniture_type.Value switch
        {
            Furniture.fireplace => HousePoiKind.Fireplace,
            Furniture.bookcase => HousePoiKind.Bookcase,
            Furniture.window => HousePoiKind.Window,
            Furniture.chair or Furniture.bench or Furniture.couch or Furniture.armchair => HousePoiKind.Seat,
            Furniture.decor or Furniture.painting or Furniture.table or Furniture.longTable => HousePoiKind.Decor,
            _ => null
        };
    }

    private void PerformOrganicPoiInteraction(NpcJob job, NPC npc, FarmHouse house, Farmer owner, HousePoi poi, ulong gameTick, CompanionState? comp = null)
    {
        bool isRaining = house.GetWeather().IsRaining || Game1.isRaining;
        string ownerName = owner.displayName ?? owner.Name;

        bool canEmote = comp == null || (!comp.HasEmotedAtCurrentPoi && gameTick >= comp.NextAllowedIndoorEmoteTick);
        if (canEmote && comp != null)
        {
            comp.HasEmotedAtCurrentPoi = true;
            comp.NextAllowedIndoorEmoteTick = gameTick + 720; // ~12s cooldown
        }

        switch (poi.Kind)
        {
            case HousePoiKind.Fireplace:
                npc.faceDirection(0); // Mirar hacia la chimenea
                if (canEmote) npc.doEmote(32); // Corazón / calidez
                string fpKey = Game1.random.NextDouble() < 0.5 ? "night.roam.fireplace.v1" : "night.roam.fireplace.v2";
                SpeakNonRepeating(npc, fpKey, new { npcName = npc.displayName, playerName = ownerName });
                break;

            case HousePoiKind.Window:
                npc.faceDirection(0); // Mirar por la ventana
                if (canEmote) npc.doEmote(isRaining ? 56 : 20);
                string winKey = isRaining ? "night.roam.rain_window.v1" : "night.roam.rain_window.v2";
                SpeakNonRepeating(npc, winKey, new { npcName = npc.displayName, playerName = ownerName });
                break;

            case HousePoiKind.Bookcase:
                if (canEmote) npc.doEmote(40); // Libro / ideas
                string bookKey = Game1.random.NextDouble() < 0.5 ? "night.roam.bookshelf.v1" : "night.roam.bookshelf.v2";
                SpeakNonRepeating(npc, bookKey, new { npcName = npc.displayName, playerName = ownerName });
                break;

            case HousePoiKind.Kitchen:
                house.playSound("glug");
                if (canEmote) npc.doEmote(32);
                string kitchKey = Game1.random.NextDouble() < 0.5 ? "night.roam.kitchen.v1" : "night.roam.kitchen.v2";
                SpeakNonRepeating(npc, kitchKey, new { npcName = npc.displayName, playerName = ownerName });
                break;

            case HousePoiKind.Seat:
                // NO usar AddSittingFarmer para evitar corrupción de offsets y bloqueos en SDV 1.6
                npc.faceDirection(2); // Mirar relajado hacia la habitación
                if (canEmote) npc.doEmote(24); // Relajación
                string chillKey = Game1.random.NextDouble() < 0.5 ? "night.roam.chill.v1" : "night.roam.chill.v2";
                SpeakNonRepeating(npc, chillKey, new { npcName = npc.displayName, playerName = ownerName });
                break;

            case HousePoiKind.Pet:
                if (canEmote) npc.doEmote(20); // Corazón
                var pet = house.characters.OfType<Pet>().FirstOrDefault();
                string petCue = pet?.petType.Value switch
                {
                    "Cat" => "cat",
                    "Dog" => "dog_bark",
                    _ => "dwop"
                };
                house.playSound(petCue);
                if (pet != null) pet.doEmote(20);
                int pIdx = Game1.random.Next(1, 3);
                SpeakNonRepeating(npc, $"home.pet.affection.v{pIdx}", new { npcName = npc.displayName, playerName = ownerName, petName = pet?.displayName ?? "mascota" });
                break;

            case HousePoiKind.Tv:
                npc.faceDirection(0);
                if (canEmote) npc.doEmote(52);
                int tvIdx = Game1.random.Next(1, 3);
                SpeakNonRepeating(npc, $"home.tv.watch.v{tvIdx}", new { npcName = npc.displayName, playerName = ownerName });
                break;

            case HousePoiKind.FishTank:
                if (canEmote) npc.doEmote(32);
                SpeakNonRepeating(npc, "night.roam.decor.v1", new { npcName = npc.displayName, playerName = ownerName });
                break;

            case HousePoiKind.Decor:
            default:
                if (canEmote) npc.doEmote(20);
                string decorKey = Game1.random.NextDouble() < 0.5 ? "night.roam.decor.v1" : "night.roam.decor.v2";
                SpeakNonRepeating(npc, decorKey, new { npcName = npc.displayName, playerName = ownerName });
                break;
        }
    }
}
