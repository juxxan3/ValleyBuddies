using System;
using System.Collections.Concurrent;
using System.Linq;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.Pathfinding;
using ValleyBuddies.Execution;

namespace ValleyBuddies.Bridge;

public static class PathfindingSafetyPatches
{
    private static IMonitor? _monitor;

    // Diccionario thread-safe para BedExitNoClip por NPC
    private static readonly ConcurrentDictionary<string, int> _bedExitNoClipUntilTick = new(StringComparer.OrdinalIgnoreCase);

    public static void EnableBedExitNoClip(string npcName, int durationTicks = 240)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;
        int targetUntil = Game1.ticks + durationTicks;
        if (_bedExitNoClipUntilTick.TryGetValue(npcName, out int existingUntil) && existingUntil > Game1.ticks + 30)
        {
            if (targetUntil > existingUntil)
            {
                _bedExitNoClipUntilTick[npcName] = targetUntil;
            }
            return;
        }

        _bedExitNoClipUntilTick[npcName] = targetUntil;
        _monitor?.Log($"[BedExit] EnableBedExitNoClip for {npcName} (duration={durationTicks} ticks, until={targetUntil})", LogLevel.Debug);
    }

    public static void ClearBedExitNoClip(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;
        _bedExitNoClipUntilTick.TryRemove(npcName, out _);
    }

    public static void ClearAllBedExitNoClip()
    {
        _bedExitNoClipUntilTick.Clear();
    }

    public static bool IsBedExitNoClipActive(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return false;
        return _bedExitNoClipUntilTick.TryGetValue(npcName, out int until) && Game1.ticks < until;
    }

    public static void Apply(Harmony harmony, IMonitor? monitor = null)
    {
        _monitor = monitor;
        try
        {
            var handleWarps = AccessTools.Method(typeof(PathFindController), "handleWarps", new[] { typeof(Rectangle) });
            if (handleWarps != null)
            {
                harmony.Patch(
                    original: handleWarps,
                    prefix: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(Prefix_HandleWarps))
                );
            }

            // Patch: PathFindController.isPlayerPresent() Postfix
            // Devuelve true para NPCs gestionados o en rutina de cónyuge, evitando que SDV 1.6 ejecute
            // el teletransporte a endPoint (línea 154) y permitiendo que caminen normalmente off-screen.
            var isPlayerPresentMethod = AccessTools.Method(typeof(PathFindController), nameof(PathFindController.isPlayerPresent));
            if (isPlayerPresentMethod != null)
            {
                harmony.Patch(
                    original: isPlayerPresentMethod,
                    postfix: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(Postfix_IsPlayerPresent))
                );
            }

            var checkSchedule = AccessTools.Method(typeof(NPC), nameof(NPC.checkSchedule), new[] { typeof(int) });
            if (checkSchedule != null)
            {
                harmony.Patch(
                    original: checkSchedule,
                    prefix: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(CheckSchedule_Prefix))
                );
            }

            // Patch 3: GameLocation.isCollidingPosition (10 parameters)
            // Bypasses NPCBarrier collision in Farm north entrance corridor (Y=4..6) during collision checks for managed companions
            // and allows BedExitNoClip over BedFurniture in FarmHouse / Cabin without clipping into north walls.
            var isColliding = AccessTools.Method(
                typeof(GameLocation),
                nameof(GameLocation.isCollidingPosition),
                new[] {
                    typeof(Rectangle),
                    typeof(xTile.Dimensions.Rectangle),
                    typeof(bool),
                    typeof(int),
                    typeof(bool),
                    typeof(Character),
                    typeof(bool),
                    typeof(bool),
                    typeof(bool),
                    typeof(bool)
                }
            );
            if (isColliding != null)
            {
                harmony.Patch(
                    original: isColliding,
                    prefix: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(IsCollidingPosition_Prefix)),
                    postfix: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(IsCollidingPosition_Postfix)),
                    finalizer: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(IsCollidingPosition_Finalizer))
                );
            }

            // Patch: NPC.returnHomeFromFarmPosition(Farm) Prefix
            // Blocks vanilla spouse-recall warps while VB manages an NPC as a companion, in home routine, or on an active outing.
            var returnHomeFromFarm = AccessTools.Method(typeof(NPC), "returnHomeFromFarmPosition", new[] { typeof(Farm) });
            if (returnHomeFromFarm != null)
            {
                harmony.Patch(
                    original: returnHomeFromFarm,
                    prefix: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(ReturnHomeFromFarm_Prefix))
                );
            }

            // Patch: NPC.shouldCollideWithBuildingLayer(GameLocation) Postfix
            // Aligns married spouses following an A* path on Farm with non-married NPCs to avoid 1-2px snags at stair/cliff edges.
            var shouldCollideBuildingLayer = AccessTools.Method(typeof(NPC), nameof(NPC.shouldCollideWithBuildingLayer), new[] { typeof(GameLocation) });
            if (shouldCollideBuildingLayer != null)
            {
                harmony.Patch(
                    original: shouldCollideBuildingLayer,
                    postfix: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(ShouldCollideWithBuildingLayer_Postfix))
                );
            }

            // Patch 4: NPC.wearNormalClothes
            // Prevents vanilla SDV 1.6 InValleyContext() from stripping beach outfit 1 second after entering Beach
            var wearNormalClothes = AccessTools.Method(typeof(NPC), nameof(NPC.wearNormalClothes));
            if (wearNormalClothes != null)
            {
                harmony.Patch(
                    original: wearNormalClothes,
                    prefix: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(WearNormalClothes_Prefix))
                );
            }

            // Patch 5: NPC.ChooseAppearance
            // Prevents vanilla SDV 1.6 from overwriting active beach outfit with default attire
            var chooseAppearance = AccessTools.Method(typeof(NPC), nameof(NPC.ChooseAppearance), new[] { typeof(LocalizedContentManager) });
            if (chooseAppearance != null)
            {
                harmony.Patch(
                    original: chooseAppearance,
                    prefix: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(ChooseAppearance_Prefix))
                );
            }

            // Patch 6: Fence.toggleGate
            // Prevents redundant gate toggling and rapid open/close sound spam by NPCs crossing gates
            var toggleGate = AccessTools.Method(typeof(Fence), nameof(Fence.toggleGate), new[] { typeof(bool), typeof(bool), typeof(Farmer) });
            if (toggleGate != null)
            {
                harmony.Patch(
                    original: toggleGate,
                    prefix: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(ToggleGate_Prefix))
                );
            }

            // Patch 7: NPC.draw(SpriteBatch, float) Prefix + Postfix
            // Visually immerses NPCs in BathHouse_Pool by cropping sprite to torso/head with gentle water bobbing
            try
            {
                var drawNpc = AccessTools.Method(typeof(NPC), nameof(NPC.draw), new[] { typeof(SpriteBatch), typeof(float) });
                if (drawNpc != null && drawNpc.DeclaringType == typeof(NPC))
                {
                    harmony.Patch(
                        original: drawNpc,
                        prefix: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(DrawNpc_Prefix)),
                        postfix: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(DrawNpc_Postfix)),
                        finalizer: new HarmonyMethod(typeof(PathfindingSafetyPatches), nameof(DrawNpc_Finalizer))
                    );
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"Failed to patch NPC.draw: {ex}", LogLevel.Warn);
            }

            _monitor?.Log("PathfindingSafetyPatches applied: handleWarps, ReturnHomeFromFarm, CheckSchedule, isCollidingPosition (NPCBarrier + BedExitNoClip), Beach outfit, Fence.toggleGate, and NPC.draw (Spa Submerge) protected.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error applying PathfindingSafetyPatches: {ex}", LogLevel.Error);
        }
    }

    internal static void IsCollidingPosition_Prefix(
        GameLocation __instance,
        Rectangle position,
        Character? character,
        ref (bool wasEventActor, bool wasFarmerPasses) __state)
    {
        __state = (false, false);
        try
        {
            if (character is NPC npc)
            {
                bool isManaged = NpcActionExecutor.Instance?.IsManagedCompanion(npc.Name) == true;
                bool isSpouseRoutine = NpcActionExecutor.Instance?.IsSpouseInHomeRoutine(npc.Name) == true;
                bool hasActiveOuting = ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.HasActiveOutingForNpc(npc.Name);

                if (isManaged || isSpouseRoutine || hasActiveOuting)
                {
                    // Exempt Back-layer NPCBarrier tiles across the entire Farm (including stairs/bridges below Y=12)
                    // ONLY when touching an NPCBarrier tile or the north Farm entrance corridor.
                    // Never exempt in FarmHouse/Cabin, and walls/cliffs/water still collide normally.
                    if (__instance is Farm && (position.Y <= 12 * 64 || DoesRectTouchNpcBarrier(__instance, position)))
                    {
                        if (!character.EventActor)
                        {
                            character.EventActor = true;
                            __state.wasEventActor = true;
                        }
                    }

                    if (__instance is FarmHouse || (__instance != null && __instance.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (IsBedExitNoClipActive(npc.Name) || IsOverlappingAnyBed(__instance, npc.GetBoundingBox()))
                        {
                            if (!character.farmerPassesThrough)
                            {
                                character.farmerPassesThrough = true;
                                __state.wasFarmerPasses = true;
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    internal static void IsCollidingPosition_Finalizer(
        Character? character,
        (bool wasEventActor, bool wasFarmerPasses) __state)
    {
        if (character == null) return;
        if (__state.wasEventActor)
            character.EventActor = false;
        if (__state.wasFarmerPasses)
            character.farmerPassesThrough = false;
    }

    internal static void IsCollidingPosition_Postfix(
        GameLocation __instance,
        Rectangle position,
        Character? character,
        (bool wasEventActor, bool wasFarmerPasses) __state,
        ref bool __result)
    {
        try
        {
            if (__state.wasEventActor && character != null)
            {
                character.EventActor = false;
            }
            if (__state.wasFarmerPasses && character != null)
            {
                character.farmerPassesThrough = false;
            }

            // BedExitNoClip Hook: si hay colisión reportada pero el NPC está saliendo de la cama
            if (__result && character is NPC npc && (__instance is FarmHouse || (__instance != null && __instance.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase))))
            {
                bool isManaged = NpcActionExecutor.Instance?.IsManagedCompanion(npc.Name) == true;
                bool isSpouseRoutine = NpcActionExecutor.Instance?.IsSpouseInHomeRoutine(npc.Name) == true;

                if (isManaged || isSpouseRoutine)
                {
                    bool activeNoClip = IsBedExitNoClipActive(npc.Name);
                    bool inBed = IsOverlappingAnyBed(__instance, npc.GetBoundingBox());

                    if (activeNoClip || inBed)
                    {
                        // Prohibido atravesar la pared norte o salirse de los límites del mapa.
                        // La almohada superior cae en Y >= bed.TileLocation.Y * 64 - 24 (~224-240px)
                        int minY = GetBedExitMinY(__instance, position);
                        if (position.Y >= minY && position.X >= 0)
                        {
                            var backLayer = __instance.map?.GetLayer("Back") ?? __instance.map?.Layers[0];
                            if (backLayer != null && position.X + position.Width <= backLayer.LayerWidth * 64 && position.Y + position.Height <= backLayer.LayerHeight * 64)
                            {
                                if (IsOnlyBedOrBedsideCollision(__instance, position, minY))
                                {
                                    __result = false;
                                }
                            }
                        }
                    }
                }
            }

            // Gate NoClip Hook: si hay colisión reportada pero el NPC está cruzando una puerta de valla abierta
            if (__result && character is NPC gateNpc && __instance != null)
            {
                bool isManaged = NpcActionExecutor.Instance?.IsManagedCompanion(gateNpc.Name) == true;
                bool isSpouseRoutine = NpcActionExecutor.Instance?.IsSpouseInHomeRoutine(gateNpc.Name) == true;

                if (isManaged || isSpouseRoutine)
                {
                    Point centerTile = new Point(position.Center.X / 64, position.Center.Y / 64);
                    Vector2 tileVec = new Vector2(centerTile.X, centerTile.Y);
                    if (__instance.Objects != null && __instance.Objects.TryGetValue(tileVec, out var obj) && obj is Fence fence && fence.isGate.Value && fence.gatePosition.Value >= Fence.gateOpenedPosition)
                    {
                        __result = false;
                    }
                }
            }

            // BathHousePool Swimming Water Passage: acompañante en traje de baño caminando/nadando en el agua de la piscina
            if (__result && character is NPC poolNpc && __instance is BathHousePool poolLoc)
            {
                if (NpcActionExecutor.Instance?.IsCompanionWearingBeachOutfit(poolNpc.Name) == true || NpcActionExecutor.Instance?.IsManagedCompanion(poolNpc.Name) == true)
                {
                    int cx = position.Center.X / 64;
                    int cy = position.Center.Y / 64;
                    if (IsTileInBathHousePoolWater(poolLoc, new Point(cx, cy)))
                    {
                        __result = false;
                    }
                }
            }

            // Animal & Pet Passthrough: acompañantes, rutinas de cónyuge y salidas activas pueden cruzar junto a animales y mascotas
            if (__result && character is NPC animalNpc && __instance != null && IsAnimalPassthroughLocation(__instance))
            {
                bool isManaged = NpcActionExecutor.Instance?.IsManagedCompanion(animalNpc.Name) == true;
                bool isSpouseRoutine = NpcActionExecutor.Instance?.IsSpouseInHomeRoutine(animalNpc.Name) == true;
                bool hasActiveOuting = ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.HasActiveOutingForNpc(animalNpc.Name);

                if ((isManaged || isSpouseRoutine || hasActiveOuting)
                    && IntersectsAnimalOrPet(__instance, position)
                    && AreRectCornersStaticallyWalkable(__instance, position, animalNpc))
                {
                    __result = false;
                }
            }
        }
        catch { }
    }

    private static bool IsAnimalPassthroughLocation(GameLocation loc)
    {
        return loc is Farm
            || loc is AnimalHouse
            || loc is FarmHouse
            || loc is Cabin
            || loc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IntersectsAnimalOrPet(GameLocation loc, Rectangle position)
    {
        if (loc is Farm farm && farm.animals != null)
        {
            foreach (var animal in farm.animals.Values)
            {
                if (animal != null && animal.GetBoundingBox().Intersects(position))
                    return true;
            }
        }
        else if (loc is AnimalHouse ah && ah.animals != null)
        {
            foreach (var animal in ah.animals.Values)
            {
                if (animal != null && animal.GetBoundingBox().Intersects(position))
                    return true;
            }
        }

        if (loc.characters != null)
        {
            for (int i = 0; i < loc.characters.Count; i++)
            {
                if (loc.characters[i] is StardewValley.Characters.Pet pet && pet.GetBoundingBox().Intersects(position))
                    return true;
            }
        }

        return false;
    }

    private static bool AreRectCornersStaticallyWalkable(GameLocation loc, Rectangle position, NPC npc)
    {
        if (position.Left < 0 || position.Top < 0 || position.Width <= 0 || position.Height <= 0)
            return false;

        int leftTile = position.Left / 64;
        int rightTile = (position.Right - 1) / 64;
        int topTile = position.Top / 64;
        int bottomTile = (position.Bottom - 1) / 64;

        if (!NpcActionExecutor.IsTileWalkable(loc, new Point(leftTile, topTile), npc, ignoreDynamicCharacters: true))
            return false;
        if (rightTile != leftTile && !NpcActionExecutor.IsTileWalkable(loc, new Point(rightTile, topTile), npc, ignoreDynamicCharacters: true))
            return false;
        if (bottomTile != topTile && !NpcActionExecutor.IsTileWalkable(loc, new Point(leftTile, bottomTile), npc, ignoreDynamicCharacters: true))
            return false;
        if (rightTile != leftTile && bottomTile != topTile && !NpcActionExecutor.IsTileWalkable(loc, new Point(rightTile, bottomTile), npc, ignoreDynamicCharacters: true))
            return false;

        return true;
    }

    private static bool IsOverlappingAnyBed(GameLocation loc, Rectangle box)
    {
        if (loc.furniture != null)
        {
            for (int i = 0; i < loc.furniture.Count; i++)
            {
                var f = loc.furniture[i];
                if (f is BedFurniture bed)
                {
                    int tilesW = Math.Max(bed.getTilesWide(), 2);
                    int tilesH = Math.Max(bed.getTilesHigh(), 3);
                    Rectangle fullBedRect = new Rectangle(
                        (int)bed.TileLocation.X * 64,
                        (int)bed.TileLocation.Y * 64 - 32,
                        tilesW * 64,
                        tilesH * 64 + 32
                    );
                    if (fullBedRect.Intersects(box) || fullBedRect.Contains(box.Center))
                        return true;
                }
            }
        }
        return false;
    }

    private static int GetBedExitMinY(GameLocation loc, Rectangle position)
    {
        if (loc.furniture != null)
        {
            for (int i = 0; i < loc.furniture.Count; i++)
            {
                var f = loc.furniture[i];
                if (f is BedFurniture bed)
                {
                    int tilesW = Math.Max(bed.getTilesWide(), 2);
                    int tilesH = Math.Max(bed.getTilesHigh(), 3);
                    Rectangle fullBedRect = new Rectangle(
                        (int)bed.TileLocation.X * 64 - 48,
                        (int)bed.TileLocation.Y * 64 - 48,
                        tilesW * 64 + 96,
                        tilesH * 64 + 96
                    );
                    if (fullBedRect.Intersects(position))
                    {
                        // Cubre toda la almohada y cabecera de la cama (~224-240px)
                        // protegiendo estrictamente la pared norte real
                        return Math.Max(0, (int)bed.TileLocation.Y * 64 - 24);
                    }
                }
            }
        }
        return 4 * 64; // Fallback seguro para otras zonas interiores
    }

    private static bool IsOnlyBedOrBedsideCollision(GameLocation loc, Rectangle position, int minY)
    {
        int tileX = position.Center.X / 64;
        int tileY = position.Center.Y / 64;

        if (position.Y < minY) return false;

        bool touchesBedOrBedside = false;
        if (loc.furniture != null)
        {
            for (int i = 0; i < loc.furniture.Count; i++)
            {
                var f = loc.furniture[i];
                if (f is BedFurniture bed)
                {
                    int tilesW = Math.Max(bed.getTilesWide(), 2);
                    int tilesH = Math.Max(bed.getTilesHigh(), 3);
                    Rectangle fullBedRect = new Rectangle(
                        (int)bed.TileLocation.X * 64 - 48,
                        (int)bed.TileLocation.Y * 64 - 48,
                        tilesW * 64 + 96,
                        tilesH * 64 + 96
                    );
                    if (fullBedRect.Intersects(position))
                    {
                        touchesBedOrBedside = true;
                        break;
                    }
                }
            }
        }

        if (!touchesBedOrBedside) return false;

        // Comprobar si hay obstáculos sólidos NO relacionados con la cama (muebles no contiguos a la cama)
        if (loc.furniture != null)
        {
            for (int i = 0; i < loc.furniture.Count; i++)
            {
                var f = loc.furniture[i];
                if (f == null || f is BedFurniture || f.furniture_type.Value == Furniture.rug)
                    continue;

                Rectangle fBox = f.GetBoundingBox();
                if (fBox.Intersects(position))
                {
                    bool isBedside = false;
                    for (int j = 0; j < loc.furniture.Count; j++)
                    {
                        if (loc.furniture[j] is BedFurniture b)
                        {
                            Rectangle bBox = b.GetBoundingBox();
                            Rectangle bBoxExp = new Rectangle(bBox.X - 64, bBox.Y - 64, bBox.Width + 128, bBox.Height + 128);
                            if (bBoxExp.Intersects(fBox))
                            {
                                isBedside = true;
                                break;
                            }
                        }
                    }
                    if (!isBedside) return false;
                }
            }
        }

        // Comprobar objetos sólidos en casilla
        Vector2 tileVec = new Vector2(tileX, tileY);
        if (loc.Objects != null && loc.Objects.TryGetValue(tileVec, out var obj) && obj != null && !obj.isPassable())
        {
            return false;
        }

        return true;
    }

    internal static bool WearNormalClothes_Prefix(NPC __instance)
    {
        try
        {
            if (__instance != null
                && !NpcActionExecutor.IsRestoringNormalClothes
                && NpcActionExecutor.Instance != null
                && NpcActionExecutor.Instance.IsManagedCompanion(__instance.Name)
                && NpcActionExecutor.Instance.IsCompanionWearingBeachOutfit(__instance.Name))
            {
                if (!__instance.shouldWearIslandAttire.Value)
                {
                    __instance.shouldWearIslandAttire.Value = true;
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error en WearNormalClothes_Prefix: {ex}", LogLevel.Trace);
        }
        return true;
    }

    internal static bool ChooseAppearance_Prefix(NPC __instance)
    {
        try
        {
            if (__instance != null
                && !NpcActionExecutor.IsApplyingBeachOutfit
                && !NpcActionExecutor.IsRestoringNormalClothes
                && NpcActionExecutor.Instance != null
                && NpcActionExecutor.Instance.IsManagedCompanion(__instance.Name)
                && NpcActionExecutor.Instance.IsCompanionWearingBeachOutfit(__instance.Name))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error en ChooseAppearance_Prefix: {ex}", LogLevel.Trace);
        }
        return true;
    }

    internal static bool ReturnHomeFromFarm_Prefix(NPC __instance)
    {
        try
        {
            if (__instance == null) return true;
            string name = __instance.Name;
            var executor = NpcActionExecutor.Instance;
            if (executor == null) return true;

            bool blocked = executor.IsManagedCompanion(name)
                        || executor.IsSpouseInHomeRoutine(name)
                        || ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.HasActiveOutingForNpc(name);
            if (blocked)
            {
                _monitor?.Log($"[SafetyPatch] Blocked returnHomeFromFarmPosition for managed NPC: {name}", LogLevel.Trace);
                return false;
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error en ReturnHomeFromFarm_Prefix: {ex}", LogLevel.Trace);
        }
        return true;
    }

    private static bool DoesRectTouchNpcBarrier(GameLocation loc, Rectangle rect)
    {
        var backLayer = loc.map?.GetLayer("Back");
        if (backLayer == null) return false;

        int minX = Math.Max(0, rect.Left / 64);
        int maxX = Math.Max(minX, Math.Min(backLayer.LayerWidth - 1, (rect.Right - 1) / 64));
        int minY = Math.Max(0, rect.Top / 64);
        int maxY = Math.Max(minY, Math.Min(backLayer.LayerHeight - 1, (rect.Bottom - 1) / 64));

        for (int tx = minX; tx <= maxX; tx++)
        {
            for (int ty = minY; ty <= maxY; ty++)
            {
                var tile = backLayer.Tiles[tx, ty];
                if (tile != null && tile.Properties.ContainsKey("NPCBarrier"))
                    return true;
            }
        }
        return false;
    }

    internal static void ShouldCollideWithBuildingLayer_Postfix(NPC __instance, GameLocation? location, ref bool __result)
    {
        try
        {
            if (!__result || __instance == null || location is not Farm)
                return;

            var ctrl = __instance.controller;
            if (ctrl == null || !ctrl.nonDestructivePathing || ctrl.pathToEndPoint == null || ctrl.pathToEndPoint.Count == 0)
                return;

            var executor = NpcActionExecutor.Instance;
            bool isManaged = executor != null && (executor.IsManagedCompanion(__instance.Name) || executor.IsSpouseInHomeRoutine(__instance.Name));
            bool hasOuting = ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.HasActiveOutingForNpc(__instance.Name);
            if (isManaged || hasOuting)
            {
                __result = false;
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error en ShouldCollideWithBuildingLayer_Postfix: {ex}", LogLevel.Trace);
        }
    }

    internal static bool CheckSchedule_Prefix(NPC __instance)
    {
        try
        {
            if (__instance == null)
                return true;

            var outingService = ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance;
            var executor = NpcActionExecutor.Instance;
            if ((executor != null && (executor.IsManagedCompanion(__instance.Name) || executor.IsSpouseInHomeRoutine(__instance.Name)))
                || outingService.IsNpcWaitingAtDoor(__instance.Name)
                || outingService.HasActiveOutingForNpc(__instance.Name))
            {
                return false; // Suprime warps y rutas programadas mientras Valley Buddies gestiona al NPC
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error en CheckSchedule_Prefix: {ex}", LogLevel.Trace);
        }
        return true;
    }

    private static bool Prefix_HandleWarps(PathFindController __instance, Character? ___character)
    {
        try
        {
            if (!Context.IsWorldReady || ___character is not NPC npc)
                return true;

            var executor = NpcActionExecutor.Instance;
            if (executor == null || (!executor.IsManagedCompanion(npc.Name) && !executor.IsSpouseInHomeRoutine(npc.Name)))
                return true;

            if (executor.IsIntentionalWarpAllowed(npc.Name))
                return true;

            return false;
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error en Prefix_HandleWarps: {ex}", LogLevel.Trace);
            return true;
        }
    }

    private static void Postfix_IsPlayerPresent(PathFindController __instance, Character? ___character, ref bool __result)
    {
        try
        {
            if (!Context.IsWorldReady || ___character is not NPC npc)
                return;

            var executor = NpcActionExecutor.Instance;
            if (executor != null && (executor.IsManagedCompanion(npc.Name) || executor.IsSpouseInHomeRoutine(npc.Name)))
            {
                // Al forzar __result = true, PathFindController.update salta la rama if (!NPCSchedule && !isPlayerPresent() ...)
                // evitando el salto forzado de posición (línea 154) y ejecutando el avance tile-por-tile con moveCharacter(time)
                // incluso cuando el jugador se encuentra fuera del mapa (AnimalHouse, Greenhouse, Farm, etc.).
                __result = true;
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"Error en Postfix_IsPlayerPresent: {ex}", LogLevel.Trace);
        }
    }

    internal static bool ToggleGate_Prefix(Fence __instance, bool open, bool is_toggling_counterpart, Farmer? who)
    {
        try
        {
            if (is_toggling_counterpart)
                return true;

            if (__instance == null || !__instance.isGate.Value)
                return true;

            // Bloquear llamadas redundantes que solo generan spam de sonido "doorClose"
            if (open && __instance.gatePosition.Value >= Fence.gateOpenedPosition)
                return false;
            if (!open && __instance.gatePosition.Value == 0)
                return false;

            // Si es llamado por NPC / pathfinder (who == null) para cerrar la puerta:
            // Proteger si la puerta fue gestionada recientemente o un acompañante/cónyuge está cruzándola
            if (who == null && !open)
            {
                if (Execution.NpcActionExecutor.Instance?.IsGateProtectedFromAutoClose(__instance) == true)
                {
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[PathfindingSafetyPatches] Error in ToggleGate_Prefix: {ex}", LogLevel.Trace);
        }
        return true;
    }

    [ThreadStatic]
    private static bool _isSubmergedDrawing;
    private static Texture2D? _swimShadowTexture;

    private static Texture2D? GetSwimShadowTexture()
    {
        if (_swimShadowTexture == null)
        {
            try
            {
                _swimShadowTexture = Game1.content.Load<Texture2D>(@"LooseSprites\swimShadow");
            }
            catch { }
        }
        return _swimShadowTexture;
    }

    public static bool IsTileInBathHousePoolWater(GameLocation? loc, Point tile)
    {
        if (loc == null) return false;
        bool isSpaPool = loc is BathHousePool || loc.NameOrUniqueName.Contains("BathHouse_Pool", StringComparison.OrdinalIgnoreCase);
        if (!isSpaPool)
        {
            return loc.isWaterTile(tile.X, tile.Y);
        }

        if (loc.isWaterTile(tile.X, tile.Y)) return true;
        if (loc.doesTileHaveProperty(tile.X, tile.Y, "Water", "Back") != null) return true;

        // Central pool rectangular bounds in BathHouse_Pool map (X: 8..21, Y: 19..29)
        if (tile.X >= 8 && tile.X <= 21 && tile.Y >= 19 && tile.Y <= 29) return true;

        return false;
    }

    internal static void DrawNpc_Prefix(NPC __instance, out (bool applied, Rectangle origRect, Vector2 origOffset) __state)
    {
        __state = (false, Rectangle.Empty, Vector2.Zero);
        if (_isSubmergedDrawing) return;

        try
        {
            if (__instance != null && __instance.Sprite != null)
            {
                var loc = __instance.currentLocation;
                if (loc != null)
                {
                    bool isSpaPool = loc is BathHousePool 
                                  || loc.NameOrUniqueName.Contains("BathHouse_Pool", StringComparison.OrdinalIgnoreCase);

                    Point tile = __instance.TilePoint;
                    if (isSpaPool && (IsTileInBathHousePoolWater(loc, tile) || __instance.swimming.Value))
                    {
                        _isSubmergedDrawing = true;
                        Rectangle origRect = __instance.Sprite.SourceRect;
                        Vector2 origOffset = __instance.drawOffset;
                        __state = (true, origRect, origOffset);

                        int croppedH = Math.Max(14, (int)(origRect.Height * 0.54f));
                        __instance.Sprite.SourceRect = new Rectangle(origRect.X, origRect.Y, origRect.Width, croppedH);

                        float bob = (float)Math.Sin(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 320.0) * 2.5f;
                        __instance.drawOffset = origOffset + new Vector2(0f, 26f + bob);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _isSubmergedDrawing = false;
            _monitor?.Log($"[PathfindingSafetyPatches] Error in DrawNpc_Prefix: {ex}", LogLevel.Trace);
        }
    }

    internal static void DrawNpc_Postfix(NPC __instance, SpriteBatch b, (bool applied, Rectangle origRect, Vector2 origOffset) __state)
    {
        try
        {
            if (__state.applied)
            {
                _isSubmergedDrawing = false;
                if (__instance?.Sprite != null)
                {
                    __instance.Sprite.SourceRect = __state.origRect;
                    __instance.drawOffset = __state.origOffset;
                }

                var swimTex = GetSwimShadowTexture();
                if (swimTex != null && __instance != null && b != null)
                {
                    Vector2 localPos = Game1.GlobalToLocal(Game1.viewport, new Vector2(__instance.Position.X + 32f, __instance.Position.Y + 48f));
                    float layerDepth = Math.Max(0f, (float)__instance.StandingPixel.Y / 10000f + 0.0001f);
                    int frameSize = Math.Min(16, swimTex.Height);
                    int frameCount = Math.Max(1, swimTex.Width / Math.Max(1, frameSize));
                    int frame = (int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 220.0) % frameCount;
                    Rectangle shadowSrc = new Rectangle(frame * frameSize, 0, frameSize, frameSize);
                    b.Draw(swimTex, localPos, shadowSrc, Color.White * 0.8f, 0f, new Vector2(frameSize / 2f, frameSize / 2f), 4f, SpriteEffects.None, layerDepth);
                }
            }
        }
        catch (Exception ex)
        {
            _isSubmergedDrawing = false;
            _monitor?.Log($"[PathfindingSafetyPatches] Error in DrawNpc_Postfix: {ex}", LogLevel.Trace);
        }
    }

    internal static void DrawNpc_Finalizer(NPC __instance, (bool applied, Rectangle origRect, Vector2 origOffset) __state)
    {
        if (!__state.applied) return;
        _isSubmergedDrawing = false;
        if (__instance?.Sprite != null)
        {
            __instance.Sprite.SourceRect = __state.origRect;
            __instance.drawOffset = __state.origOffset;
        }
    }
}
