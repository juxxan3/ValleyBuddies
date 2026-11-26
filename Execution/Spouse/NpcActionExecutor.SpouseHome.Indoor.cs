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
    public void MarkSpouseDateAccepted(NPC spouse, Farmer owner)
    {
        if (spouse == null || owner == null) return;
        ReleaseSpouseClaims(spouse.Name);
        NpcAnimationService.StopAnimationCleanly(spouse);
        spouse.Halt();
        spouse.controller = null;
        spouse.temporaryController = null;
        spouse.addedSpeed = 0;

        var state = GetOrCreateSpouseHomeState(spouse, owner);
        state.HasCompletedChoresToday = true;
        state.DidMorningChoresToday = true;
        state.ChoresPhase = 7;
        state.IsBreakfastBurned = false;
        state.PreparedBreakfastItem = null;
        state.CurrentWaypoint = null;
        state.CurrentNavTarget = null;
        state.Phase = SpouseHomePhase.Idle;

        StartFollowDirectly(spouse, owner);
    }

    public void EnterIndoorLivingPhase(SpouseHomeState state, NPC spouse, ulong gameTick)
    {
        if (spouse == null || state == null) return;

        ReleaseSpouseClaims(spouse.Name);

        spouse.Halt();
        spouse.controller = null;
        spouse.temporaryController = null;
        NpcAnimationService.StopAnimationCleanly(spouse);
        spouse.speed = 2;
        spouse.addedSpeed = 0;

        state.HasCompletedChoresToday = true;
        state.DidMorningChoresToday = true;
        state.ChoresPhase = 7; // Done
        state.Phase = SpouseHomePhase.IndoorLiving;
        state.DoorExitSubStep = 0;
        state.InsideAnimalBuilding = false;
        state.InsideGreenhouse = false;
        state.ActiveSubBuildingDoorMap = null;
        state.ActiveSubBuildingFarmDoorTile = Point.Zero;
        state.ActiveSubBuildingInsideExitTile = Point.Zero;
        state.ShouldReturnHomeAfterExitingBuilding = false;

        state.IndoorBlockIndex = 0; // Bloque 0: Lavar platos
        state.IndoorStage = IndoorBlockStage.WalkingToSpot;
        state.IndoorBlockEndTime = 0;
        state.IndoorBlockMinEndTick = 0;
        state.IndoorBlockBubbleShown = false;
        state.NextAmbientEffectTick = 0;
        state.RealStuckTicks = 0;
        state.StuckRetryCount = 0;
        state.CurrentWaypoint = null;
        state.CurrentNavTarget = null;
        state.PhaseStartedTick = gameTick;
        state.NextActionTick = gameTick + 30;

        ModEntry.ModMonitor?.Log($"[SpouseHome] Entered IndoorLiving for {spouse.Name} at {Game1.timeOfDay}", LogLevel.Debug);
    }

    /// <summary>
    /// v4.5.1: Reactiva inmediatamente la rutina autónoma de SpouseHome tras despedir al cónyuge.
    /// Limpia flags residuales de acompañante y sincroniza la fase según ubicación actual y hora.
    /// </summary>
    public void ResumeSpouseHomeAfterDismiss(NPC spouse, Farmer owner, ulong gameTick)
    {
        if (spouse == null || owner == null) return;

        // 1. Limpieza rigurosa de acompañante
        _companions.TryRemove(spouse.Name, out _);
        _active.Remove(spouse.Name);
        AbortCurrentJob(spouse.Name);

        spouse.Halt();
        spouse.controller = null;
        spouse.temporaryController = null;
        NpcAnimationService.StopAnimationCleanly(spouse);
        spouse.speed = 2;
        spouse.addedSpeed = 0;
        spouse.followSchedule = false;
        spouse.drawOffset = Vector2.Zero;
        spouse.swimming.Value = false;

        if (spouse.shouldWearIslandAttire.Value)
        {
            spouse.shouldWearIslandAttire.Value = false;
            IsRestoringNormalClothes = true;
            try
            {
                spouse.wearNormalClothes();
                spouse.ChooseAppearance();
            }
            finally
            {
                IsRestoringNormalClothes = false;
            }
        }

        var state = GetOrCreateSpouseHomeState(spouse, owner);
        state.RealStuckTicks = 0;
        state.StuckCounter = 0;
        state.StuckRetryCount = 0;
        state.CurrentWaypoint = null;
        state.CurrentNavTarget = null;
        state.NextActionTick = 0;

        var loc = spouse.currentLocation;
        bool isInsideHome = loc is FarmHouse || (loc != null && loc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase));
        bool isOutdoorsFarm = loc is Farm;

        spouse.faceGeneralDirection(owner.Position);
        spouse.showTextAboveHead(Utilities.I18n.Get("bubble.leave_farewell"), duration: 3800);
        spouse.doEmote(20);

        if (isInsideHome)
        {
            // Caso A — Está dentro de FarmHouse / Cabin
            if (loc is FarmHouse fh)
            {
                Point entry = fh.getEntryLocation();
                if (spouse.TilePoint.Y >= entry.Y - 1)
                {
                    Point safeInside = new Point(entry.X, Math.Max(4, entry.Y - 2));
                    if (IsBedPerimeterTileWalkable(fh, safeInside, spouse))
                    {
                        spouse.setTilePosition(safeInside);
                    }
                }
            }

            if (Game1.timeOfDay >= 2230 && !state.WokeUpByPlayerTonight)
            {
                state.Phase = SpouseHomePhase.GoingToBed;
                state.PhaseStartedTick = gameTick;
            }
            else if (Game1.timeOfDay >= 600 && Game1.timeOfDay < 700 && _config?.EnableSpouseMorningKiss != false && !state.HasDoneMorningKissToday)
            {
                state.Phase = SpouseHomePhase.MorningKiss;
                state.KissPhase = 0;
                state.PhaseStartedTick = gameTick;
            }
            else if (Game1.timeOfDay >= 700 && Game1.timeOfDay < 800 && _config?.EnableSpouseMorningBreakfast != false && !state.HasPreparedBreakfastToday)
            {
                state.Phase = SpouseHomePhase.CookingBreakfast;
                state.BreakfastPhase = 0;
                state.PhaseStartedTick = gameTick;
            }
            else if (Game1.timeOfDay >= 800 && Game1.timeOfDay < 1730 && _config?.EnableSpouseFarmChores != false && !state.HasCompletedChoresToday)
            {
                state.Phase = SpouseHomePhase.WalkingToDoorOut;
                state.ChoresPhase = 0;
                state.DoorExitSubStep = 0;
                state.PhaseStartedTick = gameTick;
            }
            else
            {
                state.Phase = SpouseHomePhase.IndoorLiving;
                EnterIndoorLivingPhase(state, spouse, gameTick);
            }
        }
        else if (isOutdoorsFarm)
        {
            // Caso B — Está afuera en Farm
            if (Game1.timeOfDay >= 800 && Game1.timeOfDay < 1730 && _config?.EnableSpouseFarmChores != false && !state.HasCompletedChoresToday)
            {
                state.Phase = SpouseHomePhase.FarmChores;
                state.ChoresPhase = 1; // Directo a quehaceres en Farm
                state.PhaseStartedTick = gameTick;
            }
            else
            {
                state.Phase = SpouseHomePhase.ReturningHome;
                state.ChoresPhase = 6; // Regreso caminando a FarmHouse
                state.PhaseStartedTick = gameTick;
            }
        }
        else
        {
            // Caso C — En otro mapa (p. ej. Town, Beach, Saloon, BathHouse_Pool)
            state.Phase = SpouseHomePhase.Idle;
            var house = GetOwnerHouse(owner);
            if (house != null)
            {
                DelayedAction.functionAfterDelay(() =>
                {
                    try
                    {
                        if (IsManagedCompanion(spouse.Name) || _companions.ContainsKey(spouse.Name) ||
                            (_active.TryGetValue(spouse.Name, out var activeAfterDismiss) && !activeAfterDismiss.Done))
                        {
                            return;
                        }

                        if (spouse.currentLocation != house)
                        {
                            Point safeTarget = FindBestWalkableIndoorStandTile(house, house.getKitchenStandingSpot(), 3);
                            if (safeTarget == Point.Zero || safeTarget.Y < 4) safeTarget = new Point(house.getKitchenStandingSpot().X, 5);

                            SafeWarpNpc(spouse, house, safeTarget.ToVector2());
                            try { house.playSound("wand"); } catch { }

                            if (Game1.timeOfDay >= 2230 && !state.WokeUpByPlayerTonight)
                            {
                                state.Phase = SpouseHomePhase.GoingToBed;
                                state.PhaseStartedTick = ModEntry.CurrentTick;
                            }
                            else
                            {
                                state.Phase = SpouseHomePhase.IndoorLiving;
                                EnterIndoorLivingPhase(state, spouse, ModEntry.CurrentTick);
                            }
                            ModEntry.ModMonitor?.Log($"[SpouseHome] Safely returned {spouse.Name} to FarmHouse after dismiss outside farm.", LogLevel.Debug);
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.ModMonitor?.Log($"[SpouseHome] Error returning {spouse.Name} to FarmHouse: {ex}", LogLevel.Warn);
                    }
                }, 3000);
            }
        }

        RecordMemory(spouse.Name, "I returned to my daily routine at the farm after spending time with my spouse.");
        ModEntry.ModMonitor?.Log($"[SpouseHome] Resumed {spouse.Name} after dismiss -> Phase={state.Phase} at {spouse.currentLocation?.Name} ({Game1.timeOfDay})", LogLevel.Debug);
    }

    /// <summary>
    /// Emite exactamente una burbuja de diálogo según el bloque interior activo.
    /// </summary>
    private static void EmitIndoorBlockBubble(Farmer owner, NPC spouse, int blockType)
    {
        int v = Game1.random.Next(1, 3);
        string bubbleMsg = string.Empty;
        switch (blockType)
        {
            case 0:
                bubbleMsg = I18n.Get($"spouse.home.dishes.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
                break;
            case 1:
                bubbleMsg = I18n.Get($"spouse.home.tidy.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
                break;
            case 2:
                string roomKey = ResolveSpouseRoomActivityKey(spouse);
                bubbleMsg = I18n.Get(roomKey, new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
                break;
            case 3:
                bubbleMsg = I18n.Get($"spouse.home.relax.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
                break;
        }
        if (!string.IsNullOrEmpty(bubbleMsg))
        {
            ModEntry.ConversationBridge.ShowCompanionBubble(spouse, bubbleMsg, 4000);
        }
    }

    /// <summary>
    /// Inicia animación temporal para el bloque interior (ej. penny_dishes o hobby nativo).
    /// </summary>
    private static void StartIndoorBlockAnimation(FarmHouse house, NPC spouse, int blockType, Point spot)
    {
        if (blockType == 0)
        {
            if (NpcAnimationService.TryPlayDishwashingAnimation(spouse))
                return;
        }
        else if (blockType == 2)
        {
            // Bloque 2: Habitación propia del cónyuge - hobby nativo
            if (NpcAnimationService.TryPlayCharacterHobbyAnimation(spouse))
                return;

            int emote = spouse.Name.ToLowerInvariant() switch
            {
                "penny" or "elliott" or "harvey" => 40,   // Ideas / lectura
                "sam" or "leah" => 56,                  // Música / arte
                "abigail" or "sebastian" => 52,         // Videojuegos / arcade
                "alex" or "emily" => 32,                // Feliz / energía
                "krobus" => 20,                         // Paz / afecto
                _ => 32
            };
            spouse.doEmote(emote);
        }
    }

    /// <summary>
    /// Efectos sutiles ambientales cada 5 segundos durante el bloque interior.
    /// </summary>
    private static void PlayIndoorBlockAmbientEffect(FarmHouse house, NPC spouse, int blockType, Point spot)
    {
        if (blockType == 0)
        {
            ToolAnimationService.PlayDishwashingBubbles(house, spot);
        }
        else if (blockType == 1)
        {
            ToolAnimationService.PlayTidyingSparkles(house, spot);
        }
        else if (blockType == 2)
        {
            if (!spouse.IsEmoting) spouse.doEmote(Game1.random.NextDouble() < 0.5 ? 56 : 20);
        }
        else if (blockType == 3)
        {
            if (!spouse.IsEmoting) spouse.doEmote(20);
        }
    }

    /// <summary>
    /// Determina si existen quehaceres pendientes en la granja hoy.
    /// </summary>
    /// <summary>
    /// Master update loop for Living Home Post-Marriage routine.
    /// Runs exclusively on Host.
    /// </summary>
    public void TickSpouseLivingHome(ulong gameTick)
    {
        if (!StardewModdingAPI.Context.IsMainPlayer) return;
        if (_config?.EnableSpouseLivingHome == false) return;
        if (Utility.isFestivalDay() || Game1.weddingToday) return;

        foreach (var farmer in Game1.getAllFarmers())
        {
            if (farmer == null || !farmer.isMarriedOrRoommates()) continue;

            string spouseName = farmer.spouse;
            if (string.IsNullOrWhiteSpace(spouseName)) continue;

            var spouse = Game1.getCharacterFromName(spouseName);
            if (spouse == null || spouse.currentLocation == null) continue;

            // Si el cónyuge está reclutado o ejecutando un trabajo activo, ceder 100% ANTES de tocar spouse.speed / spouse.addedSpeed!
            if (IsManagedCompanion(spouse.Name) || _companions.ContainsKey(spouse.Name) || (_active.TryGetValue(spouse.Name, out var activeJob) && !activeJob.Done))
            {
                ReleaseSpouseClaims(spouse.Name);
                continue;
            }

            // Ritmo sereno y natural permanente (speed = 2, addedSpeed = 0) mientras está en rutina de hogar
            spouse.speed = 2;
            spouse.addedSpeed = 0;

            var state = GetOrCreateSpouseHomeState(spouse, farmer);

            // Step controller if location has no players present (unattended FarmHouse or Cabin)
            if (spouse.currentLocation != null && spouse.currentLocation.farmers.Count == 0)
            {
                StepNpcControllerIfLocationUnattended(spouse, spouse.currentLocation);
            }

            // Guardia anti-vacío: impedir que el cónyuge quede atrapado sobre la pared norte (Y < 4) en FarmHouse
            if (spouse.currentLocation is FarmHouse fh && !spouse.isSleeping.Value && spouse.TilePoint.Y < 4)
            {
                if (!IsNpcInsideBedFootprint(spouse, fh, out _))
                {
                    Point safeStand = FindBestWalkableIndoorStandTile(fh, new Point(spouse.TilePoint.X, 5), 4);
                    if (safeStand != Point.Zero && safeStand.Y >= 4)
                    {
                        spouse.Position = new Vector2(safeStand.X * 64f, safeStand.Y * 64f);
                        spouse.Halt();
                        spouse.controller = null;
                        state.CurrentNavTarget = null;
                        state.CurrentWaypoint = null;
                        ModEntry.ModMonitor?.Log($"[SpouseHome] Rescued {spouse.Name} from north wall boundary Y={spouse.TilePoint.Y} to safe indoor stand tile {safeStand}", LogLevel.Debug);
                    }
                }
            }

            // Detección precisa de salida programada al pueblo en spouse.Schedule (1 vez por mañana)
            if (!state.CheckedTownOutingToday && gameTick >= _dayStartedTick + 20)
            {
                state.CheckedTownOutingToday = true;
                if (spouse.Schedule != null && spouse.Schedule.Count > 0)
                {
                    int earliestTime = int.MaxValue;
                    string targetLoc = "";
                    foreach (var kvp in spouse.Schedule)
                    {
                        var entry = kvp.Value;
                        if (entry != null && !string.IsNullOrWhiteSpace(entry.targetLocationName))
                        {
                            string locName = entry.targetLocationName;
                            if (!locName.Equals("FarmHouse", StringComparison.OrdinalIgnoreCase)
                                && !locName.Equals("Farm", StringComparison.OrdinalIgnoreCase)
                                && !locName.Contains("Cabin", StringComparison.OrdinalIgnoreCase))
                            {
                                if (kvp.Key < earliestTime)
                                {
                                    earliestTime = kvp.Key;
                                    targetLoc = locName;
                                }
                            }
                        }
                    }

                    if (earliestTime != int.MaxValue)
                    {
                        state.HasTownOutingToday = true;
                        state.TownOutingStartTime = earliestTime;
                        ModEntry.ModMonitor?.Log($"[SpouseHome] {spouse.Name} has vanilla town outing today at {earliestTime} -> {targetLoc}", LogLevel.Debug);
                    }
                }
            }

            // Bed slide out when waking up / extracted from bed
            if (TickBedSlideOut(spouse, null, state))
            {
                continue;
            }

            // Sub-handler: Entregar cosecha retenida si el jugador dueño entra a la misma localización
            if (state.IsDeliveringPendingHarvest || (state.HasPendingHarvestForOwner && state.PendingHarvestItems.Count > 0 && spouse.currentLocation == farmer.currentLocation))
            {
                TickDeliveringPendingHarvest(farmer, spouse, state, gameTick);
                continue;
            }

            // FASE 1: Mañana cariñosa (06:00 - 07:00 AM)
            if (_config?.EnableSpouseMorningKiss != false && Game1.timeOfDay >= 600 && Game1.timeOfDay < 700)
            {
                TickSpouseMorningKiss(farmer, spouse, state, gameTick);
                continue;
            }

            // FASE 2: Desayuno casero + Beso de despedida + Espera relajada (07:00 - 08:00 AM, o hasta 08:30)
            bool breakfastInProgress = state.PreparedBreakfastItem != null || state.BreakfastPhase == 1 || state.BreakfastPhase == 2;
            if (_config?.EnableSpouseMorningBreakfast != false && Game1.timeOfDay >= 700 && (Game1.timeOfDay < 800 || (Game1.timeOfDay < 830 && breakfastInProgress)))
            {
                TickSpouseBreakfast(farmer, spouse, state, gameTick);
                continue;
            }

            // Bloque 2.1: Cita Sorpresa de Cónyuge tras desayuno (~07:20 - 09:00 AM, expira limpiamente a las 09:00)
            if (Game1.timeOfDay >= 900)
            {
                ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.ExpirePendingSpouseDateIfTimedOut(farmer.UniqueMultiplayerID);
            }
            else if (Game1.timeOfDay >= 720 &&
                     state.HasPreparedBreakfastToday &&
                     !state.HasTownOutingToday &&
                     !state.IsBreakfastBurned &&
                     state.PreparedBreakfastItem == null &&
                     ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.HasPendingSpouseDateFor(farmer.UniqueMultiplayerID, spouse.Name))
            {
                var homeLoc = spouse.currentLocation as FarmHouse ?? GetOwnerHouse(farmer);
                if (homeLoc != null && spouse.currentLocation == homeLoc && farmer.currentLocation == homeLoc)
                {
                    if (ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.TryTriggerPendingSpouseDate(spouse, farmer, homeLoc))
                    {
                        continue;
                    }
                }
            }

            // Si venció el horario del desayuno (>= 08:30) y aún queda un plato preparado sin entregar, guardarlo en la nevera
            if (state.PreparedBreakfastItem != null)
            {
                var house = spouse.currentLocation as FarmHouse ?? Utility.getHomeOfFarmer(farmer);
                var fridge = house?.GetFridge();
                Item? leftover = fridge != null ? fridge.addItem(state.PreparedBreakfastItem) : state.PreparedBreakfastItem;
                if (leftover != null)
                {
                    if (farmer.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
                        GiveItemToFarmerDirectly(farmer, leftover);
                    else
                        _multiplayerSync?.SendSpouseGift(farmer.UniqueMultiplayerID, leftover.QualifiedItemId, leftover.Stack, spouse.Name);
                }
                state.PreparedBreakfastItem = null;
                state.HasPreparedBreakfastToday = true;
            }

            // Días de salida al pueblo (vanilla town outings): ceder control armoniosamente
            if (state.HasTownOutingToday)
            {
                if (!state.AnnouncedWorkDayToday && Game1.timeOfDay >= 750 && state.PreparedBreakfastItem == null)
                {
                    state.AnnouncedWorkDayToday = true;
                    string pName = farmer.displayName ?? farmer.Name;
                    string notice = Utilities.I18n.Get("spouse.workday.notice", new { playerName = pName });
                    spouse.showTextAboveHead(notice, duration: 4000);
                    spouse.doEmote(32);
                    DelayedAction.functionAfterDelay(() =>
                    {
                        try
                        {
                            if (spouse.currentLocation != null)
                            {
                                string noChores = Utilities.I18n.Get("spouse.workday.no_chores", new { playerName = pName });
                                spouse.showTextAboveHead(noChores, duration: 4000);
                                spouse.doEmote(20);
                            }
                        }
                        catch { }
                    }, 4200);
                }

                // Cuando termina el desayuno (>= 800) y tiene salida programada hoy, ceder inmediatamente a su horario vanilla
                if (!state.YieldedToTownScheduleToday && Game1.timeOfDay >= 800 && state.PreparedBreakfastItem == null)
                {
                    state.YieldedToTownScheduleToday = true;
                    state.HasCompletedChoresToday = true;
                    spouse.followSchedule = true;
                    spouse.checkSchedule(Game1.timeOfDay);
                    ModEntry.ModMonitor?.Log($"[SpouseHome] Yielding {spouse.Name} to vanilla town schedule at {Game1.timeOfDay} (Outing at {state.TownOutingStartTime})", LogLevel.Debug);
                    continue;
                }

                if (state.YieldedToTownScheduleToday)
                {
                    var house = GetOwnerHouse(farmer);
                    if (spouse.currentLocation == house && Game1.timeOfDay >= 1700)
                    {
                        state.YieldedToTownScheduleToday = false;
                        EnterIndoorLivingPhase(state, spouse, gameTick);
                        ModEntry.ModMonitor?.Log($"[SpouseHome] {spouse.Name} returned home from town outing. Resuming IndoorLiving at {Game1.timeOfDay}", LogLevel.Debug);
                    }
                    else
                    {
                        // Continúa cumpliendo su horario vanilla en el pueblo
                        continue;
                    }
                }
            }

            // FASE 3: Salida a granja y quehaceres (08:00 - 17:30 PM)
            // INVARIANTE: Si está dentro de un corral/establo o invernadero, o regresando a pie a casa (ChoresPhase == 6), DEBE continuar en Chores hasta entrar físicamente a FarmHouse
            bool isInSubBuilding = spouse.currentLocation is AnimalHouse || (spouse.currentLocation != null && spouse.currentLocation.Name.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase));
            bool isReturningHome = state.ChoresPhase == 6 && spouse.currentLocation is Farm;
            if (_config?.EnableSpouseFarmChores != false && (!state.HasCompletedChoresToday || isInSubBuilding || isReturningHome) && (Game1.timeOfDay < 1730 || isInSubBuilding || isReturningHome))
            {
                if (Game1.timeOfDay >= 1730)
                {
                    state.ShouldReturnHomeAfterExitingBuilding = true;
                }
                state.Phase = (state.ChoresPhase == 0) ? SpouseHomePhase.WalkingToDoorOut : (state.ChoresPhase == 6 ? SpouseHomePhase.ReturningHome : SpouseHomePhase.FarmChores);
                TickSpouseChores(farmer, spouse, state, gameTick);
                continue;
            }

            // FASE 4: Vida doméstica interior (08:00 - 22:30 si completó quehaceres/lluvia, o después de 17:30 PM)
            if (Game1.timeOfDay >= 800 && Game1.timeOfDay < 2230)
            {
                if (isInSubBuilding)
                {
                    state.ShouldReturnHomeAfterExitingBuilding = true;
                    TickSpouseChores(farmer, spouse, state, gameTick);
                    continue;
                }

                if (state.HasCompletedChoresToday || _config?.EnableSpouseFarmChores == false || Game1.timeOfDay >= 1730)
                {
                    TickSpouseIndoorLiving(farmer, spouse, state, gameTick);
                    continue;
                }
            }

            // FASE 5: Acostarse en la cama doble a las 22:30 / 23:00
            if (Game1.timeOfDay >= 2230)
            {
                TickSpouseBed(farmer, spouse, state, gameTick);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FASE 1: Mañana cariñosa (06:00 - 07:00 AM)
    // ─────────────────────────────────────────────────────────────────────────

    private void TickSpouseMorningKiss(Farmer owner, NPC spouse, SpouseHomeState state, ulong gameTick)
    {
        if (gameTick < _dayStartedTick + 50) return; // Wait 0.8s for fade-in

        var house = spouse.currentLocation as FarmHouse;
        if (house == null && !spouse.currentLocation.NameOrUniqueName.Contains("Cabin")) return;
        var loc = spouse.currentLocation;

        spouse.speed = 2;
        spouse.addedSpeed = 0;

        // Despertar y extraer de la cama si está durmiendo o dentro de la huella de la cama
        if (IsNpcInsideBedFootprint(spouse, loc, out _) || spouse.isSleeping.Value)
        {
            WakeAndExtractNpcFromBed(spouse, loc, null, owner);
            return;
        }

        if (!state.HasDoneMorningKissToday)
        {
            // Si el jugador dueño no está en la misma casa, avanzar limpiamente sin congelar
            if (owner.currentLocation != loc)
            {
                state.HasDoneMorningKissToday = true;
                state.KissPhase = 1;
                state.PhaseStartedTick = gameTick;
                spouse.Halt();
                spouse.controller = null;
                return;
            }

            if (state.PhaseStartedTick == 0)
            {
                state.PhaseStartedTick = gameTick;
            }

            Point playerTile = owner.TilePoint;
            Point spouseTile = spouse.TilePoint;
            float dist = Vector2.Distance(spouseTile.ToVector2(), playerTile.ToVector2());

            // Watchdog generoso de 30 s (1800 ticks) para cruzar toda la casa
            if (gameTick >= state.PhaseStartedTick + 1800)
            {
                state.HasDoneMorningKissToday = true;
                state.KissPhase = 1;
                state.PhaseStartedTick = gameTick;
                spouse.Halt();
                spouse.controller = null;
                ModEntry.ModMonitor?.Log($"[SpouseHome] Morning kiss watchdog reached for {spouse.Name}, proceeding to kitchen.", LogLevel.Debug);
                return;
            }

            if (dist > 1.85f)
            {
                Point targetStand = GetAdjacentWalkableTile(loc, playerTile, spouseTile, spouse, maxRadius: 2);
                HumanNavigateToTarget(GetSpouseNavJob(spouse), spouse, loc, targetStand, gameTick, arrivalChebyshev: 1, maxStuckTicks: 300, tickStep: 4);
                return;
            }

            // Llegó junto al jugador -> Ejecutar beso
            spouse.controller = null;
            spouse.Halt();
            spouse.faceGeneralDirection(owner.Position);

            var friendship = owner.friendshipData.TryGetValue(spouse.Name, out var f) ? f : null;
            bool isRoommate = friendship != null && friendship.IsRoommate();

            // Affection burst kiss animation
            ToolAnimationService.PlayAffectionBurst(loc, spouse, owner, isKiss: !isRoommate, playPlayerLean: !owner.isMoving());

            // Saludo cariñoso matutino
            string greetingKey = ResolveSpouseMorningGreetingKey(spouse, isRoommate);
            string greetingText = I18n.Get(greetingKey, new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
            ModEntry.ConversationBridge.ShowCompanionBubble(spouse, greetingText, 4500);

            state.HasDoneMorningKissToday = true;
            state.KissPhase = 1; // Transición: Caminar hacia la cocina
            state.PhaseStartedTick = gameTick;
            state.NextActionTick = gameTick + 150; // 2.5s pausa afectiva
            ModEntry.ModMonitor?.Log($"[SpouseHome] Morning kiss for {spouse.Name} completed successfully.", LogLevel.Debug);
            return;
        }

        if (gameTick < state.NextActionTick) return;

        // Sub-paso post-beso (06:00 - 07:00): Caminar con calma a la cocina y esperar hasta las 07:00 AM
        if (house != null)
        {
            Point kitchenSpot = house.getKitchenStandingSpot();
            if (kitchenSpot == Point.Zero || !house.isTilePassable(new xTile.Dimensions.Location(kitchenSpot.X, kitchenSpot.Y), Game1.viewport))
            {
                Point fallback = new Point(kitchenSpot != Point.Zero ? kitchenSpot.X : 7, kitchenSpot != Point.Zero ? kitchenSpot.Y + 1 : 24);
                kitchenSpot = GetAdjacentWalkableTile(house, fallback, spouse.TilePoint, spouse, maxRadius: 2);
            }
            if (kitchenSpot.Y < 4) kitchenSpot.Y = 4;

            if (state.KissPhase == 1)
            {
                float distToKitchen = Vector2.Distance(spouse.TilePoint.ToVector2(), kitchenSpot.ToVector2());
                if (distToKitchen > 1.5f && (state.PhaseStartedTick == 0 || gameTick < state.PhaseStartedTick + 900))
                {
                    HumanNavigateToTarget(GetSpouseNavJob(spouse), spouse, house, kitchenSpot, gameTick, arrivalChebyshev: 1, maxStuckTicks: 300, tickStep: 4);
                    return;
                }

                // Llegó a la cocina
                spouse.Halt();
                spouse.controller = null;
                spouse.faceDirection(0); // Mirar a los mostradores
                state.KissPhase = 2;
                return;
            }

            if (state.KissPhase == 2)
            {
                spouse.Halt();
                spouse.controller = null;
                if (spouse.FacingDirection != 0) spouse.faceDirection(0);
            }
        }
    }

    private static string ResolveSpouseMorningGreetingKey(NPC spouse, bool isRoommate)
    {
        if (isRoommate)
        {
            int idx = Game1.random.Next(1, 4);
            return $"spouse.morning.roommate.v{idx}";
        }

        string name = spouse.Name;
        int v = Game1.random.Next(1, 5);

        // 1. Sweet / Homey
        if (name is "Penny" or "Harvey" or "Gus" or "Sophia" or "Claire" or "Evelyn")
            return $"spouse.morning.sweet.v{v}";

        // 2. Playful / Adventurous
        if (name is "Abigail" or "Sam" or "Alex" or "Haley" or "Lance" or "Jio")
            return $"spouse.morning.playful.v{v}";

        // 3. Poetic / Dreamer
        if (name is "Elliott" or "Leah" or "Emily" or "Wizard" or "Magnus")
            return $"spouse.morning.poetic.v{v}";

        // 4. Shy / Reserved but affectionate
        if (name is "Sebastian" or "Shane" or "Maru" or "Victor" or "Olivia")
            return $"spouse.morning.shy.v{v}";

        // Default sweet
        return $"spouse.morning.sweet.v{v}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FASE 2: Desayuno casero aleatorio + Beso de despedida + TV (07:00 - 08:00 AM)
    // ─────────────────────────────────────────────────────────────────────────

    private const string SeenRecipesKey = "Juxxan.ValleyBuddies/SeenCookingRecipes";

    private void PrepareSpouseBreakfast(Farmer owner, NPC spouse, SpouseHomeState state)
    {
        try
        {
            Dictionary<string, string>? cookingData = null;
            try
            {
                cookingData = Game1.content.Load<Dictionary<string, string>>("Data/CookingRecipes");
            }
            catch (Exception ex)
            {
                ModEntry.ModMonitor?.Log($"[SpouseHome] Failed loading Data/CookingRecipes: {ex.Message}", LogLevel.Warn);
            }

            List<string> knownRecipes = owner.cookingRecipes != null ? owner.cookingRecipes.Keys.ToList() : new List<string>();

            // Check seen recipes in modData
            bool hasSeenKey = owner.modData.ContainsKey(SeenRecipesKey);
            HashSet<string> seenRecipes = new(StringComparer.OrdinalIgnoreCase);

            if (hasSeenKey)
            {
                string stored = owner.modData[SeenRecipesKey] ?? string.Empty;
                foreach (var r in stored.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    seenRecipes.Add(r.Trim());
                }
            }
            else
            {
                // First time: initialize with all current known recipes so starting recipes are not treated as newly learned
                foreach (var r in knownRecipes)
                {
                    seenRecipes.Add(r);
                }
                owner.modData[SeenRecipesKey] = string.Join("|", seenRecipes);
            }

            // Identify newly learned recipes (known by player but not in seenRecipes)
            List<string> newlyLearned = new();
            if (hasSeenKey)
            {
                foreach (var r in knownRecipes)
                {
                    if (!seenRecipes.Contains(r))
                    {
                        newlyLearned.Add(r);
                    }
                }
            }

            Item? chosenDish = null;
            bool wasNewlyLearned = false;

            Item? ResolveRecipeItem(string recipeName)
            {
                if (cookingData != null && cookingData.TryGetValue(recipeName, out var rawData))
                {
                    var parts = rawData.Split('/');
                    if (parts.Length > 2)
                    {
                        string outToken = parts[2].Split(' ')[0];
                        string qid = ItemRegistry.QualifyItemId(outToken) ?? ("(O)" + outToken);
                        return SafeItemResolver.TryCreateValidItem(qid);
                    }
                }
                return null;
            }

            // If player recently learned a new recipe, surprise them with it!
            if (newlyLearned.Count > 0)
            {
                var shuffledNew = newlyLearned.OrderBy(_ => Game1.random.Next()).ToList();
                foreach (var recipeName in shuffledNew)
                {
                    var item = ResolveRecipeItem(recipeName);
                    if (item != null)
                    {
                        chosenDish = item;
                        wasNewlyLearned = true;
                        seenRecipes.Add(recipeName);
                        owner.modData[SeenRecipesKey] = string.Join("|", seenRecipes);
                        break;
                    }
                }
            }

            // If no newly learned recipe, pick randomly from known recipes (avoiding yesterday's dish)
            if (chosenDish == null && knownRecipes.Count > 0)
            {
                var candidatePool = knownRecipes.OrderBy(_ => Game1.random.Next()).ToList();
                foreach (var recipeName in candidatePool)
                {
                    var item = ResolveRecipeItem(recipeName);
                    if (item != null && item.QualifiedItemId != state.LastBreakfastItemId)
                    {
                        chosenDish = item;
                        break;
                    }
                }

                if (chosenDish == null)
                {
                    foreach (var recipeName in candidatePool)
                    {
                        var item = ResolveRecipeItem(recipeName);
                        if (item != null)
                        {
                            chosenDish = item;
                            break;
                        }
                    }
                }
            }

            // Fallback safe basics pool: Fried Egg (O)194, Coffee (O)395, Green Tea (O)614, Omelet (O)195
            if (chosenDish == null)
            {
                string[] safeBasics = { "(O)194", "(O)395", "(O)614", "(O)195" };
                var eligibleBasics = safeBasics.Where(id => id != state.LastBreakfastItemId).ToArray();
                if (eligibleBasics.Length == 0) eligibleBasics = safeBasics;

                string chosenId = eligibleBasics[Game1.random.Next(eligibleBasics.Length)];
                chosenDish = SafeItemResolver.TryCreateValidItem(chosenId);
            }

            // Safety net: never allow null or ErrorItem
            if (chosenDish == null || chosenDish.Name.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                chosenDish = SafeItemResolver.TryCreateValidItem("(O)194") ?? ItemRegistry.Create("(O)194", 1);
            }

            state.BreakfastWasNewlyLearned = wasNewlyLearned;
            state.LastBreakfastItemId = chosenDish.QualifiedItemId;
            state.PreparedBreakfastItem = chosenDish;
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[SpouseHome] Error preparing breakfast: {ex}", LogLevel.Error);
            state.BreakfastWasNewlyLearned = false;
            state.PreparedBreakfastItem = SafeItemResolver.TryCreateValidItem("(O)194") ?? ItemRegistry.Create("(O)194", 1);
            state.LastBreakfastItemId = "(O)194";
        }
    }

    private void TickSpouseBreakfast(Farmer owner, NPC spouse, SpouseHomeState state, ulong gameTick)
    {
        if (gameTick < state.NextActionTick) return;

        var house = spouse.currentLocation as FarmHouse;
        if (house == null) return;

        spouse.speed = 2;
        spouse.addedSpeed = 0;

        Point kitchenSpot = house.getKitchenStandingSpot();
        if (kitchenSpot == Point.Zero || !house.isTilePassable(new xTile.Dimensions.Location(kitchenSpot.X, kitchenSpot.Y), Game1.viewport))
        {
            Point fallback = new Point(kitchenSpot != Point.Zero ? kitchenSpot.X : 7, kitchenSpot != Point.Zero ? kitchenSpot.Y + 1 : 24);
            kitchenSpot = GetAdjacentWalkableTile(house, fallback, spouse.TilePoint, spouse, maxRadius: 2);
        }
        if (kitchenSpot.Y < 4) kitchenSpot.Y = 4;

        float distToKitchen = Vector2.Distance(spouse.TilePoint.ToVector2(), kitchenSpot.ToVector2());

        // Subfase 0: Preparar desayuno en cocina (CookingBreakfast)
        if (state.BreakfastPhase == 0)
        {
            var res = WalkPhysicallyTo(spouse, house, kitchenSpot, state, gameTick, 1.5f);
            if (res == WalkResult.Walking) return;
            if (res == WalkResult.Unreachable)
            {
                state.BreakfastPhase = 3;
                state.HasPreparedBreakfastToday = true;
                return;
            }

            // Llegó a la estufa
            spouse.controller = null;
            spouse.Halt();
            spouse.faceDirection(0); // Mirar estufa

            // Verificación de micro-evento: Desayuno quemado (Pity Timer 7% -> 15% tras 14 días)
            double burnChance = ValleyBuddies.Systems.LivingEvents.ValleyPacingDirector.Instance.ComputeBurnedBreakfastChance(owner.UniqueMultiplayerID, Game1.Date.TotalDays);
            if (owner.currentLocation == house &&
                ValleyBuddies.Systems.LivingEvents.LivingEventManager.Instance.CanTriggerNow("burned_breakfast", owner, spouse) &&
                Game1.random.NextDouble() < burnChance)
            {
                state.IsBreakfastBurned = true;
                state.BurnedBreakfastPromptShown = false;
                state.BurnedBreakfastWalkStartTick = gameTick;
                ValleyBuddies.Systems.LivingEvents.Events.BurnedBreakfastEvent.Instance.TriggerBurn(spouse, owner, kitchenSpot);

                state.BreakfastPhase = 1; // Pasar a entrega/caminar hacia el jugador
                state.PhaseStartedTick = gameTick;
                state.NextActionTick = gameTick + 160;
                return;
            }

            // Vapor y sonido
            ToolAnimationService.PlayCookingSteamEffect(house, kitchenSpot);

            // Preparar plato dinámico según recetas conocidas del jugador
            PrepareSpouseBreakfast(owner, spouse, state);

            // Verificación de micro-evento: Prueba de sabor (~10% escalado por preset)
            double tasteChance = 0.10 * ValleyBuddies.Systems.LivingEvents.ValleyPacingDirector.Instance.ProbabilityFactor;
            if (owner.currentLocation == house &&
                Vector2.Distance(spouse.TilePoint.ToVector2(), owner.TilePoint.ToVector2()) <= 6f &&
                ValleyBuddies.Systems.LivingEvents.LivingEventManager.Instance.CanTriggerNow("kitchen_taste_test", owner, spouse) &&
                Game1.random.NextDouble() < tasteChance)
            {
                ValleyBuddies.Systems.LivingEvents.Events.KitchenTasteTestEvent.Instance.TriggerTasteTest(spouse, owner, state.PreparedBreakfastItem);
            }
            else
            {
                int bubbleIdx = Game1.random.Next(1, 4);
                string breakfastMsg = I18n.Get($"spouse.breakfast.ready.v{bubbleIdx}", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName, itemName = state.PreparedBreakfastItem?.DisplayName ?? "Breakfast" });
                ModEntry.ConversationBridge.ShowCompanionBubble(spouse, breakfastMsg, 3500);
                if (!spouse.IsEmoting) spouse.doEmote(20);
            }

            ModEntry.ModMonitor?.Log($"[SpouseHome] {spouse.Name} cooked breakfast ({state.PreparedBreakfastItem?.DisplayName ?? "Breakfast"}, newlyLearned={state.BreakfastWasNewlyLearned}) at {kitchenSpot}", LogLevel.Debug);
            state.BreakfastPhase = 1; // Pasar a entrega
            state.PhaseStartedTick = gameTick;
            state.NextActionTick = gameTick + 240; // 4s cocinando con vapor
            return;
        }

        // Subfase 1: Entregar desayuno al jugador (DeliveringBreakfast)
        if (state.BreakfastPhase == 1)
        {
            if (state.IsBreakfastBurned)
            {
                if (owner.currentLocation == house)
                {
                    float distToPlayer = Vector2.Distance(spouse.TilePoint.ToVector2(), owner.TilePoint.ToVector2());
                    bool reachedPlayer = distToPlayer <= 2.2f || (state.BurnedBreakfastWalkStartTick > 0 && gameTick - state.BurnedBreakfastWalkStartTick > 520);
                    if (reachedPlayer)
                    {
                        spouse.Halt();
                        spouse.controller = null;
                        spouse.faceGeneralDirection(owner.Position);
                        if (!state.BurnedBreakfastPromptShown)
                        {
                            state.BurnedBreakfastPromptShown = true;
                            ValleyBuddies.Systems.LivingEvents.Events.BurnedBreakfastEvent.Instance.PresentPrompt(spouse, owner);
                        }
                        state.IsBreakfastBurned = false;
                        state.HasPreparedBreakfastToday = true;
                        state.BreakfastPhase = 3; // Caminar a TV/Sala a descansar
                        state.PhaseStartedTick = gameTick;
                        return;
                    }

                    var walkRes = WalkPhysicallyTo(spouse, house, owner.TilePoint, state, gameTick, 2.0f);
                    if (walkRes == WalkResult.Walking)
                    {
                        return;
                    }
                }

                // Si el dueño no está en la casa, terminar
                state.IsBreakfastBurned = false;
                state.HasPreparedBreakfastToday = true;
                state.BreakfastPhase = 3;
                state.PhaseStartedTick = gameTick;
                return;
            }

            if (state.PreparedBreakfastItem == null)
            {
                state.BreakfastPhase = 3;
                state.HasPreparedBreakfastToday = true;
                return;
            }

            // Si el jugador está dentro de la casa
            if (owner.currentLocation == house)
            {
                float distToPlayer = Vector2.Distance(spouse.TilePoint.ToVector2(), owner.TilePoint.ToVector2());
                if (distToPlayer <= 2.2f)
                {
                    spouse.Halt();
                    spouse.controller = null;
                    spouse.faceGeneralDirection(owner.Position);

                    bool delivered = false;
                    if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
                    {
                        delivered = owner.addItemToInventoryBool(state.PreparedBreakfastItem);
                        if (delivered) Game1.playSound("coin");
                    }
                    else
                    {
                        _multiplayerSync?.SendSpouseGift(owner.UniqueMultiplayerID, state.PreparedBreakfastItem!.QualifiedItemId, 1, spouse.Name);
                        delivered = true;
                    }

                    if (delivered)
                    {
                        string givenMsg;
                        if (state.BreakfastWasNewlyLearned)
                        {
                            int lv = Game1.random.Next(1, 3);
                            givenMsg = I18n.Get($"spouse.breakfast.learned_recipe.v{lv}", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName, itemName = state.PreparedBreakfastItem?.DisplayName ?? "Breakfast" });
                        }
                        else
                        {
                            givenMsg = I18n.Get("spouse.breakfast.given", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName, itemName = state.PreparedBreakfastItem?.DisplayName ?? "Breakfast" });
                        }
                        ModEntry.ConversationBridge.ShowCompanionBubble(spouse, givenMsg, 3000);
                        if (!spouse.IsEmoting) spouse.doEmote(20);
                    }
                    else
                    {
                        var fridge = house.GetFridge();
                        Item? leftover = fridge != null ? fridge.addItem(state.PreparedBreakfastItem) : state.PreparedBreakfastItem;
                        if (leftover == null)
                        {
                            string fridgeMsg = I18n.Get("spouse.breakfast.left_in_fridge", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName, itemName = state.PreparedBreakfastItem?.DisplayName ?? "Breakfast" });
                            ModEntry.ConversationBridge.ShowCompanionBubble(spouse, fridgeMsg, 3000);
                        }
                        else
                        {
                            GiveItemToFarmerDirectly(owner, leftover);
                            string givenMsg = I18n.Get("spouse.breakfast.given", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName, itemName = leftover.DisplayName ?? "Breakfast" });
                            ModEntry.ConversationBridge.ShowCompanionBubble(spouse, givenMsg, 3000);
                        }
                    }

                    state.PreparedBreakfastItem = null;
                    state.HasPreparedBreakfastToday = true;

                    // Si el jugador está junto al cónyuge (<= 2.5f), pasar a beso de despedida tras 1.5s de pausa
                    if (distToPlayer <= 2.5f)
                    {
                        state.BreakfastPhase = 2; // Farewell kiss
                        state.NextActionTick = gameTick + 90;
                    }
                    else
                    {
                        state.BreakfastPhase = 3; // Caminar a la TV/Sala
                        state.PhaseStartedTick = gameTick;
                    }
                    return;
                }

                // Caminar hacia el jugador dentro de la casa
                var walkRes = WalkPhysicallyTo(spouse, house, owner.TilePoint, state, gameTick, 2.0f);
                if (walkRes == WalkResult.Walking)
                {
                    return;
                }
            }

            // Si el jugador no está en casa o expiró el watchdog -> guardar en nevera (o entregar si está llena)
            var fallbackFridge = house.GetFridge();
            Item? fridgeLeftover = (fallbackFridge != null && state.PreparedBreakfastItem != null)
                ? fallbackFridge.addItem(state.PreparedBreakfastItem)
                : state.PreparedBreakfastItem;
            if (fridgeLeftover == null && state.PreparedBreakfastItem != null)
            {
                string fMsg = I18n.Get("spouse.breakfast.left_in_fridge", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName, itemName = state.PreparedBreakfastItem?.DisplayName ?? "Breakfast" });
                ModEntry.ConversationBridge.ShowCompanionBubble(spouse, fMsg, 3000);
            }
            else if (fridgeLeftover != null)
            {
                if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
                    GiveItemToFarmerDirectly(owner, fridgeLeftover);
                else
                    _multiplayerSync?.SendSpouseGift(owner.UniqueMultiplayerID, fridgeLeftover.QualifiedItemId, fridgeLeftover.Stack, spouse.Name);
                string givenFallbackMsg = I18n.Get("spouse.breakfast.given", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName, itemName = fridgeLeftover.DisplayName ?? "Breakfast" });
                ModEntry.ConversationBridge.ShowCompanionBubble(spouse, givenFallbackMsg, 3000);
            }
            state.PreparedBreakfastItem = null;
            state.HasPreparedBreakfastToday = true;
            state.BreakfastPhase = 3; // Caminar a TV/Sala
            state.PhaseStartedTick = gameTick;
            return;
        }

        // Subfase 2: Beso de despedida (FarewellKiss)
        if (state.BreakfastPhase == 2)
        {
            if (owner.currentLocation == house && Vector2.Distance(spouse.TilePoint.ToVector2(), owner.TilePoint.ToVector2()) <= 2.5f)
            {
                spouse.Halt();
                spouse.controller = null;
                spouse.faceGeneralDirection(owner.Position);

                var friendship = owner.friendshipData.TryGetValue(spouse.Name, out var f) ? f : null;
                bool isRoommate = friendship != null && friendship.IsRoommate();

                ToolAnimationService.PlayAffectionBurst(house, spouse, owner, isKiss: !isRoommate, playPlayerLean: !owner.isMoving());

                int fv = Game1.random.Next(1, 3);
                string farewellKey = $"spouse.chores.farewell_kiss.v{fv}";
                string farewellMsg = I18n.Get(farewellKey, new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
                ModEntry.ConversationBridge.ShowCompanionBubble(spouse, farewellMsg, 4000);

                state.DidFarewellKissToday = true;
                state.NextActionTick = gameTick + 120;
            }

            state.BreakfastPhase = 3; // Caminar a TV/Sala
            state.PhaseStartedTick = gameTick;
            return;
        }

        // Subfase 3: Caminar tranquilamente a la TV o chimenea (WalkToTv/Relax)
        if (state.BreakfastPhase == 3)
        {
            Point tvSpot = GetRelaxFurnitureSpot(house, spouse);
            var res = WalkPhysicallyTo(spouse, house, tvSpot, state, gameTick, 1.5f);
            if (res == WalkResult.Walking) return;

            // Llegó a la TV / sala
            spouse.Halt();
            spouse.controller = null;
            spouse.faceDirection(0); // Mirar la TV o chimenea
            state.BreakfastPhase = 4; // Esperar relajada hasta las 08:00 AM
            return;
        }

        // Subfase 4: Esperar pacíficamente en la TV hasta las 08:00 AM
        if (state.BreakfastPhase == 4)
        {
            spouse.Halt();
            spouse.controller = null;

            // Disparar Cita Sorpresa de Cónyuge en la sala si ya terminó el desayuno
            if (Game1.timeOfDay >= 720 && Game1.timeOfDay < 900 &&
                !state.HasTownOutingToday &&
                state.HasPreparedBreakfastToday &&
                ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.HasPendingSpouseDateFor(owner.UniqueMultiplayerID, spouse.Name))
            {
                ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.TryTriggerPendingSpouseDate(spouse, owner, house);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FASE 3: Salida real por la puerta a la granja y quehaceres (>= 08:00 AM)
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // FASE 4: Vida doméstica interior en bloques de 30 minutos (1 sola burbuja)
    // ─────────────────────────────────────────────────────────────────────────

    private void TickSpouseIndoorLiving(Farmer owner, NPC spouse, SpouseHomeState state, ulong gameTick)
    {
        if (gameTick < state.NextActionTick) return;

        var house = spouse.currentLocation as FarmHouse ?? GetOwnerHouse(owner);
        if (house == null || spouse.currentLocation != house) return;

        spouse.speed = 2;
        spouse.addedSpeed = 0;

        Point entry = house.getEntryLocation();

        // Protección anti-atasco en nicho de puerta: Si el cónyuge está parado en el umbral, mover inmediatamente hacia el interior
        if (spouse.TilePoint.Y >= entry.Y - 1)
        {
            Point safeSpot = new Point(entry.X, Math.Max(4, entry.Y - 2));
            if (IsBedPerimeterTileWalkable(house, safeSpot, spouse))
            {
                spouse.setTilePosition(safeSpot);
            }
        }

        // Acariciar mascota dentro de FarmHouse si está presente y no ha sido acariciada hoy
        var indoorPet = house.characters.OfType<Pet>().FirstOrDefault(p => p != null && !state.PettedPetNamesToday.Contains(p.Name));
        if (indoorPet != null && Vector2.Distance(spouse.Tile, indoorPet.Tile) <= 2.2f)
        {
            spouse.faceGeneralDirection(indoorPet.Position);
            if (!spouse.IsEmoting) spouse.doEmote(20);
            if (!indoorPet.IsEmoting) indoorPet.doEmote(20);
            string petSnd = indoorPet.petType.Value.Equals("Cat", StringComparison.OrdinalIgnoreCase) ? "cat" : (indoorPet.petType.Value.Equals("Dog", StringComparison.OrdinalIgnoreCase) ? "dog_bark" : "dwop");
            try { indoorPet.playContentSound(); } catch { house.playSound(petSnd); }
            indoorPet.friendshipTowardFarmer.Value = Math.Min(1000, indoorPet.friendshipTowardFarmer.Value + 12);
            indoorPet.grantedFriendshipForPet.Value = true;
            state.PettedPetNamesToday.Add(indoorPet.Name);
        }

        // Determinar destino y orientación según bloque activo (0 a 3)
        Point targetSpot = Point.Zero;
        int targetFacing = 0;
        int blockIdx = state.IndoorBlockIndex % 4;

        if (blockIdx == 0) // Bloque 0: Lavar platos en el fregadero (30 min)
        {
            Point kitchenSpot = house.getKitchenStandingSpot();
            if (kitchenSpot == Point.Zero) kitchenSpot = new Point(7, 5);
            targetSpot = FindBestWalkableIndoorStandTile(house, kitchenSpot, 3);
            targetFacing = 0;
        }
        else if (blockIdx == 1) // Bloque 1: Limpiar y ordenar la casita (30 min)
        {
            Point kitchenSpot = house.getKitchenStandingSpot();
            if (kitchenSpot == Point.Zero) kitchenSpot = new Point(7, 5);
            Point tidySpot = new Point(kitchenSpot.X + 4, kitchenSpot.Y + 1);
            targetSpot = FindBestWalkableIndoorStandTile(house, tidySpot, 3);
            targetFacing = 0;
        }
        else if (blockIdx == 2) // Bloque 2: Habitación de cónyuge (30 min)
        {
            targetSpot = GetSpouseRoomTile(house, spouse);
            targetFacing = 2;
        }
        else if (blockIdx == 3) // Bloque 3: TV / Chimenea / Mascota (30 min)
        {
            if (indoorPet != null && indoorPet.TilePoint.Y >= 4)
            {
                Point petStand = FindBestWalkableIndoorStandTile(house, indoorPet.TilePoint, 2);
                if (petStand != Point.Zero && petStand.Y >= 4)
                {
                    targetSpot = petStand;
                    targetFacing = spouse.FacingDirection;
                }
                else
                {
                    targetSpot = GetRelaxFurnitureSpot(house, spouse);
                    targetFacing = 0;
                }
            }
            else
            {
                targetSpot = GetRelaxFurnitureSpot(house, spouse);
                targetFacing = 0;
            }
        }

        // Sub-etapa 1: Caminando hacia el sitio del bloque
        if (state.IndoorStage == IndoorBlockStage.WalkingToSpot)
        {
            var res = WalkPhysicallyTo(spouse, house, targetSpot, state, gameTick, 1.5f);
            if (res == WalkResult.Walking) return;

            if (res == WalkResult.Unreachable)
            {
                ModEntry.ModMonitor?.Log($"[SpouseHome] {spouse.Name} could not reach IndoorBlock {blockIdx} at {targetSpot}, skipping to next block.", LogLevel.Debug);
                state.IndoorBlockIndex++;
                state.IndoorStage = IndoorBlockStage.WalkingToSpot;
                state.CurrentWaypoint = null;
                state.CurrentNavTarget = null;
                return;
            }

            // Llegó físicamente al sitio: iniciar PerformingBlock y activar el reloj de 30 minutos
            spouse.Halt();
            spouse.controller = null;
            spouse.faceDirection(targetFacing);
            state.IndoorStage = IndoorBlockStage.PerformingBlock;
            state.IndoorBlockEndTime = AdvanceGameTime(Game1.timeOfDay, 30);
            state.IndoorBlockMinEndTick = gameTick + 1080; // 18s de respaldo
            state.IndoorBlockBubbleShown = false;
            state.NextAmbientEffectTick = gameTick + 300;

            ModEntry.ModMonitor?.Log($"[SpouseHome] {spouse.Name} started IndoorBlock {blockIdx} until {state.IndoorBlockEndTime}", LogLevel.Debug);

            // EXACTAMENTE 1 sola burbuja localizada al llegar
            EmitIndoorBlockBubble(owner, spouse, blockIdx);
            state.IndoorBlockBubbleShown = true;

            // Iniciar animación si aplica
            StartIndoorBlockAnimation(house, spouse, blockIdx, targetSpot);
            return;
        }

        // Sub-etapa 2: Ejecutando el bloque en el sitio (PerformingBlock)
        if (state.IndoorStage == IndoorBlockStage.PerformingBlock)
        {
            spouse.controller = null;
            if (spouse.Sprite?.CurrentAnimation == null)
            {
                spouse.Halt();
                spouse.faceDirection(targetFacing);
            }

            // Efectos visuales sutiles cada ~5 segundos (300 ticks) SIN spam de texto ni sonidos molestos
            if (gameTick >= state.NextAmbientEffectTick)
            {
                state.NextAmbientEffectTick = gameTick + 300;
                PlayIndoorBlockAmbientEffect(house, spouse, blockIdx, targetSpot);
            }

            // Verificación de micro-eventos domésticos cada 3600 ticks (60s reales) dentro de FarmHouse / Cabin
            if (gameTick % 3600 == 0 &&
                _config?.EnableLivingMicroEvents != false &&
                owner.currentLocation == house &&
                Vector2.Distance(spouse.Tile, owner.Tile) <= 7f)
            {
                double pf = ValleyBuddies.Systems.LivingEvents.ValleyPacingDirector.Instance.MicroEventProbabilityFactor;
                if (Game1.timeOfDay >= 1500 && Game1.timeOfDay <= 2130 &&
                    ValleyBuddies.Systems.LivingEvents.LivingEventManager.Instance.CanTriggerNow("home_snack_craving", owner, spouse) &&
                    Game1.random.NextDouble() < 0.05 * pf)
                {
                    ValleyBuddies.Systems.LivingEvents.Events.HomeSnackCravingEvent.Instance.TriggerSnackCraving(spouse, owner);
                }
                else if (ValleyBuddies.Systems.LivingEvents.LivingEventManager.Instance.CanTriggerNow("house_bug_scare", owner, spouse) &&
                         Game1.random.NextDouble() < 0.04 * pf)
                {
                    ValleyBuddies.Systems.LivingEvents.Events.HouseBugScareEvent.Instance.TriggerBugScare(spouse, owner);
                }
            }

            // Comprobar si concluyó el bloque de 30 minutos de juego
            bool timeBlockElapsed = (state.IndoorBlockEndTime > 0 && Game1.timeOfDay >= state.IndoorBlockEndTime);
            bool safetyTimeoutElapsed = (state.IndoorBlockMinEndTick > 0 && gameTick >= state.IndoorBlockMinEndTick + 600);

            if (timeBlockElapsed || safetyTimeoutElapsed)
            {
                NpcAnimationService.StopAnimationCleanly(spouse);
                state.IndoorBlockIndex++;
                state.IndoorStage = IndoorBlockStage.WalkingToSpot;
                state.CurrentWaypoint = null;
                state.CurrentNavTarget = null;
                state.IndoorBlockEndTime = 0;
                state.IndoorBlockMinEndTick = 0;
                state.IndoorBlockBubbleShown = false;
                state.NextActionTick = gameTick + 30;
                spouse.Halt();
                spouse.controller = null;
                return;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FASE 5: Acostarse en la cama doble a las 22:30 / 23:00
    // ─────────────────────────────────────────────────────────────────────────

    private void TickSpouseBed(Farmer owner, NPC spouse, SpouseHomeState state, ulong gameTick)
    {
        var house = spouse.currentLocation as FarmHouse ?? GetOwnerHouse(owner);
        if (house == null || spouse.currentLocation != house) return;

        spouse.speed = 2;
        spouse.addedSpeed = 0;

        // Si ya está durmiendo en la cama
        if (spouse.isSleeping.Value)
        {
            if (gameTick % 240 == 0 && !spouse.IsEmoting)
            {
                spouse.doEmote(24);
            }
            return;
        }

        if (state.WokeUpByPlayerTonight)
        {
            return;
        }

        // Buscar cama doble (o individual como respaldo)
        var beds = house.furniture?.OfType<BedFurniture>().ToList() ?? new List<BedFurniture>();
        var doubleBed = beds.FirstOrDefault(b => b.bedType == BedFurniture.BedType.Double) ?? house.GetPlayerBed();
        if (doubleBed == null) return;

        Point targetSpot;
        if (doubleBed.bedType != BedFurniture.BedType.Double)
        {
            Point singleSpot = doubleBed.GetBedSpot();
            bool ownerInSingleBed = owner.isInBed.Value || owner.TilePoint == singleSpot;
            targetSpot = ownerInSingleBed ? FindWalkableBedsideSpot(house, singleSpot) : singleSpot;
        }
        else
        {
            Point rightSpot = new Point((int)doubleBed.TileLocation.X + 2, (int)doubleBed.TileLocation.Y + 1);
            Point leftSpot = new Point((int)doubleBed.TileLocation.X + 1, (int)doubleBed.TileLocation.Y + 1);
            Point ownerTile = owner.TilePoint;

            // Preferir el lado opuesto al jugador
            bool ownerOnRight = ownerTile == rightSpot || (owner.isInBed.Value && ownerTile.X >= rightSpot.X);
            targetSpot = ownerOnRight ? leftSpot : rightSpot;
        }

        if (state.Phase != SpouseHomePhase.GoingToBed)
        {
            state.Phase = SpouseHomePhase.GoingToBed;
            state.PhaseStartedTick = gameTick;
            if (!state.AnnouncedBedtimeToday)
            {
                state.AnnouncedBedtimeToday = true;
                if (!spouse.IsEmoting) spouse.doEmote(24); // Yawn
                int v = Game1.random.Next(1, 3);
                string bedMsg = I18n.Get($"spouse.home.going_to_bed.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
                ModEntry.ConversationBridge.ShowCompanionBubble(spouse, bedMsg, 3500);
            }
        }

        // Obtener perímetro caminable real alrededor de doubleBed (filtrando Y >= 4 y aceptando alfombras)
        var perimeter = GetBedPerimeterCandidates(house, doubleBed, spouse)
            .Where(p => p.Y >= 4)
            .ToList();

        float distToBed = Vector2.Distance(spouse.TilePoint.ToVector2(), targetSpot.ToVector2());

        // Si ya está en o muy cerca del perímetro o de targetSpot (<= 2.6f), acostarse
        if (distToBed <= 2.6f || (perimeter.Count > 0 && perimeter.Any(p => Vector2.Distance(spouse.TilePoint.ToVector2(), p.ToVector2()) <= 1.6f)))
        {
            EnterSleepingInBed(spouse, null, doubleBed, targetSpot);
            ModEntry.ModMonitor?.Log($"[SpouseHome] {spouse.Name} arrived at bed perimeter and went to sleep in double bed at {targetSpot}", LogLevel.Debug);
            return;
        }

        // Caminar físicamente hacia el candidato más cercano del perímetro
        WalkResult res = WalkResult.Unreachable;
        if (perimeter.Count > 0)
        {
            Point bestSpot = perimeter[0];
            res = WalkPhysicallyTo(spouse, house, bestSpot, state, gameTick, 1.6f);
            if (res == WalkResult.Unreachable && perimeter.Count > 1)
            {
                bestSpot = perimeter[1];
                res = WalkPhysicallyTo(spouse, house, bestSpot, state, gameTick, 1.6f);
            }
        }
        else
        {
            Point card = FindClosestWalkableCardinalNeighbor(house, targetSpot, spouse);
            if (card != Point.Zero && card.Y >= 4)
            {
                res = WalkPhysicallyTo(spouse, house, card, state, gameTick, 1.6f);
            }
        }

        if (res == WalkResult.Walking) return;

        if (res == WalkResult.Arrived || distToBed <= 2.6f)
        {
            EnterSleepingInBed(spouse, null, doubleBed, targetSpot);
            ModEntry.ModMonitor?.Log($"[SpouseHome] {spouse.Name} went to sleep in double bed at {targetSpot}", LogLevel.Debug);
            return;
        }

        // Si resultó Unreachable por timeout pero ya está dentro del dormitorio (dist <= 6.5f), acostarse por fallback
        if (res == WalkResult.Unreachable && distToBed <= 6.5f)
        {
            EnterSleepingInBed(spouse, null, doubleBed, targetSpot);
            ModEntry.ModMonitor?.Log($"[SpouseHome] {spouse.Name} used bed fallback at {targetSpot} (dist was {distToBed:F1})", LogLevel.Debug);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MÉTODOS DE SOPORTE: HABITACIÓN DE CÓNYUGE, TV, INVERNADERO Y CULTIVOS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Encuentra la casilla transitable óptima dentro de FarmHouse validando:
    /// 1. Y >= 4 y lejos del nicho de entrada.
    /// 2. Libre de colisión de mapa (isTilePassable).
    /// 3. Sin muebles sólidos intersectando su hitbox.
    /// 4. Sin BedFurniture ni objetos sólidos.
    /// 5. Al menos 2 vecinos cardinales transitables para evitar quedar encerrado detrás de muebles.
    /// </summary>
}
