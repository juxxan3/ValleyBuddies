using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using ValleyBuddies.Systems;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    public void HandleOfflineInviteConfirmed(NPC npc, Farmer who)
    {
        if (npc == null || who == null) return;

        if (Game1.IsMultiplayer && !Context.IsMainPlayer)
        {
            _multiplayerSync?.SendOfflineInviteRequest(npc.Name, who.UniqueMultiplayerID);
            return;
        }

        ProcessHostOfflineInvite(npc.Name, who.UniqueMultiplayerID);
    }

    public void ProcessHostOfflineInvite(string npcName, long requesterId)
    {
        if (!Context.IsMainPlayer) return;

        var npc = Game1.getCharacterFromName(npcName);
        var requester = Game1.GetPlayer(requesterId, true) ?? Game1.MasterPlayer;
        string requesterName = requester?.Name ?? "Player";
        string npcDisplayName = npc?.displayName ?? npcName;

        // 1. Chequeo atómico: ¿ya está con otro granjero?
        if (TryGetCompanionInfo(npcName, out var compInfo) && compInfo.FarmerId != 0 && compInfo.FarmerId != requesterId)
        {
            string otherPlayerName = !string.IsNullOrWhiteSpace(compInfo.FarmerName) ? compInfo.FarmerName : "otro granjero";
            if (requesterId == Game1.player.UniqueMultiplayerID)
            {
                Game1.showRedMessage(Utilities.I18n.Get("mp.invite.already_following_other", new { npcName = npcDisplayName, ownerName = otherPlayerName }));
            }
            else
            {
                _multiplayerSync?.SendOfflineInviteResult(requesterId, new Multiplayer.OfflineInviteResultMessage
                {
                    Success = false,
                    ReasonKey = "mp.invite.already_following_other",
                    NpcName = npcName,
                    RequesterFarmerId = requesterId,
                    OwnerFarmerId = compInfo.FarmerId,
                    OwnerFarmerName = otherPlayerName
                });
            }
            return;
        }

        // 2. Registrar al acompañante en el Host
        SetCompanion(npcName, requesterId, isVisitingFarm: false, isFollowing: true, farmerName: requesterName);
        Enqueue(new NpcJob
        {
            NpcName = npcName,
            Kind = JobKind.Follow,
            FarmerId = requesterId,
            FarmerName = requesterName,
            CreatedTick = Game1.ticks
        });

        // 3. Notificar a todos los peers
        if (Game1.IsMultiplayer)
        {
            ValleyBuddies.ModEntry.BroadcastJob(npcName, JobKind.Follow, requesterId, requesterName);
        }

        // 4. Si el solicitante es el Host local
        if (requesterId == Game1.player.UniqueMultiplayerID)
        {
            Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("offline.invite.recruited", new { npcName = npcDisplayName }), HUDMessage.newQuest_type));
            npc?.doEmote(20);
            if (npc != null && requester != null)
            {
                DelayedAction.functionAfterDelay(() =>
                {
                    ShowFollowerActionMenu(npc, requester);
                }, 200);
            }
        }
        else
        {
            // Solicitante remoto (Farmhand)
            _multiplayerSync?.SendOfflineInviteResult(requesterId, new Multiplayer.OfflineInviteResultMessage
            {
                Success = true,
                NpcName = npcName,
                RequesterFarmerId = requesterId,
                OwnerFarmerId = requesterId,
                OwnerFarmerName = requesterName
            });
        }
    }

    public void HandleOfflineInviteResultFromHost(Multiplayer.OfflineInviteResultMessage msg)
    {
        if (msg == null) return;

        var npc = Game1.getCharacterFromName(msg.NpcName);
        string npcDisplayName = npc?.displayName ?? msg.NpcName;

        if (msg.Success)
        {
            SetCompanion(msg.NpcName, msg.RequesterFarmerId, isVisitingFarm: false, isFollowing: true, farmerName: msg.OwnerFarmerName ?? string.Empty);

            Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("offline.invite.recruited", new { npcName = npcDisplayName }), HUDMessage.newQuest_type));
            if (npc != null && npc.currentLocation == Game1.currentLocation)
            {
                npc.doEmote(20);
                DelayedAction.functionAfterDelay(() =>
                {
                    ShowFollowerActionMenu(npc, Game1.player);
                }, 200);
            }
        }
        else
        {
            string ownerName = !string.IsNullOrWhiteSpace(msg.OwnerFarmerName) ? msg.OwnerFarmerName : "otro granjero";
            string reasonKey = !string.IsNullOrWhiteSpace(msg.ReasonKey) ? msg.ReasonKey : "mp.invite.already_following_other";
            Game1.showRedMessage(Utilities.I18n.Get(reasonKey, new { npcName = npcDisplayName, ownerName = ownerName }));
        }
    }

    public void ProcessHostNightActionRequest(string npcName, long requesterId, string action, string? locName)
    {
        if (!Context.IsMainPlayer) return;

        if (TryGetCompanionInfo(npcName, out var compInfo) && compInfo.FarmerId != 0 && compInfo.FarmerId != requesterId)
            return;

        var npc = Game1.getCharacterFromName(npcName);
        var requester = Game1.GetPlayer(requesterId, true) ?? Game1.MasterPlayer;
        if (npc == null || requester == null) return;

        switch (action)
        {
            case "stay_in_house":
            case "sleep_in":
                SetEveningResolution(npc.Name, EveningResolutionState.StayingInHouse);
                ExecuteSleepoverEvent(npc, requester);
                ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.Sleepover, requesterId, requester.Name);
                BroadcastNightStateSync(npc.Name);
                break;

            case "setup_camp":
                ExecuteCampfireEvent(npc, requester);
                BroadcastNightStateSync(npc.Name);
                break;

            case "leave_night":
                if (_companions.TryGetValue(npc.Name, out var cState))
                {
                    cState.EveningResolution = EveningResolutionState.Leaving;
                    cState.NightState = NightLifecycleState.DismissedForNight;
                    WakeAndExtractNpcFromBed(npc, npc.currentLocation, cState, requester);
                }
                else
                {
                    WakeAndExtractNpcFromBed(npc, npc.currentLocation, null, requester);
                }
                SetEveningResolution(npc.Name, EveningResolutionState.Leaving);
                var leavePlayerName = requester?.Name ?? "Player";
                SpeakNonRepeating(npc, "night.menu.leave_night_bye", new { playerName = leavePlayerName, npcName = npc.displayName, farmerName = leavePlayerName });
                DismissCompanion(npc.Name);
                BroadcastNightStateSync(npc.Name);
                break;
        }
    }

    public void BroadcastNightStateSync(string npcName, CompanionState? comp = null, NPC? npc = null, long? specificPeerId = null)
    {
        if (!Context.IsMainPlayer || !Context.IsMultiplayer) return;

        if (comp == null)
            _companions.TryGetValue(npcName, out comp);
        if (npc == null)
            npc = Game1.getCharacterFromName(npcName);

        var msg = new Multiplayer.NightStateSyncMessage
        {
            NpcName = npcName,
            OwnerFarmerId = comp?.FarmerId ?? 0,
            LocationName = npc?.currentLocation?.NameOrUniqueName ?? string.Empty,
            NightState = (int)(comp?.NightState ?? NightLifecycleState.DaytimeActive),
            EveningResolution = (int)(comp?.EveningResolution ?? EveningResolutionState.Undecided),
            IsRestingInPlace = comp?.IsRestingInPlace ?? false,
            IsSettledInsideHouseForNight = comp?.IsSettledInsideHouseForNight ?? false,
            IsSleepingInBed = comp?.NightState == NightLifecycleState.SleepingInBed || (npc != null && npc.isSleeping.Value),
            TargetBedPosition = comp?.LockedBedPixelPosition,
            BedTileX = comp?.AssignedBedSpot?.X ?? 0,
            BedTileY = comp?.AssignedBedSpot?.Y ?? 0,
            FrozenHousePosition = comp?.FrozenHousePosition,
            HasActiveTempCamp = _overnightGuests.ContainsKey(npcName)
        };

        _multiplayerSync?.BroadcastNightState(msg, specificPeerId);
    }

    public void ApplyNightStateSync(Multiplayer.NightStateSyncMessage msg)
    {
        if (msg == null || string.IsNullOrWhiteSpace(msg.NpcName)) return;

        if (!_companions.TryGetValue(msg.NpcName, out var comp))
        {
            comp = new CompanionState
            {
                NpcName = msg.NpcName,
                FarmerId = msg.OwnerFarmerId
            };
            _companions[msg.NpcName] = comp;
        }

        comp.NightState = (NightLifecycleState)msg.NightState;
        comp.EveningResolution = (EveningResolutionState)msg.EveningResolution;
        comp.IsRestingInPlace = msg.IsRestingInPlace;
        comp.IsSettledInsideHouseForNight = msg.IsSettledInsideHouseForNight;
        comp.FrozenHousePosition = msg.FrozenHousePosition;

        var npc = Game1.getCharacterFromName(msg.NpcName);
        if (npc == null) return;

        if (msg.IsSleepingInBed)
        {
            Point bedSpot = new Point(msg.BedTileX, msg.BedTileY);
            comp.AssignedBedSpot = bedSpot;
            Vector2 bedPixelPos = msg.TargetBedPosition ?? new Vector2(bedSpot.X * 64f, bedSpot.Y * 64f - 16f);
            comp.LockedBedPixelPosition = bedPixelPos;

            npc.Halt();
            npc.controller = null;
            npc.temporaryController = null;
            npc.Position = bedPixelPos;
            npc.faceDirection(2);
            npc.isSleeping.Value = true;
            npc.drawOffset = Vector2.Zero;
            try { npc.playSleepingAnimation(); } catch { }
            if (!npc.IsEmoting) npc.doEmote(24);
        }
        else if (comp.NightState == NightLifecycleState.DaytimeActive)
        {
            npc.isSleeping.Value = false;
            npc.drawOffset = Vector2.Zero;
            try { npc.Sprite?.StopAnimation(); } catch { }
        }
    }

    public void SendMultiplayerSnapshotToPeer(long peerId)
    {
        if (!Context.IsMainPlayer) return;

        foreach (var kvp in _active)
        {
            var job = kvp.Value;
            if (job == null || job.Done) continue;
            ValleyBuddies.ModEntry.BroadcastJob(job.NpcName, job.Kind, job.OwnerPlayerId, job.FarmerName, "silent_resume", specificPeerId: peerId);
        }

        foreach (var kvp in _companions)
        {
            var comp = kvp.Value;
            if (comp == null) continue;
            var npc = Game1.getCharacterFromName(kvp.Key);
            BroadcastNightStateSync(kvp.Key, comp, npc, peerId);
        }
    }

    public void HandlePeerDisconnected(long disconnectedPlayerId)
    {
        if (!Context.IsMainPlayer) return;

        ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.ClearForPlayer(disconnectedPlayerId);
        ValleyBuddies.Systems.LivingEvents.CinematicDirectorService.Instance.ClearForPlayer(disconnectedPlayerId);

        var npcsToClean = _companions
            .Where(kvp => kvp.Value.FarmerId == disconnectedPlayerId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var npcName in npcsToClean)
        {
            var npc = Game1.getCharacterFromName(npcName);
            if (_companions.TryGetValue(npcName, out var comp))
            {
                if (npc != null)
                {
                    npc.addedSpeed = 0;
                    if (npc.currentLocation is not Beach)
                    {
                        try { npc.shouldWearIslandAttire.Value = false; } catch { }
                    }
                    npc.isSleeping.Value = false;
                    npc.drawOffset = Vector2.Zero;
                    try { npc.Sprite?.StopAnimation(); } catch { }
                }

                if (_overnightGuests.TryRemove(npcName, out var guestInfo))
                {
                    if (guestInfo.DidCampfire)
                    {
                        foreach (var loc in Game1.locations)
                        {
                            if (loc != null)
                            {
                                RemoveCampObjectsAt(loc, guestInfo.CampfireTile, guestInfo.TentTile);
                            }
                        }
                    }
                }

                AbortCurrentJob(npcName);
                if (npc != null)
                {
                    RestoreNpcToVanillaState(npc, disconnectedPlayerId, resumeSchedule: true);
                }

                if (npc != null && npc.currentLocation != null)
                {
                    if (npc.currentLocation is Cabin || npc.currentLocation is MineShaft || npc.currentLocation is VolcanoDungeon)
                    {
                        var targetLoc = Game1.getLocationFromName(npc.DefaultMap) ?? Game1.getFarm();
                        if (targetLoc != null)
                        {
                            var defaultTile = npc.DefaultPosition != Vector2.Zero ? npc.DefaultPosition / 64f : new Vector2(10, 10);
                            Game1.warpCharacter(npc, targetLoc.NameOrUniqueName, defaultTile);
                        }
                    }
                }

                _companions.TryRemove(npcName, out _);
            }

            if (Game1.IsMultiplayer)
            {
                ValleyBuddies.ModEntry.BroadcastJob(npcName, JobKind.Done, 0, string.Empty);
                BroadcastNightStateSync(npcName, null, npc);
            }
        }
    }

    public bool ApplyRemoteJob(string npcName, JobKind kind, long farmerId, string farmerName, string extra)
    {
        bool isSilentResume = string.Equals(extra, "silent_resume", StringComparison.OrdinalIgnoreCase);

        if (Game1.IsMasterGame && _companions.TryGetValue(npcName, out var existingComp) && existingComp.FarmerId != 0 && farmerId != 0 && existingComp.FarmerId != farmerId)
        {
            var originalOwner = Game1.GetPlayer(existingComp.FarmerId, onlyOnline: true);
            if (originalOwner != null && _config?.AllowHostOverrideCompanion != true)
            {
                var targetNpc = FindNpc(npcName);
                string npcDisplay = targetNpc?.displayName ?? npcName;
                string ownerDisplay = originalOwner.Name ?? existingComp.FarmerName;
                ValleyBuddies.ModEntry.MultiplayerSync?.SendPrivateNotice(farmerId, Utilities.I18n.Get("mp.not_owner", new { npcName = npcDisplay, ownerName = ownerDisplay }));
                return false;
            }
        }

        if (Context.IsMainPlayer && kind == JobKind.Sleepover && !isSilentResume && !string.IsNullOrWhiteSpace(extra))
        {
            bool isExteriorDest = string.Equals(extra, "Farm", StringComparison.OrdinalIgnoreCase)
                || (Game1.getLocationFromName(extra) is GameLocation dLoc && dLoc is not FarmHouse && dLoc is not Cabin && !dLoc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase));

            if (isExteriorDest && _companions.TryGetValue(npcName, out var existingNightComp) && existingNightComp.FarmerId == farmerId)
            {
                var existingNpc = FindNpc(npcName);
                bool settledOrSleeping = existingNightComp.IsSettledInsideHouseForNight
                    || existingNightComp.IsRestingInPlace
                    || existingNightComp.NightState is NightLifecycleState.SleepingInBed or NightLifecycleState.IndoorRoaming or NightLifecycleState.IndoorResting or NightLifecycleState.NightSettled
                    || (existingNpc != null && existingNpc.isSleeping.Value);

                if (settledOrSleeping)
                {
                    return true;
                }
            }
        }

        bool hasActiveCamp = _overnightGuests.TryGetValue(npcName, out var campGuest) && campGuest.DidCampfire;
        bool preserveCamp = (GetEveningResolution(npcName) == EveningResolutionState.Camping || hasActiveCamp)
            && (IsTemporaryEveningActivity(kind) || kind == JobKind.Campfire || kind == JobKind.CampSleepTogether || (isSilentResume && kind == JobKind.Sleepover));
        AbortCurrentJob(npcName, preserveOvernightCamp: preserveCamp);

        if (isSilentResume)
        {
            if (kind == JobKind.Follow)
            {
                SetCompanion(npcName, farmerId, isVisitingFarm: false, isFollowing: true, farmerName: farmerName);
            }
            else if (kind == JobKind.SitOrRest)
            {
                SetCompanion(npcName, farmerId, isVisitingFarm: false, isFollowing: false, farmerName: farmerName);
                if (_companions.TryGetValue(npcName, out var compState))
                {
                    compState.IsRestingInPlace = true;
                }
            }
            else if (kind == JobKind.VisitFarm)
            {
                SetCompanion(npcName, farmerId, isVisitingFarm: true, isFollowing: false, farmerName: farmerName);
            }
            else if (kind == JobKind.Campfire || kind == JobKind.CampSleepTogether)
            {
                SetCompanion(npcName, farmerId, isVisitingFarm: true, isFollowing: false, farmerName: farmerName);
                if (_companions.TryGetValue(npcName, out var campComp))
                {
                    campComp.NightState = NightLifecycleState.CampOutdoorIdle;
                    campComp.EveningResolution = EveningResolutionState.Camping;
                    campComp.IsRestingInPlace = true;
                }
                SetEveningResolution(npcName, EveningResolutionState.Camping);
            }

            var resumedMpJob = new NpcJob
            {
                NpcName = npcName,
                Kind = kind,
                FarmerId = farmerId,
                FarmerName = farmerName,
                CreatedTick = Game1.ticks
            };
            if ((kind == JobKind.Campfire || kind == JobKind.CampSleepTogether) && _overnightGuests.TryGetValue(npcName, out var savedCamp))
            {
                resumedMpJob.Data["campTileX"] = savedCamp.CampfireTile.X;
                resumedMpJob.Data["campTileY"] = savedCamp.CampfireTile.Y;
                resumedMpJob.Data["tentTileX"] = savedCamp.TentTile.X;
                resumedMpJob.Data["tentTileY"] = savedCamp.TentTile.Y;
            }
            resumedMpJob.Data["follow_pause_until"] = ModEntry.CurrentTick + 30;
            resumedMpJob.Data["last_warp_tick"] = ModEntry.CurrentTick;
            _active[npcName] = resumedMpJob;
            return true;
        }

        if (kind == JobKind.Done || kind == JobKind.LeaveFarm)
        {
            DismissCompanion(npcName);
            return true;
        }

        if (kind == JobKind.Campfire)
        {
            if (Context.IsMainPlayer)
            {
                var campNpc = FindNpc(npcName);
                var campFarmer = Game1.GetPlayer(farmerId, true);
                if (campNpc != null) ExecuteCampfireEvent(campNpc, campFarmer);
            }
            else
            {
                SetCompanion(npcName, farmerId, isVisitingFarm: true, isFollowing: false, farmerName: farmerName);
                if (_companions.TryGetValue(npcName, out var cCamp))
                {
                    cCamp.NightState = NightLifecycleState.CampOutdoorIdle;
                    cCamp.EveningResolution = EveningResolutionState.Camping;
                    cCamp.IsRestingInPlace = true;
                }
                SetEveningResolution(npcName, EveningResolutionState.Camping);
                _active[npcName] = new NpcJob { NpcName = npcName, Kind = JobKind.Campfire, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks };
            }
            return true;
        }

        if (kind == JobKind.CampSleepTogether)
        {
            if (Context.IsMainPlayer)
            {
                var campNpc = FindNpc(npcName);
                var campFarmer = Game1.GetPlayer(farmerId, true);
                if (campNpc != null) ExecuteSleepTentTogetherEvent(campNpc, campFarmer);
            }
            else
            {
                SetCompanion(npcName, farmerId, isVisitingFarm: true, isFollowing: false, farmerName: farmerName);
                if (_companions.TryGetValue(npcName, out var cTent))
                {
                    cTent.NightState = NightLifecycleState.CampOutdoorIdle;
                    cTent.EveningResolution = EveningResolutionState.Camping;
                    cTent.IsRestingInPlace = true;
                }
                SetEveningResolution(npcName, EveningResolutionState.Camping);
                _active[npcName] = new NpcJob { NpcName = npcName, Kind = JobKind.CampSleepTogether, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks };
            }
            return true;
        }

        if (kind == JobKind.Sleepover)
        {
            if (Context.IsMainPlayer)
            {
                var sleepNpc = FindNpc(npcName);
                var sleepFarmer = Game1.GetPlayer(farmerId, true);
                if (sleepNpc != null) ExecuteSleepoverEvent(sleepNpc, sleepFarmer, extra);
            }
            else
            {
                SetCompanion(npcName, farmerId, isVisitingFarm: true, isFollowing: false, farmerName: farmerName);
                if (_companions.TryGetValue(npcName, out var cSleep))
                {
                    cSleep.EveningResolution = EveningResolutionState.StayingInHouse;
                }
                SetEveningResolution(npcName, EveningResolutionState.StayingInHouse);
                _active[npcName] = new NpcJob { NpcName = npcName, Kind = JobKind.Sleepover, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks };
            }
            return true;
        }

        var targetFarmer = (farmerId != 0) ? Game1.GetPlayer(farmerId, true) : Game1.player;
        var npc = FindNpc(npcName);
        string displayName = !string.IsNullOrWhiteSpace(farmerName) ? farmerName : (targetFarmer?.Name ?? "amigo");

        if (kind == JobKind.Follow)
        {
            SetCompanion(npcName, farmerId, isVisitingFarm: false, isFollowing: true, farmerName: farmerName);

            // Regla de Oro: PRIMERO CAMINAR. Solo teletransportar si están en mapas diferentes.
            if (Game1.IsMasterGame && npc != null && targetFarmer != null)
            {
                var targetMap = !string.IsNullOrEmpty(extra) ? (Game1.getLocationFromName(extra) ?? targetFarmer.currentLocation) : targetFarmer.currentLocation;
                if (targetMap != null)
                {
                    if (npc.currentLocation?.NameOrUniqueName != targetMap.NameOrUniqueName)
                    {
                        Point spawn = GetCrossMapSpawnTile(targetMap, targetFarmer.TilePoint, targetFarmer.FacingDirection);
                        Game1.warpCharacter(npc, targetMap, new Vector2(spawn.X, spawn.Y));
                        npc.controller = null;
                        npc.Halt();
                    }
                    npc.followSchedule = false;
                    CheckBeachOutfit(npc, targetMap);
                    CheckCaveArrival(npc, targetMap, targetFarmer);
                    npc.showTextAboveHead(Utilities.I18n.Get("speech.follow_confirm", new { playerName = displayName }), duration: 3800);
                    npc.doEmote(20);
                }
            }
        }
        else if (kind == JobKind.ComeHere)
        {
            SetCompanion(npcName, farmerId, isVisitingFarm: false, isFollowing: false, farmerName: farmerName);

            if (Game1.IsMasterGame && npc != null && targetFarmer != null)
            {
                var targetMap = targetFarmer.currentLocation ?? (!string.IsNullOrEmpty(extra) ? Game1.getLocationFromName(extra) : null);
                if (targetMap != null)
                {
                    if (npc.currentLocation?.NameOrUniqueName != targetMap.NameOrUniqueName)
                    {
                        Point spawn = GetAdjacentWalkableTile(targetMap, targetFarmer.TilePoint, targetFarmer.TilePoint, npc);
                        Game1.warpCharacter(npc, targetMap, new Vector2(spawn.X, spawn.Y));
                        npc.controller = null;
                        npc.Halt();
                    }
                    npc.followSchedule = false;
                    npc.faceTowardFarmerForPeriod(3000, 3, false, targetFarmer);
                    npc.showTextAboveHead(Utilities.I18n.Get("speech.follow_confirm", new { playerName = displayName }), duration: 3800);
                    npc.doEmote(32);
                }
            }
        }
        else if (kind == JobKind.VisitFarm)
        {
            SetCompanion(npcName, farmerId, isVisitingFarm: true, isFollowing: false, farmerName: farmerName);

            if (Game1.IsMasterGame && npc != null && targetFarmer != null)
            {
                GameLocation? targetMap = null;
                if (!string.IsNullOrEmpty(extra) && extra != "Farm")
                {
                    targetMap = FindLocationByNameOrPlayer(extra, targetFarmer);
                }
                if (targetMap == null)
                {
                    if (targetFarmer.currentLocation != null && (targetFarmer.currentLocation is Farm || targetFarmer.currentLocation is StardewValley.Locations.FarmHouse || targetFarmer.currentLocation.NameOrUniqueName.Contains("Cabin") || targetFarmer.currentLocation.NameOrUniqueName.Contains("Farm")))
                    {
                        targetMap = targetFarmer.currentLocation;
                    }
                    else
                    {
                        targetMap = Game1.getFarm();
                    }
                }

                if (targetMap != null)
                {
                    if (npc.currentLocation?.NameOrUniqueName != targetMap.NameOrUniqueName)
                    {
                        Point spawn;
                        if (targetMap is Farm farmLoc)
                        {
                            if (targetFarmer.currentLocation == farmLoc)
                            {
                                spawn = GetAdjacentWalkableTile(farmLoc, targetFarmer.TilePoint, targetFarmer.TilePoint, npc);
                            }
                            else
                            {
                                Point door = GetOwnerHomeExteriorDoorTile(farmLoc, targetFarmer);
                                Point belowDoor = new Point(door.X, door.Y + 1);
                                spawn = IsTileWalkable(farmLoc, belowDoor, npc) ? belowDoor : GetFarmSafePorchTile(farmLoc);
                            }
                        }
                        else if (targetMap is StardewValley.Locations.FarmHouse fh)
                        {
                            Point entry = fh.getEntryLocation();
                            spawn = FindSafeIndoorSpawnTile(fh, entry, targetFarmer);
                        }
                        else
                        {
                            spawn = GetAdjacentWalkableTile(targetMap, targetFarmer.TilePoint, targetFarmer.TilePoint, npc);
                        }

                        Game1.warpCharacter(npc, targetMap, new Vector2(spawn.X, spawn.Y));
                        npc.controller = null;
                        npc.Halt();
                    }
                    npc.followSchedule = false;
                    CheckBeachOutfit(npc, targetMap);
                    npc.showTextAboveHead(Utilities.I18n.Get("speech.follow_confirm", new { playerName = displayName }), duration: 4200);
                    npc.doEmote(32);
                    _debug_log($"[Actions] {npc.Name} set to visit {targetMap.NameOrUniqueName} with {displayName}!");
                }
            }
        }
        else if (kind == JobKind.HangOut || kind == JobKind.GoToLocation)
        {
            SetCompanion(npcName, farmerId, isVisitingFarm: true, isFollowing: false, farmerName: farmerName);

            if (Game1.IsMasterGame && npc != null && targetFarmer != null)
            {
                string locName = !string.IsNullOrEmpty(extra) ? extra : (targetFarmer.currentLocation?.NameOrUniqueName ?? "Saloon");
                GameLocation? targetMap = Game1.getLocationFromName(locName) ?? targetFarmer.currentLocation ?? Game1.getFarm();
                if (targetMap != null)
                {
                    if (npc.currentLocation?.NameOrUniqueName != targetMap.NameOrUniqueName)
                    {
                        Point spawn;
                        if (targetFarmer.currentLocation == targetMap)
                        {
                            spawn = GetAdjacentWalkableTile(targetMap, targetFarmer.TilePoint, targetFarmer.TilePoint, npc);
                        }
                        else
                        {
                            Point entry = targetMap.warps.Count > 0
                                ? new Point(targetMap.warps[0].TargetX, targetMap.warps[0].TargetY)
                                : targetMap.getRandomTile().ToPoint();
                            if (entry == Point.Zero) entry = new Point(10, 10);
                            spawn = GetAdjacentWalkableTile(targetMap, entry, entry, npc);
                        }

                        Game1.warpCharacter(npc, targetMap, new Vector2(spawn.X, spawn.Y));
                        npc.controller = null;
                        npc.Halt();
                    }
                    npc.followSchedule = false;
                    CheckBeachOutfit(npc, targetMap);
                    npc.showTextAboveHead(Utilities.I18n.Get("speech.follow_confirm", new { playerName = displayName }), duration: 3800);
                    npc.doEmote(32);
                }
            }
        }
        else if (!_companions.ContainsKey(npcName))
            SetCompanion(npcName, farmerId, isVisitingFarm: Game1.getFarm() != null, isFollowing: false, farmerName: farmerName);

        var job = new NpcJob
        {
            NpcName = npcName,
            Kind = kind,
            FarmerId = farmerId,
            FarmerName = farmerName,
            CreatedTick = Game1.ticks
        };
        if (!string.IsNullOrEmpty(extra))
        {
            job.Data["extra"] = extra;
            job.Data["extraParam"] = extra;
            if (kind == JobKind.ChopTrees) job.Data["treeFilter"] = extra;
            else if (kind == JobKind.HarvestCrops && bool.TryParse(extra, out bool hf)) job.Data["harvestFlowers"] = hf;
            else if (kind == JobKind.FetchFromChest) job.Data["fetchCategory"] = extra;
            else if (kind == JobKind.TakeFromChest) job.Data["item"] = extra;
            else if (kind == JobKind.GiveItem) job.Data["itemName"] = extra;
            else if (kind == JobKind.HangOut || kind == JobKind.GoToLocation || kind == JobKind.VisitFarm || kind == JobKind.MeetAtTime) job.Data["locationName"] = extra;
            else if (kind == JobKind.DoAnimation) job.Data["anim"] = extra;
            else if (kind == JobKind.ExternalTask) { job.Data["externalTaskId"] = extra; job.Data["taskId"] = extra; }
        }

        Enqueue(job);
        return true;
    }
}
