using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Pathfinding;
using ValleyBuddies.Systems;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    private readonly HashSet<string> _announcedLaddersOnCurrentFloor = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _activeCaveLights = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string locName, ulong expireTick)> _activeCombatFlashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string LocName, Vector2 Tile), int> _rockDurability = new();
    private readonly Dictionary<(string LocName, Vector2 Tile), ulong> _temporaryBlockedCaveTiles = new();

    /// <summary>
    /// Checks if the location is an active underground danger floor (MineShaft 1-120, Skull Cavern 121+, VolcanoDungeon 1+).
    /// </summary>
    public static bool IsUndergroundDangerLocation(GameLocation? loc)
    {
        if (loc == null) return false;
        if (loc is MineShaft) return true;
        if (loc is VolcanoDungeon vd && vd.level.Value >= 1) return true;
        return false;
    }

    /// <summary>
    /// Checks if the location is a cave or mine entrance/lobby (Mine lobby, SkullCave lobby, VolcanoDungeon entrance).
    /// </summary>
    public static bool IsCaveOrMineEntrance(GameLocation? loc)
    {
        if (loc == null) return false;
        string name = loc.NameOrUniqueName ?? string.Empty;
        if (name.Equals("Mine", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("SkullCave", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals("VolcanoDungeon0", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Returns the entrance lobby corresponding to an underground danger floor.
    /// </summary>
    public static GameLocation? GetCaveEntranceLocation(GameLocation? dangerLoc)
    {
        if (dangerLoc is MineShaft ms)
        {
            if (ms.mineLevel >= 121)
                return Game1.getLocationFromName("SkullCave");
            return Game1.getLocationFromName("Mine");
        }
        if (dangerLoc is VolcanoDungeon)
        {
            return Game1.getLocationFromName("VolcanoDungeon0");
        }
        return Game1.getLocationFromName("Mine");
    }

    /// <summary>
    /// Finds a safe lateral entry spawn tile adjacent to the arrival point, preventing obstruction of ladders or warps.
    /// </summary>
    public static Point FindSafeCaveEntryTile(GameLocation loc, Point arrivalTile, int playerFacingDirection, NPC? npc)
    {
        Point[] lateralOffsets;
        switch (playerFacingDirection)
        {
            case 0: // Up (moving North) -> Check East, West, then South
            case 2: // Down (moving South) -> Check East, West, then North
                lateralOffsets = new[]
                {
                    new Point(1, 0), new Point(-1, 0),
                    new Point(1, 1), new Point(-1, 1),
                    new Point(1, -1), new Point(-1, -1),
                    new Point(0, -1), new Point(0, 1),
                    new Point(2, 0), new Point(-2, 0)
                };
                break;
            default: // Left / Right (moving East/West) -> Check North, South, then East/West
                lateralOffsets = new[]
                {
                    new Point(0, 1), new Point(0, -1),
                    new Point(1, 1), new Point(-1, 1),
                    new Point(1, -1), new Point(-1, -1),
                    new Point(-1, 0), new Point(1, 0),
                    new Point(0, 2), new Point(0, -2)
                };
                break;
        }

        foreach (var offset in lateralOffsets)
        {
            Point cand = new Point(arrivalTile.X + offset.X, arrivalTile.Y + offset.Y);
            if (IsValidCaveTile(loc, cand, arrivalTile, npc))
                return cand;
        }

        // Secondary fallback search radius
        for (int r = 1; r <= 3; r++)
        {
            for (int dy = -r; dy <= r; dy++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    Point cand = new Point(arrivalTile.X + dx, arrivalTile.Y + dy);
                    if (IsValidCaveTile(loc, cand, arrivalTile, npc))
                        return cand;
                }
            }
        }

        return GetAdjacentWalkableTile(loc, arrivalTile, arrivalTile, npc);
    }

    /// <summary>
    /// Finds a safe tile 1-2 tiles away from a ladder to wait beside it without blocking exit or ladder.
    /// </summary>
    public static Point FindSafeLadderWaitTile(GameLocation loc, Point ladderTile, NPC? npc)
    {
        Point[] offsets = new[]
        {
            new Point(1, 0), new Point(-1, 0),
            new Point(0, 1), new Point(0, -1),
            new Point(1, 1), new Point(-1, 1),
            new Point(1, -1), new Point(-1, -1),
            new Point(2, 0), new Point(-2, 0),
            new Point(0, 2), new Point(0, -2)
        };

        foreach (var off in offsets)
        {
            Point cand = new Point(ladderTile.X + off.X, ladderTile.Y + off.Y);
            if (IsValidCaveTile(loc, cand, ladderTile, npc))
                return cand;
        }

        return FindSafeCaveEntryTile(loc, ladderTile, 2, npc);
    }

    private static bool IsValidCaveTile(GameLocation loc, Point cand, Point arrivalTile, NPC? npc)
    {
        if (cand == arrivalTile) return false;
        if (cand.X < 1 || cand.Y < 1) return false;

        if (loc.objects.ContainsKey(new Vector2(cand.X, cand.Y))) return false;
        if (loc.isWaterTile(cand.X, cand.Y)) return false;

        if (loc is VolcanoDungeon vd)
        {
            if (!vd.isTileOnClearAndSolidGround(new Vector2(cand.X, cand.Y)))
                return false;
        }

        var bLayer = loc.map?.GetLayer("Buildings");
        if (bLayer != null && cand.X < bLayer.LayerWidth && cand.Y < bLayer.LayerHeight)
        {
            int idx = bLayer.Tiles[cand.X, cand.Y]?.TileIndex ?? -1;
            if (idx == 173 || idx == 174 || idx == 115 || idx == 116)
                return false;
        }

        if (!IsTileWalkable(loc, cand, npc))
            return false;

        return true;
    }

    /// <summary>
    /// Finds a free cardinal adjacent walkable tile for rocks, monsters or ladders.
    /// Never passes non-walkable solid tiles to PathFindController.
    /// </summary>
    public static Point FindAdjacentWalkableCaveTile(GameLocation loc, Point targetTile, Point fromTile, NPC? npc)
    {
        Point[] cardinalDirs = { new(0, 1), new(0, -1), new(1, 0), new(-1, 0) };
        Point best = Point.Zero;
        int bestDistSq = int.MaxValue;

        foreach (var dir in cardinalDirs)
        {
            Point cand = new(targetTile.X + dir.X, targetTile.Y + dir.Y);
            if (cand.X < 1 || cand.Y < 1) continue;
            if (loc.objects.ContainsKey(new Vector2(cand.X, cand.Y))) continue;
            if (loc.isWaterTile(cand.X, cand.Y)) continue;
            if (loc is VolcanoDungeon vd && !vd.isTileOnClearAndSolidGround(new Vector2(cand.X, cand.Y))) continue;

            var bLayer = loc.map?.GetLayer("Buildings");
            if (bLayer != null && cand.X < bLayer.LayerWidth && cand.Y < bLayer.LayerHeight)
            {
                int idx = bLayer.Tiles[cand.X, cand.Y]?.TileIndex ?? -1;
                if (idx == 173 || idx == 174 || idx == 115 || idx == 116) continue;
            }

            if (IsTileWalkable(loc, cand, npc, ignoreDynamicCharacters: true))
            {
                int distSq = (cand.X - fromTile.X) * (cand.X - fromTile.X) + (cand.Y - fromTile.Y) * (cand.Y - fromTile.Y);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    best = cand;
                }
            }
        }

        if (best != Point.Zero) return best;

        // Fallback radius 2 if all immediate cardinal tiles are blocked
        for (int dx = -2; dx <= 2; dx++)
        {
            for (int dy = -2; dy <= 2; dy++)
            {
                if (Math.Abs(dx) + Math.Abs(dy) != 2) continue;
                Point cand = new(targetTile.X + dx, targetTile.Y + dy);
                if (cand.X < 1 || cand.Y < 1) continue;
                if (loc.objects.ContainsKey(new Vector2(cand.X, cand.Y))) continue;
                if (loc.isWaterTile(cand.X, cand.Y)) continue;
                if (IsTileWalkable(loc, cand, npc, ignoreDynamicCharacters: true))
                {
                    int distSq = (cand.X - fromTile.X) * (cand.X - fromTile.X) + (cand.Y - fromTile.Y) * (cand.Y - fromTile.Y);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = cand;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Local stepper fallback: takes a single cardinal step towards goal when PathFindController fails on uneven terrain.
    /// </summary>
    public static bool TryStepTowardsTarget(NPC npc, GameLocation loc, Point goal)
    {
        if (npc == null || loc == null) return false;
        Point pTile = npc.TilePoint;
        Point[] dirs = { new(0, 1), new(0, -1), new(1, 0), new(-1, 0) };

        Point bestStep = Point.Zero;
        int bestDistSq = int.MaxValue;

        foreach (var dir in dirs)
        {
            Point cand = new(pTile.X + dir.X, pTile.Y + dir.Y);
            if (cand.X < 1 || cand.Y < 1) continue;
            if (loc.objects.ContainsKey(new Vector2(cand.X, cand.Y))) continue;
            if (loc.isWaterTile(cand.X, cand.Y)) continue;
            if (loc is VolcanoDungeon vd && !vd.isTileOnClearAndSolidGround(new Vector2(cand.X, cand.Y))) continue;

            if (IsTileWalkable(loc, cand, npc, ignoreDynamicCharacters: true))
            {
                int distSq = (cand.X - goal.X) * (cand.X - goal.X) + (cand.Y - goal.Y) * (cand.Y - goal.Y);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestStep = cand;
                }
            }
        }

        if (bestStep != Point.Zero)
        {
            var stepPath = new Stack<Point>();
            stepPath.Push(bestStep);
            npc.Halt();
            npc.controller = new PathFindController(stepPath, loc, npc, bestStep)
            {
                nonDestructivePathing = true
            };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Displays a localized speech bubble with absolute shielding against raw key strings leaking on screen.
    /// </summary>
    public static void ShowSafeBubble(NPC npc, string key, object? tokens = null, int duration = 3200)
    {
        if (npc == null || string.IsNullOrWhiteSpace(key)) return;
        try
        {
            string text = tokens != null ? Utilities.I18n.Get(key, tokens) : Utilities.I18n.Get(key);
            if (string.IsNullOrWhiteSpace(text) || text == key || (text.StartsWith("cave.") && text.Contains(".")))
            {
                return; // Suppress raw keys
            }
            npc.showTextAboveHead(text, duration: duration);
        }
        catch { }
    }

    /// <summary>
    /// Creates or updates a dynamic light source on dark mine floors and VolcanoDungeon.
    /// </summary>
    public void UpdateCompanionCaveLight(NPC npc, GameLocation loc)
    {
        if (npc == null || loc == null) return;
        string lightId = $"ValleyBuddies_CaveLight_{npc.Name}";

        var profile = CavePersonalityRegistry.ResolveProfile(npc);
        bool isDark = (loc is MineShaft ms && ms.isDarkArea())
                   || (loc is VolcanoDungeon vd && vd.level.Value >= 1);

        if (isDark && profile.DarkFloorLightRadius > 0f)
        {
            Vector2 lightPos = npc.Position + new Vector2(32f, 0f);
            Color lightColor = (profile.Archetype == CaveArchetype.TechnicalArcane)
                ? (npc.Name.Equals("Apples", StringComparison.OrdinalIgnoreCase) ||
                   npc.Name.Equals("Krobus", StringComparison.OrdinalIgnoreCase) ||
                   npc.Name.Equals("Wizard", StringComparison.OrdinalIgnoreCase)
                    ? new Color(170, 220, 255)
                    : new Color(255, 245, 210))
                : new Color(255, 230, 180);

            if (loc.hasLightSource(lightId))
            {
                loc.repositionLightSource(lightId, lightPos);
            }
            else
            {
                loc.sharedLights[lightId] = new LightSource(lightId, 1, lightPos, profile.DarkFloorLightRadius, lightColor);
            }
            _activeCaveLights[npc.Name] = loc.NameOrUniqueName;
        }
        else
        {
            RemoveCompanionCaveLight(npc.Name, loc);
        }
    }

    /// <summary>
    /// Safely removes an active companion cave light source.
    /// </summary>
    public void RemoveCompanionCaveLight(string npcName, GameLocation? specificLocation = null)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;
        string lightId = $"ValleyBuddies_CaveLight_{npcName}";

        try
        {
            if (specificLocation != null && specificLocation.hasLightSource(lightId))
            {
                specificLocation.removeLightSource(lightId);
            }

            if (_activeCaveLights.TryRemove(npcName, out string? locName))
            {
                if (!string.IsNullOrEmpty(locName))
                {
                    var loc = Game1.getLocationFromName(locName);
                    if (loc != null && loc.hasLightSource(lightId))
                    {
                        loc.removeLightSource(lightId);
                    }
                }
            }

            if (Game1.currentLocation != null && Game1.currentLocation.hasLightSource(lightId))
            {
                Game1.currentLocation.removeLightSource(lightId);
            }
        }
        catch { }
    }

    /// <summary>
    /// Spawns a temporary combat flash light (radius 2.4f) on the struck enemy for 2.0 seconds (120 ticks).
    /// </summary>
    private void TriggerCombatFlashLight(NPC npc, GameLocation loc, Vector2 enemyPos, ulong gameTick)
    {
        if (npc == null || loc == null) return;
        string flashId = $"ValleyBuddies_CombatFlash_{npc.Name}";
        Vector2 flashPos = enemyPos + new Vector2(32f, 0f);

        try
        {
            if (loc.hasLightSource(flashId))
            {
                loc.repositionLightSource(flashId, flashPos);
            }
            else
            {
                loc.sharedLights[flashId] = new LightSource(flashId, 1, flashPos, 2.4f, new Color(255, 240, 190));
            }
            _activeCombatFlashes[npc.Name] = (loc.NameOrUniqueName, gameTick + 120);
        }
        catch { }
    }

    private void UpdateCombatFlashLights(ulong gameTick)
    {
        if (_activeCombatFlashes.IsEmpty) return;

        foreach (var kvp in _activeCombatFlashes.ToList())
        {
            if (gameTick >= kvp.Value.expireTick)
            {
                string flashId = $"ValleyBuddies_CombatFlash_{kvp.Key}";
                try
                {
                    var loc = Game1.getLocationFromName(kvp.Value.locName);
                    if (loc != null && loc.hasLightSource(flashId))
                    {
                        loc.removeLightSource(flashId);
                    }
                    if (Game1.currentLocation != null && Game1.currentLocation.hasLightSource(flashId))
                    {
                        Game1.currentLocation.removeLightSource(flashId);
                    }
                }
                catch { }
                _activeCombatFlashes.TryRemove(kvp.Key, out _);
            }
        }
    }

    public void PurgeAllCombatFlashes()
    {
        try
        {
            foreach (var kvp in _activeCombatFlashes.ToList())
            {
                string flashId = $"ValleyBuddies_CombatFlash_{kvp.Key}";
                foreach (var l in Game1.locations)
                {
                    if (l != null && l.hasLightSource(flashId))
                        l.removeLightSource(flashId);
                }
                if (Game1.currentLocation != null && Game1.currentLocation.hasLightSource(flashId))
                    Game1.currentLocation.removeLightSource(flashId);
            }
            _activeCombatFlashes.Clear();
        }
        catch { }
    }

    /// <summary>
    /// Rigorous cleanup of all cave lights across all locations to avoid XML serialization leaks or crashes on save.
    /// </summary>
    public void RemoveAllCaveLights()
    {
        PurgeAllCombatFlashes();
        try
        {
            foreach (var kvp in _activeCaveLights)
            {
                string lightId = $"ValleyBuddies_CaveLight_{kvp.Key}";
                foreach (var l in Game1.locations)
                {
                    if (l == null) continue;
                    if (l.hasLightSource(lightId))
                        l.removeLightSource(lightId);
                }
                if (Game1.currentLocation != null && Game1.currentLocation.hasLightSource(lightId))
                    Game1.currentLocation.removeLightSource(lightId);
            }
            _activeCaveLights.Clear();
        }
        catch { }
    }

    /// <summary>
    /// Resets the current floor state when advancing to a new mine/cave level (or scoped to a single location in Co-op).
    /// </summary>
    public void ResetCaveFloorState(string? locationName = null)
    {
        if (string.IsNullOrWhiteSpace(locationName))
        {
            InvalidateWalkabilityCache();
            _announcedLaddersOnCurrentFloor.Clear();
            _rockDurability.Clear();
            _temporaryBlockedCaveTiles.Clear();
            PurgeAllCombatFlashes();
            foreach (var comp in _companions.Values)
            {
                comp.CurrentCaveRockTarget = null;
                comp.CaveWanderTarget = null;
                comp.IsFleeingBomb = false;
                comp.CaveStuckSinceTick = 0;
            }
            return;
        }

        InvalidateWalkabilityCache(locationName);

        string prefix = locationName + "|";
        _announcedLaddersOnCurrentFloor.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

        foreach (var k in _rockDurability.Keys.Where(k => string.Equals(k.LocName, locationName, StringComparison.OrdinalIgnoreCase)).ToList())
            _rockDurability.Remove(k);

        foreach (var k in _temporaryBlockedCaveTiles.Keys.Where(k => string.Equals(k.LocName, locationName, StringComparison.OrdinalIgnoreCase)).ToList())
            _temporaryBlockedCaveTiles.Remove(k);

        foreach (var comp in _companions.Values)
        {
            var cNpc = FindNpc(comp.NpcName);
            string? cLoc = cNpc?.currentLocation?.NameOrUniqueName;
            if (string.IsNullOrEmpty(cLoc) || string.Equals(cLoc, locationName, StringComparison.OrdinalIgnoreCase))
            {
                comp.CurrentCaveRockTarget = null;
                comp.CaveWanderTarget = null;
                comp.IsFleeingBomb = false;
                comp.CaveStuckSinceTick = 0;
            }
        }
    }

    /// <summary>
    /// Centralized autonomous cave loop called exclusively during subterranean floors, yielding 100% of movement control.
    /// </summary>
    public void TickCaveAutonomy(NpcJob? job, NPC npc, Farmer player, ulong gameTick)
    {
        var loc = npc.currentLocation;
        if (loc == null || player == null) return;

        // Grounded, deliberate speed in subterranean floors
        npc.speed = 2;

        // Update dynamic lantern / arcane aura
        UpdateCompanionCaveLight(npc, loc);

        // Update combat flash lights
        UpdateCombatFlashLights(gameTick);

        // Clean expired blocked tiles
        if (_temporaryBlockedCaveTiles.Count > 0 && gameTick % 60 == 0)
        {
            var expired = _temporaryBlockedCaveTiles.Where(kv => gameTick >= kv.Value).Select(kv => kv.Key).ToList();
            foreach (var k in expired) _temporaryBlockedCaveTiles.Remove(k);
        }

        var profile = CavePersonalityRegistry.ResolveProfile(npc);

        // Child safety: protected from danger floors
        if (profile.IsChildProtected)
        {
            return;
        }

        _companions.TryGetValue(npc.Name, out var compState);

        // Initialize Morale if not yet set
        int hearts = player.getFriendshipHeartLevelForNPC(npc.Name);
        if (compState != null && compState.MaxMorale <= 0)
        {
            compState.MaxMorale = profile.BaseMaxMorale + (hearts * 3);
            compState.CurrentMorale = compState.MaxMorale;
        }

        // Out-of-combat morale recovery: +2 morale per second after 10s without being hit
        if (compState != null && !compState.IsRecoveringBreath)
        {
            if (gameTick > compState.LastMoraleHitTick + 600 && gameTick % 60 == 0)
            {
                compState.CurrentMorale = Math.Min(compState.MaxMorale, compState.CurrentMorale + 2);
            }
        }

        // Catching breath state (15s duration)
        if (compState != null && compState.IsRecoveringBreath)
        {
            if (gameTick >= compState.RecoveringBreathUntilTick)
            {
                compState.IsRecoveringBreath = false;
                compState.CurrentMorale = compState.MaxMorale;
                npc.doEmote(32);
                ShowSafeBubble(npc, "cave.morale.recovered", new { playerName = player.displayName, npcName = npc.displayName }, duration: 3000);
            }
            else
            {
                // Regroup closely behind player
                if (gameTick >= compState.LastBreathEmoteTick + 180)
                {
                    compState.LastBreathEmoteTick = gameTick;
                    npc.doEmote(28); // sweating / tired
                }
                if (Vector2.Distance(npc.Tile, player.Tile) > 2.5f && job != null)
                {
                    Point safeFollow = GetAdjacentWalkableTile(loc, player.TilePoint, npc.TilePoint, npc);
                    if (safeFollow != Point.Zero)
                    {
                        MoveNpcTowards(job, npc, loc, safeFollow, gameTick);
                    }
                }
                return; // Cannot mine or attack while catching breath
            }
        }

        // 1. Bomb Evasion (every 15 ticks)
        if (gameTick % 15 == 0)
        {
            CheckNearbyBombs(job, npc, loc, player, gameTick, compState);
        }
        if (compState != null && compState.IsFleeingBomb)
        {
            return; // Busy fleeing from bomb
        }

        // 2. Monster contact check (Morale loss)
        CheckMonsterCollisionsOnNpc(npc, loc, player, gameTick, compState);
        if (compState != null && compState.IsRecoveringBreath)
        {
            return;
        }

        // 3. Ladder and Shaft Detector (every 45 ticks)
        if (gameTick % 45 == 0)
        {
            ScanForLaddersAndShafts(npc, loc, player, job);
        }

        // 4. Support Healer Actions (HP & Stamina with independent explicit cooldowns)
        if (profile.CanHealPlayerHp && compState != null)
        {
            TickSupportHealerHp(npc, loc, player, gameTick, compState, hearts);
        }
        if (profile.GivesPeriodicStaminaSupport && compState != null)
        {
            TickSupportHealerStamina(npc, loc, player, gameTick, compState);
        }

        // 5. Class-specific combat & defense (Vanguard melee, TechnicalArcane ranged, etc.)
        TickCaveCombatAndDefense(job, npc, loc, player, gameTick, profile, compState);

        // 6. Stance-Specific Movement & Autonomous Exploration (Balanced, LadderHunter, Guard, StayBack)
        TickCaveStanceActions(job, npc, loc, player, gameTick, profile, compState);

        // 7. Ambient Biome Reaction (every 60 ticks check)
        if (gameTick % 60 == 0)
        {
            CheckCaveBiomeReaction(npc, loc, player, gameTick);
        }

        // 8. Anti-stuck progress monitor every 90 ticks (1.5s)
        if (compState != null && gameTick % 90 == 0)
        {
            MonitorCaveNavigationProgress(job, npc, loc, gameTick, compState);
        }

        // Passthrough near player for smooth cave mobility
        if (Vector2.Distance(npc.Tile, player.Tile) <= 2.2f)
        {
            npc.farmerPassesThrough = true;
        }
    }

    private void MonitorCaveNavigationProgress(NpcJob? job, NPC npc, GameLocation loc, ulong gameTick, CompanionState compState)
    {
        if (compState.CurrentCaveRockTarget.HasValue || compState.CaveWanderTarget.HasValue || compState.CaveStance == CaveTacticStance.StayBack)
        {
            float moved = Vector2.Distance(npc.Position, compState.LastCaveProgressPos);
            if (moved < 10f)
            {
                if (compState.CaveStuckSinceTick == 0)
                {
                    compState.CaveStuckSinceTick = gameTick;
                }
                else if (gameTick - compState.CaveStuckSinceTick >= 150) // 2.5 seconds stuck
                {
                    // Target unreachable: blacklist for 20s and clear
                    string navLocKey = npc.currentLocation?.NameOrUniqueName ?? string.Empty;
                    if (compState.CurrentCaveRockTarget.HasValue)
                    {
                        _temporaryBlockedCaveTiles[(navLocKey, compState.CurrentCaveRockTarget.Value)] = gameTick + 1200;
                    }
                    if (compState.CaveWanderTarget.HasValue)
                    {
                        _temporaryBlockedCaveTiles[(navLocKey, new Vector2(compState.CaveWanderTarget.Value.X, compState.CaveWanderTarget.Value.Y))] = gameTick + 1200;
                    }
                    npc.controller = null;
                    npc.Halt();
                    compState.CurrentCaveRockTarget = null;
                    compState.CaveWanderTarget = null;
                    compState.CaveStuckSinceTick = 0;
                    compState.NextCaveRockSearchTick = gameTick + 30;
                }
            }
            else
            {
                compState.CaveStuckSinceTick = 0;
            }
        }
        else
        {
            compState.CaveStuckSinceTick = 0;
        }
        compState.LastCaveProgressPos = npc.Position;
    }

    private void CheckNearbyBombs(NpcJob? job, NPC npc, GameLocation loc, Farmer player, ulong gameTick, CompanionState? compState)
    {
        if (loc.temporarySprites == null || loc.temporarySprites.Count == 0)
        {
            if (compState != null) compState.IsFleeingBomb = false;
            return;
        }

        TemporaryAnimatedSprite? activeBomb = null;
        float closestDist = float.MaxValue;
        Vector2 npcTile = npc.Tile;

        foreach (var sprite in loc.temporarySprites)
        {
            if (sprite != null && sprite.bombRadius > 0)
            {
                Vector2 bombTile = sprite.position / 64f;
                float d = Vector2.Distance(npcTile, bombTile);
                if (d <= sprite.bombRadius + 1.5f && d < closestDist)
                {
                    closestDist = d;
                    activeBomb = sprite;
                }
            }
        }

        if (activeBomb == null)
        {
            if (compState != null && compState.IsFleeingBomb)
            {
                compState.IsFleeingBomb = false;
            }
            return;
        }

        if (compState != null)
        {
            compState.IsFleeingBomb = true;
            compState.CurrentCaveRockTarget = null; // Cancel current rock target
            compState.CaveWanderTarget = null;

            // Bomb warning bubble (cooldown 300 ticks / 5s)
            if (gameTick >= compState.LastBombWarningTick + 300)
            {
                compState.LastBombWarningTick = gameTick;
                npc.doEmote(16); // Exclamation
                string key = Game1.random.NextDouble() < 0.5 ? "cave.bomb.watch_out.v1" : "cave.bomb.watch_out.v2";
                ShowSafeBubble(npc, key, new { playerName = player.displayName, npcName = npc.displayName }, duration: 3200);
            }

            Vector2 bombTile = activeBomb.position / 64f;
            Vector2 awayDir = npcTile - bombTile;
            if (awayDir == Vector2.Zero) awayDir = new Vector2(1f, 0f);
            awayDir.Normalize();

            float safeDistance = activeBomb.bombRadius + 2.5f;
            Point targetPoint = new Point((int)Math.Round(bombTile.X + awayDir.X * safeDistance), (int)Math.Round(bombTile.Y + awayDir.Y * safeDistance));
            Point safeTile = GetAdjacentWalkableTile(loc, targetPoint, npc.TilePoint, npc);
            if (safeTile != Point.Zero && job != null)
            {
                MoveNpcTowards(job, npc, loc, safeTile, gameTick);
            }
        }
    }

    private void CheckMonsterCollisionsOnNpc(NPC npc, GameLocation loc, Farmer player, ulong gameTick, CompanionState? compState)
    {
        if (compState == null || compState.IsRecoveringBreath) return;
        if (gameTick < compState.LastMoraleHitTick + 75) // 1.25s invulnerability buffer
            return;

        var monsters = loc.characters?.OfType<StardewValley.Monsters.Monster>();
        if (monsters == null) return;

        Rectangle npcBox = npc.GetBoundingBox();
        foreach (var m in monsters)
        {
            if (m != null && m.Health > 0 && !m.isInvincible() && m.GetBoundingBox().Intersects(npcBox))
            {
                compState.LastMoraleHitTick = gameTick;
                int drain = Game1.random.Next(15, 23);
                compState.CurrentMorale -= drain;
                npc.jumpWithoutSound(2.5f);
                npc.doEmote(16); // Shock/surprise
                loc.playSound("clubhit");

                if (compState.CurrentMorale <= 0)
                {
                    compState.CurrentMorale = 0;
                    compState.IsRecoveringBreath = true;
                    compState.RecoveringBreathUntilTick = gameTick + 900; // 15 seconds
                    compState.CurrentCaveRockTarget = null;
                    compState.CaveWanderTarget = null;
                    compState.LastBreathEmoteTick = gameTick;
                    npc.doEmote(28); // Sweat/tired
                    ShowSafeBubble(npc, "cave.morale.tired", new { playerName = player.displayName, npcName = npc.displayName }, duration: 4000);
                }
                break;
            }
        }
    }

    private void TickSupportHealerHp(NPC npc, GameLocation loc, Farmer player, ulong gameTick, CompanionState compState, int hearts)
    {
        if (player.health >= player.maxHealth * 0.80f) return;
        if (gameTick < compState.NextCaveHpHealTick) return;
        if (Vector2.Distance(npc.Tile, player.Tile) > 8.0f) return;
        if (compState.IsFleeingBomb || compState.IsRecoveringBreath) return;

        compState.NextCaveHpHealTick = gameTick + 600; // 10.0s cooldown
        float pct = hearts >= 8 ? 0.40f : (hearts >= 5 ? 0.36f : 0.30f);
        int healAmount = Math.Max(30, (int)Math.Round(player.maxHealth * pct));
        int actualHealed = Math.Min(healAmount, player.maxHealth - player.health);
        if (actualHealed <= 0) return;

        if (Context.IsMultiplayer && Context.IsMainPlayer && player.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID)
        {
            _multiplayerSync?.SendSupportHeal(player.UniqueMultiplayerID, actualHealed, 0f, npc.Name);
        }
        else
        {
            player.health += actualHealed;
            loc.debris.Add(new Debris(actualHealed, new Vector2(player.getStandingPosition().X + 8, player.getStandingPosition().Y), Color.LimeGreen, 1f, player));
        }
        loc.playSound("healSound");
        if (!npc.IsEmoting) npc.doEmote(20); // Heart

        if (gameTick >= compState.LastCaveHpBubbleTick + 1800)
        {
            compState.LastCaveHpBubbleTick = gameTick;
            string key = Game1.random.NextDouble() < 0.5 ? "cave.healer.hp_boost.v1" : "cave.healer.hp_boost.v2";
            ShowSafeBubble(npc, key, new { playerName = player.displayName, npcName = npc.displayName }, duration: 3200);
        }
    }

    private void TickSupportHealerStamina(NPC npc, GameLocation loc, Farmer player, ulong gameTick, CompanionState compState)
    {
        if (player.Stamina >= player.MaxStamina * 0.75f) return;
        if (gameTick < compState.NextCaveStaminaHealTick) return;
        if (Vector2.Distance(npc.Tile, player.Tile) > 8.0f) return;
        if (compState.IsFleeingBomb || compState.IsRecoveringBreath) return;

        compState.NextCaveStaminaHealTick = gameTick + 3600; // 60s cooldown
        float staminaGain = Math.Max(25f, (float)Math.Round(player.MaxStamina * 0.18f));
        if (Context.IsMultiplayer && Context.IsMainPlayer && player.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID)
        {
            _multiplayerSync?.SendSupportHeal(player.UniqueMultiplayerID, 0, staminaGain, npc.Name);
        }
        else
        {
            player.Stamina = Math.Min(player.MaxStamina, player.Stamina + staminaGain);
        }
        loc.playSound("healSound");
        if (!npc.IsEmoting) npc.doEmote(20); // Heart
        string key = Game1.random.NextDouble() < 0.5 ? "cave.support.stamina_boost.v1" : "cave.support.stamina_boost.v2";
        ShowSafeBubble(npc, key, new { playerName = player.displayName, npcName = npc.displayName }, duration: 3200);
    }

    private void TickCaveCombatAndDefense(NpcJob? job, NPC npc, GameLocation loc, Farmer player, ulong gameTick, CavePersonalityProfile profile, CompanionState? compState)
    {
        if (_config?.EnableCompanionCombat == false)
            return;
        if (compState == null || compState.IsRecoveringBreath || compState.IsFleeingBomb || profile.CombatStyle == CaveCombatStyle.None)
            return;

        if (compState.CaveStance == CaveTacticStance.StayBack)
        {
            // Emergency shove only if a monster bumps right into the waiting NPC (<= 1.5 tiles)
            var closeMonster = loc.characters?.OfType<StardewValley.Monsters.Monster>()
                .FirstOrDefault(m => m != null && m.Health > 0 && !m.isInvincible() && Vector2.Distance(m.Tile, npc.Tile) <= 1.5f);
            if (closeMonster != null && gameTick >= compState.LastCaveAttackTick + (ulong)profile.AttackCooldownTicks)
            {
                compState.LastCaveAttackTick = gameTick;
                npc.faceGeneralDirection(closeMonster.Position);
                ToolAnimationService.PlayWeaponSwing(loc, npc, closeMonster.Position);
                loc.playSound("clubhit");
                loc.damageMonster(closeMonster.GetBoundingBox(), 2, 4, false, profile.KnockbackPower, 0, 0f, 1f, true, player);
                TriggerCombatFlashLight(npc, loc, closeMonster.Position, gameTick);
            }
            return;
        }

        var monsters = loc.characters?.OfType<StardewValley.Monsters.Monster>()
            .Where(m => m != null && m.Health > 0 && !m.isInvincible())
            .ToList();
        if (monsters == null || monsters.Count == 0) return;

        int hearts = player.getFriendshipHeartLevelForNPC(npc.Name);
        float heartMult = hearts >= 8 ? 1.20f : (hearts >= 5 ? 1.00f : 0.85f);

        switch (profile.CombatStyle)
        {
            case CaveCombatStyle.ProactiveMelee:
            {
                float searchRange = compState.CaveStance == CaveTacticStance.GuardCombat ? 8.0f : 6.0f;
                var target = monsters
                    .Where(m => Vector2.Distance(m.Tile, npc.Tile) <= searchRange)
                    .OrderBy(m => Vector2.Distance(m.Tile, npc.Tile))
                    .FirstOrDefault();

                if (target == null) return;
                float dist = Vector2.Distance(target.Tile, npc.Tile);

                if (dist > 1.8f)
                {
                    // Vanguard physically moves to melee range
                    if (compState.CaveStance == CaveTacticStance.GuardCombat || compState.CurrentCaveRockTarget == null)
                    {
                        Point monsterTile = target.TilePoint;
                        Point walkDest = FindAdjacentWalkableCaveTile(loc, monsterTile, npc.TilePoint, npc);
                        if (walkDest != Point.Zero && job != null)
                        {
                            npc.addedSpeed = 1;
                            MoveNpcTowards(job, npc, loc, walkDest, gameTick);
                            if ((npc.controller == null || npc.controller.pathToEndPoint == null || npc.controller.pathToEndPoint.Count == 0) && dist <= 6.0f)
                            {
                                TryStepTowardsTarget(npc, loc, walkDest);
                            }
                        }
                    }
                }
                else
                {
                    npc.addedSpeed = 0;
                    // In melee range (<= 1.8 tiles): apply melee strike
                    int attackCooldown = profile.AttackCooldownTicks;
                    if (gameTick >= compState.LastCaveAttackTick + (ulong)attackCooldown)
                    {
                        compState.LastCaveAttackTick = gameTick;
                        npc.faceGeneralDirection(target.Position);
                        npc.shake(140);
                        ToolAnimationService.PlayWeaponSwing(loc, npc, target.Position);

                        // Sound with rate limiter
                        if (gameTick >= compState.LastCaveAttackSoundTick + 90)
                        {
                            compState.LastCaveAttackSoundTick = gameTick;
                            loc.playSound("daggerswipe");
                        }

                        int depthBonus = (loc is MineShaft ms && ms.mineLevel > 80) ? 3 : 0;
                        int minDmg = (int)Math.Round(profile.BaseMinDamage * heartMult) + depthBonus;
                        int maxDmg = (int)Math.Round(profile.BaseMaxDamage * heartMult) + depthBonus;

                        loc.damageMonster(target.GetBoundingBox(), minDmg, maxDmg, false, profile.KnockbackPower, 0, 0.08f, 2f, true, player);
                        TriggerCombatFlashLight(npc, loc, target.Position, gameTick);
                        try { TryAwardDailyActivityFriendship(player, npc, "cave_coop", 1, 25); } catch { }
                    }
                }
                break;
            }

            case CaveCombatStyle.RangedArcane:
            {
                var target = monsters
                    .Where(m => Vector2.Distance(m.Tile, npc.Tile) <= 6.5f)
                    .OrderBy(m => Vector2.Distance(m.Tile, npc.Tile))
                    .FirstOrDefault();

                if (target == null) return;
                float dist = Vector2.Distance(target.Tile, npc.Tile);

                if (dist < 2.0f)
                {
                    // Step back to maintain comfortable distance
                    Vector2 retreatDir = npc.Tile - target.Tile;
                    if (retreatDir == Vector2.Zero) retreatDir = new Vector2(1f, 0f);
                    retreatDir.Normalize();
                    Point retreatTile = new Point((int)Math.Round(npc.Tile.X + retreatDir.X * 2f), (int)Math.Round(npc.Tile.Y + retreatDir.Y * 2f));
                    Point safeWalk = GetAdjacentWalkableTile(loc, retreatTile, npc.TilePoint, npc);
                    if (safeWalk != Point.Zero && job != null)
                    {
                        MoveNpcTowards(job, npc, loc, safeWalk, gameTick);
                    }
                }
                else if (dist >= 2.0f && dist <= 6.5f)
                {
                    int attackCooldown = profile.AttackCooldownTicks;
                    if (gameTick >= compState.LastCaveAttackTick + (ulong)attackCooldown)
                    {
                        compState.LastCaveAttackTick = gameTick;
                        npc.faceGeneralDirection(target.Position);
                        npc.shake(100);

                        if (gameTick >= compState.LastCaveAttackSoundTick + 90)
                        {
                            compState.LastCaveAttackSoundTick = gameTick;
                            loc.playSound("throw");
                        }

                        // Arcane cyan sparkle sprite
                        BroadcastTemporarySprite(loc, new TemporaryAnimatedSprite(362, target.Position, Color.Cyan, 6, false, 70f));

                        int depthBonus = (loc is MineShaft ms && ms.mineLevel > 80) ? 2 : 0;
                        int minDmg = (int)Math.Round(profile.BaseMinDamage * heartMult) + depthBonus;
                        int maxDmg = (int)Math.Round(profile.BaseMaxDamage * heartMult) + depthBonus;

                        loc.damageMonster(target.GetBoundingBox(), minDmg, maxDmg, false, profile.KnockbackPower, 0, 0.10f, 2f, true, player);
                        TriggerCombatFlashLight(npc, loc, target.Position, gameTick);
                        try { TryAwardDailyActivityFriendship(player, npc, "cave_coop", 1, 25); } catch { }
                    }
                }
                break;
            }

            case CaveCombatStyle.ShortSelfDefense:
            {
                float defenseRadius = compState.CaveStance == CaveTacticStance.GuardCombat ? 4.5f : 2.5f;
                var target = monsters
                    .Where(m => Vector2.Distance(m.Tile, npc.Tile) <= defenseRadius || Vector2.Distance(m.Tile, player.Tile) <= defenseRadius)
                    .OrderBy(m => Vector2.Distance(m.Tile, npc.Tile))
                    .FirstOrDefault();

                if (target == null) return;
                float dist = Vector2.Distance(target.Tile, npc.Tile);

                if (dist <= 2.2f)
                {
                    int attackCooldown = profile.AttackCooldownTicks;
                    if (gameTick >= compState.LastCaveAttackTick + (ulong)attackCooldown)
                    {
                        compState.LastCaveAttackTick = gameTick;
                        npc.faceGeneralDirection(target.Position);
                        npc.shake(120);

                        if (gameTick >= compState.LastCaveAttackSoundTick + 90)
                        {
                            compState.LastCaveAttackSoundTick = gameTick;
                            loc.playSound("daggerswipe");
                        }

                        int depthBonus = (loc is MineShaft ms && ms.mineLevel > 80) ? 2 : 0;
                        int minDmg = (int)Math.Round(profile.BaseMinDamage * heartMult) + depthBonus;
                        int maxDmg = (int)Math.Round(profile.BaseMaxDamage * heartMult) + depthBonus;

                        loc.damageMonster(target.GetBoundingBox(), minDmg, maxDmg, false, profile.KnockbackPower, 0, 0.04f, 2f, true, player);
                        TriggerCombatFlashLight(npc, loc, target.Position, gameTick);
                        try { TryAwardDailyActivityFriendship(player, npc, "cave_coop", 1, 25); } catch { }
                    }
                }
                else if (compState.CaveStance == CaveTacticStance.GuardCombat && dist <= defenseRadius)
                {
                    Point monsterTile = target.TilePoint;
                    Point walkDest = GetAdjacentWalkableTile(loc, monsterTile, npc.TilePoint, npc);
                    if (walkDest != Point.Zero && job != null)
                    {
                        MoveNpcTowards(job, npc, loc, walkDest, gameTick);
                    }
                }
                break;
            }

            case CaveCombatStyle.PassiveHealOnly:
            {
                var target = monsters
                    .Where(m => Vector2.Distance(m.Tile, npc.Tile) <= 3.0f)
                    .OrderBy(m => Vector2.Distance(m.Tile, npc.Tile))
                    .FirstOrDefault();

                if (target == null) return;
                float dist = Vector2.Distance(target.Tile, npc.Tile);

                if (dist <= 1.5f)
                {
                    // Cornered: nervous shove strictly 2-4 damage
                    int attackCooldown = profile.AttackCooldownTicks;
                    if (gameTick >= compState.LastCaveAttackTick + (ulong)attackCooldown)
                    {
                        compState.LastCaveAttackTick = gameTick;
                        npc.faceGeneralDirection(target.Position);
                        loc.playSound("clubhit");
                        loc.damageMonster(target.GetBoundingBox(), 2, 4, false, profile.KnockbackPower, 0, 0f, 1f, true, player);
                        TriggerCombatFlashLight(npc, loc, target.Position, gameTick);
                    }
                }
                else
                {
                    // Flee from monster towards player or open tile
                    Point safeDest = GetAdjacentWalkableTile(loc, player.TilePoint, npc.TilePoint, npc);
                    if (safeDest != Point.Zero && job != null)
                    {
                        MoveNpcTowards(job, npc, loc, safeDest, gameTick);
                    }
                }
                break;
            }

            case CaveCombatStyle.AvoidanceOnly:
            {
                var target = monsters
                    .Where(m => Vector2.Distance(m.Tile, npc.Tile) <= 3.5f)
                    .OrderBy(m => Vector2.Distance(m.Tile, npc.Tile))
                    .FirstOrDefault();

                if (target == null) return;
                float dist = Vector2.Distance(target.Tile, npc.Tile);

                if (dist <= 1.5f)
                {
                    // Cornered: 2-4 damage push
                    int attackCooldown = profile.AttackCooldownTicks;
                    if (gameTick >= compState.LastCaveAttackTick + (ulong)attackCooldown)
                    {
                        compState.LastCaveAttackTick = gameTick;
                        npc.faceGeneralDirection(target.Position);
                        loc.playSound("clubhit");
                        loc.damageMonster(target.GetBoundingBox(), 2, 4, false, profile.KnockbackPower, 0, 0f, 1f, true, player);
                        TriggerCombatFlashLight(npc, loc, target.Position, gameTick);
                    }
                }
                else
                {
                    // Flee from monster
                    if (gameTick >= compState.LastAvoidanceEmoteTick + 300)
                    {
                        compState.LastAvoidanceEmoteTick = gameTick;
                        npc.doEmote(28); // nervous / sweat
                    }
                    Point safeDest = GetAdjacentWalkableTile(loc, player.TilePoint, npc.TilePoint, npc);
                    if (safeDest != Point.Zero && job != null)
                    {
                        MoveNpcTowards(job, npc, loc, safeDest, gameTick);
                    }
                }
                break;
            }
        }
    }

    private void TickCaveStanceActions(NpcJob? job, NPC npc, GameLocation loc, Farmer player, ulong gameTick, CavePersonalityProfile profile, CompanionState? compState)
    {
        if (compState == null || job == null) return;
        float distToOwner = Vector2.Distance(npc.Tile, player.Tile);

        switch (compState.CaveStance)
        {
            case CaveTacticStance.StayBack:
            {
                // Wait safely by the entry ladder of this floor
                if (compState.FloorSafeWaitTile == Point.Zero)
                {
                    compState.FloorSafeWaitTile = FindSafeLadderWaitTile(loc, compState.FloorEntryLadderTile != Point.Zero ? compState.FloorEntryLadderTile : player.TilePoint, npc);
                }

                Point waitTile = compState.FloorSafeWaitTile;
                float distToWait = Vector2.Distance(npc.Tile, new Vector2(waitTile.X, waitTile.Y));

                if (distToWait > 1.5f)
                {
                    npc.addedSpeed = 1;
                    MoveNpcTowards(job, npc, loc, waitTile, gameTick);
                    if ((npc.controller == null || npc.controller.pathToEndPoint == null || npc.controller.pathToEndPoint.Count == 0) && distToWait <= 6.0f)
                    {
                        TryStepTowardsTarget(npc, loc, waitTile);
                    }
                }
                else
                {
                    npc.addedSpeed = 0;
                    npc.controller = null;
                    npc.Halt();
                    npc.faceGeneralDirection(player.Position);
                }
                break;
            }

            case CaveTacticStance.GuardCombat:
            {
                // Patrols around owner (2.0 to 4.5 tiles).
                if (distToOwner > 4.5f)
                {
                    Point nearOwner = FindAdjacentWalkableCaveTile(loc, player.TilePoint, npc.TilePoint, npc);
                    if (nearOwner != Point.Zero)
                    {
                        npc.addedSpeed = 1;
                        MoveNpcTowards(job, npc, loc, nearOwner, gameTick);
                        if ((npc.controller == null || npc.controller.pathToEndPoint == null || npc.controller.pathToEndPoint.Count == 0) && distToOwner <= 6.0f)
                        {
                            TryStepTowardsTarget(npc, loc, nearOwner);
                        }
                    }
                }
                else if (distToOwner < 2.0f)
                {
                    npc.addedSpeed = 0;
                    npc.controller = null;
                    npc.Halt();
                }
                else
                {
                    npc.addedSpeed = 0;
                    // Mine nearby stones in path
                    TickRealisticCaveMining(job, npc, loc, player, gameTick, profile, compState, maxRockDistFromNpc: 3.5f, maxRockDistFromPlayer: 5.0f);
                }
                break;
            }

            case CaveTacticStance.LadderHunter:
            {
                // If a ladder / shaft was announced on this floor, companion walks towards safe tile 2-3 tiles beside it
                string locKey = loc.NameOrUniqueName ?? string.Empty;
                string ladderPrefix = locKey + "|";
                string? firstLadder = _announcedLaddersOnCurrentFloor.FirstOrDefault(k => k.StartsWith(ladderPrefix, StringComparison.OrdinalIgnoreCase));
                if (firstLadder != null)
                {
                    string[] parts = firstLadder.Substring(ladderPrefix.Length).Split('_');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int lx) && int.TryParse(parts[1], out int ly))
                    {
                        Point lPt = new Point(lx, ly);
                        Point waitNearLadder = FindAdjacentWalkableCaveTile(loc, lPt, npc.TilePoint, npc);
                        if (waitNearLadder == Point.Zero)
                            waitNearLadder = FindSafeLadderWaitTile(loc, lPt, npc);

                        float distToLadderArea = Vector2.Distance(npc.Tile, new Vector2(waitNearLadder.X, waitNearLadder.Y));

                        if (distToLadderArea > 1.8f)
                        {
                            npc.addedSpeed = 1;
                            MoveNpcTowards(job, npc, loc, waitNearLadder, gameTick);
                            if ((npc.controller == null || npc.controller.pathToEndPoint == null || npc.controller.pathToEndPoint.Count == 0) && distToLadderArea <= 6.0f)
                            {
                                TryStepTowardsTarget(npc, loc, waitNearLadder);
                            }
                            return;
                        }
                        else
                        {
                            npc.addedSpeed = 0;
                            npc.controller = null;
                            npc.Halt();
                            npc.faceGeneralDirection(new Vector2(lx * 64f + 32f, ly * 64f + 32f));
                            // Also clear any rock right beside the ladder
                            TickRealisticCaveMining(job, npc, loc, player, gameTick, profile, compState, maxRockDistFromNpc: 2.5f, maxRockDistFromPlayer: 12f);
                            return;
                        }
                    }
                }

                // If player is too far (> 14 tiles), regroup
                if (distToOwner > 14.0f)
                {
                    compState.CurrentCaveRockTarget = null;
                    Point regroupTile = FindAdjacentWalkableCaveTile(loc, player.TilePoint, npc.TilePoint, npc);
                    if (regroupTile != Point.Zero)
                    {
                        npc.addedSpeed = 1;
                        MoveNpcTowards(job, npc, loc, regroupTile, gameTick);
                        if (npc.controller == null || npc.controller.pathToEndPoint == null || npc.controller.pathToEndPoint.Count == 0)
                        {
                            TryStepTowardsTarget(npc, loc, regroupTile);
                        }
                    }
                    return;
                }

                // Break stones in 10-tile radius
                bool mined = TickRealisticCaveMining(job, npc, loc, player, gameTick, profile, compState, maxRockDistFromNpc: 10.0f, maxRockDistFromPlayer: 14.0f);
                if (!mined && compState.CurrentCaveRockTarget == null && gameTick >= compState.NextCaveRockSearchTick)
                {
                    TickCaveWanderExploration(job, npc, loc, player, gameTick, compState);
                }
                break;
            }

            case CaveTacticStance.Balanced:
            default:
            {
                // Autonomous exploration around the floor!
                if (distToOwner > 14.0f)
                {
                    compState.CurrentCaveRockTarget = null;
                    compState.CaveWanderTarget = null;
                    Point regroupTile = FindAdjacentWalkableCaveTile(loc, player.TilePoint, npc.TilePoint, npc);
                    if (regroupTile != Point.Zero)
                    {
                        npc.addedSpeed = 1;
                        MoveNpcTowards(job, npc, loc, regroupTile, gameTick);
                        if (npc.controller == null || npc.controller.pathToEndPoint == null || npc.controller.pathToEndPoint.Count == 0)
                        {
                            TryStepTowardsTarget(npc, loc, regroupTile);
                        }
                    }
                    return;
                }

                // Mine rocks within 10 tiles of NPC (prioritizing 14 tiles of player)
                bool minedOrMovingToRock = TickRealisticCaveMining(job, npc, loc, player, gameTick, profile, compState, maxRockDistFromNpc: 10.0f, maxRockDistFromPlayer: 14.0f);

                // If not mining and no rock target, explore / wander the floor naturally
                if (!minedOrMovingToRock && compState.CurrentCaveRockTarget == null && gameTick >= compState.NextCaveRockSearchTick)
                {
                    TickCaveWanderExploration(job, npc, loc, player, gameTick, compState);
                }
                break;
            }
        }
    }

    private void TickCaveWanderExploration(NpcJob job, NPC npc, GameLocation loc, Farmer player, ulong gameTick, CompanionState compState)
    {
        if (compState.CaveWanderTarget.HasValue)
        {
            Point target = compState.CaveWanderTarget.Value;
            float d = Vector2.Distance(npc.Tile, new Vector2(target.X, target.Y));
            if (d <= 1.5f || Vector2.Distance(npc.Tile, player.Tile) > 13f)
            {
                compState.CaveWanderTarget = null;
                npc.addedSpeed = 0;
                npc.controller = null;
                npc.Halt();
                compState.NextCaveRockSearchTick = gameTick + (ulong)Game1.random.Next(25, 51);
                return;
            }

            npc.addedSpeed = 1;
            MoveNpcTowards(job, npc, loc, target, gameTick);
            if ((npc.controller == null || npc.controller.pathToEndPoint == null || npc.controller.pathToEndPoint.Count == 0) && d <= 6.0f)
            {
                TryStepTowardsTarget(npc, loc, target);
            }
            return;
        }

        Point pTile = npc.TilePoint;
        List<Point> candidates = new List<Point>();
        for (int i = 0; i < 12; i++)
        {
            int dx = Game1.random.Next(-8, 9);
            int dy = Game1.random.Next(-8, 9);
            int distSq = dx * dx + dy * dy;
            if (distSq < 16 || distSq > 64) continue; // between 4 and 8 tiles

            Point cand = new Point(pTile.X + dx, pTile.Y + dy);
            Vector2 candV = new Vector2(cand.X, cand.Y);
            string wanderLocKey = loc.NameOrUniqueName ?? string.Empty;
            if (_temporaryBlockedCaveTiles.ContainsKey((wanderLocKey, candV))) continue;

            if (IsValidCaveTile(loc, cand, pTile, npc) && Vector2.Distance(candV, player.Tile) <= 12f)
            {
                candidates.Add(cand);
            }
        }

        if (candidates.Count > 0)
        {
            compState.CaveWanderTarget = candidates[Game1.random.Next(candidates.Count)];
        }
    }

    private bool TickRealisticCaveMining(NpcJob? job, NPC npc, GameLocation loc, Farmer player, ulong gameTick, CavePersonalityProfile profile, CompanionState? compState, float maxRockDistFromNpc = 8.0f, float maxRockDistFromPlayer = 12.0f)
    {
        if (profile.IsChildProtected || profile.MiningSpeedMultiplier <= 0f) return false;
        if (compState == null || compState.IsRecoveringBreath || compState.IsFleeingBomb) return false;
        if (compState.CaveStance == CaveTacticStance.StayBack) return false;

        string mineLocKey = loc.NameOrUniqueName ?? string.Empty;

        float distToOwner = Vector2.Distance(npc.Tile, player.Tile);
        if (distToOwner > 14.0f)
        {
            compState.CurrentCaveRockTarget = null;
            return false;
        }

        // Validate existing target
        if (compState.CurrentCaveRockTarget.HasValue)
        {
            Vector2 rockPos = compState.CurrentCaveRockTarget.Value;
            if (!loc.objects.TryGetValue(rockPos, out var stoneObj) || stoneObj == null || !IsBreakableCaveStone(stoneObj))
            {
                compState.CurrentCaveRockTarget = null;
                _rockDurability.Remove((mineLocKey, rockPos));
                compState.NextCaveRockSearchTick = gameTick + (ulong)Game1.random.Next(25, 51);
                return false;
            }

            float distToRock = Vector2.Distance(npc.Tile, rockPos);
            if (distToRock > 1.6f)
            {
                if (job != null)
                {
                    Point walkDest = FindAdjacentWalkableCaveTile(loc, new Point((int)rockPos.X, (int)rockPos.Y), npc.TilePoint, npc);
                    if (walkDest != Point.Zero)
                    {
                        npc.addedSpeed = 1;
                        MoveNpcTowards(job, npc, loc, walkDest, gameTick);
                        if ((npc.controller == null || npc.controller.pathToEndPoint == null || npc.controller.pathToEndPoint.Count == 0) && distToRock <= 6.0f)
                        {
                            TryStepTowardsTarget(npc, loc, walkDest);
                        }
                        return true;
                    }
                    else
                    {
                        _temporaryBlockedCaveTiles[(mineLocKey, rockPos)] = gameTick + 900;
                        compState.CurrentCaveRockTarget = null;
                        compState.NextCaveRockSearchTick = gameTick + 25;
                        return false;
                    }
                }
                return false;
            }

            // NPC is adjacent to the rock!
            npc.addedSpeed = 0;
            npc.controller = null;
            npc.Halt();
            npc.faceGeneralDirection(rockPos * 64f + new Vector2(32f, 32f));

            int hearts = player.getFriendshipHeartLevelForNPC(npc.Name);
            float heartMult = hearts >= 8 ? 0.88f : (hearts >= 5 ? 1.0f : 1.15f);
            // v4.3.3: +18 ticks extra de peso al golpear menas/nodos de mineral (da más sensación de peso)
            bool isOreNode = IsOreOrGemNode(stoneObj);
            int oreBonus = isOreNode ? 18 : 0;
            int swingInterval = Math.Max(25, (int)Math.Round(profile.MiningSwingIntervalTicks * heartMult) + oreBonus);

            if (gameTick >= compState.LastCaveSwingTick + (ulong)swingInterval)
            {
                compState.LastCaveSwingTick = gameTick;

                if (!_rockDurability.TryGetValue((mineLocKey, rockPos), out int durability))
                {
                    durability = GetInitialRockDurability(stoneObj);
                }

                durability -= profile.PickaxePower;
                _rockDurability[(mineLocKey, rockPos)] = durability;

                loc.playSound("hammer");
                npc.jumpWithoutSound(3.2f);
                ToolAnimationService.PlayPickaxeSwing(loc, npc, new Point((int)rockPos.X, (int)rockPos.Y), profile.Archetype == CaveArchetype.MinerArtisan);

                if (durability <= 0)
                {
                    loc.playSound("stoneCrack");
                    string stoneId = stoneObj.ItemId ?? "390";

                    if (loc is MineShaft ms)
                    {
                        ms.checkStoneForItems(stoneId, (int)rockPos.X, (int)rockPos.Y, player);
                        loc.objects.Remove(rockPos);
                    }
                    else
                    {
                        loc.objects.Remove(rockPos);
                        Game1.createRadialDebris(loc, 14, (int)rockPos.X, (int)rockPos.Y, Game1.random.Next(2, 5), false);
                        Game1.createItemDebris(ItemRegistry.Create("(O)390", 1), rockPos * 64f, -1, loc);
                        if (loc is VolcanoDungeon && Game1.random.NextDouble() < 0.4)
                        {
                            Game1.createItemDebris(ItemRegistry.Create("(O)848", 1), rockPos * 64f, -1, loc);
                        }
                    }

                    ApplyMiningBonus(npc, loc, player, profile, rockPos, stoneId);
                    try { TryAwardDailyActivityFriendship(player, npc, "cave_coop", 1, 25); } catch { }

                    _rockDurability.Remove((mineLocKey, rockPos));
                    compState.CurrentCaveRockTarget = null;
                    compState.NextCaveRockSearchTick = gameTick + (ulong)Game1.random.Next(25, 51);
                }
            }
            return true;
        }

        // No current rock target: check if human pause has elapsed
        if (gameTick < compState.NextCaveRockSearchTick)
            return false;

        // Scan breakable rocks
        var candidates = loc.objects.Pairs
            .Where(p => p.Value != null && IsBreakableCaveStone(p.Value)
                     && !_temporaryBlockedCaveTiles.ContainsKey((mineLocKey, p.Key))
                     && Vector2.Distance(npc.Tile, p.Key) <= maxRockDistFromNpc
                     && Vector2.Distance(player.Tile, p.Key) <= maxRockDistFromPlayer
                     && HasWalkableAdjacentTile(loc, p.Key, npc))
            .ToList();

        if (candidates.Count == 0) return false;

        KeyValuePair<Vector2, StardewValley.Object> chosen;

        if (compState.CaveStance == CaveTacticStance.LadderHunter)
        {
            chosen = candidates[Game1.random.Next(candidates.Count)];
        }
        else if (profile.PrioritizesOreNodes || (compState.CaveStance == CaveTacticStance.Balanced && (profile.Archetype == CaveArchetype.MinerArtisan || profile.Archetype == CaveArchetype.TechnicalArcane)))
        {
            var oreCandidates = candidates.Where(p => IsOreOrGemNode(p.Value)).ToList();
            if (oreCandidates.Count > 0 && Game1.random.NextDouble() < 0.70)
            {
                chosen = oreCandidates[Game1.random.Next(oreCandidates.Count)];
            }
            else
            {
                chosen = candidates[Game1.random.Next(candidates.Count)];
            }
        }
        else
        {
            chosen = candidates[Game1.random.Next(candidates.Count)];
        }

        compState.CurrentCaveRockTarget = chosen.Key;
        compState.CurrentCaveRockTargetStartTick = gameTick;
        return true;
    }

    private static int GetInitialRockDurability(StardewValley.Object obj)
    {
        string id = obj.ItemId ?? string.Empty;
        string name = obj.Name ?? string.Empty;

        // v4.3.3: Dureza base escalada a 12/18/24 para aritmética exacta por clase:
        //   MinerArtisan  Power=12 → piedra común 1 golpe, mena básica 2, mena dura 2
        //   Vanguard/Tech Power= 6 → piedra común 2 golpes, mena básica 3, mena dura 4
        //   Cautious      Power= 4 → piedra común 3 golpes, mena básica 5, mena dura 6
        //   SupportHealer Power= 3 → piedra común 4 golpes, mena básica 6, mena dura 8

        // Iron, Gold, Iridium, Volcano, Mystic (nodos duros = 24)
        if (id is "380" or "384" or "386" or "848" or "764" or "765"
            || name.Contains("Iron", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Gold", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Iridium", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Mystic", StringComparison.OrdinalIgnoreCase))
            return 24;

        // Copper, Coal, Gem nodes, Quartz (nodos básicos = 18)
        if (id is "378" or "751" or "80" or "82" or "84" or "86"
            || name.Contains("Copper", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Coal", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Gem", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Quartz", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Node", StringComparison.OrdinalIgnoreCase))
            return 18;

        // Common gray stone (dureza base = 12)
        return 12;
    }

    private static bool HasWalkableAdjacentTile(GameLocation loc, Vector2 tile, NPC npc)
    {
        Point pt = new Point((int)tile.X, (int)tile.Y);
        Point walkAdj = FindAdjacentWalkableCaveTile(loc, pt, npc.TilePoint, npc);
        return walkAdj != Point.Zero;
    }

    private void ScanForLaddersAndShafts(NPC npc, GameLocation loc, Farmer player, NpcJob? job)
    {
        if (npc == null || loc == null || player == null) return;
        if (loc is not MineShaft ms) return;

        var profile = CavePersonalityRegistry.ResolveProfile(npc);
        if (profile.LadderSpotRadius <= 0) return;

        var bLayer = loc.map?.GetLayer("Buildings");
        if (bLayer == null) return;

        Point pTile = npc.TilePoint;
        int rad = profile.LadderSpotRadius;
        int minX = Math.Max(0, pTile.X - rad);
        int maxX = Math.Min(bLayer.LayerWidth - 1, pTile.X + rad);
        int minY = Math.Max(0, pTile.Y - rad);
        int maxY = Math.Min(bLayer.LayerHeight - 1, pTile.Y + rad);

        Point entryTile = Point.Zero;
        if (job != null && job.Data.TryGetValue("cave_entry_tile", out var et) && et is Point p)
        {
            entryTile = p;
        }

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                var tile = bLayer.Tiles[x, y];
                if (tile == null) continue;
                int idx = tile.TileIndex;
                if (idx == 173 || idx == 174)
                {
                    Point ladderPt = new Point(x, y);
                    if (ladderPt == entryTile) continue;

                    string ladderKey = $"{loc.NameOrUniqueName ?? string.Empty}|{ladderPt.X}_{ladderPt.Y}";
                    if (!_announcedLaddersOnCurrentFloor.Contains(ladderKey))
                    {
                        _announcedLaddersOnCurrentFloor.Add(ladderKey);
                        npc.faceGeneralDirection(new Vector2(x * 64f + 32f, y * 64f + 32f));
                        npc.doEmote(16); // Exclamation
                        if (idx == 173)
                        {
                            string k = Game1.random.NextDouble() < 0.5 ? "cave.ladder.found.v1" : "cave.ladder.found.v2";
                            ShowSafeBubble(npc, k, new { playerName = player.displayName, npcName = npc.displayName }, duration: 3200);
                        }
                        else
                        {
                            string k = Game1.random.NextDouble() < 0.5 ? "cave.shaft.found.v1" : "cave.shaft.found.v2";
                            ShowSafeBubble(npc, k, new { playerName = player.displayName, npcName = npc.displayName }, duration: 3200);
                        }
                        return;
                    }
                }
            }
        }
    }

    private static bool IsBreakableCaveStone(StardewValley.Object obj)
    {
        if (ValleyBuddies.AgencyCore.FarmTaskHelper.IsProtectedPlayerObject(obj)) return false;
        string name = obj.Name ?? string.Empty;
        string id = obj.ItemId ?? string.Empty;
        string qid = obj.QualifiedItemId ?? string.Empty;

        return name.Contains("Stone", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Ore", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Node", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Gem", StringComparison.OrdinalIgnoreCase)
            || id is "390" or "378" or "380" or "384" or "386" or "751" or "764" or "765" or "843" or "844" or "848" or "849" or "850"
            || id is "2" or "4" or "6" or "8" or "10" or "12" or "14" or "668" or "670" or "760" or "762" or "816" or "817" or "818" or "819"
            || qid.StartsWith("(O)390") || qid.StartsWith("(O)378") || qid.StartsWith("(O)380") || qid.StartsWith("(O)384") || qid.StartsWith("(O)386");
    }

    private static bool IsOreOrGemNode(StardewValley.Object obj)
    {
        if (obj == null) return false;
        string name = obj.Name ?? string.Empty;
        string id = obj.ItemId ?? string.Empty;
        return name.Contains("Ore", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Node", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Gem", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Copper", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Iron", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Gold", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Iridium", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Coal", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Quartz", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Mystic", StringComparison.OrdinalIgnoreCase)
            || id is "378" or "380" or "384" or "386" or "751" or "764" or "765" or "848" or "80" or "82" or "84" or "86";
    }

    private static void ApplyMiningBonus(NPC npc, GameLocation loc, Farmer player, CavePersonalityProfile profile, Vector2 stoneTile, string stoneId)
    {
        if (Game1.random.NextDouble() >= profile.BonusDropChance)
            return;

        switch (profile.Archetype)
        {
            case CaveArchetype.MinerArtisan:
            {
                string extraId = "(O)382"; // Coal
                double roll = Game1.random.NextDouble();
                if (roll < 0.35) extraId = "(O)382";
                else if (roll < 0.70)
                {
                    if (loc is MineShaft ms)
                    {
                        if (ms.mineLevel < 40) extraId = "(O)378"; // Copper
                        else if (ms.mineLevel < 80) extraId = "(O)380"; // Iron
                        else if (ms.mineLevel <= 120) extraId = "(O)384"; // Gold
                        else extraId = "(O)386"; // Iridium
                    }
                    else if (loc is VolcanoDungeon) extraId = "(O)848"; // Cinder shard
                    else extraId = "(O)380";
                }
                else
                {
                    extraId = "(O)535"; // Geode
                }
                Game1.createItemDebris(ItemRegistry.Create(extraId, 1), stoneTile * 64f, -1, loc);
                loc.playSound("coin");
                if (Game1.random.NextDouble() < 0.25)
                    ShowSafeBubble(npc, "cave.bonus.ore_found", duration: 1500);
                break;
            }

            case CaveArchetype.SupportHealer:
            {
                string geodeId = "(O)535";
                if (loc is MineShaft ms)
                {
                    if (ms.mineLevel < 40) geodeId = "(O)535";
                    else if (ms.mineLevel < 80) geodeId = "(O)536";
                    else if (ms.mineLevel <= 120) geodeId = "(O)537";
                    else geodeId = "(O)749"; // Omni
                }
                else if (loc is VolcanoDungeon)
                {
                    geodeId = "(O)749";
                }
                Game1.createItemDebris(ItemRegistry.Create(geodeId, 1), stoneTile * 64f, -1, loc);
                loc.playSound("coin");
                if (Game1.random.NextDouble() < 0.25)
                    ShowSafeBubble(npc, "cave.bonus.geode_found", duration: 1500);
                break;
            }

            case CaveArchetype.CautiousFortune:
            {
                string[] items = new[] { "(O)80", "(O)86", "(O)84", "(O)82", "(O)78" };
                if (loc is MineShaft ms)
                {
                    if (ms.mineLevel < 40) items = new[] { "(O)80", "(O)86", "(O)78" };
                    else if (ms.mineLevel < 80) items = new[] { "(O)84", "(O)62", "(O)70" };
                    else if (ms.mineLevel <= 120) items = new[] { "(O)82", "(O)64", "(O)60" };
                    else items = new[] { "(O)72", "(O)66", "(O)848" };
                }
                string chosen = items[Game1.random.Next(items.Length)];
                Game1.createItemDebris(ItemRegistry.Create(chosen, 1), stoneTile * 64f, -1, loc);
                loc.playSound("coin");
                if (Game1.random.NextDouble() < 0.25)
                    ShowSafeBubble(npc, "cave.bonus.gem_found", duration: 1500);
                break;
            }
        }
    }

    /// <summary>
    /// Checks and triggers non-repeating ambient biome remarks when entering or exploring caves.
    /// </summary>
    public void CheckCaveBiomeReaction(NPC npc, GameLocation loc, Farmer player, ulong gameTick)
    {
        if (npc == null || loc == null || player == null) return;
        if (!IsUndergroundDangerLocation(loc)) return;

        if (_companions.TryGetValue(npc.Name, out var comp))
        {
            if (gameTick < comp.LastBiomeReactionTick + 7200 && comp.LastBiomeName == loc.NameOrUniqueName)
                return;

            comp.LastBiomeReactionTick = gameTick;
            comp.LastBiomeName = loc.NameOrUniqueName;
        }

        var profile = CavePersonalityRegistry.ResolveProfile(npc);
        string key = "";

        if (loc is MineShaft ms)
        {
            if (ms.isMonsterArea)
            {
                key = (profile.Archetype == CaveArchetype.Vanguard)
                    ? "cave.biome.infested.brave"
                    : "cave.biome.infested.cautious";
            }
            else if (ms.isDarkArea())
            {
                key = (profile.Archetype == CaveArchetype.Vanguard || profile.Archetype == CaveArchetype.TechnicalArcane)
                    ? "cave.biome.dark.light"
                    : "cave.biome.dark.nervous";
            }
            else if (ms.mineLevel >= 121)
            {
                key = Game1.random.NextDouble() < 0.5 ? "cave.biome.skull_cavern.v1" : "cave.biome.skull_cavern.v2";
            }
            else if (ms.mineLevel >= 80 && ms.mineLevel <= 120)
            {
                key = Game1.random.NextDouble() < 0.5 ? "cave.biome.lava.v1" : "cave.biome.lava.v2";
            }
            else if (ms.mineLevel >= 40 && ms.mineLevel < 80)
            {
                key = Game1.random.NextDouble() < 0.5 ? "cave.biome.frozen.v1" : "cave.biome.frozen.v2";
            }
            else
            {
                key = Game1.random.NextDouble() < 0.5 ? "cave.biome.dark.light" : "cave.biome.dark.nervous";
            }
        }
        else if (loc is VolcanoDungeon vd && vd.level.Value >= 1)
        {
            key = Game1.random.NextDouble() < 0.5 ? "cave.biome.volcano.v1" : "cave.biome.volcano.v2";
        }

        if (!string.IsNullOrEmpty(key))
        {
            SpeakNonRepeating(npc, key, new { playerName = player.displayName, npcName = npc.displayName });
        }
    }

    /// <summary>
    /// Displays the Cave Tactics submenu allowing the player to set tactical companion stances.
    /// Immediately activates the chosen stance and clears previous movement blocks.
    /// </summary>
    public void ShowCaveTacticsSubmenu(NPC npc, Farmer player)
    {
        if (npc == null || Game1.currentLocation == null) return;
        _companions.TryGetValue(npc.Name, out var comp);
        var currentStance = comp?.CaveStance ?? CaveTacticStance.Balanced;

        var responses = new List<Response>
        {
            new Response("stance_balanced", (currentStance == CaveTacticStance.Balanced ? "✓ " : "") + Utilities.I18n.Get("cave.stance.balanced")),
            new Response("stance_ladder",   (currentStance == CaveTacticStance.LadderHunter ? "✓ " : "") + Utilities.I18n.Get("cave.stance.ladder")),
            new Response("stance_guard",    (currentStance == CaveTacticStance.GuardCombat ? "✓ " : "") + Utilities.I18n.Get("cave.stance.guard")),
            new Response("stance_stay_back", (currentStance == CaveTacticStance.StayBack ? "✓ " : "") + Utilities.I18n.Get("cave.stance.stay_back")),
            new Response("back",            Utilities.I18n.Get("menu.tasks.back"))
        };

        Game1.currentLocation.afterQuestion = (Farmer who, string answer) =>
        {
            var activeFarmer = who ?? player;
            if (answer == "back")
            {
                DelayedAction.functionAfterDelay(() => ShowFollowerActionMenu(npc, activeFarmer), 100);
                return;
            }
            if (string.IsNullOrEmpty(answer))
            {
                ResumeCompanionDefaultJob(npc, activeFarmer);
                return;
            }

            if (comp == null)
            {
                SetCompanion(npc.Name, activeFarmer.UniqueMultiplayerID, false, true, activeFarmer.Name);
                _companions.TryGetValue(npc.Name, out comp);
            }

            // Ensure companion has an active JobKind.Follow job in _active
            if (!_active.TryGetValue(npc.Name, out var actJob) || actJob.Done)
            {
                actJob = new NpcJob
                {
                    NpcName = npc.Name,
                    Kind = JobKind.Follow,
                    FarmerId = player.UniqueMultiplayerID,
                    FarmerName = player.Name,
                    CreatedTick = Game1.ticks
                };
                _active[npc.Name] = actJob;
            }
            else
            {
                actJob.Kind = JobKind.Follow;
                actJob.Done = false;
            }

            if (comp != null)
            {
                // Ensure companion is active, reset targets and timers
                comp.IsCaveAutonomyActive = true;
                comp.IsFollowing = true;
                comp.IsRestingInPlace = false;
                comp.CurrentCaveRockTarget = null;
                comp.CurrentCaveCombatTarget = null;
                comp.CaveWanderTarget = null;
                comp.IsFleeingBomb = false;
                comp.CaveStuckSinceTick = 0;
                comp.NextCaveActionTick = 0;
                comp.NextCaveMoveTick = 0;
                comp.NextCaveRockSearchTick = 0;
                comp.LastCaveSwingTick = 0;
                npc.controller = null;
                npc.Halt();
            }

            if (Game1.IsMultiplayer && !Context.IsMainPlayer)
            {
                CaveTacticStance chosenStance = answer switch
                {
                    "stance_ladder" => CaveTacticStance.LadderHunter,
                    "stance_guard" => CaveTacticStance.GuardCombat,
                    "stance_stay_back" => CaveTacticStance.StayBack,
                    _ => CaveTacticStance.Balanced
                };
                if (answer != "back")
                {
                    if (comp != null) comp.CaveStance = chosenStance;
                    _multiplayerSync?.SendCaveStanceSync(npc.Name, player.UniqueMultiplayerID, (int)chosenStance);
                }
            }

            switch (answer)
            {
                case "stance_balanced":
                    if (comp != null) comp.CaveStance = CaveTacticStance.Balanced;
                    npc.doEmote(32);
                    ShowSafeBubble(npc, "cave.stance.confirmed.balanced", new { playerName = player.displayName, npcName = npc.displayName }, duration: 3200);
                    break;
                case "stance_ladder":
                    if (comp != null) comp.CaveStance = CaveTacticStance.LadderHunter;
                    npc.doEmote(16);
                    ShowSafeBubble(npc, "cave.stance.confirmed.ladder", new { playerName = player.displayName, npcName = npc.displayName }, duration: 3200);
                    break;
                case "stance_guard":
                    if (comp != null) comp.CaveStance = CaveTacticStance.GuardCombat;
                    npc.doEmote(20);
                    ShowSafeBubble(npc, "cave.stance.confirmed.guard", new { playerName = player.displayName, npcName = npc.displayName }, duration: 3200);
                    break;
                case "stance_stay_back":
                    if (comp != null)
                    {
                        comp.CaveStance = CaveTacticStance.StayBack;
                        comp.FloorSafeWaitTile = FindSafeLadderWaitTile(npc.currentLocation, comp.FloorEntryLadderTile != Point.Zero ? comp.FloorEntryLadderTile : player.TilePoint, npc);
                    }
                    npc.doEmote(28);
                    ShowSafeBubble(npc, "cave.stance.confirmed.stay_back", new { playerName = player.displayName, npcName = npc.displayName }, duration: 3200);
                    break;
            }
        };

        Game1.currentLocation.createQuestionDialogue(
            Utilities.I18n.Get("menu.cave.tactics.title", new { npcName = npc.displayName }),
            responses.ToArray(),
            "caveTacticsSubmenu");
    }

    public void ProcessHostCaveStanceSync(string npcName, long requesterId, int stanceInt)
    {
        if (!Context.IsMainPlayer) return;

        if (TryGetCompanionInfo(npcName, out var compInfo) && compInfo.FarmerId != 0 && compInfo.FarmerId != requesterId)
            return;

        var npc = Game1.getCharacterFromName(npcName);
        var player = Game1.GetPlayer(requesterId, true) ?? Game1.MasterPlayer;

        if (!_companions.TryGetValue(npcName, out var comp))
        {
            SetCompanion(npcName, requesterId, false, true, player?.Name ?? "");
            _companions.TryGetValue(npcName, out comp);
        }

        if (!_active.TryGetValue(npcName, out var actJob) || actJob.Done)
        {
            actJob = new NpcJob
            {
                NpcName = npcName,
                Kind = JobKind.Follow,
                FarmerId = requesterId,
                FarmerName = player?.Name ?? "",
                CreatedTick = Game1.ticks
            };
            _active[npcName] = actJob;
        }
        else
        {
            actJob.Kind = JobKind.Follow;
            actJob.Done = false;
        }

        if (comp != null)
        {
            comp.CaveStance = (CaveTacticStance)stanceInt;
            comp.IsCaveAutonomyActive = true;
            comp.IsFollowing = true;
            comp.IsRestingInPlace = false;
            comp.CurrentCaveRockTarget = null;
            comp.CurrentCaveCombatTarget = null;
            comp.CaveWanderTarget = null;
            comp.IsFleeingBomb = false;
            comp.CaveStuckSinceTick = 0;
            comp.NextCaveActionTick = 0;
            comp.NextCaveMoveTick = 0;
            comp.NextCaveRockSearchTick = 0;
            comp.LastCaveSwingTick = 0;
            if (comp.CaveStance == CaveTacticStance.StayBack && npc != null && npc.currentLocation != null)
            {
                comp.FloorSafeWaitTile = FindSafeLadderWaitTile(npc.currentLocation, comp.FloorEntryLadderTile != Point.Zero ? comp.FloorEntryLadderTile : (player?.TilePoint ?? npc.TilePoint), npc);
            }
        }

        if (npc != null)
        {
            npc.controller = null;
            npc.Halt();
        }
    }
}
