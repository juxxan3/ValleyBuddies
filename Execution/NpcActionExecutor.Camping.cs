using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using ValleyBuddies.Systems;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    private readonly ConcurrentDictionary<string, string> _activeNightTorches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Coloca el objeto de fogata nativa (BC)146 de manera unificada con fallback seguro y marcador TempCamp.
    /// </summary>
    private static void PlaceTemporaryCampfireObject(GameLocation loc, Vector2 tileVec)
    {
        if (loc == null) return;
        try
        {
            var campfireObj = ItemRegistry.Create<StardewValley.Object>("(BC)146");
            if (campfireObj != null)
            {
                campfireObj.modData["Juxxan.ValleyBuddies/TempCamp"] = "true";
                loc.objects[tileVec] = campfireObj;
                return;
            }
        }
        catch { }

        try
        {
            var fallback = new Torch("146", true);
            fallback.modData["Juxxan.ValleyBuddies/TempCamp"] = "true";
            try { fallback.initializeLightSource(tileVec, false); } catch { }
            loc.objects[tileVec] = fallback;
        }
        catch { }
    }

    public void RemoveCompanionNightTorch(string npcName, GameLocation? fallbackLocation = null)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;
        string lightId = $"AliveNpcs_Torch_{npcName}";
        try
        {
            if (_activeNightTorches.TryRemove(npcName, out string? prevLocName) && !string.IsNullOrWhiteSpace(prevLocName))
            {
                var prevLoc = Game1.getLocationFromName(prevLocName);
                if (prevLoc != null && prevLoc.hasLightSource(lightId))
                    prevLoc.removeLightSource(lightId);
            }
            if (fallbackLocation != null && fallbackLocation.hasLightSource(lightId))
                fallbackLocation.removeLightSource(lightId);
            var npc = FindNpc(npcName);
            if (npc?.currentLocation != null && npc.currentLocation.hasLightSource(lightId))
                npc.currentLocation.removeLightSource(lightId);
        }
        catch { }
    }

    public void RemoveAllNightTorches()
    {
        try
        {
            foreach (var kvp in _activeNightTorches.ToList())
            {
                RemoveCompanionNightTorch(kvp.Key);
            }
            _activeNightTorches.Clear();
        }
        catch { }
    }

    /// <summary>
    /// Cleans up any un-serializable transient terrain features (like Tent) and temporary campfire objects
    /// from farm before Stardew Valley's nightly SaveGame.Save() XML serializer runs.
    /// Retains _overnightGuests records so they can wake up on the porch with morning coffee on DayStarted!
    /// </summary>
    public static void RemoveCampObjectsAt(GameLocation? loc, Point campfireTile, Point tentTile)
    {
        if (loc == null) return;
        try
        {
            if (tentTile != Point.Zero && loc.terrainFeatures != null)
            {
                Vector2 tVec = new Vector2(tentTile.X, tentTile.Y);
                if (loc.terrainFeatures.TryGetValue(tVec, out var tf) && tf is StardewValley.TerrainFeatures.Tent && tf.modData.ContainsKey("Juxxan.ValleyBuddies/TempCamp"))
                {
                    loc.terrainFeatures.Remove(tVec);
                }
            }
            if (campfireTile != Point.Zero)
            {
                if (loc.objects != null)
                {
                    Vector2 cVec = new Vector2(campfireTile.X, campfireTile.Y);
                    if (loc.objects.TryGetValue(cVec, out var obj) && obj != null && obj.modData.ContainsKey("Juxxan.ValleyBuddies/TempCamp"))
                    {
                        loc.objects.Remove(cVec);
                    }
                }
                loc.removeLightSource($"ValleyBuddies_Campfire_{campfireTile.X}_{campfireTile.Y}");
                loc.removeLightSource($"AliveNpcs_Campfire_{campfireTile.X}_{campfireTile.Y}");
            }
        }
        catch { }
    }

    public void CleanupCampBeforeSave()
    {
        RemoveAllCaveLights();
        RemoveAllNightTorches();
        try
        {
            // Rescue any pending gathered crops/materials or chest items before resetting active jobs for save
            foreach (var kv in _active.ToList())
            {
                if (!kv.Value.Done
                    && (kv.Value.TryGetState<ValleyBuddies.AgencyCore.IPendingItemCarrierState>(out _)
                        || kv.Value.TryGetState<ValleyBuddies.AgencyCore.FetchFromChestState>(out _))
                    && _jobHandlers.TryGetHandler(kv.Value.Kind, out var handler))
                {
                    var activeNpc = FindNpc(kv.Key);
                    if (activeNpc != null)
                    {
                        Farmer? owner = GetJobFarmer(kv.Value) ?? GetCompanionOwner(kv.Key) ?? Game1.player;
                        _companions.TryGetValue(kv.Key, out var companion);
                        var abortCtx = new ValleyBuddies.AgencyCore.JobExecutionContext(
                            kv.Value,
                            activeNpc,
                            owner,
                            companion,
                            ModEntry.CurrentTick,
                            _navigator,
                            _farmWorld,
                            RecordMemory,
                            RecordDepositedItem,
                            _debug_log);
                        handler.OnAbort(abortCtx);
                    }
                    kv.Value.Done = true;
                }
            }

            var farm = Game1.getFarm();
            if (farm == null) return;

            // 1. Remove any known tent or campfire placed by overnight guests
            foreach (var kvp in _overnightGuests.ToList())
            {
                var info = kvp.Value;
                if (info.DidCampfire)
                {
                    RemoveCampObjectsAt(farm, info.CampfireTile, info.TentTile);
                }
            }

            // 2. Extra safety sweep across all locations: purge ONLY temporary mod-placed Tent instances
            // so player-placed vanilla Tent Kits are never destroyed.
            foreach (var loc in Game1.locations)
            {
                if (loc == null || loc.terrainFeatures == null) continue;
                var tentKeysToRemove = loc.terrainFeatures.Pairs
                    .Where(p => p.Value is StardewValley.TerrainFeatures.Tent && p.Value.modData.ContainsKey("Juxxan.ValleyBuddies/TempCamp"))
                    .Select(p => p.Key)
                    .ToList();

                foreach (var key in tentKeysToRemove)
                {
                    loc.terrainFeatures.Remove(key);
                }
            }
        }
        catch (Exception ex)
        {
            _debug_log($"[Actions] Error during CleanupCampBeforeSave: {ex.Message}");
        }

        // Reset all active followers and controllers while preserving _overnightGuests for morning wakeup
        ResetAllCompanionsAndJobsToDefault(preserveOvernightGuests: true);
    }

    private static bool DoesCampStillExist(GameLocation? loc, OvernightGuestInfo info)
    {
        if (loc == null || !info.DidCampfire) return false;
        if (info.TentTile != Point.Zero && loc.terrainFeatures != null
            && loc.terrainFeatures.TryGetValue(info.TentTile.ToVector2(), out var tf)
            && tf is StardewValley.TerrainFeatures.Tent
            && tf.modData.ContainsKey("Juxxan.ValleyBuddies/TempCamp"))
        {
            return true;
        }
        if (info.CampfireTile != Point.Zero && loc.Objects != null
            && loc.Objects.TryGetValue(info.CampfireTile.ToVector2(), out var obj)
            && obj != null
            && obj.modData.ContainsKey("Juxxan.ValleyBuddies/TempCamp"))
        {
            return true;
        }
        return false;
    }

    private static NpcJob CreateCampJobWithCoords(string npcName, JobKind kind, long farmerId, string farmerName, OvernightGuestInfo info)
    {
        var job = new NpcJob
        {
            NpcName = npcName,
            Kind = kind,
            FarmerId = farmerId,
            FarmerName = farmerName,
            CreatedTick = Game1.ticks
        };
        job.Data["campTileX"] = info.CampfireTile.X;
        job.Data["campTileY"] = info.CampfireTile.Y;
        job.Data["tentTileX"] = info.TentTile.X;
        job.Data["tentTileY"] = info.TentTile.Y;
        return job;
    }

    private void HandleOvernightGuestsMorning()
    {
        if (!Context.IsMainPlayer) return;
        if (_overnightGuests.IsEmpty) return;

        var farm = Game1.getFarm();
        if (farm == null) return;

        Point porch = GetFarmSafePorchTile(farm);
        long localHostId = Game1.player?.UniqueMultiplayerID ?? 0;

        foreach (var kvp in _overnightGuests.ToList())
        {
            string guestName = kvp.Key;
            var info = kvp.Value;
            var guest = FindNpc(guestName);
            if (guest == null) continue;

            // Clean up temporary campfire object, light and tent across all locations
            if (info.DidCampfire)
            {
                RemoveCampObjectsAt(farm, info.CampfireTile, info.TentTile);
                foreach (var loc in Game1.locations)
                {
                    if (loc != null && loc != farm)
                        RemoveCampObjectsAt(loc, info.CampfireTile, info.TentTile);
                }
            }

            Farmer? targetFarmer = null;
            bool isRemoteInviter = false;
            bool isInviterOffline = false;
            if (info.FarmerId != 0)
            {
                targetFarmer = Game1.GetPlayer(info.FarmerId, onlyOnline: true);
                if (targetFarmer == null)
                {
                    isInviterOffline = true;
                    targetFarmer = Game1.GetPlayer(info.FarmerId, onlyOnline: false);
                }
                isRemoteInviter = info.FarmerId != localHostId;
            }
            else if (!Context.IsMultiplayer)
            {
                targetFarmer = Game1.player;
            }

            // Handle indoor sleepover: NPC leaves a sweet handwritten note and hot coffee while farmer sleeps
            if (info.DidSleepover)
            {
                if (_config?.EnableSleepoverCoffeeLetter != false)
                {
                    GetMorningGoodbyeNote(guest, targetFarmer, out string noteTitle, out string noteContent);

                    string farewellKey = $"chat.morning.farewell.{guest.Name.ToLowerInvariant()}";
                    string farewellChat = Utilities.I18n.Get(farewellKey);
                    if (farewellChat == farewellKey || string.IsNullOrWhiteSpace(farewellChat))
                    {
                        farewellChat = Utilities.I18n.Get("chat.morning.farewell.default");
                    }

                    // Deliver to Farm Computer Messenger app and broadcast activity event
                    _monitor?.Log($"[AliveNpcs Multiplayer] [MP] ✓ ActivityEvent broadcast: had a love conversation with {guest.Name}: \"[Messenger text chat on the farmer's phone, not face to face.] {farewellChat}\"", StardewModdingAPI.LogLevel.Info);
                    ValleyBuddies.Bridge.ValleyBuddiesReflection.AddMessengerMessage(guest.Name, targetFarmer?.displayName ?? "friend", farewellChat);

                    string sleepoverHud = Utilities.I18n.Get("hud.morning.left_note_coffee", new { npcName = guest.displayName });

                    if (targetFarmer != null && !isRemoteInviter)
                    {
                        var breakfast = ItemRegistry.Create("(O)395", 1); // Fresh hot coffee!
                        if (breakfast != null)
                            GiveItemToFarmerDirectly(targetFarmer, breakfast);
                        _pendingMorningNote = noteContent;

                        DelayedAction.functionAfterDelay(() =>
                        {
                            Game1.addHUDMessage(new HUDMessage(sleepoverHud, HUDMessage.newQuest_type));
                            Game1.chatBox?.addMessage($"{guest.displayName}: \"{farewellChat}\"", new Color(255, 182, 193));
                        }, 2500);
                    }
                    else if (targetFarmer != null && isRemoteInviter && !isInviterOffline)
                    {
                        long inviterId = targetFarmer.UniqueMultiplayerID;
                        string guestDisplay = guest.displayName;
                        string guestNpcName = guest.Name;
                        DelayedAction.functionAfterDelay(() =>
                        {
                            try
                            {
                                _multiplayerSync?.SendSpouseGift(inviterId, "(O)395", 1, guestNpcName);
                                _multiplayerSync?.SendPrivateNotice(inviterId, sleepoverHud, isError: false, hudType: HUDMessage.newQuest_type);
                                _multiplayerSync?.SendPrivateNotice(inviterId, $"{guestDisplay}: \"{farewellChat}\"", isError: false, hudType: HUDMessage.newQuest_type);
                            }
                            catch { }
                        }, 1800);
                    }
                    else if (info.FarmerId != 0)
                    {
                        _mailService?.QueueLetter(guest.Name, noteTitle, noteContent, "(O)395", info.FarmerId, _multiplayerSync);
                    }
                }

                RecordMemory(guest.Name, $"I spent the night sleeping over at the farmer's house. In the early morning, while they were peacefully asleep in bed, I brewed them a fresh hot coffee, sent them a sweet message, left a note on their table, and headed back to the valley to begin my day.");

                // Guest departs quietly back to their regular daily schedule
                DismissCompanion(guest.Name);
                guest.followSchedule = true;
                continue;
            }

            // Campout guests: wake up on porch of the inviting farmer and greet them
            Point targetDoor = GetOwnerHomeExteriorDoorTile(farm, targetFarmer);
            Point wakeSpot = new Point(targetDoor.X, targetDoor.Y + 2);
            if (!IsTileWalkable(farm, wakeSpot, guest))
                wakeSpot = GetAdjacentWalkableTile(farm, targetDoor, porch, guest);
            if (wakeSpot == Point.Zero || !IsTileWalkable(farm, wakeSpot, guest))
                wakeSpot = porch;

            SafeWarpNpc(guest, farm, new Vector2(wakeSpot.X, wakeSpot.Y));
            guest.faceDirection(2);
            guest.controller = null;
            guest.Halt();

            string coffeeHud = Utilities.I18n.Get("hud.morning.brewed_coffee", new { npcName = guest.displayName });
            if (targetFarmer != null && !isRemoteInviter)
            {
                var breakfast = ItemRegistry.Create("(O)395", 1);
                if (breakfast != null)
                    GiveItemToFarmerDirectly(targetFarmer, breakfast);
                Game1.addHUDMessage(new HUDMessage(coffeeHud, HUDMessage.newQuest_type));
            }
            else if (targetFarmer != null && isRemoteInviter && !isInviterOffline)
            {
                long inviterId = targetFarmer.UniqueMultiplayerID;
                string guestNpcName = guest.Name;
                DelayedAction.functionAfterDelay(() =>
                {
                    try
                    {
                        _multiplayerSync?.SendSpouseGift(inviterId, "(O)395", 1, guestNpcName);
                        _multiplayerSync?.SendPrivateNotice(inviterId, coffeeHud, isError: false, hudType: HUDMessage.newQuest_type);
                    }
                    catch { }
                }, 1800);
            }
            else if (info.FarmerId != 0)
            {
                GetMorningGoodbyeNote(guest, targetFarmer, out string noteTitle, out string noteContent);
                _mailService?.QueueLetter(guest.Name, noteTitle, noteContent, "(O)395", info.FarmerId, _multiplayerSync);
            }

            guest.doEmote(32);
            guest.showTextAboveHead(Utilities.I18n.Get("bubble.morning_camp"), duration: 5500);

            RecordMemory(guest.Name, "I spent the night camping out on the farm, woke up early to the fresh morning air, made coffee for the farmer, and wished them a great day.");

            DelayedAction.functionAfterDelay(() =>
            {
                try
                {
                    if (guest.currentLocation == farm)
                    {
                        guest.showTextAboveHead(Utilities.I18n.Get("bubble.morning_depart"), duration: 4200);
                        DismissCompanion(guest.Name);
                    }
                }
                catch { }
            }, 12000);
        }

        _overnightGuests.Clear();
    }

    // ─────────────── Late Night & Camping implementations ─────────

    private void CheckLateNightCurfew(ulong gameTick)
    {
        if (!Context.IsMainPlayer) return;
        if (Game1.timeOfDay < 2500) return;

        foreach (var kv in _active.ToList())
        {
            var job = kv.Value;
            if (job.Done) continue;

            string npcName = kv.Key;
            var npc = FindNpc(npcName);
            if (npc == null) continue;

            var owner = GetJobFarmer(job) ?? Game1.player;

            // 1. Excepciones: Si ya acordó quedarse a dormir en casa o acampar, NO despedir
            bool isSleepingOrCamping = false;
            if (_companions.TryGetValue(npcName, out var comp))
            {
                if (comp.NightState == NightLifecycleState.SleepingInBed ||
                    comp.NightState == NightLifecycleState.GoingToBed ||
                    comp.NightState == NightLifecycleState.CampOutdoorIdle ||
                    comp.EveningResolution == EveningResolutionState.StayInHouse ||
                    comp.EveningResolution == EveningResolutionState.StayingInHouse ||
                    comp.EveningResolution == EveningResolutionState.Camping ||
                    comp.EveningResolution == EveningResolutionState.GoToBed ||
                    comp.EveningResolution == EveningResolutionState.OutdoorCamp)
                {
                    isSleepingOrCamping = true;
                }
            }
            if (job.Kind == JobKind.Sleepover || job.Kind == JobKind.Campfire || job.Kind == JobKind.CampSleepTogether)
            {
                isSleepingOrCamping = true;
            }

            if (isSleepingOrCamping) continue;

            // 2. Procesar despedida automática de las 1:00 AM
            if (!job.Data.ContainsKey("curfew_dismissing"))
            {
                npc.controller = null;
                npc.Halt();
                if (owner != null) npc.faceGeneralDirection(owner.Position);

                job.Data["curfew_dismissing"] = true;
                job.Data["curfew_dismiss_at"] = gameTick + 300; // Pausa de 5.0s (300 ticks) para lectura

                SpeakNonRepeating(npc, "night.curfew.1am", new { playerName = owner?.Name ?? "Player", npcName = npc.displayName, farmerName = owner?.Name ?? "Player", ownerName = owner?.Name ?? "Player" });
                npc.doEmote(24); // Bostezo/sueño
            }
            else
            {
                // Durante la pausa de 5s, mantener quieto mirando al dueño
                npc.controller = null;
                npc.Halt();
                if (owner != null) npc.faceGeneralDirection(owner.Position);

                ulong dismissAt = job.Data.TryGetValue("curfew_dismiss_at", out var da) && da is ulong u ? u : 0UL;
                if (gameTick >= dismissAt)
                {
                    if (owner != null)
                    {
                        DeliverPendingItemsToPlayer(job, npc, owner);
                    }

                    DismissCompanion(npcName);
                    if (IsMarriedToAnyFarmer(npc))
                    {
                        continue;
                    }

                    npc.followSchedule = true;
                    npc.drawOffset = Vector2.Zero;

                    // Reproducir sonido nativo de teletransporte
                    npc.currentLocation?.playSound("wand");

                    // Teletransportar limpiamente a su mapa/hogar por defecto con coordenadas enteras de tile
                    string defMap = !string.IsNullOrEmpty(npc.DefaultMap) ? npc.DefaultMap : "Town";
                    GameLocation? homeLoc = Game1.getLocationFromName(defMap);
                    int homeTileX = (int)(npc.DefaultPosition.X / 64f);
                    int homeTileY = (int)(npc.DefaultPosition.Y / 64f);
                    if (homeTileX <= 0 && homeTileY <= 0)
                    {
                        homeTileX = 10;
                        homeTileY = 10;
                    }
                    Vector2 defPos = new Vector2(homeTileX, homeTileY);
                    if (homeLoc != null)
                    {
                        Game1.warpCharacter(npc, homeLoc, defPos);
                    }
                    npc.controller = null;
                    npc.Halt();
                }
            }
        }
    }

    private void CheckLateNightCompanions()
    {
        if (Game1.timeOfDay < 1900) return;
        var player = Game1.player;
        if (player == null || Game1.activeClickableMenu != null) return;

        foreach (var kvp in _companions)
        {
            string compName = kvp.Key;
            if (_nightPromptedTonight.Contains(compName) 
                || _overnightGuests.ContainsKey(compName)
                || kvp.Value.EveningResolution != EveningResolutionState.Undecided)
                continue;

            var compNpc = FindNpc(compName);
            if (compNpc != null && (compNpc.currentLocation is Farm || compNpc.currentLocation is StardewValley.Locations.FarmHouse))
            {
                // Multi-player routing: only prompt the specific player who invited the NPC!
                long inviterId = kvp.Value.FarmerId;
                if (inviterId != 0 && inviterId != player.UniqueMultiplayerID)
                {
                    // If we are Host, check if the inviter is near this NPC or on the farm, and send them the prompt!
                    if (Game1.IsMasterGame)
                    {
                        var inviterFarmer = Game1.GetPlayer(inviterId, true);
                        if (inviterFarmer != null)
                        {
                            bool sameLoc = (inviterFarmer.currentLocation == compNpc.currentLocation);
                            bool nearOrOnFarm = (sameLoc && Vector2.Distance(compNpc.Tile, inviterFarmer.Tile) <= 12f)
                                             || (inviterFarmer.currentLocation is Farm || inviterFarmer.currentLocation is StardewValley.Locations.FarmHouse);
                            if (nearOrOnFarm)
                            {
                                _nightPromptedTonight.Add(compName);
                                ValleyBuddies.ModEntry.SendNightPromptToPlayer(inviterId, compName);
                            }
                        }
                    }
                    continue; // Never prompt local player if they did not invite this NPC!
                }

                // If this machine's local player is the inviter (or single-player):
                if (compNpc.currentLocation == player.currentLocation && Vector2.Distance(compNpc.Tile, player.Tile) <= 8f)
                {
                    _nightPromptedTonight.Add(compName);
                    PromptNighttimeActivity(compNpc);
                    break;
                }
            }
        }
    }

    public void PromptNighttimeActivity(NPC npc)
    {
        if (npc == null || Game1.currentLocation == null) return;

        npc.doEmote(24); // Thinking / late
        string[] warnKeys = new[] { "night.warning.late.v1", "night.warning.late.v2", "night.warning.late.v3" };
        string chosenKey = warnKeys[Game1.random.Next(warnKeys.Length)];
        var owner = GetCompanionOwner(npc.Name) ?? Game1.player;
        string pName = owner?.Name ?? "Player";
        SpeakNonRepeating(npc, chosenKey, new { playerName = pName, npcName = npc.displayName, farmerName = pName });
    }

    private void ExecuteCampfireEvent(NPC npc, Farmer? who)
    {
        if (Game1.IsMultiplayer && !Context.IsMainPlayer) return;

        var loc = npc.currentLocation;
        if (loc == null) return;
        var farmer = who ?? Game1.player;

        if (_overnightGuests.TryGetValue(npc.Name, out var existingGuest) && existingGuest.DidCampfire)
        {
            bool campExists = DoesCampStillExist(loc, existingGuest)
                || Game1.locations.Any(l => l != null && DoesCampStillExist(l, existingGuest))
                || GetEveningResolution(npc.Name) == EveningResolutionState.Camping;
            if (campExists)
            {
                long reFarmerId = existingGuest.FarmerId != 0 ? existingGuest.FarmerId : (farmer?.UniqueMultiplayerID ?? 0);
                string reFarmerName = Game1.GetPlayer(reFarmerId, onlyOnline: false)?.Name ?? farmer?.Name ?? string.Empty;
                SetCompanion(npc.Name, reFarmerId, isVisitingFarm: true, isFollowing: false, farmerName: reFarmerName);
                if (_companions.TryGetValue(npc.Name, out var existingComp))
                {
                    existingComp.NightState = NightLifecycleState.CampOutdoorIdle;
                    existingComp.EveningResolution = EveningResolutionState.Camping;
                    existingComp.IsFollowing = false;
                    existingComp.IsRestingInPlace = true;
                }
                SetEveningResolution(npc.Name, EveningResolutionState.Camping);
                _active[npc.Name] = CreateCampJobWithCoords(npc.Name, JobKind.Campfire, reFarmerId, reFarmerName, existingGuest);
                return;
            }
        }

        if (loc is StardewValley.Locations.FarmHouse || loc is StardewValley.Locations.Cabin || !loc.IsOutdoors)
        {
            string indoorMsg = Utilities.I18n.Get("night.camp.indoor_blocked");
            ModEntry.ConversationBridge.ShowCompanionBubble(npc, indoorMsg);
            NotifyFarmer(farmer, indoorMsg, isError: true);
            return;
        }

        // Validación de terreno 3x3
        Point center = farmer.TilePoint;
        Point? safeTopLeft = FindSafeCamp3x3Area(loc, center);

        if (!safeTopLeft.HasValue)
        {
            NotifyFarmer(farmer, Utilities.I18n.Get("night.camp.no_space"), isError: true);
            return;
        }

        Point fireTile = new(safeTopLeft.Value.X + 1, safeTopLeft.Value.Y + 1);
        Point tentTile = new(safeTopLeft.Value.X + 1, safeTopLeft.Value.Y); // 1 tile al norte dentro del área 3x3
        Vector2 firePos = new(fireTile.X, fireTile.Y);
        Vector2 tentVec = new(tentTile.X, tentTile.Y);

        loc.playSound("axe");     // pitch tent sound
        loc.playSound("fireball"); // light campfire sound

        // 1. Fogata temporal (BC)146 etiquetada con TempCamp
        PlaceTemporaryCampfireObject(loc, firePos);

        // 2. Luz de fogata cálida en sharedLights
        string campLightId = $"ValleyBuddies_Campfire_{fireTile.X}_{fireTile.Y}";
        if (!loc.hasLightSource(campLightId))
        {
            loc.sharedLights[campLightId] = new LightSource(
                campLightId,
                1,
                new Vector2(firePos.X * 64f + 32f, firePos.Y * 64f + 32f),
                4.2f,
                new Color(0, 80, 255),
                LightSource.LightContext.None,
                0L,
                loc.NameOrUniqueName
            );
        }

        // 3. Carpa temporal (Tent) de Stardew Valley 1.6 etiquetada con TempCamp
        try
        {
            var tent = new StardewValley.TerrainFeatures.Tent(tentVec);
            tent.modData["Juxxan.ValleyBuddies/TempCamp"] = "true";
            loc.terrainFeatures[tentVec] = tent;
        }
        catch (Exception ex)
        {
            _debug_log($"[Actions] Could not place Tent terrain feature: {ex.Message}");
        }

        // 4. Posicionar al NPC junto a la fogata mirando hacia el fuego
        Point safeNpcTile = new Point(safeTopLeft.Value.X, safeTopLeft.Value.Y + 1);
        if (!IsTileWalkable(loc, safeNpcTile))
            safeNpcTile = GetAdjacentWalkableTile(loc, fireTile, npc.TilePoint);

        if (safeNpcTile != Point.Zero && safeNpcTile != fireTile && safeNpcTile != tentTile)
        {
            npc.Position = new Vector2(safeNpcTile.X * 64f, safeNpcTile.Y * 64f);
        }
        npc.faceGeneralDirection(firePos * 64f);

        // 5. Registrar estado de acompañante, job de campamento y huésped nocturno
        long myFarmerId = farmer?.UniqueMultiplayerID ?? 0;
        string myFarmerName = farmer?.Name ?? string.Empty;

        SetCompanion(npc.Name, myFarmerId, isVisitingFarm: true, isFollowing: false, farmerName: myFarmerName);
        if (_companions.TryGetValue(npc.Name, out var comp))
        {
            comp.NightState = NightLifecycleState.CampOutdoorIdle;
            comp.EveningResolution = EveningResolutionState.Camping;
            comp.IsFollowing = false;
            comp.IsRestingInPlace = true;
        }
        SetEveningResolution(npc.Name, EveningResolutionState.Camping);
        _nightPromptedTonight.Add(npc.Name);

        var job = new NpcJob
        {
            NpcName = npc.Name,
            Kind = JobKind.Campfire,
            FarmerId = myFarmerId,
            FarmerName = myFarmerName,
            CreatedTick = Game1.ticks
        };
        job.Data["campTileX"] = fireTile.X;
        job.Data["campTileY"] = fireTile.Y;
        job.Data["tentTileX"] = tentTile.X;
        job.Data["tentTileY"] = tentTile.Y;
        _active[npc.Name] = job;

        _overnightGuests[npc.Name] = new OvernightGuestInfo
        {
            NpcName = npc.Name,
            FarmerId = myFarmerId,
            DidCampfire = true,
            CampfireTile = fireTile,
            TentTile = tentTile
        };

        RecordMemory(npc.Name, "We set up a camping tent and lit a real warm campfire under the stars on the farm, chatting peacefully by the fire and camping outside until morning.");

        npc.doEmote(20);
        NotifyFarmer(farmer, Utilities.I18n.Get("hud.camp.camping_until_morning", new { npcName = npc.displayName }), isError: false, hudType: HUDMessage.newQuest_type);

        DelayedAction.functionAfterDelay(() =>
        {
            try
            {
                loc.playSound("gulp");
                npc.doEmote(20);
                int cozyIndex = Game1.random.Next(1, 5);
                npc.showTextAboveHead(Utilities.I18n.Get($"bubble.camp.cozy_{cozyIndex}"), duration: 4500);
            }
            catch { }
        }, 1200);

        DelayedAction.functionAfterDelay(() =>
        {
            try
            {
                string pName = farmer?.Name ?? Game1.player?.Name ?? "Player";
                SpeakNonRepeating(npc, "night.camp.stargazing.v1", new { playerName = pName, npcName = npc.displayName, farmerName = pName });
            }
            catch { }
        }, 4000);
    }

    // ─────────────────── CAMPAMENTO SEGURO Y MODDATA v4.0 ───────────────────

    public static void PurgeTemporaryCampEntities(GameLocation loc)
    {
        if (loc == null) return;
        try
        {
            if (loc.objects != null)
            {
                var toRemove = new List<Vector2>();
                foreach (var kvp in loc.objects.Pairs)
                {
                    if (kvp.Value != null && kvp.Value.modData.ContainsKey("Juxxan.ValleyBuddies/TempCamp"))
                    {
                        toRemove.Add(kvp.Key);
                    }
                }
                foreach (var tile in toRemove)
                {
                    loc.objects.Remove(tile);
                }
            }

            if (loc.terrainFeatures != null)
            {
                var tfToRemove = new List<Vector2>();
                foreach (var kvp in loc.terrainFeatures.Pairs)
                {
                    if (kvp.Value is StardewValley.TerrainFeatures.Tent && kvp.Value.modData.ContainsKey("Juxxan.ValleyBuddies/TempCamp"))
                    {
                        tfToRemove.Add(kvp.Key);
                    }
                }
                foreach (var tile in tfToRemove)
                {
                    loc.terrainFeatures.Remove(tile);
                }
            }

            if (loc.sharedLights != null)
            {
                var lightsToRemove = loc.sharedLights.Keys.Where(k => k.StartsWith("AliveNpcs_Campfire_") || k.StartsWith("ValleyBuddies_Campfire_")).ToList();
                foreach (var lk in lightsToRemove)
                {
                    loc.sharedLights.Remove(lk);
                }
            }
        }
        catch { }
    }

    private static Point? FindSafeCamp3x3Area(GameLocation loc, Point center)
    {
        // 1. Probar primero el centro exacto alrededor del jugador
        Point initialTopLeft = new(center.X - 1, center.Y - 1);
        if (Is3x3AreaClearAndSafe(loc, initialTopLeft))
            return initialTopLeft;

        // 2. Si hay obstáculos, buscar en radio <= 4 casillas
        for (int r = 1; r <= 4; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    Point candCenter = new(center.X + dx, center.Y + dy);
                    Point candTopLeft = new(candCenter.X - 1, candCenter.Y - 1);
                    if (Is3x3AreaClearAndSafe(loc, candTopLeft))
                        return candTopLeft;
                }
            }
        }
        return null;
    }

    private static bool Is3x3AreaClearAndSafe(GameLocation loc, Point topLeft)
    {
        int width = loc.Map?.Layers[0]?.LayerWidth ?? 100;
        int height = loc.Map?.Layers[0]?.LayerHeight ?? 100;

        for (int x = topLeft.X; x < topLeft.X + 3; x++)
        {
            for (int y = topLeft.Y; y < topLeft.Y + 3; y++)
            {
                if (x < 2 || x >= width - 2 || y < 2 || y >= height - 2)
                    return false;

                Vector2 v = new Vector2(x, y);

                // Proteger cofres, máquinas, aspersores y cualquier objeto del jugador
                if (loc.objects.ContainsKey(v))
                    return false;

                // Proteger cultivos, árboles, caminos y cualquier terrainFeature del jugador
                if (loc.terrainFeatures.ContainsKey(v))
                    return false;

                // Evitar solapamiento con warps
                if (loc.warps != null)
                {
                    foreach (var w in loc.warps)
                    {
                        if (Math.Abs(w.X - x) <= 1 && Math.Abs(w.Y - y) <= 1)
                            return false;
                    }
                }

                // Asegurar que el suelo sea transitable, no sea agua y sea colocable
                if (!loc.isTilePassable(v) || loc.isWaterTile(x, y) || !loc.isTilePlaceable(v, false))
                    return false;
            }
        }
        return true;
    }

    private void TickCampOutdoorIdle(NpcJob job, NPC npc)
    {
        if (npc == null) return;
        npc.Halt();
        npc.controller = null;
        npc.temporaryController = null;

        if (Game1.random.NextDouble() < 0.003)
        {
            var owner = GetCompanionOwner(npc.Name) ?? Game1.player;
            string pName = owner?.Name ?? "Player";
            string[] starKeys = new[] { "night.camp.stargazing.v1", "night.camp.stargazing.v2" };
            string chosen = starKeys[Game1.random.Next(starKeys.Length)];
            SpeakNonRepeating(npc, chosen, new { playerName = pName, npcName = npc.displayName, farmerName = pName });
            npc.doEmote(20);
        }
    }
    private void ExecuteSleepoverEvent(NPC npc, Farmer? who, string? targetLocationName = null)
    {
        var loc = npc.currentLocation;
        if (loc == null) return;
        var farmer = who ?? Game1.player;

        long myFarmerId = farmer?.UniqueMultiplayerID ?? 0;
        string myFarmerName = farmer?.Name ?? string.Empty;

        // Register as overnight guest
        _overnightGuests[npc.Name] = new OvernightGuestInfo
        {
            NpcName = npc.Name,
            FarmerId = myFarmerId,
            DidSleepover = true
        };

        _nightPromptedTonight.Add(npc.Name);

        bool isOutsideFarmAndHouse = !(loc is Farm) && !(loc is StardewValley.Locations.FarmHouse) && !loc.NameOrUniqueName.Contains("Cabin");

        if (isOutsideFarmAndHouse)
        {
            npc.doEmote(20); // Heart
            SpeakNonRepeating(npc, "night.stay_in_house.will_follow_home", new { playerName = farmer?.Name ?? "Player", npcName = npc.displayName, farmerName = farmer?.Name ?? "Player", ownerName = farmer?.Name ?? "Player" });
        }
        else
        {
            npc.doEmote(20); // Heart
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.night_sleepover_walk_in"), duration: 3800);
        }

        // Create the active job
        var job = new NpcJob
        {
            NpcName = npc.Name,
            Kind = isOutsideFarmAndHouse ? JobKind.Follow : JobKind.Sleepover,
            FarmerId = myFarmerId,
            FarmerName = myFarmerName,
            CreatedTick = Game1.ticks
        };
        if (!string.IsNullOrEmpty(targetLocationName))
        {
            job.Data["targetHouseName"] = targetLocationName;
        }
        if (isOutsideFarmAndHouse)
        {
            job.Data["stay_in_house_on_entry"] = true;
        }
        _active[npc.Name] = job;
        SetCompanion(npc.Name, myFarmerId, isVisitingFarm: true, isFollowing: true, farmerName: myFarmerName);

        if (_companions.TryGetValue(npc.Name, out var comp))
        {
            comp.FarmerId = myFarmerId;
            comp.FarmerName = myFarmerName;
            comp.EveningResolution = EveningResolutionState.StayInHouse;
            comp.NightState = NightLifecycleState.NightSettled;
            comp.IsRestingInPlace = false;
            comp.IsFollowing = true;
            comp.IsVisitingFarm = true;
            comp.WokeUpByPlayerTonight = false;
        }

        // Find target house/cabin location only if inside Farm or FarmHouse
        if (!isOutsideFarmAndHouse)
        {
            GameLocation? targetHouse = null;
            if (!string.IsNullOrEmpty(targetLocationName))
            {
                targetHouse = FindLocationByNameOrPlayer(targetLocationName, farmer);
            }
            if (targetHouse == null && farmer?.currentLocation is StardewValley.Locations.FarmHouse fh)
            {
                targetHouse = fh;
            }

            // If player is already inside FarmHouse/Cabin, warp NPC inside right away safely away from door warp
            if (targetHouse != null && (targetHouse is StardewValley.Locations.FarmHouse || targetHouse.NameOrUniqueName.Contains("Cabin")))
            {
                Point entry = (targetHouse is StardewValley.Locations.FarmHouse tfh) ? tfh.getEntryLocation() : new Point(3, 11);
                Point interiorSpawn = FindSafeIndoorSpawnTile(targetHouse, entry, farmer);
                Game1.warpCharacter(npc, targetHouse, new Vector2(interiorSpawn.X, interiorSpawn.Y));
                npc.controller = null;
                npc.Halt();
                npc.faceDirection(2);
                npc.doEmote(20);

                // Set init pause so TickIndoorRoaming doesn't fire emote spam immediately
                if (_companions.TryGetValue(npc.Name, out var compWarp))
                {
                    compWarp.IndoorInitPauseUntilTick = ModEntry.CurrentTick + 180; // 3s pause
                    compWarp.HasEmotedAtCurrentPoi = false;
                    compWarp.LastIndoorPoi = null;
                    compWarp.NightState = NightLifecycleState.IndoorRoaming;
                    compWarp.IsSettledInsideHouseForNight = true;
                    compWarp.SettledHouseName = targetHouse.NameOrUniqueName;
                }
            }
        }

        SetEveningResolution(npc.Name, EveningResolutionState.StayingInHouse);
        RecordMemory(npc.Name, "I agreed to sleep over at the farmer's house for the night. We spent cozy time together indoors until bedtime.");
        NotifyFarmer(farmer, Utilities.I18n.Get("hud.sleepover.staying_tonight", new { npcName = npc.displayName }), isError: false, hudType: HUDMessage.newQuest_type);
    }

    private void TickSleepover(NpcJob job, NPC npc, ulong gameTick)
    {
        var loc = npc.currentLocation;
        if (loc == null) return;
        var farmer = GetJobFarmer(job);
        _companions.TryGetValue(npc.Name, out var comp);

        bool isNpcInHouse = loc is StardewValley.Locations.FarmHouse || (loc != null && loc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase));

        // ── ANCLAJE NOCTURNO INTERIOR (v4.3.3 — FrozenHousePosition) ──
        // Si el NPC está congelado esperando a que el jugador regrese a casa, no ejecutar rutas
        if (comp?.IsPausedWhileOwnerOutsideHouse == true && isNpcInHouse)
            return;

        if (isNpcInHouse && loc != null)
        {
            string settledName = loc.NameOrUniqueName;
            if (comp != null)
            {
                comp.IsSettledInsideHouseForNight = true;
                comp.SettledHouseName = settledName;
            }
            job.Data["targetHouseName"] = settledName;

            // Continúa ejecutando su rutina orgánica interior aunque el granjero esté temporalmente afuera
            if (loc is StardewValley.Locations.FarmHouse farmHouse)
            {
                if (farmer != null && (_config == null || _config.EnableCozyIndoorEvening))
                {
                    StartOrTickIndoorEvening(job, npc, farmHouse, farmer, gameTick);
                    return;
                }

                // BEDTIME ROUTINE: 11:00 PM (2300) or later
                if (Game1.timeOfDay >= 2300)
                {
                    bool announcedBedtime = job.Data.TryGetValue("announcedBedtime", out var ab) && (bool)ab;
                    if (!announcedBedtime)
                    {
                        job.Data["announcedBedtime"] = true;
                        npc.controller = null;
                        npc.Halt();
                        npc.doEmote(24); // Sleepy yawn
                        int bedtimeIdx = Game1.random.Next(1, 4);
                        npc.showTextAboveHead(Utilities.I18n.Get($"bubble.sleepover.bedtime_{bedtimeIdx}"), duration: 3800);
                    }

                    // Locate player bed spot and find open, walkable bedside tile
                    Point rawBed = farmHouse.GetPlayerBedSpot();
                    if (rawBed == Point.Zero)
                        rawBed = new Point(farmHouse.getEntryLocation().X, farmHouse.getEntryLocation().Y - 6);

                    Point bedSide = FindWalkableBedsideSpot(farmHouse, rawBed);
                    float distToBed = Vector2.Distance(npc.Tile, new Vector2(bedSide.X, bedSide.Y));
                    if (distToBed > 1.2f)
                    {
                        MoveNpcTowards(job, npc, farmHouse, bedSide, gameTick);
                    }
                    else
                    {
                        // At bedside: halt, face the bed, stop animation and rest peacefully
                        if (npc.controller != null) { npc.controller = null; npc.Halt(); }
                        int dx = rawBed.X - bedSide.X;
                        int dy = rawBed.Y - bedSide.Y;
                        if (Math.Abs(dx) >= Math.Abs(dy))
                            npc.faceDirection(dx > 0 ? 1 : 3);
                        else
                            npc.faceDirection(dy > 0 ? 2 : 0);

                        npc.Sprite?.StopAnimation();

                        if (gameTick % 360 == 0 && Game1.random.NextDouble() < 0.30)
                        {
                            npc.doEmote(24);
                        }
                    }
                    return;
                }

                // BEFORE 11:00 PM: Follow farmer smoothly inside house or relax nearby!
                if (farmer != null && farmer.currentLocation == farmHouse)
                {
                    float distToFarmer = Vector2.Distance(npc.Tile, farmer.Tile);
                    if (distToFarmer > 2.8f)
                    {
                        Point nearFarmer = GetAdjacentWalkableTile(farmHouse, farmer.TilePoint, npc.TilePoint);
                        MoveNpcTowards(job, npc, farmHouse, nearFarmer, gameTick);
                        return;
                    }
                    else
                    {
                        npc.faceTowardFarmerForPeriod(2000, 3, false, farmer);
                        if (gameTick % 360 == 0 && Game1.random.NextDouble() < 0.30)
                        {
                            int[] humanEmotes = new[] { 8, 20, 40, 56, 60, (Game1.timeOfDay >= 2100 ? 24 : 8) };
                            npc.doEmote(humanEmotes[Game1.random.Next(humanEmotes.Length)]);
                            if (Game1.random.NextDouble() < 0.35)
                            {
                                int cozyIdx = Game1.random.Next(1, 6);
                                npc.showTextAboveHead(Utilities.I18n.Get($"bubble.sleepover.cozy_{cozyIdx}"), duration: 3500);
                            }
                        }
                    }
                }
                else
                {
                    // Farmer stepped away: relax in cozy spot of living room / near fireplace
                    int lastActionTick = job.Data.TryGetValue("lastIndoorActionTick", out var lat) ? (int)lat : 0;
                    if (((int)gameTick - lastActionTick) >= 240)
                    {
                        job.Data["lastIndoorActionTick"] = (int)gameTick;
                        Point cozySpot = FindCozyIndoorSpot(farmHouse);
                        if (IsTileWalkable(farmHouse, cozySpot) && Vector2.Distance(npc.Tile, new Vector2(cozySpot.X, cozySpot.Y)) > 1.8f)
                        {
                            MoveNpcTowards(job, npc, farmHouse, cozySpot, gameTick);
                        }
                        else
                        {
                            npc.faceDirection(Game1.random.Next(4));
                            if (Game1.random.NextDouble() < 0.25)
                                npc.doEmote(Game1.timeOfDay >= 2100 ? 24 : 40);
                        }
                    }
                }
            }
            return;
        }

        // OUTSIDE (not yet inside the farmhouse):
        string? targetHouseName = job.Data.TryGetValue("targetHouseName", out var thn) ? thn?.ToString() : null;
        GameLocation? targetHouse = FindLocationByNameOrPlayer(targetHouseName, farmer);
        if (targetHouse == null && farmer?.currentLocation is StardewValley.Locations.FarmHouse fh)
            targetHouse = fh;

        bool farmerInHouse = (farmer?.currentLocation != null && (farmer.currentLocation == targetHouse || farmer.currentLocation is StardewValley.Locations.FarmHouse || farmer.currentLocation.NameOrUniqueName.Contains("Cabin")));
        if (farmerInHouse && targetHouse == null) targetHouse = farmer!.currentLocation;

        // If farmer is in FarmHouse/Cabin and NPC is not in the same location, warp NPC inside safely next to farmer
        if (farmerInHouse && targetHouse != null && loc != targetHouse)
        {
            Point entry = (targetHouse is StardewValley.Locations.FarmHouse tfh) ? tfh.getEntryLocation() : new Point(3, 11);
            Point interiorSpawn = FindSafeIndoorSpawnTile(targetHouse, entry, farmer);
            Game1.warpCharacter(npc, targetHouse, new Vector2(interiorSpawn.X, interiorSpawn.Y));
            npc.controller = null;
            npc.Halt();
            npc.faceDirection(2);
            npc.doEmote(20);
            if (comp != null)
            {
                comp.IsSettledInsideHouseForNight = true;
                comp.SettledHouseName = targetHouse.NameOrUniqueName;
            }
            job.Data["targetHouseName"] = targetHouse.NameOrUniqueName;
            return;
        }

        // If outside (not yet inside the farmhouse): follow the farmer home smoothly!
        if (loc != targetHouse && !farmerInHouse)
        {
            if (loc != null && farmer != null && farmer.currentLocation != null && loc.NameOrUniqueName != farmer.currentLocation.NameOrUniqueName)
            {
                Point entry = FindSafeWarpSpawnTile(farmer.currentLocation, farmer.TilePoint, farmer.FacingDirection);
                StopNpcCleanly(npc);
                Game1.warpCharacter(npc, farmer.currentLocation, new Vector2(entry.X, entry.Y));
                job.Data["follow_pause_until"] = gameTick + 18;
                return;
            }

            if (farmer != null && farmer.currentLocation == loc)
            {
                TickFollow(job, npc, gameTick);
                return;
            }
            else if (loc is Farm farmLoc)
            {
                Point porch = GetFarmSafePorchTile(farmLoc);
                float distToPorch = Vector2.Distance(npc.Tile, new Vector2(porch.X, porch.Y));
                if (distToPorch > 1.8f)
                {
                    MoveNpcTowards(job, npc, farmLoc, porch, gameTick);
                }
                else
                {
                    if (npc.controller != null) { npc.controller = null; npc.Halt(); }
                    npc.faceDirection(0); // Face porch door waiting for farmer
                }
                return;
            }
        }
    }

    private static Point FindWalkableBedsideSpot(GameLocation loc, Point bedSpot)
    {
        Point[] candidates = new[]
        {
            new Point(bedSpot.X - 1, bedSpot.Y),
            new Point(bedSpot.X + 2, bedSpot.Y),
            new Point(bedSpot.X - 1, bedSpot.Y + 1),
            new Point(bedSpot.X + 2, bedSpot.Y + 1),
            new Point(bedSpot.X, bedSpot.Y + 2),
            new Point(bedSpot.X + 1, bedSpot.Y + 2),
            new Point(bedSpot.X, bedSpot.Y - 1),
            new Point(bedSpot.X + 1, bedSpot.Y - 1)
        };

        foreach (var p in candidates)
        {
            if (IsTileWalkable(loc, p))
                return p;
        }

        for (int r = 2; r <= 4; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    Point p = new Point(bedSpot.X + dx, bedSpot.Y + dy);
                    if (IsTileWalkable(loc, p))
                        return p;
                }
            }
        }

        return bedSpot;
    }

    private static Point FindCozyIndoorSpot(StardewValley.Locations.FarmHouse fh)
    {
        Point entry = fh.getEntryLocation();
        Point[] spots = new[]
        {
            new Point(entry.X, Math.Max(1, entry.Y - 3)),
            new Point(entry.X - 2, Math.Max(1, entry.Y - 3)),
            new Point(entry.X + 2, Math.Max(1, entry.Y - 3)),
            new Point(entry.X, Math.Max(1, entry.Y - 5))
        };
        foreach (var s in spots)
        {
            if (IsTileWalkable(fh, s))
                return s;
        }
        return new Point(entry.X, Math.Max(1, entry.Y - 2));
    }

    private static void GetMorningGoodbyeNote(NPC guest, Farmer? targetFarmer, out string title, out string content)
    {
        string pName = targetFarmer?.displayName ?? Game1.player?.displayName ?? "friend";
        title = Utilities.I18n.Get("letter.morning.title", new { npcName = guest.displayName });

        string charKey = $"letter.morning.{guest.Name.ToLowerInvariant()}";
        string charContent = Utilities.I18n.Get(charKey, new { farmerName = pName, npcName = guest.displayName });
        if (charContent != charKey && !string.IsNullOrWhiteSpace(charContent))
        {
            content = charContent;
            return;
        }

        int randIdx = Game1.random.Next(1, 4);
        content = Utilities.I18n.Get($"letter.morning.random_{randIdx}", new { farmerName = pName, npcName = guest.displayName });
    }

    private void ShowNightDialogue(NPC npc, Farmer? who)
    {
        if (npc == null || Game1.currentLocation == null) return;
        var farmer = who ?? Game1.player;
        int hearts = farmer != null ? farmer.getFriendshipHeartLevelForNPC(npc.Name) : 0;

        npc.controller = null;
        npc.Halt();
        if (farmer != null)
            npc.faceTowardFarmerForPeriod(4000, 3, false, farmer);

        string chosenDialogue = hearts >= 7
            ? Utilities.I18n.Get($"night.chat.high_{Game1.random.Next(1, 6)}", new { farmerName = farmer?.displayName ?? "" })
            : (hearts >= 4
                ? Utilities.I18n.Get($"night.chat.mid_{Game1.random.Next(1, 5)}")
                : Utilities.I18n.Get($"night.chat.casual_{Game1.random.Next(1, 4)}"));

        npc.doEmote(hearts >= 7 ? 20 : (hearts >= 4 ? 32 : 16)); // Heart / Smile / Note
        npc.showTextAboveHead(chosenDialogue, duration: 4800);
        RecordMemory(npc.Name, $"We had a heartfelt conversation together under the starry night sky on the farm: \"{chosenDialogue}\"");

        try
        {
            TryAwardDailyActivityFriendship(farmer, npc, "night_dialogue", 10, 10);
        }
        catch { }

        DelayedAction.functionAfterDelay(() =>
        {
            if (Game1.activeClickableMenu == null)
            {
                ShowFollowerActionMenu(npc, farmer);
            }
        }, 5000);
    }

    private void UpdateCompanionNightTorches(ulong gameTick)
    {
        if (gameTick % 10 != 0) return;
        if (_companions.IsEmpty && _activeNightTorches.IsEmpty) return;

        // Purge any tracked torches whose companion was dismissed or vanished
        foreach (var trackedName in _activeNightTorches.Keys.ToList())
        {
            if (!_companions.ContainsKey(trackedName))
            {
                RemoveCompanionNightTorch(trackedName);
            }
        }

        foreach (var kvp in _companions)
        {
            var compNpc = FindNpc(kvp.Key);
            if (compNpc == null || compNpc.currentLocation == null)
            {
                RemoveCompanionNightTorch(kvp.Key);
                continue;
            }

            var loc = compNpc.currentLocation;
            string curLocName = loc.NameOrUniqueName ?? string.Empty;
            string lightId = $"AliveNpcs_Torch_{compNpc.Name}";

            if (_activeNightTorches.TryGetValue(compNpc.Name, out string? prevLocName) &&
                !string.Equals(prevLocName, curLocName, StringComparison.OrdinalIgnoreCase))
            {
                var prevLoc = Game1.getLocationFromName(prevLocName);
                if (prevLoc != null && prevLoc.hasLightSource(lightId))
                    prevLoc.removeLightSource(lightId);
            }

            bool isDark = Game1.timeOfDay >= 1800 || (!loc.IsOutdoors && curLocName.Contains("Mine", StringComparison.OrdinalIgnoreCase));

            if (isDark && !compNpc.IsInvisible)
            {
                Vector2 torchPos = compNpc.Position + new Vector2(32f, 16f);
                if (loc.hasLightSource(lightId))
                {
                    loc.repositionLightSource(lightId, torchPos);
                }
                else
                {
                    loc.sharedLights[lightId] = new LightSource(
                        lightId,
                        1, // LightSource.lantern
                        torchPos,
                        2.8f,
                        new Color(0, 80, 255), // warm golden lantern glow
                        LightSource.LightContext.None,
                        0L,
                        curLocName
                    );
                }
                _activeNightTorches[compNpc.Name] = curLocName;
            }
            else
            {
                if (loc.hasLightSource(lightId))
                {
                    loc.removeLightSource(lightId);
                }
                _activeNightTorches.TryRemove(compNpc.Name, out _);
            }
        }
    }

    private void ExecuteSleepTentTogetherEvent(NPC npc, Farmer? who)
    {
        if (Game1.IsMultiplayer && !Context.IsMainPlayer) return;

        var loc = npc.currentLocation;
        var farmer = who ?? Game1.player;
        if (loc == null || !loc.IsOutdoors || loc is StardewValley.Locations.FarmHouse || loc is StardewValley.Locations.Cabin)
        {
            NotifyFarmer(farmer, Utilities.I18n.Get("error.camp_indoors_not_allowed"), isError: true, hudType: HUDMessage.error_type);
            return;
        }

        if (_overnightGuests.TryGetValue(npc.Name, out var existingTentGuest) && existingTentGuest.DidCampfire)
        {
            bool campExists = DoesCampStillExist(loc, existingTentGuest)
                || Game1.locations.Any(l => l != null && DoesCampStillExist(l, existingTentGuest))
                || GetEveningResolution(npc.Name) == EveningResolutionState.Camping;
            if (campExists)
            {
                long reFarmerId = existingTentGuest.FarmerId != 0 ? existingTentGuest.FarmerId : (farmer?.UniqueMultiplayerID ?? 0);
                string reFarmerName = Game1.GetPlayer(reFarmerId, onlyOnline: false)?.Name ?? farmer?.Name ?? string.Empty;
                SetCompanion(npc.Name, reFarmerId, isVisitingFarm: true, isFollowing: false, farmerName: reFarmerName);
                if (_companions.TryGetValue(npc.Name, out var existingTentComp))
                {
                    existingTentComp.NightState = NightLifecycleState.CampOutdoorIdle;
                    existingTentComp.EveningResolution = EveningResolutionState.Camping;
                    existingTentComp.IsFollowing = false;
                    existingTentComp.IsRestingInPlace = true;
                }
                SetEveningResolution(npc.Name, EveningResolutionState.Camping);
                _active[npc.Name] = CreateCampJobWithCoords(npc.Name, JobKind.CampSleepTogether, reFarmerId, reFarmerName, existingTentGuest);
                return;
            }
        }

        // 1. Find placement for tent (placed behind / north of the campout)
        Vector2 tentVec = npc.Tile + new Vector2(0, -2);
        if (!IsTileWalkable(loc, new Point((int)tentVec.X, (int)tentVec.Y)) || loc.objects.ContainsKey(tentVec) || loc.terrainFeatures.ContainsKey(tentVec))
        {
            foreach (var offset in new[] { new Vector2(0, -2), new Vector2(2, -1), new Vector2(-2, -1), new Vector2(2, 0), new Vector2(-2, 0), new Vector2(1, -2) })
            {
                var test = npc.Tile + offset;
                if (test != npc.Tile && IsTileWalkable(loc, new Point((int)test.X, (int)test.Y)) && !loc.objects.ContainsKey(test) && !loc.terrainFeatures.ContainsKey(test))
                {
                    tentVec = test;
                    break;
                }
            }
        }

        // 2. Find realistic placement for campfire in front of the tent (2 tiles south)
        Vector2 campVec = tentVec + new Vector2(0, 2);
        if (!IsTileWalkable(loc, new Point((int)campVec.X, (int)campVec.Y)) || loc.objects.ContainsKey(campVec) || campVec == tentVec || campVec == npc.Tile)
        {
            foreach (var offset in new[] { new Vector2(0, 2), new Vector2(1, 2), new Vector2(-1, 2), new Vector2(0, 3), new Vector2(2, 1), new Vector2(-2, 1) })
            {
                var test = tentVec + offset;
                if (test != tentVec && test != npc.Tile && IsTileWalkable(loc, new Point((int)test.X, (int)test.Y)) && !loc.objects.ContainsKey(test))
                {
                    campVec = test;
                    break;
                }
            }
        }

        Point campTile = new Point((int)campVec.X, (int)campVec.Y);
        Point tentTile = new Point((int)tentVec.X, (int)tentVec.Y);

        loc.playSound("axe");
        loc.playSound("fireball");

        PlaceTemporaryCampfireObject(loc, campVec);

        string campLightId = $"ValleyBuddies_Campfire_{campTile.X}_{campTile.Y}";
        if (!loc.hasLightSource(campLightId))
        {
            loc.sharedLights[campLightId] = new LightSource(
                campLightId,
                1,
                new Vector2(campVec.X * 64f + 32f, campVec.Y * 64f + 32f),
                4.2f,
                new Color(0, 80, 255),
                LightSource.LightContext.None,
                0L,
                loc.NameOrUniqueName
            );
        }

        try
        {
            var tent = new StardewValley.TerrainFeatures.Tent(tentTile.ToVector2());
            tent.modData["Juxxan.ValleyBuddies/TempCamp"] = "true";
            loc.terrainFeatures[tentVec] = tent;
        }
        catch { }

        // If local player, run the cinematic cutscene under the stars
        if (farmer != null && farmer == Game1.player)
        {
            Point desiredFarmerTile = new Point((int)Math.Round(campVec.X - 1f), (int)Math.Round(campVec.Y + 1f));
            if (!IsTileWalkable(loc, desiredFarmerTile, null) || desiredFarmerTile == campTile || desiredFarmerTile == tentTile)
                desiredFarmerTile = GetAdjacentWalkableTile(loc, campTile, farmer.TilePoint);

            Point desiredNpcTile = new Point((int)Math.Round(campVec.X + 1f), (int)Math.Round(campVec.Y + 1f));
            if (!IsTileWalkable(loc, desiredNpcTile, npc) || desiredNpcTile == campTile || desiredNpcTile == tentTile || desiredNpcTile == desiredFarmerTile)
                desiredNpcTile = GetAdjacentWalkableTile(loc, campTile, npc.TilePoint, npc);

            Game1.freezeControls = true;
            Game1.globalFadeToBlack(() =>
            {
                // Align positions sitting/standing in front of the campfire looking at the stars and fire
                farmer.Position = desiredFarmerTile != Point.Zero
                    ? new Vector2(desiredFarmerTile.X * 64f, desiredFarmerTile.Y * 64f)
                    : (campVec + new Vector2(-0.8f, 1.2f)) * 64f;
                npc.Position = desiredNpcTile != Point.Zero
                    ? new Vector2(desiredNpcTile.X * 64f, desiredNpcTile.Y * 64f)
                    : (campVec + new Vector2(0.8f, 1.2f)) * 64f;
                farmer.faceDirection(0); // Face up towards the campfire, tent and night sky
                npc.faceDirection(0);
                npc.controller = null;
                npc.Halt();

                Game1.globalFadeToClear(() =>
                {
                    // Spawn glowing stars and floating embers
                    for (int i = 0; i < 16; i++)
                    {
                        Vector2 p = (npc.Position + farmer.Position) / 2f + new Vector2(Game1.random.Next(-50, 51), Game1.random.Next(-30, 31));
                        BroadcastTemporarySprite(loc, new TemporaryAnimatedSprite(
                            "LooseSprites\\Cursors",
                            new Rectangle(372, 1956, 10, 10),
                            p,
                            false,
                            0.002f,
                            Color.Gold)
                        {
                            scale = 2.5f,
                            motion = new Vector2((float)(Game1.random.NextDouble() * 1.6 - 0.8), (float)(Game1.random.NextDouble() * -2.0 - 0.8)),
                            acceleration = new Vector2(0f, -0.01f),
                            interval = 80f,
                            totalNumberOfLoops = 2,
                            animationLength = 6
                        });
                    }

                    // Act 1: Look at the stars and constellations
                    loc.playSound("crystal");
                    npc.doEmote(16); // Melody / Note
                    farmer.doEmote(16);
                    string starLine = GetStargazingDialogue(npc, farmer);
                    npc.showTextAboveHead(starLine, duration: 4200);

                    // Act 2: Look at each other and blush tenderly
                    DelayedAction.functionAfterDelay(() =>
                    {
                        try
                        {
                            npc.faceTowardFarmerForPeriod(4500, 3, false, farmer);
                            farmer.faceGeneralDirection(npc.Position);
                            loc.playSound("dwop");
                            npc.doEmote(52); // Blush!
                            farmer.doEmote(52);

                            int tenderIdx = Game1.random.Next(1, 4);
                            string tenderKey = npc.Gender == Gender.Male ? $"bubble.camp.tender_m_{tenderIdx}" : $"bubble.camp.tender_f_{tenderIdx}";
                            string chosenTender = Utilities.I18n.Get(tenderKey, new { farmerName = farmer.displayName });
                            npc.showTextAboveHead(chosenTender, duration: 4200);
                        }
                        catch { }
                    }, 4600);

                    // Act 3: Heart emote and fade into tent sleepover
                    DelayedAction.functionAfterDelay(() =>
                    {
                        try
                        {
                            npc.doEmote(20); // Heart
                            farmer.doEmote(20);
                            loc.playSound("yoba");
                            loc.playSound("axe");

                            Game1.globalFadeToBlack(() =>
                            {
                                try
                                {
                                    farmer.changeFriendship(40, npc);
                                }
                                catch { }

                                SetEveningResolution(npc.Name, EveningResolutionState.Camping);
                                npc.followSchedule = false;
                                
                                npc.controller = null;
                                npc.temporaryController = null;
                                npc.Halt();

                                SetCompanion(npc.Name, farmer.UniqueMultiplayerID, isVisitingFarm: true, isFollowing: false, farmerName: farmer.Name);
                                if (_companions.TryGetValue(npc.Name, out var compStateTogether))
                                {
                                    compStateTogether.IsFollowing = false;
                                    compStateTogether.IsRestingInPlace = true;
                                }

                                var tentJob = new NpcJob
                                {
                                    NpcName = npc.Name,
                                    Kind = JobKind.CampSleepTogether,
                                    FarmerId = farmer.UniqueMultiplayerID,
                                    FarmerName = farmer.Name,
                                    CreatedTick = Game1.ticks
                                };
                                tentJob.Data["campTileX"] = campTile.X;
                                tentJob.Data["campTileY"] = campTile.Y;
                                tentJob.Data["tentTileX"] = tentTile.X;
                                tentJob.Data["tentTileY"] = tentTile.Y;
                                _active[npc.Name] = tentJob;

                                _overnightGuests[npc.Name] = new OvernightGuestInfo
                                {
                                    NpcName = npc.Name,
                                    FarmerId = farmer.UniqueMultiplayerID,
                                    DidCampfire = true,
                                    DidSleepover = false,
                                    CampfireTile = campTile,
                                    TentTile = tentTile
                                };

                                RecordMemory(npc.Name, $"We shared an intimate starry night campout in the tent on the farm, stargazing by the warm campfire together.");
                                NotifyFarmer(farmer, Utilities.I18n.Get("hud.camp.shared_starry_night", new { npcName = npc.displayName }), isError: false, hudType: HUDMessage.newQuest_type);

                                Game1.globalFadeToClear(() =>
                                {
                                    Game1.freezeControls = false;
                                }, 0.02f);
                            }, 0.02f);
                        }
                        catch
                        {
                            Game1.freezeControls = false;
                        }
                    }, 9200);
                }, 0.02f);
            }, 0.02f);

            // Safety timeout to ensure player is NEVER stuck frozen under any edge case
            DelayedAction.functionAfterDelay(() =>
            {
                Game1.freezeControls = false;
            }, 14000);
        }
        else
        {
            // Remote player / background handling
            npc.controller = null;
            npc.Halt();
            if (farmer != null)
                npc.faceTowardFarmerForPeriod(4000, 3, false, farmer);

            npc.doEmote(20);
            farmer?.doEmote(52);

            try
            {
                farmer?.changeFriendship(40, npc);
            }
            catch { }

            string starLine = GetStargazingDialogue(npc, farmer ?? Game1.player);
            npc.showTextAboveHead(starLine, duration: 5000);

            _overnightGuests[npc.Name] = new OvernightGuestInfo
            {
                NpcName = npc.Name,
                FarmerId = farmer?.UniqueMultiplayerID ?? 0,
                DidCampfire = true,
                DidSleepover = false,
                CampfireTile = campTile,
                TentTile = tentTile
            };

            SetEveningResolution(npc.Name, EveningResolutionState.Camping);
            npc.followSchedule = false;
            
            npc.controller = null;
            npc.temporaryController = null;
            npc.Halt();

            if (farmer != null)
            {
                SetCompanion(npc.Name, farmer.UniqueMultiplayerID, isVisitingFarm: true, isFollowing: false, farmerName: farmer.Name);
                if (_companions.TryGetValue(npc.Name, out var compStateRem))
                {
                    compStateRem.IsFollowing = false;
                    compStateRem.IsRestingInPlace = true;
                }
            }

            var remoteTentJob = new NpcJob
            {
                NpcName = npc.Name,
                Kind = JobKind.CampSleepTogether,
                FarmerId = farmer?.UniqueMultiplayerID ?? 0,
                FarmerName = farmer?.Name ?? string.Empty,
                CreatedTick = Game1.ticks
            };
            remoteTentJob.Data["campTileX"] = campTile.X;
            remoteTentJob.Data["campTileY"] = campTile.Y;
            remoteTentJob.Data["tentTileX"] = tentTile.X;
            remoteTentJob.Data["tentTileY"] = tentTile.Y;
            _active[npc.Name] = remoteTentJob;

            RecordMemory(npc.Name, $"We shared an intimate starry night campout in the tent on the farm: \"{starLine}\"");
            NotifyFarmer(farmer, Utilities.I18n.Get("hud.camp.share_tent_tonight", new { npcName = npc.displayName }), isError: false, hudType: HUDMessage.newQuest_type);
        }
    }

    private void NotifyFarmer(Farmer? owner, string message, bool isError = true, int hudType = HUDMessage.error_type)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (owner == null || owner.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
        {
            if (isError)
                Game1.showRedMessage(message);
            else
                Game1.addHUDMessage(new HUDMessage(message, hudType));
        }
        else
        {
            _multiplayerSync?.SendPrivateNotice(owner.UniqueMultiplayerID, message, isError, hudType);
        }
    }

    private static string GetStargazingDialogue(NPC npc, Farmer farmer)
    {
        string pName = farmer?.displayName ?? "friend";
        string key = $"dialogue.stargaze.{npc.Name.ToLowerInvariant()}";
        string text = Utilities.I18n.Get(key, new { farmerName = pName });
        if (text == key || string.IsNullOrWhiteSpace(text))
        {
            text = Utilities.I18n.Get("dialogue.stargaze.default", new { farmerName = pName });
        }
        return text;
    }

    /// <summary>
    /// Anchors the NPC safely near the campfire and camping tent throughout the night,
    /// preventing vanilla schedule resumption or rogue pathfinding towards map exits.
    /// </summary>
    private void TickCamping(NpcJob job, NPC npc, ulong gameTick)
    {
        if (npc == null || npc.currentLocation == null) return;

        if (!job.Data.ContainsKey("campTileX"))
        {
            if (_overnightGuests.TryGetValue(npc.Name, out var existingGuest) && existingGuest.DidCampfire)
            {
                job.Data["campTileX"] = existingGuest.CampfireTile.X;
                job.Data["campTileY"] = existingGuest.CampfireTile.Y;
                job.Data["tentTileX"] = existingGuest.TentTile.X;
                job.Data["tentTileY"] = existingGuest.TentTile.Y;
            }
            else
            {
                if (Game1.IsMultiplayer && !Context.IsMainPlayer) return;
                var farmer = GetJobFarmer(job) ?? Game1.player;
                ExecuteCampfireEvent(npc, farmer);
                if (!_overnightGuests.ContainsKey(npc.Name))
                {
                    job.Done = true;
                }
                return;
            }
        }

        // Prevent vanilla schedule takeover or rogue pathfinding
        npc.followSchedule = false;
        
        if (npc.controller != null || npc.temporaryController != null)
        {
            npc.controller = null;
            npc.temporaryController = null;
            npc.Halt();
        }

        Point campTile = Point.Zero;
        if (job.Data.TryGetValue("campTileX", out var cx) && job.Data.TryGetValue("campTileY", out var cy))
        {
            campTile = new Point(Convert.ToInt32(cx), Convert.ToInt32(cy));
        }
        else if (_overnightGuests.TryGetValue(npc.Name, out var guest))
        {
            campTile = guest.CampfireTile;
        }

        if (campTile != Point.Zero)
        {
            Vector2 campVec = new Vector2(campTile.X, campTile.Y);
            float dist = Vector2.Distance(npc.Tile, campVec);
            if (dist > 2.5f)
            {
                Point safeSpot = GetAdjacentWalkableTile(npc.currentLocation, campTile, npc.TilePoint);
                if (safeSpot != Point.Zero)
                {
                    npc.Position = new Vector2(safeSpot.X * 64f, safeSpot.Y * 64f);
                }
                npc.Halt();
            }
            npc.faceGeneralDirection(new Vector2(campTile.X * 64f + 32f, campTile.Y * 64f + 32f));
        }

        // Periodic cozy emotes
        if (gameTick % 300 == 0)
        {
            if (Game1.timeOfDay >= 2300)
            {
                npc.doEmote(24); // Yawn
            }
            else
            {
                int[] cozyEmotes = new[] { 20, 32, 16 };
                npc.doEmote(cozyEmotes[Game1.random.Next(cozyEmotes.Length)]);
            }
        }

        if (gameTick % 600 == 0 && Game1.random.NextDouble() < 0.25)
        {
            var farmer = GetJobFarmer(job) ?? Game1.player;
            string pName = farmer?.Name ?? "Player";
            string[] starKeys = new[] { "night.camp.stargazing.v1", "night.camp.stargazing.v2" };
            string chosen = starKeys[Game1.random.Next(starKeys.Length)];
            SpeakNonRepeating(npc, chosen, new { playerName = pName, npcName = npc.displayName, farmerName = pName });
        }
    }
}
