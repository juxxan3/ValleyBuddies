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
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Systems;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    private void TickSpouseChores(Farmer owner, NPC spouse, SpouseHomeState state, ulong gameTick)
    {
        if (gameTick < state.NextActionTick) return;

        var farm = Game1.getFarm();
        if (farm == null) return;

        spouse.speed = 2;
        spouse.addedSpeed = 0;

        if (state.InsideAnimalBuilding && spouse.currentLocation is AnimalHouse currentAh)
        {
            StepNpcControllerIfLocationUnattended(spouse, currentAh);
        }

        bool mustExit = state.ShouldReturnHomeAfterExitingBuilding || Game1.timeOfDay >= 1730;

        // FASE 0: Salida real por la puerta a Farm (con 2 pasos, verificación <= 1.6f y cero teletransporte)
        if (state.ChoresPhase == 0)
        {
            // Si ya está en Farm, saltar directo a su rol correspondiente
            if (spouse.currentLocation is Farm)
            {
                state.ChoresPhase = (!owner.IsMainPlayer) ? 5 : 1;
                state.PhaseStartedTick = gameTick;
                return;
            }

            // Si no hay tareas en granja hoy (o llueve y no hay animales/invernadero), pasar directamente a vida doméstica interior
            if (!HasAnyFarmChores(farm, state, _config, owner))
            {
                ModEntry.ModMonitor?.Log($"[SpouseHome] No farm chores detected today for {spouse.Name}. Transitioning directly to IndoorLiving.", LogLevel.Debug);
                EnterIndoorLivingPhase(state, spouse, gameTick);
                return;
            }

            var house = spouse.currentLocation as FarmHouse ?? GetOwnerHouse(owner);
            if (house != null && spouse.currentLocation == house)
            {
                Point entry = house.getEntryLocation();
                Point doorApproach = new Point(entry.X, Math.Max(4, entry.Y - 2));
                Point doorThreshold = new Point(entry.X, entry.Y - 1);

                if (state.DoorExitSubStep == 0)
                {
                    var res = WalkPhysicallyTo(spouse, house, doorApproach, state, gameTick, 1.5f);
                    if (res == WalkResult.Walking) return;
                    state.DoorExitSubStep = 1;
                }

                if (state.DoorExitSubStep == 1)
                {
                    var res = WalkPhysicallyTo(spouse, house, doorThreshold, state, gameTick, 1.4f);
                    float distToThreshold = Vector2.Distance(spouse.TilePoint.ToVector2(), doorThreshold.ToVector2());

                    // CONDICIÓN OBLIGATORIA DE CRUCE: dist <= 1.6f o unreachable de emergencia tras 10s
                    if (distToThreshold > 1.6f && res == WalkResult.Walking)
                    {
                        return;
                    }

                    if (res == WalkResult.Unreachable && distToThreshold > 1.6f)
                    {
                        ModEntry.ModMonitor?.Log($"[SpouseHome] DoorOut fallback for {spouse.Name} after being blocked.", LogLevel.Debug);
                    }

                    // Cruce físico de la puerta
                    spouse.Halt();
                    spouse.controller = null;
                    spouse.faceDirection(2);

                    if (Game1.currentLocation == house)
                    {
                        Game1.playSound("doorClose");
                    }

                    Vector2 porchTile = GetSafeFarmPorchTile(farm, house, owner);
                    SafeWarpNpc(spouse, farm, porchTile);

                    if (Game1.currentLocation is Farm)
                    {
                        Game1.playSound("doorClose");
                    }

                    spouse.speed = 2;
                    spouse.addedSpeed = 0;
                    spouse.controller = null;
                    spouse.Halt();
                    spouse.faceDirection(2);

                    state.HasGoneOutToday = true;
                    ModEntry.ModMonitor?.Log($"[SpouseHome] Warped {spouse.Name} out to Farm at {porchTile} for FarmChores (distToThreshold={distToThreshold:F2})", LogLevel.Debug);

                    string headingOutMsg = I18n.Get("spouse.chores.heading_out", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
                    ModEntry.ConversationBridge.ShowCompanionBubble(spouse, headingOutMsg, 3500);
                }
            }

            if (!owner.IsMainPlayer)
            {
                state.ChoresPhase = 5; // Farmhand spouse starts with Livestock/Pet role
                ModEntry.ModMonitor?.Log($"[SpouseHome] Farmhand spouse {spouse.Name} heading to Livestock/Pet chores.", LogLevel.Debug);
            }
            else
            {
                state.ChoresPhase = 1; // Host spouse starts with Agricultural role
                ModEntry.ModMonitor?.Log($"[SpouseHome] Host spouse {spouse.Name} heading to Agricultural chores.", LogLevel.Debug);
            }
            state.NextActionTick = gameTick + 45; // 0.75s pausa natural
            state.PhaseStartedTick = gameTick;
            state.DoorExitSubStep = 0;
            return;
        }

        // FASE 1: Regar cultivos secos en la granja exterior
        if (state.ChoresPhase == 1)
        {
            state.HasStartedAgriculturalChoresToday = true;
            if (!Game1.IsRainingHere(farm) && state.WateredCount < 150)
            {
                Point dryCrop = FindUnwateredCropTile(farm, spouse.TilePoint, 35, state.SkippedChoreTilesToday, spouse.Name);
                if (dryCrop != Point.Zero)
                {
                    _claimedWaterTiles[dryCrop] = spouse.Name;
                    var res = WalkPhysicallyTo(spouse, farm, dryCrop, state, gameTick, 1.5f);
                    if (res == WalkResult.Walking) return;
                    if (res == WalkResult.Unreachable)
                    {
                        _claimedWaterTiles.Remove(dryCrop);
                        state.SkippedChoreTilesToday.Add(dryCrop);
                        return;
                    }

                    if (res == WalkResult.Arrived)
                    {
                        _claimedWaterTiles.Remove(dryCrop);
                        spouse.Halt();
                        spouse.faceGeneralDirection(dryCrop.ToVector2() * 64f);
                        ToolAnimationService.PlayWateringCanPour(farm, spouse, dryCrop);
                        if (farm.terrainFeatures.TryGetValue(dryCrop.ToVector2(), out var tf) && tf is HoeDirt dirt)
                        {
                            dirt.state.Value = HoeDirt.watered;
                        }
                        state.WateredCount++;
                        state.NextActionTick = gameTick + 40; // ~0.7s pausa calmada
                        return;
                    }
                }
            }

            ReleaseSpouseClaims(spouse.Name);
            state.ChoresPhase = 2; // Siguiente: Harvesting Farm
            state.PhaseStartedTick = gameTick;
            ModEntry.ModMonitor?.Log($"[SpouseHome] Chores phase transition for {spouse.Name} -> 2 (Harvesting Farm)", LogLevel.Debug);
            return;
        }

        // FASE 2: Cosechar cultivos maduros en la granja exterior (con escudo anti-colmenas y cosecha directa 1x)
        if (state.ChoresPhase == 2)
        {
            if (_config?.EnableSpouseAutoHarvest == true && state.HarvestedCount < 120)
            {
                Point ripeCrop = FindHarvestableCropTile(farm, spouse.TilePoint, 35, state.SkippedChoreTilesToday, spouse.Name);
                if (ripeCrop != Point.Zero)
                {
                    _claimedHarvestTiles[ripeCrop] = spouse.Name;
                    var res = WalkPhysicallyTo(spouse, farm, ripeCrop, state, gameTick, 1.5f);
                    if (res == WalkResult.Walking) return;
                    if (res == WalkResult.Unreachable)
                    {
                        _claimedHarvestTiles.Remove(ripeCrop);
                        state.SkippedChoreTilesToday.Add(ripeCrop);
                        return;
                    }

                    if (res == WalkResult.Arrived)
                    {
                        _claimedHarvestTiles.Remove(ripeCrop);
                        if (farm.terrainFeatures.TryGetValue(ripeCrop.ToVector2(), out var tf) && tf is HoeDirt dirt && dirt.crop != null)
                        {
                            spouse.Halt();
                            spouse.faceGeneralDirection(ripeCrop.ToVector2() * 64f);

                            if (TryHarvestSpouseCrop(dirt, ripeCrop, farm, state))
                            {
                                state.HarvestedCount++;
                                if (dirt.readyForHarvest())
                                    state.SkippedChoreTilesToday.Add(ripeCrop);
                                state.NextActionTick = gameTick + 40;
                                return;
                            }
                            state.SkippedChoreTilesToday.Add(ripeCrop);
                            return;
                        }
                    }
                }
            }

            ReleaseSpouseClaims(spouse.Name);
            state.ChoresPhase = 3; // Siguiente: Paso 3 — Invernadero (Greenhouse)
            state.PhaseStartedTick = gameTick;
            ModEntry.ModMonitor?.Log($"[SpouseHome] Chores phase transition for {spouse.Name} -> 3 (Greenhouse)", LogLevel.Debug);
            return;
        }

        // PASO 3: Invernadero (Greenhouse) — regar y cosechar adentro, saliendo físicamente a Farm
        if (state.ChoresPhase == 3)
        {
            bool isGhUnlocked = farm.greenhouseUnlocked.Value || Game1.MasterPlayer.hasOrWillReceiveMail("ccPantry");
            var gh = Game1.getLocationFromName("Greenhouse");
            Point ghDoor = GetGreenhouseDoor(farm);

            if (isGhUnlocked && gh is GameLocation validGh && ((!state.GreenhouseVisitedToday && !mustExit) || state.InsideGreenhouse))
            {
                bool ghNeedsWater = FindUnwateredCropTile(validGh, new Point(10, 15), 25, state.SkippedChoreTilesToday, spouse.Name) != Point.Zero;
                bool ghNeedsHarvest = (_config?.EnableSpouseAutoHarvest == true) && FindHarvestableCropTile(validGh, new Point(10, 15), 25, state.SkippedChoreTilesToday, spouse.Name) != Point.Zero;

                if (ghNeedsWater || ghNeedsHarvest || state.InsideGreenhouse)
                {
                    if (!state.InsideGreenhouse)
                    {
                        Point approach = new Point(ghDoor.X, ghDoor.Y + 1);
                        var res = WalkPhysicallyTo(spouse, farm, approach, state, gameTick, 1.8f);
                        if (res == WalkResult.Walking) return;
                        if (res == WalkResult.Unreachable)
                        {
                            state.GreenhouseVisitedToday = true;
                            state.ChoresPhase = 4;
                            state.PhaseStartedTick = gameTick;
                            return;
                        }

                        string ghMsg = I18n.Get("spouse.chores.heading_greenhouse", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
                        ModEntry.ConversationBridge.ShowCompanionBubble(spouse, ghMsg, 3500);

                        if (Game1.currentLocation is Farm) Game1.playSound("doorClose");
                        Point insideTile = new Point(10, 22);
                        if (validGh.warps != null && validGh.warps.Count > 0)
                        {
                            var w = validGh.warps.FirstOrDefault(wp => wp.TargetName.Equals("Farm", StringComparison.OrdinalIgnoreCase)) ?? validGh.warps[0];
                            insideTile = new Point(w.X, Math.Max(1, w.Y - 1));
                        }

                        SafeWarpNpc(spouse, validGh, insideTile.ToVector2());
                        if (Game1.currentLocation == validGh) Game1.playSound("doorClose");
                        spouse.speed = 2;
                        spouse.addedSpeed = 0;
                        spouse.Halt();

                        state.InsideGreenhouse = true;
                        state.ActiveSubBuildingDoorMap = "Greenhouse";
                        state.ActiveSubBuildingFarmDoorTile = approach;
                        state.ActiveSubBuildingInsideExitTile = insideTile;
                        state.GreenhouseVisitedToday = true;
                        state.PhaseStartedTick = gameTick;
                        state.NextActionTick = gameTick + 40;
                        return;
                    }
                    else
                    {
                        if (spouse.currentLocation == validGh)
                        {
                            StepNpcControllerIfLocationUnattended(spouse, validGh);

                            if (!mustExit && state.GreenhouseWateredCount < 150)
                            {
                                Point dryCrop = FindUnwateredCropTile(validGh, spouse.TilePoint, 30, state.SkippedChoreTilesToday, spouse.Name);
                                if (dryCrop != Point.Zero)
                                {
                                    var res = WalkPhysicallyTo(spouse, validGh, dryCrop, state, gameTick, 1.5f);
                                    if (res == WalkResult.Walking) return;
                                    if (res == WalkResult.Unreachable)
                                    {
                                        state.SkippedChoreTilesToday.Add(dryCrop);
                                        return;
                                    }

                                    if (res == WalkResult.Arrived)
                                    {
                                        spouse.Halt();
                                        spouse.faceGeneralDirection(dryCrop.ToVector2() * 64f);
                                        ToolAnimationService.PlayWateringCanPour(validGh, spouse, dryCrop);
                                        if (validGh.terrainFeatures.TryGetValue(dryCrop.ToVector2(), out var tf) && tf is HoeDirt dirt)
                                        {
                                            dirt.state.Value = HoeDirt.watered;
                                        }
                                        state.GreenhouseWateredCount++;
                                        state.NextActionTick = gameTick + 40;
                                        return;
                                    }
                                }
                            }

                            if (!mustExit && _config?.EnableSpouseAutoHarvest == true && state.GreenhouseHarvestedCount < 120)
                            {
                                Point ripeCrop = FindHarvestableCropTile(validGh, spouse.TilePoint, 30, state.SkippedChoreTilesToday, spouse.Name);
                                if (ripeCrop != Point.Zero)
                                {
                                    var res = WalkPhysicallyTo(spouse, validGh, ripeCrop, state, gameTick, 1.5f);
                                    if (res == WalkResult.Walking) return;
                                    if (res == WalkResult.Unreachable)
                                    {
                                        state.SkippedChoreTilesToday.Add(ripeCrop);
                                        return;
                                    }

                                    if (res == WalkResult.Arrived)
                                    {
                                        if (validGh.terrainFeatures.TryGetValue(ripeCrop.ToVector2(), out var tf) && tf is HoeDirt dirt && dirt.crop != null)
                                        {
                                            spouse.Halt();
                                            spouse.faceGeneralDirection(ripeCrop.ToVector2() * 64f);

                                            if (TryHarvestSpouseCrop(dirt, ripeCrop, validGh, state))
                                            {
                                                state.GreenhouseHarvestedCount++;
                                                if (dirt.readyForHarvest())
                                                    state.SkippedChoreTilesToday.Add(ripeCrop);
                                                state.NextActionTick = gameTick + 40;
                                                return;
                                            }
                                            state.SkippedChoreTilesToday.Add(ripeCrop);
                                            return;
                                        }
                                    }
                                }
                            }

                            // Caminar a la puerta interior de salida del Invernadero
                            Point insideExitTile = state.ActiveSubBuildingInsideExitTile != Point.Zero
                                ? state.ActiveSubBuildingInsideExitTile
                                : (validGh.warps != null && validGh.warps.Count > 0 ? new Point(validGh.warps[0].X, Math.Max(1, validGh.warps[0].Y - 1)) : new Point(10, 22));

                            var exitRes = WalkPhysicallyTo(spouse, validGh, insideExitTile, state, gameTick, 1.8f);
                            if (exitRes == WalkResult.Walking) return;

                            if (Game1.currentLocation == validGh) Game1.playSound("doorClose");
                            SafeWarpNpc(spouse, farm, new Vector2(ghDoor.X, ghDoor.Y + 1));
                            if (Game1.currentLocation is Farm) Game1.playSound("doorClose");
                            spouse.speed = 2;
                            spouse.addedSpeed = 0;
                            spouse.Halt();

                            state.InsideGreenhouse = false;
                            state.ActiveSubBuildingDoorMap = null;
                            state.NextActionTick = gameTick + 40;

                            state.ChoresPhase = 4; // Siguiente: Paso 4 — Depositar cosecha en cofres
                            state.PhaseStartedTick = gameTick;
                            return;
                        }
                        else
                        {
                            state.InsideGreenhouse = false;
                            state.ActiveSubBuildingDoorMap = null;
                        }
                    }
                }
            }

            if (state.InsideGreenhouse)
            {
                if (gh is GameLocation validGh2 && spouse.currentLocation == validGh2)
                {
                    Point insideExitTile = new Point(10, 22);
                    var exitRes = WalkPhysicallyTo(spouse, validGh2, insideExitTile, state, gameTick, 1.8f);
                    if (exitRes == WalkResult.Walking) return;
                    SafeWarpNpc(spouse, farm, new Vector2(ghDoor.X, ghDoor.Y + 1));
                }
                state.InsideGreenhouse = false;
                state.ActiveSubBuildingDoorMap = null;
            }

            state.ChoresPhase = 4; // Siguiente: Paso 4 — Depositar cosecha en cofres
            state.PhaseStartedTick = gameTick;
            ModEntry.ModMonitor?.Log($"[SpouseHome] Chores phase transition for {spouse.Name} -> 4 (Depositing Harvest in Chests)", LogLevel.Debug);
            return;
        }

        // PASO 4: Depositar TODA la cosecha y productos animales en Cofres de Farm
        if (state.ChoresPhase == 4)
        {
            static bool CanChestAcceptAnyItem(Chest c, IEnumerable<Item> items)
            {
                if (c.Items.Count < c.GetActualCapacity()) return true;
                foreach (var existing in c.Items)
                {
                    if (existing == null) return true;
                    foreach (var it in items)
                    {
                        if (it != null && existing.canStackWith(it) && existing.Stack < existing.maximumStackSize())
                            return true;
                    }
                }
                return false;
            }

            if (state.HarvestBasket.Count > 0 && spouse.currentLocation is Farm)
            {
                var chest = farm.Objects.Values.OfType<Chest>()
                    .Where(c => c.playerChest.Value && !c.fridge.Value && c.SpecialChestType == Chest.SpecialChestTypes.None && !state.SkippedChoreTilesToday.Contains(new Point((int)c.TileLocation.X, (int)c.TileLocation.Y)) && CanChestAcceptAnyItem(c, state.HarvestBasket))
                    .OrderBy(c => Vector2.DistanceSquared(c.TileLocation, spouse.TilePoint.ToVector2()))
                    .FirstOrDefault();

                if (chest != null)
                {
                    Point chestTile = new Point((int)chest.TileLocation.X, (int)chest.TileLocation.Y);
                    Point standTile = FindClosestWalkableCardinalNeighbor(farm, chestTile, spouse);

                    var res = WalkPhysicallyTo(spouse, farm, standTile, state, gameTick, 1.5f);
                    if (res == WalkResult.Walking) return;

                    float distToChest = Vector2.Distance(spouse.TilePoint.ToVector2(), chestTile.ToVector2());
                    if (res == WalkResult.Arrived && distToChest <= 2.2f)
                    {
                        spouse.Halt();
                        spouse.faceGeneralDirection(chestTile.ToVector2() * 64f + new Vector2(32f, 32f));
                        farm.playSound("openChest");
                        foreach (var item in state.HarvestBasket.ToList())
                        {
                            if (chest.addItem(item) == null)
                                state.HarvestBasket.Remove(item);
                        }
                        string depMsg = I18n.Get("spouse.chores.deposited_chest", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
                        ModEntry.ConversationBridge.ShowCompanionBubble(spouse, depMsg, 3000);
                        state.NextActionTick = gameTick + 45;

                        // Si todavía quedan ítems (este cofre se llenó), marcar cofre como lleno hoy y buscar otro si hay disponible
                        if (state.HarvestBasket.Count > 0)
                        {
                            state.SkippedChoreTilesToday.Add(chestTile);
                            bool anotherChestAvailable = farm.Objects.Values.OfType<Chest>()
                                .Any(c => c != chest && c.playerChest.Value && !c.fridge.Value && c.SpecialChestType == Chest.SpecialChestTypes.None && !state.SkippedChoreTilesToday.Contains(new Point((int)c.TileLocation.X, (int)c.TileLocation.Y)) && CanChestAcceptAnyItem(c, state.HarvestBasket));
                            if (anotherChestAvailable)
                            {
                                return; // Siguiente tick busca el otro cofre
                            }
                            else
                            {
                                state.HasPendingHarvestForOwner = true;
                            }
                        }
                    }
                    else
                    {
                        state.SkippedChoreTilesToday.Add(chestTile);
                        ModEntry.ModMonitor?.Log($"[SpouseHome] {spouse.Name} could not reach or fill chest at {chestTile} (res={res}, dist={distToChest:F2}). Retaining items in basket.", LogLevel.Debug);
                        state.HasPendingHarvestForOwner = true;
                    }
                }
                else
                {
                    // No hay cofres disponibles con espacio: retener para entrega al dueño
                    state.HasPendingHarvestForOwner = true;
                }
            }

            if (!owner.IsMainPlayer)
            {
                if (!state.HasStartedAgriculturalChoresToday && !mustExit && Game1.timeOfDay < 1600 && !state.GreenhouseVisitedToday)
                {
                    state.ChoresPhase = 1; // Farmhand spouse assists with agricultural role
                    state.PhaseStartedTick = gameTick;
                    ModEntry.ModMonitor?.Log($"[SpouseHome] Farmhand spouse {spouse.Name} deposited livestock products ({Game1.timeOfDay}). Assisting with agricultural chores.", LogLevel.Debug);
                    return;
                }
                state.ChoresPhase = 6; // Farmhand spouse finished chores -> return home
                state.PhaseStartedTick = gameTick;
                ModEntry.ModMonitor?.Log($"[SpouseHome] Farmhand spouse {spouse.Name} finished chores. Returning home.", LogLevel.Debug);
                return;
            }

            if (!state.LivestockCompletedToday && !mustExit && Game1.timeOfDay < 1600)
            {
                state.ChoresPhase = 5; // Host spouse assists with livestock
                state.PhaseStartedTick = gameTick;
                ModEntry.ModMonitor?.Log($"[SpouseHome] Host spouse {spouse.Name} finished agricultural chores early ({Game1.timeOfDay}). Assisting with livestock.", LogLevel.Debug);
                return;
            }
            else
            {
                state.ChoresPhase = 6; // Return home cleanly without phase loops
                state.PhaseStartedTick = gameTick;
                ModEntry.ModMonitor?.Log($"[SpouseHome] Host spouse {spouse.Name} transitioning to ReturnHome (LivestockCompleted={state.LivestockCompletedToday}, time={Game1.timeOfDay}).", LogLevel.Debug);
                return;
            }
        }

        // PASO 5: Ganado, productos animales y corrales (Farm exteriores + Coop/Barn interiores)
        if (state.ChoresPhase == 5)
        {
            // 5A: Mascotas, productos en el suelo y animales exteriores en Farm
            if (!state.InsideAnimalBuilding && !mustExit)
            {
                // Acariciar mascota (Pet: gato o perro)
                var unpettedPet = farm.characters.OfType<Pet>()
                    .Where(p => p != null && !state.PettedPetNamesToday.Contains(p.Name))
                    .OrderBy(p => Vector2.DistanceSquared(spouse.Tile, p.Tile))
                    .FirstOrDefault();
                if (unpettedPet != null)
                {
                    Point petTile = unpettedPet.TilePoint;
                    var res = WalkPhysicallyTo(spouse, farm, petTile, state, gameTick, 2.0f);
                    if (res == WalkResult.Walking) return;
                    if (res == WalkResult.Unreachable)
                    {
                        state.PettedPetNamesToday.Add(unpettedPet.Name);
                        return;
                    }

                    if (res == WalkResult.Arrived)
                    {
                        spouse.Halt();
                        spouse.faceGeneralDirection(unpettedPet.Position);
                        if (!spouse.IsEmoting) spouse.doEmote(20);
                        if (!unpettedPet.IsEmoting) unpettedPet.doEmote(20);

                        string petSound = unpettedPet.petType.Value.Equals("Cat", StringComparison.OrdinalIgnoreCase) ? "cat" : (unpettedPet.petType.Value.Equals("Dog", StringComparison.OrdinalIgnoreCase) ? "dog_bark" : "dwop");
                        try { unpettedPet.playContentSound(); } catch { farm.playSound(petSound); }
                        unpettedPet.friendshipTowardFarmer.Value = Math.Min(1000, unpettedPet.friendshipTowardFarmer.Value + 12);
                        unpettedPet.grantedFriendshipForPet.Value = true;
                        state.PettedPetNamesToday.Add(unpettedPet.Name);
                        state.NextActionTick = gameTick + 45;
                        return;
                    }
                }

                // Llenar cuenco de agua de la mascota si está seco
                if (!state.FilledPetBowlToday)
                {
                    var dryBowl = farm.buildings.OfType<PetBowl>().FirstOrDefault(b => b != null && !b.watered.Value);
                    if (dryBowl != null)
                    {
                        Point bowlTile = new Point(dryBowl.tileX.Value, dryBowl.tileY.Value + 1);
                        var res = WalkPhysicallyTo(spouse, farm, bowlTile, state, gameTick, 1.8f);
                        if (res == WalkResult.Walking) return;
                        if (res == WalkResult.Unreachable)
                        {
                            state.FilledPetBowlToday = true;
                            return;
                        }

                        if (res == WalkResult.Arrived)
                        {
                            spouse.Halt();
                            dryBowl.watered.Value = true;
                            farm.playSound("glug");
                            if (!spouse.IsEmoting) spouse.doEmote(20);
                            state.FilledPetBowlToday = true;
                            state.NextActionTick = gameTick + 45;
                            return;
                        }
                    }
                    else
                    {
                        state.FilledPetBowlToday = true;
                    }
                }

                // Recoger productos animales naturales en el suelo de Farm (trufas, huevos exteriores, lana, etc.)
                var extFloorProd = farm.Objects.Pairs
                    .Where(kv => FarmTaskHelper.IsAnimalProductObject(farm, kv.Value) && !state.SkippedChoreTilesToday.Contains(new Point((int)kv.Key.X, (int)kv.Key.Y)))
                    .OrderBy(kv => Vector2.DistanceSquared(spouse.Tile, kv.Key))
                    .FirstOrDefault();

                if (extFloorProd.Value != null)
                {
                    Point prodTile = new Point((int)extFloorProd.Key.X, (int)extFloorProd.Key.Y);
                    var res = WalkPhysicallyTo(spouse, farm, prodTile, state, gameTick, 1.8f);
                    if (res == WalkResult.Walking) return;
                    if (res == WalkResult.Unreachable)
                    {
                        state.SkippedChoreTilesToday.Add(prodTile);
                        return;
                    }

                    if (res == WalkResult.Arrived)
                    {
                        spouse.Halt();
                        spouse.faceGeneralDirection(extFloorProd.Key * 64f);
                        farm.Objects.Remove(extFloorProd.Key);
                        AddHarvestItem(state.HarvestBasket, extFloorProd.Value);
                        farm.playSound("coin");
                        if (!spouse.IsEmoting) spouse.doEmote(20);
                        state.NextActionTick = gameTick + 35;
                        return;
                    }
                }

                if (state.PettedCount < 100)
                {
                    var unpetted = farm.animals.Values
                        .Where(a => a != null && !a.wasPet.Value && !state.SkippedAnimalIdsToday.Contains(a.myID.Value) && (!_claimedAnimalIds.TryGetValue(a.myID.Value, out var claimer) || string.Equals(claimer, spouse.Name, StringComparison.OrdinalIgnoreCase)))
                        .OrderBy(a => Vector2.DistanceSquared(spouse.Tile, a.Tile))
                        .FirstOrDefault();
                    if (unpetted != null)
                    {
                        _claimedAnimalIds[unpetted.myID.Value] = spouse.Name;
                        Point animalTile = unpetted.TilePoint;
                        var res = WalkPhysicallyTo(spouse, farm, animalTile, state, gameTick, 1.8f);
                        if (res == WalkResult.Walking) return;
                        if (res == WalkResult.Unreachable)
                        {
                            _claimedAnimalIds.Remove(unpetted.myID.Value);
                            state.SkippedAnimalIdsToday.Add(unpetted.myID.Value);
                            return;
                        }

                        if (res == WalkResult.Arrived)
                        {
                            _claimedAnimalIds.Remove(unpetted.myID.Value);
                            spouse.Halt();
                            spouse.faceGeneralDirection(unpetted.Position);
                            if (!unpetted.wasPet.Value)
                            {
                                try { unpetted.pet(owner, is_auto_pet: false); } catch { }
                            }
                            if (!unpetted.wasPet.Value)
                            {
                                unpetted.wasPet.Value = true;
                                unpetted.friendshipTowardFarmer.Value = Math.Min(1000, unpetted.friendshipTowardFarmer.Value + 15);
                                unpetted.happiness.Value = (byte)Math.Min(255, (int)unpetted.happiness.Value + 30);
                            }
                            if (FarmTaskHelper.TryHarvestAnimalProduce(unpetted, farm) is Item extProd)
                            {
                                AddHarvestItem(state.HarvestBasket, extProd);
                            }
                            if (!unpetted.IsEmoting) unpetted.doEmote(20);
                            unpetted.makeSound();
                            if (!spouse.IsEmoting) spouse.doEmote(20);

                            state.PettedCount++;
                            state.NextActionTick = gameTick + 45;
                            return;
                        }
                    }
                }

                // Ordeñar o esquilar animales exteriores con producto listo
                var harvestableExtAnimal = farm.animals.Values
                    .Where(a => FarmTaskHelper.IsReadyForToolHarvest(a) && !state.SkippedAnimalIdsToday.Contains(a.myID.Value) && (!_claimedAnimalIds.TryGetValue(a.myID.Value, out var hClaim) || string.Equals(hClaim, spouse.Name, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(a => Vector2.DistanceSquared(spouse.Tile, a.Tile))
                    .FirstOrDefault();

                if (harvestableExtAnimal != null)
                {
                    _claimedAnimalIds[harvestableExtAnimal.myID.Value] = spouse.Name;
                    var res = WalkPhysicallyTo(spouse, farm, harvestableExtAnimal.TilePoint, state, gameTick, 1.8f);
                    if (res == WalkResult.Walking) return;
                    if (res == WalkResult.Unreachable)
                    {
                        _claimedAnimalIds.Remove(harvestableExtAnimal.myID.Value);
                        state.SkippedAnimalIdsToday.Add(harvestableExtAnimal.myID.Value);
                        return;
                    }

                    if (res == WalkResult.Arrived)
                    {
                        _claimedAnimalIds.Remove(harvestableExtAnimal.myID.Value);
                        spouse.Halt();
                        spouse.faceGeneralDirection(harvestableExtAnimal.Position);
                        if (FarmTaskHelper.TryHarvestAnimalProduce(harvestableExtAnimal, farm) is Item prod)
                        {
                            AddHarvestItem(state.HarvestBasket, prod);
                        }
                        if (!spouse.IsEmoting) spouse.doEmote(20);
                        state.NextActionTick = gameTick + 40;
                        return;
                    }
                }
            }

            // 5B: Corrales y establos interiores (Coop / Barn)
            var animalBuildings = farm.buildings
                .Where(b => b != null
                    && b.indoors.Value is AnimalHouse ah
                    && !state.VisitedAnimalHousesToday.Contains(ah.NameOrUniqueName)
                    && (!_claimedAnimalHouses.TryGetValue(ah.NameOrUniqueName, out var hClaimer) || string.Equals(hClaimer, spouse.Name, StringComparison.OrdinalIgnoreCase))
                    && (ah.Objects.Pairs.Any(kv => FarmTaskHelper.IsAnimalProductObject(ah, kv.Value) && !state.SkippedChoreTilesToday.Contains(new Point((int)kv.Key.X, (int)kv.Key.Y)))
                        || ah.animals.Values.Any(a => a != null
                            && ((!a.wasPet.Value && state.PettedCount < 100) || FarmTaskHelper.IsReadyForToolHarvest(a))
                            && !state.SkippedAnimalIdsToday.Contains(a.myID.Value)
                            && (!_claimedAnimalIds.TryGetValue(a.myID.Value, out var aClaimer) || string.Equals(aClaimer, spouse.Name, StringComparison.OrdinalIgnoreCase)))))
                .OrderBy(b => Vector2.DistanceSquared(spouse.Tile, new Vector2(b.getPointForHumanDoor().X, b.getPointForHumanDoor().Y)))
                .ToList();

            if (!state.InsideAnimalBuilding)
            {
                if (mustExit || animalBuildings.Count == 0)
                {
                    ReleaseSpouseClaims(spouse.Name);
                    state.LivestockCompletedToday = true;
                    if (state.HarvestBasket.Count > 0)
                    {
                        state.ChoresPhase = 4; // Depositar productos animales recogidos antes de ReturnHome
                        state.PhaseStartedTick = gameTick;
                        ModEntry.ModMonitor?.Log($"[SpouseHome] {spouse.Name} finished livestock chores with {state.HarvestBasket.Count} item stacks. Transitioning to Phase 4 (Chests).", LogLevel.Debug);
                        return;
                    }
                    if (!owner.IsMainPlayer && !state.HasStartedAgriculturalChoresToday && !mustExit && Game1.timeOfDay < 1600 && !state.GreenhouseVisitedToday)
                    {
                        state.ChoresPhase = 1; // Farmhand spouse assists with agricultural role (Watering -> Harvesting -> Greenhouse)
                        state.PhaseStartedTick = gameTick;
                        ModEntry.ModMonitor?.Log($"[SpouseHome] Farmhand spouse {spouse.Name} finished livestock early ({Game1.timeOfDay}). Assisting with agricultural chores.", LogLevel.Debug);
                        return;
                    }
                    else
                    {
                        state.ChoresPhase = 6; // Siguiente: Paso 6 — Regreso a casa
                        state.PhaseStartedTick = gameTick;
                        ModEntry.ModMonitor?.Log($"[SpouseHome] Chores phase transition for {spouse.Name} -> 6 (Coop/Barn completed or time limit)", LogLevel.Debug);
                        return;
                    }
                }

                var targetBuilding = animalBuildings[0];
                if (targetBuilding.indoors.Value is AnimalHouse targetAhInit)
                {
                    _claimedAnimalHouses[targetAhInit.NameOrUniqueName] = spouse.Name;
                }
                Point doorPt = targetBuilding.getPointForHumanDoor();
                Point approachTile = new Point(doorPt.X, doorPt.Y + 1);

                var res = WalkPhysicallyTo(spouse, farm, approachTile, state, gameTick, 1.8f);
                if (res == WalkResult.Walking) return;
                if (res == WalkResult.Unreachable)
                {
                    if (targetBuilding.indoors.Value is AnimalHouse targetAh)
                    {
                        _claimedAnimalHouses.Remove(targetAh.NameOrUniqueName);
                        state.VisitedAnimalHousesToday.Add(targetAh.NameOrUniqueName);
                    }
                    return;
                }

                // Entrar físicamente al corral/establo con spawn interior despejado
                if (Game1.currentLocation is Farm) Game1.playSound("doorClose");
                var ah = (AnimalHouse)targetBuilding.indoors.Value;
                Point insideExitTile = Point.Zero;
                if (ah.warps != null && ah.warps.Count > 0)
                {
                    var w = ah.warps.FirstOrDefault(wp => wp.TargetName.Equals("Farm", StringComparison.OrdinalIgnoreCase)) ?? ah.warps[0];
                    insideExitTile = new Point(w.X, Math.Max(1, w.Y - 1));
                }
                else
                {
                    insideExitTile = new Point(ah.map.Layers[0].LayerWidth / 2, ah.map.Layers[0].LayerHeight - 2);
                }
                Point insideTile = FarmTaskHelper.FindSafeAnimalHouseEntryTile(ah, spouse);

                SafeWarpNpc(spouse, ah, insideTile.ToVector2());
                if (Game1.currentLocation == ah) Game1.playSound("doorClose");
                spouse.speed = 2;
                spouse.addedSpeed = 0;
                spouse.Halt();

                state.VisitedAnimalHousesToday.Add(ah.NameOrUniqueName);
                state.InsideAnimalBuilding = true;
                state.ActiveSubBuildingDoorMap = ah.NameOrUniqueName;
                state.ActiveSubBuildingFarmDoorTile = approachTile;
                state.ActiveSubBuildingInsideExitTile = insideExitTile;
                state.CurrentBuildingId = targetBuilding.buildingType.Value;
                state.PhaseStartedTick = gameTick;
                state.NextActionTick = gameTick + 40;
                return;
            }
            else
            {
                var ah = spouse.currentLocation as AnimalHouse;
                if (ah != null)
                {
                    StepNpcControllerIfLocationUnattended(spouse, ah);

                    if (!mustExit)
                    {
                        // 1. Productos en el suelo primero (despejando el pasillo)
                        var floorProd = ah.Objects.Pairs
                            .Where(kv => FarmTaskHelper.IsAnimalProductObject(ah, kv.Value) && !state.SkippedChoreTilesToday.Contains(new Point((int)kv.Key.X, (int)kv.Key.Y)))
                            .OrderBy(kv => Vector2.DistanceSquared(spouse.Tile, kv.Key))
                            .FirstOrDefault();

                        if (floorProd.Value != null)
                        {
                            Point prodTile = new Point((int)floorProd.Key.X, (int)floorProd.Key.Y);
                            var res = WalkPhysicallyTo(spouse, ah, prodTile, state, gameTick, 2.2f);
                            if (res == WalkResult.Walking) return;
                            if (res == WalkResult.Unreachable)
                            {
                                state.SkippedChoreTilesToday.Add(prodTile);
                                return;
                            }

                            if (res == WalkResult.Arrived)
                            {
                                spouse.Halt();
                                spouse.faceGeneralDirection(floorProd.Key * 64f);
                                ah.Objects.Remove(floorProd.Key);
                                AddHarvestItem(state.HarvestBasket, floorProd.Value);
                                ah.playSound("coin");
                                if (!spouse.IsEmoting) spouse.doEmote(20);
                                state.NextActionTick = gameTick + 35;
                                return;
                            }
                        }

                        // 2. Acariciar animales sin acariciar ordenados por cercanía
                        var unpetted = ah.animals.Values
                            .Where(a => a != null && !a.wasPet.Value && !state.SkippedAnimalIdsToday.Contains(a.myID.Value) && (!_claimedAnimalIds.TryGetValue(a.myID.Value, out var claimer) || string.Equals(claimer, spouse.Name, StringComparison.OrdinalIgnoreCase)))
                            .OrderBy(a => Vector2.DistanceSquared(spouse.Tile, a.Tile))
                            .FirstOrDefault();
                        if (unpetted != null && state.PettedCount < 100)
                        {
                            _claimedAnimalIds[unpetted.myID.Value] = spouse.Name;
                            Point animalTile = unpetted.TilePoint;
                            var res = WalkPhysicallyTo(spouse, ah, animalTile, state, gameTick, 2.4f);
                            if (res == WalkResult.Walking) return;
                            if (res == WalkResult.Unreachable)
                            {
                                _claimedAnimalIds.Remove(unpetted.myID.Value);
                                state.SkippedAnimalIdsToday.Add(unpetted.myID.Value);
                                return;
                            }

                            if (res == WalkResult.Arrived)
                            {
                                _claimedAnimalIds.Remove(unpetted.myID.Value);
                                spouse.Halt();
                                spouse.faceGeneralDirection(unpetted.Position);
                                if (!unpetted.wasPet.Value)
                                {
                                    try { unpetted.pet(owner, is_auto_pet: false); } catch { }
                                    if (!unpetted.wasPet.Value)
                                    {
                                        unpetted.wasPet.Value = true;
                                        unpetted.friendshipTowardFarmer.Value = Math.Min(1000, unpetted.friendshipTowardFarmer.Value + 15);
                                        unpetted.happiness.Value = (byte)Math.Min(255, (int)unpetted.happiness.Value + 30);
                                        if (!unpetted.IsEmoting) unpetted.doEmote(20);
                                    }
                                }
                                if (FarmTaskHelper.TryHarvestAnimalProduce(unpetted, ah) is Item inProd)
                                {
                                    AddHarvestItem(state.HarvestBasket, inProd);
                                }
                                unpetted.makeSound();
                                if (!spouse.IsEmoting) spouse.doEmote(20);

                                state.PettedCount++;
                                state.NextActionTick = gameTick + 40;
                                return;
                            }
                        }

                        // 3. Ordeñar/esquilar animales restantes con producto listo
                        var harvestableInAh = ah.animals.Values
                            .Where(a => FarmTaskHelper.IsReadyForToolHarvest(a) && !state.SkippedAnimalIdsToday.Contains(a.myID.Value) && (!_claimedAnimalIds.TryGetValue(a.myID.Value, out var hc) || string.Equals(hc, spouse.Name, StringComparison.OrdinalIgnoreCase)))
                            .OrderBy(a => Vector2.DistanceSquared(spouse.Tile, a.Tile))
                            .FirstOrDefault();

                        if (harvestableInAh != null)
                        {
                            _claimedAnimalIds[harvestableInAh.myID.Value] = spouse.Name;
                            var res = WalkPhysicallyTo(spouse, ah, harvestableInAh.TilePoint, state, gameTick, 2.4f);
                            if (res == WalkResult.Walking) return;
                            if (res == WalkResult.Unreachable)
                            {
                                _claimedAnimalIds.Remove(harvestableInAh.myID.Value);
                                state.SkippedAnimalIdsToday.Add(harvestableInAh.myID.Value);
                                return;
                            }

                            if (res == WalkResult.Arrived)
                            {
                                _claimedAnimalIds.Remove(harvestableInAh.myID.Value);
                                spouse.Halt();
                                spouse.faceGeneralDirection(harvestableInAh.Position);
                                if (FarmTaskHelper.TryHarvestAnimalProduce(harvestableInAh, ah) is Item prod)
                                {
                                    AddHarvestItem(state.HarvestBasket, prod);
                                }
                                if (!spouse.IsEmoting) spouse.doEmote(20);
                                state.NextActionTick = gameTick + 40;
                                return;
                            }
                        }
                    }

                    // Caminar físicamente hacia la puerta interior de salida del corral
                    Point insideExitTile = state.ActiveSubBuildingInsideExitTile;
                    if (insideExitTile == Point.Zero)
                    {
                        if (ah.warps != null && ah.warps.Count > 0)
                        {
                            var w = ah.warps.FirstOrDefault(wp => wp.TargetName.Equals("Farm", StringComparison.OrdinalIgnoreCase)) ?? ah.warps[0];
                            insideExitTile = new Point(w.X, Math.Max(1, w.Y - 1));
                        }
                        else
                        {
                            insideExitTile = new Point(ah.map.Layers[0].LayerWidth / 2, ah.map.Layers[0].LayerHeight - 2);
                        }
                    }

                    var exitRes = WalkPhysicallyTo(spouse, ah, insideExitTile, state, gameTick, 1.8f);
                    if (exitRes == WalkResult.Walking) return;

                    // Salir del corral de vuelta a Farm exactamente por la puerta exterior correspondiente
                    if (Game1.currentLocation == ah) Game1.playSound("doorClose");
                    Point farmDoorApproach = state.ActiveSubBuildingFarmDoorTile != Point.Zero
                        ? state.ActiveSubBuildingFarmDoorTile
                        : (farm.buildings.FirstOrDefault(b => b.indoors.Value == ah) is Building bld ? new Point(bld.getPointForHumanDoor().X, bld.getPointForHumanDoor().Y + 1) : new Point(64, 15));

                    SafeWarpNpc(spouse, farm, farmDoorApproach.ToVector2());
                    if (Game1.currentLocation is Farm) Game1.playSound("doorClose");
                    spouse.speed = 2;
                    spouse.addedSpeed = 0;
                    spouse.Halt();

                    if (state.ActiveSubBuildingDoorMap != null)
                    {
                        _claimedAnimalHouses.Remove(state.ActiveSubBuildingDoorMap);
                    }
                    state.InsideAnimalBuilding = false;
                    state.ActiveSubBuildingDoorMap = null;
                    state.CurrentBuildingId = null;
                    state.NextActionTick = gameTick + 40;

                    if (mustExit)
                    {
                        ReleaseSpouseClaims(spouse.Name);
                        state.LivestockCompletedToday = true;
                        state.ChoresPhase = (state.HarvestBasket.Count > 0) ? 4 : 6;
                        state.PhaseStartedTick = gameTick;
                    }
                    else
                    {
                        // Al salir del corral, reevaluar 5A en Farm para animales o productos exteriores pendientes
                        return;
                    }
                    return;
                }
                else
                {
                    if (state.ActiveSubBuildingDoorMap != null)
                    {
                        _claimedAnimalHouses.Remove(state.ActiveSubBuildingDoorMap);
                    }
                    state.InsideAnimalBuilding = false;
                    state.ActiveSubBuildingDoorMap = null;
                    state.CurrentBuildingId = null;
                }
            }

            ReleaseSpouseClaims(spouse.Name);
            state.LivestockCompletedToday = true;
            state.ChoresPhase = (state.HarvestBasket.Count > 0) ? 4 : 6;
            state.PhaseStartedTick = gameTick;
            return;
        }

        // PASO 6: Regreso a casa caminando físicamente (CERO teletransporte a distancia)
        if (state.ChoresPhase == 6)
        {
            Point farmDoor = GetOwnerHomeExteriorDoorTile(farm, owner);
            Point frontDoorApproach = new Point(farmDoor.X, farmDoor.Y + 1);
            if (!farm.isTilePassable(new xTile.Dimensions.Location(frontDoorApproach.X, frontDoorApproach.Y), Game1.viewport))
            {
                frontDoorApproach = FindClosestWalkableCardinalNeighbor(farm, farmDoor, spouse);
            }

            var walkRes = WalkPhysicallyTo(spouse, farm, frontDoorApproach, state, gameTick, 1.8f);
            float distToDoor = Vector2.Distance(spouse.TilePoint.ToVector2(), farmDoor.ToVector2());

            if (distToDoor > 2.2f && walkRes == WalkResult.Walking)
            {
                return;
            }

            if (distToDoor <= 2.2f || walkRes == WalkResult.Arrived || walkRes == WalkResult.Unreachable)
            {
                if (walkRes == WalkResult.Unreachable && distToDoor > 2.2f)
                {
                    ModEntry.ModMonitor?.Log($"[SpouseHome] ReturnHome fallback for {spouse.Name} after being blocked in Farm.", LogLevel.Debug);
                }

                // Entrar limpiamente a FarmHouse (NUNCA en el nicho de la puerta, Y >= 4)
                var house = GetOwnerHouse(owner);
                if (house != null)
                {
                    if (Game1.currentLocation is Farm)
                    {
                        Game1.playSound("doorClose");
                    }
                    Point entryLoc = house.getEntryLocation();
                    Point safeInside = new Point(entryLoc.X, Math.Max(4, entryLoc.Y - 2));
                    if (!house.isTilePassable(new xTile.Dimensions.Location(safeInside.X, safeInside.Y), Game1.viewport))
                    {
                        safeInside = GetAdjacentWalkableTile(house, safeInside, entryLoc, spouse, 3);
                    }
                    if (safeInside.Y < 4) safeInside.Y = 4;

                    SafeWarpNpc(spouse, house, safeInside.ToVector2());

                    if (Game1.currentLocation == house)
                    {
                        Game1.playSound("doorClose");
                    }

                    spouse.speed = 2;
                    spouse.addedSpeed = 0;
                    spouse.controller = null;
                    spouse.Halt();
                    spouse.faceDirection(0);

                    string finishMsg = I18n.Get("spouse.chores.finished", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
                    ModEntry.ConversationBridge.ShowCompanionBubble(spouse, finishMsg, 3500);
                }

                // Transición limpia y completa a vida doméstica interior
                EnterIndoorLivingPhase(state, spouse, gameTick);
                return;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ENTREGA DE COSECHA RETENIDA AL REGRESAR EL JUGADOR (OnPlayerWarped)
    // ─────────────────────────────────────────────────────────────────────────

    private void TickDeliveringPendingHarvest(Farmer owner, NPC spouse, SpouseHomeState state, ulong gameTick)
    {
        if (state.HarvestBasket.Count == 0)
        {
            state.HasPendingHarvestForOwner = false;
            state.IsDeliveringPendingHarvest = false;
            return;
        }

        if (spouse.currentLocation != owner.currentLocation)
        {
            state.IsDeliveringPendingHarvest = false;
            return;
        }

        if (state.PhaseStartedTick == 0) state.PhaseStartedTick = gameTick;

        float dist = Vector2.Distance(spouse.TilePoint.ToVector2(), owner.TilePoint.ToVector2());
        if (dist <= 2.2f)
        {
            spouse.Halt();
            spouse.controller = null;
            spouse.faceGeneralDirection(owner.Position);
            if (!spouse.IsEmoting) spouse.doEmote(20);

            Game1.playSound("dwop");

            int v = Game1.random.Next(1, 3);
            string greetingMsg = I18n.Get($"spouse.chores.harvest_while_away.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
            ModEntry.ConversationBridge.ShowCompanionBubble(spouse, greetingMsg, 4000);

            DeliverHarvestBasketToPlayer(owner, spouse, state);
            return;
        }

        if (gameTick >= state.PhaseStartedTick + 1200)
        {
            state.IsDeliveringPendingHarvest = false;
            spouse.Halt();
            spouse.controller = null;
            return;
        }

        HumanNavigateToTarget(GetSpouseNavJob(spouse), spouse, spouse.currentLocation, owner.TilePoint, gameTick, arrivalChebyshev: 1, maxStuckTicks: 300, tickStep: 4);
    }

    private static bool TryHarvestSpouseCrop(HoeDirt dirt, Point tilePt, GameLocation loc, SpouseHomeState state)
    {
        var crop = dirt.crop;
        if (crop == null) return false;
        // Guard: crop must actually be ready (catches regrowth edge cases)
        if (!dirt.readyForHarvest()) return false;

        var cropData = crop.GetData();
        string harvestId = !string.IsNullOrWhiteSpace(cropData?.HarvestItemId)
            ? cropData.HarvestItemId
            : (crop.isWildSeedCrop() && !string.IsNullOrWhiteSpace(crop.whichForageCrop.Value)
                ? crop.whichForageCrop.Value
                : crop.indexOfHarvest.Value);
        if (string.IsNullOrWhiteSpace(harvestId))
            harvestId = crop.indexOfHarvest.Value;
        if (string.IsNullOrWhiteSpace(harvestId))
            return false;

        string qid = harvestId.StartsWith("(") ? harvestId : $"(O){harvestId}";
        var itemData = ItemRegistry.GetData(qid);
        if (itemData == null)
            return false;

        int minStack = cropData?.HarvestMinStack ?? 1;
        int maxStack = cropData?.HarvestMaxStack ?? 1;
        int stackToGive = minStack;
        if (maxStack > minStack)
            stackToGive = Game1.random.Next(minStack, maxStack + 1);
        if (cropData?.ExtraHarvestChance > 0 && Game1.random.NextDouble() < cropData.ExtraHarvestChance)
            stackToGive++;

        Item? harvestedItem = (crop.tintColor.Value != Color.White && itemData.Category == StardewValley.Object.flowersCategory)
            ? new StardewValley.Objects.ColoredObject(itemData.ItemId, stackToGive, crop.tintColor.Value)
            : ItemRegistry.Create(qid, stackToGive, allowNull: true);

        if (harvestedItem == null)
            return false;

        AddHarvestItem(state.HarvestBasket, harvestedItem);

        int regrowDays = cropData?.RegrowDays ?? -1;
        if (regrowDays > 0)
        {
            crop.fullyGrown.Value = true;
            crop.dayOfCurrentPhase.Value = regrowDays;
            crop.updateDrawMath(tilePt.ToVector2());
        }
        else
        {
            dirt.crop = null;
        }

        loc.playSound("harvest");
        return true;
    }

}
