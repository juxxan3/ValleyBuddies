using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using ValleyBuddies.Systems;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    public void OpenCompanionHubMenu(NPC npc, Farmer who) =>
        HandlePlayerInteraction(npc, who);

    public void HandlePlayerInteraction(NPC npc, Farmer player)
    {
        if (npc == null || player == null || Game1.currentLocation == null) return;
        if (ValleyBuddies.Bridge.DialogueBypassPatches.IsFreeChatTriggered) return;

        // If this visitor has a spontaneous surprise gift, deliver it first with heart emote!
        if (_active.TryGetValue(npc.Name, out var actJob) &&
            actJob.Data.TryGetValue("spontaneousGiftGiven", out var given) && !(bool)given &&
            actJob.Data.TryGetValue("spontaneousGiftId", out var gId))
        {
            string giftItemId = gId.ToString()!;
            var giftItem = ItemRegistry.Create(giftItemId, 1);
            if (giftItem != null)
            {
                if (player.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
                    GiveItemToFarmerDirectly(player, giftItem);
                else
                    _multiplayerSync?.SendSpouseGift(player.UniqueMultiplayerID, giftItem.QualifiedItemId, giftItem.Stack, npc.Name);
                Game1.currentLocation.playSound("coin");
                npc.doEmote(20); // Heart
                npc.showTextAboveHead(Utilities.I18n.Get("speech.spontaneous_gift", new { playerName = player.displayName, item = giftItem.DisplayName }), duration: 4000);
                RecordMemory(npc.Name, $"I paid a surprise visit to the farm and gave the farmer a special gift I brought for them: {giftItem.DisplayName}.");
            }
            actJob.Data["spontaneousGiftGiven"] = true;
            return;
        }

        bool hasCompanion = TryGetCompanionInfo(npc.Name, out var compInfo);

        // If the NPC is associated with another player in multiplayer:
        if (hasCompanion && compInfo.FarmerId != 0 && compInfo.FarmerId != player.UniqueMultiplayerID)
        {
            string ownerName = !string.IsNullOrWhiteSpace(compInfo.FarmerName)
                ? compInfo.FarmerName
                : ((Game1.GetPlayer(compInfo.FarmerId, true) ?? Game1.MasterPlayer)?.Name ?? "el granjero");

            npc.controller = null;
            npc.Halt();
            npc.faceTowardFarmerForPeriod(3000, 3, false, player);
            npc.doEmote(32); // Friendly wave

            if (compInfo.IsFollowing)
            {
                npc.showTextAboveHead(Utilities.I18n.Get("speech.already_accompanying_other", new { playerName = player.displayName, ownerName = ownerName }), duration: 4000);
            }
            else
            {
                npc.showTextAboveHead(Utilities.I18n.Get("speech.visiting_other_farm", new { playerName = player.displayName, ownerName = ownerName }), duration: 4000);
            }

            var otherResponses = new List<Response>
            {
                new Response("chat",  Utilities.I18n.Get("menu.hub.chat", new { npcName = npc.displayName })),
                new Response("greet", Utilities.I18n.Get("menu.hub.greet")),
            };

            Game1.currentLocation.afterQuestion = (Farmer who, string answer) =>
            {
                if (answer == "chat")
                {
                    DelayedAction.functionAfterDelay(() =>
                    {
                        ValleyBuddies.Bridge.DialogueBypassPatches.TriggerFreeChat(npc, who);
                    }, 100);
                }
                else
                {
                    npc.doEmote(32);
                }
            };

            Game1.currentLocation.createQuestionDialogue(
                Utilities.I18n.Get("speech.greet_kindly", new { name = npc.displayName }),
                otherResponses.ToArray(),
                "afterQuestion");
            return;
        }

        // If this player is the owner / inviter (or singleplayer), show the full follower/visitor action menu
        ShowFollowerActionMenu(npc, player);
    }

    public void ShowFollowerActionMenu(NPC npc, Farmer? interactingFarmer = null)
    {
        if (npc == null || Game1.currentLocation == null) return;
        Farmer player = interactingFarmer ?? Game1.player;

        // Immediately halt NPC and stop current movement
        npc.controller = null;
        npc.Halt();
        if (player != null)
            npc.faceTowardFarmerForPeriod(3000, 3, false, player);

        bool isEvening = Game1.timeOfDay >= 1800;
        bool isNight = Game1.timeOfDay >= 2100 && (npc.currentLocation is Farm || npc.currentLocation is StardewValley.Locations.FarmHouse);
        bool isIndoors = npc.currentLocation is StardewValley.Locations.FarmHouse || npc.currentLocation is StardewValley.Locations.Cabin;
        bool isOutdoors = npc.currentLocation != null && npc.currentLocation.IsOutdoors && !isIndoors;
        int hearts = player != null ? player.getFriendshipHeartLevelForNPC(npc.Name) : 0;
        bool isFollowing = _companions.TryGetValue(npc.Name, out var comp) && comp.IsFollowing;

        string locName = npc.currentLocation?.NameOrUniqueName ?? string.Empty;
        bool isBeach = npc.currentLocation is StardewValley.Locations.Beach || locName.Contains("Beach", StringComparison.OrdinalIgnoreCase) || locName.Contains("IslandSouth", StringComparison.OrdinalIgnoreCase);
        bool isSecretWoodsNight = (npc.currentLocation is StardewValley.Locations.Woods
            || locName.Equals("Woods", StringComparison.OrdinalIgnoreCase)
            || locName.Contains("SecretWoods", StringComparison.OrdinalIgnoreCase))
            && Game1.timeOfDay >= 2000;

        List<Response> responses = new List<Response>();
        EveningResolutionState res = comp?.EveningResolution ?? GetEveningResolution(npc.Name);

        // FASE 1: Matriz estricta de opciones nocturnas (solo si Undecided y a partir de las 18:00)
        if (isEvening && res == EveningResolutionState.Undecided)
        {
            if (isIndoors)
            {
                responses.Add(new Response("stay_in_house", Utilities.I18n.Get("night.menu.stay_in_house")));
                responses.Add(new Response("leave_night", Utilities.I18n.Get("night.menu.leave_night")));
            }
            else if (isOutdoors)
            {
                responses.Add(new Response("stay_in_house", Utilities.I18n.Get("night.menu.stay_in_house")));
                responses.Add(new Response("setup_camp", Utilities.I18n.Get("night.menu.setup_camp")));
                responses.Add(new Response("leave_night", Utilities.I18n.Get("night.menu.leave_night")));
            }
        }

        if (isEvening)
        {
            responses.Add(new Response("night_chat", Utilities.I18n.Get("menu.night.chat")));
            string followToggleLabel = isFollowing
                ? Utilities.I18n.Get("night.rest.start_option")
                : Utilities.I18n.Get("night.rest.stop_option");
            responses.Add(new Response("follow_toggle", followToggleLabel));
            responses.Add(new Response("night_activities_submenu", Utilities.I18n.Get("menu.night.activities")));
            if (isSecretWoodsNight)
            {
                responses.Add(new Response("secret_spirits", Utilities.I18n.Get("menu.hub.secret_spirits")));
            }
            if (IsCaveOrMineLocation(npc.currentLocation))
            {
                responses.Add(new Response("cave_tactics", Utilities.I18n.Get("menu.cave.tactics")));
            }
            responses.Add(new Response("dismiss", Utilities.I18n.Get("menu.hub.dismiss")));
            responses.Add(new Response("cancel", Utilities.I18n.Get("menu.hub.close")));
        }
        else
        {
            responses.Add(new Response("chat", Utilities.I18n.Get("menu.hub.chat", new { npcName = npc.displayName })));
            responses.Add(new Response("follow_toggle", isFollowing ? Utilities.I18n.Get("menu.hub.wait") : Utilities.I18n.Get("menu.hub.follow")));
            if (_config?.AllowFarmWork != false)
            {
                responses.Add(new Response("work_submenu", Utilities.I18n.Get("menu.hub.tasks")));
            }
            responses.Add(new Response("activities_submenu", Utilities.I18n.Get("menu.hub.activities")));
            if (isBeach)
            {
                responses.Add(new Response("beach_pebbles", Utilities.I18n.Get("menu.hub.beach_pebbles")));
            }
            if (isSecretWoodsNight)
            {
                responses.Add(new Response("secret_spirits", Utilities.I18n.Get("menu.hub.secret_spirits")));
            }
            if (IsCaveOrMineLocation(npc.currentLocation))
            {
                responses.Add(new Response("cave_tactics", Utilities.I18n.Get("menu.cave.tactics")));
            }
            responses.Add(new Response("dismiss", Utilities.I18n.Get("menu.hub.dismiss")));
            responses.Add(new Response("cancel", Utilities.I18n.Get("menu.hub.close")));
        }

        if (ValleyBuddies.Systems.PicnicSceneService.Instance.HasActivePicnicForNpc(npc.Name))
        {
            responses.Insert(0, new Response("pack_picnic", Utilities.I18n.Get("menu.picnic.pack")));
        }

        Game1.currentLocation.afterQuestion = (Farmer who, string answer) =>
        {
            bool hasActivePicnic = ValleyBuddies.Systems.PicnicSceneService.Instance.HasActivePicnicForNpc(npc.Name);
            bool isSubmenuOrNonDisruptive =
                answer == "work_submenu"
                || answer == "activities_submenu"
                || answer == "night_activities_submenu"
                || answer == "cave_tactics"
                || answer == "cancel"
                || answer == "chat"
                || answer == "night_chat"
                || string.IsNullOrEmpty(answer);
            bool keepActivePicnic = hasActivePicnic && isSubmenuOrNonDisruptive;

            if (!isSubmenuOrNonDisruptive)
            {
                AbortCurrentJob(npc.Name);
                npc.controller = null;
                npc.Halt();
            }

            Farmer activeFarmer = who ?? player ?? Game1.player;
            long myFarmerId = activeFarmer.UniqueMultiplayerID;
            string myFarmerName = activeFarmer.Name;

            void ResumeDefault() => ResumeCompanionDefaultJob(npc, activeFarmer, keepActivePicnic);

            switch (answer)
            {
                case "pack_picnic":
                    if (ValleyBuddies.Systems.PicnicSceneService.Instance.HasActivePicnicForNpc(npc.Name))
                    {
                        string finishMsg = Utilities.I18n.Get("picnic.finished", new { playerName = activeFarmer.displayName ?? activeFarmer.Name, npcName = npc.displayName });
                        ModEntry.ConversationBridge.ShowCompanionBubble(npc, finishMsg, 3500);
                        npc.doEmote(20);
                    }
                    ValleyBuddies.Systems.PicnicSceneService.Instance.EndPicnic(npc.Name);
                    ResumeDefault();
                    break;

                case "stay_in_house":
                case "sleep_in":
                    if (Game1.IsMultiplayer && !Context.IsMainPlayer)
                    {
                        _multiplayerSync?.SendNightActionRequest(npc.Name, myFarmerId, "stay_in_house", npc.currentLocation?.NameOrUniqueName);
                        break;
                    }
                    SetEveningResolution(npc.Name, EveningResolutionState.StayingInHouse);
                    ExecuteSleepoverEvent(npc, activeFarmer);
                    ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.Sleepover, myFarmerId, myFarmerName);
                    BroadcastNightStateSync(npc.Name);
                    break;

                case "setup_camp":
                    if (Game1.IsMultiplayer && !Context.IsMainPlayer)
                    {
                        _multiplayerSync?.SendNightActionRequest(npc.Name, myFarmerId, "setup_camp", npc.currentLocation?.NameOrUniqueName);
                        break;
                    }
                    ExecuteCampfireEvent(npc, activeFarmer);
                    BroadcastNightStateSync(npc.Name);
                    break;

                case "leave_night":
                    if (Game1.IsMultiplayer && !Context.IsMainPlayer)
                    {
                        _multiplayerSync?.SendNightActionRequest(npc.Name, myFarmerId, "leave_night", npc.currentLocation?.NameOrUniqueName);
                        break;
                    }
                    if (comp != null)
                    {
                        comp.EveningResolution = EveningResolutionState.Leaving;
                        comp.NightState = NightLifecycleState.DismissedForNight;
                    }
                    SetEveningResolution(npc.Name, EveningResolutionState.Leaving);
                    var leavePlayerName = activeFarmer?.Name ?? Game1.player?.Name ?? "Player";
                    SpeakNonRepeating(npc, "night.menu.leave_night_bye", new { playerName = leavePlayerName, npcName = npc.displayName, farmerName = leavePlayerName });
                    WakeAndExtractNpcFromBed(npc, npc.currentLocation, comp, activeFarmer);
                    DismissCompanion(npc.Name);
                    BroadcastNightStateSync(npc.Name);
                    break;

                case "night_chat":
                    ShowNightDialogue(npc, activeFarmer);
                    ResumeDefault();
                    break;
                case "chat":
                    DelayedAction.functionAfterDelay(() =>
                    {
                        ValleyBuddies.Bridge.DialogueBypassPatches.AllowVanillaTalkOnce = true;
                        try
                        {
                            ValleyBuddies.Bridge.DialogueBypassPatches.TriggerFreeChat(npc, activeFarmer);
                        }
                        finally
                        {
                            ValleyBuddies.Bridge.DialogueBypassPatches.AllowVanillaTalkOnce = false;
                        }
                    }, 150);
                    ResumeDefault();
                    break;
                case "follow_toggle":
                    if (isFollowing)
                    {
                        npc.controller = null;
                        npc.Halt();
                        npc.doEmote(32);
                        if (_companions.TryGetValue(npc.Name, out var compState))
                        {
                            compState.IsFollowing = false;
                            compState.IsRestingInPlace = true;
                            if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                            {
                                Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.SitOrRest, FarmerId = myFarmerId, FarmerName = myFarmerName, CreatedTick = Game1.ticks });
                            }
                            ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.SitOrRest, myFarmerId, myFarmerName);
                        }
                        Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("speech.wait_confirm", new { playerName = activeFarmer.displayName }), HUDMessage.newQuest_type));
                    }
                    else
                    {
                        if (_companions.TryGetValue(npc.Name, out var compState))
                        {
                            compState.IsRestingInPlace = false;
                            compState.IsSettledInsideHouseForNight = false;
                            compState.SettledHouseName = null;
                            compState.IsPausedWhileOwnerOutsideHouse = false;
                            compState.FrozenHousePosition = null;
                            compState.WokeUpByPlayerTonight = true;
                            compState.NightState = NightLifecycleState.DaytimeActive;
                        }
                        SetCompanion(npc.Name, myFarmerId, isVisitingFarm: false, isFollowing: true, farmerName: myFarmerName);
                        WakeAndExtractNpcFromBed(npc, npc.currentLocation, compState, activeFarmer);
                        if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                        {
                            Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.Follow, FarmerId = myFarmerId, FarmerName = myFarmerName, CreatedTick = Game1.ticks });
                        }
                        ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.Follow, myFarmerId, myFarmerName);
                        BroadcastNightStateSync(npc.Name);
                        Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("speech.follow_confirm", new { playerName = activeFarmer.displayName }), HUDMessage.newQuest_type));
                    }
                    break;
                case "work_submenu":
                    DelayedAction.functionAfterDelay(() => ShowWorkSubmenu(npc, myFarmerId, myFarmerName), 100);
                    break;
                case "activities_submenu":
                    DelayedAction.functionAfterDelay(() => ShowActivitiesSubmenu(npc, myFarmerId, myFarmerName), 100);
                    break;
                case "night_activities_submenu":
                    DelayedAction.functionAfterDelay(() => ShowNightActivitiesSubmenu(npc, myFarmerId, myFarmerName), 100);
                    break;
                case "beach_pebbles":
                    if (activeFarmer != null) ExecuteBeachPebbleSkimming(npc, activeFarmer);
                    ResumeDefault();
                    break;
                case "secret_spirits":
                    if (activeFarmer != null) ExecuteSecretWoodsSpirits(npc, activeFarmer);
                    ResumeDefault();
                    break;
                case "cave_tactics":
                    DelayedAction.functionAfterDelay(() => ShowCaveTacticsSubmenu(npc, activeFarmer), 100);
                    break;
                case "dismiss":
                    SetEveningResolution(npc.Name, EveningResolutionState.Leaving);
                    if (npc.isSleeping.Value || (npc.currentLocation != null && IsNpcInsideBedFootprint(npc, npc.currentLocation, out _)))
                    {
                        WakeAndExtractNpcFromBed(npc, npc.currentLocation, _companions.TryGetValue(npc.Name, out var cDis) ? cDis : null, activeFarmer);
                    }
                    DismissCompanion(npc.Name);
                    ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.Done, myFarmerId, myFarmerName);
                    Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("speech.dismiss_confirm", new { playerName = activeFarmer.displayName }), HUDMessage.newQuest_type));
                    break;
                case "cancel":
                default:
                    ResumeDefault();
                    break;
            }
        };

        Game1.currentLocation.createQuestionDialogue(
            Utilities.I18n.Get(isNight ? "menu.night.title" : "menu.hub.title", new { npcName = npc.displayName }),
            responses.ToArray(),
            "afterQuestion");
    }

    private void ResumeCompanionDefaultJob(NPC npc, Farmer? activeFarmer, bool keepActivePicnic = false)
    {
        if (npc == null) return;
        if (Game1.IsMultiplayer && !Context.IsMainPlayer) return;
        if (keepActivePicnic && _active.TryGetValue(npc.Name, out var activePic) && activePic.Kind == JobKind.Picnic && !activePic.Done)
        {
            return;
        }
        if (_companions.TryGetValue(npc.Name, out var cResume))
        {
            if (_active.TryGetValue(npc.Name, out var currentJob) && !currentJob.Done)
            {
                if ((cResume.IsFollowing && currentJob.Kind == JobKind.Follow)
                    || (cResume.IsRestingInPlace && currentJob.Kind == JobKind.SitOrRest)
                    || (GetEveningResolution(npc.Name) == EveningResolutionState.Camping && (currentJob.Kind == JobKind.Campfire || currentJob.Kind == JobKind.CampSleepTogether))
                    || (!cResume.IsFollowing && !cResume.IsRestingInPlace && cResume.IsVisitingFarm && currentJob.Kind == JobKind.VisitFarm))
                {
                    return;
                }
            }

            long fid = activeFarmer?.UniqueMultiplayerID ?? cResume.FarmerId;
            string fName = activeFarmer?.Name ?? cResume.FarmerName ?? string.Empty;
            if (cResume.IsFollowing)
                Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.Follow, FarmerId = fid, FarmerName = fName, CreatedTick = Game1.ticks });
            else if (cResume.IsRestingInPlace)
                Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.SitOrRest, FarmerId = fid, FarmerName = fName, CreatedTick = Game1.ticks });
            else if (cResume.IsVisitingFarm)
                Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.VisitFarm, FarmerId = fid, FarmerName = fName, CreatedTick = Game1.ticks });
        }
    }

    private void ShowWorkSubmenu(NPC npc, long farmerId, string farmerName)
    {
        var workResponses = new List<Response>
        {
            new Response("debris",      Utilities.I18n.Get("menu.tasks.debris")),
            new Response("chop_trees",  Utilities.I18n.Get("menu.tasks.trees")),
            new Response("chop_twigs",  Utilities.I18n.Get("menu.tasks.twigs")),
            new Response("mine",        Utilities.I18n.Get("menu.tasks.rocks")),
            new Response("water",       Utilities.I18n.Get("menu.tasks.water")),
            new Response("harvest",     Utilities.I18n.Get("menu.tasks.harvest")),
            new Response("pet",         Utilities.I18n.Get("menu.tasks.pet")),
            new Response("fetch_chest", Utilities.I18n.Get("menu.tasks.fetch_chest")),
            new Response("back",        Utilities.I18n.Get("menu.tasks.back"))
        };

        Game1.currentLocation.afterQuestion = (Farmer who, string answer) =>
        {
            switch (answer)
            {
                case "debris":
                    RequestChoreExecution(npc, () =>
                    {
                        if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                            Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.ClearDebris, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks });
                        ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.ClearDebris, farmerId, farmerName);
                        Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("hud.help_debris", new { npcName = npc.displayName }), HUDMessage.newQuest_type));
                    }, who);
                    break;
                case "chop_trees":
                    DelayedAction.functionAfterDelay(() => ShowTreeSelectionSubmenu(npc, farmerId, farmerName), 120);
                    break;
                case "chop_twigs":
                    RequestChoreExecution(npc, () =>
                    {
                        if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                            Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.ChopWood, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks });
                        ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.ChopWood, farmerId, farmerName);
                        Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("hud.help_twigs", new { npcName = npc.displayName }), HUDMessage.newQuest_type));
                    }, who);
                    break;
                case "mine":
                    RequestChoreExecution(npc, () =>
                    {
                        if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                            Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.MineNearbyRock, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks });
                        ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.MineNearbyRock, farmerId, farmerName);
                        Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("hud.help_mine", new { npcName = npc.displayName }), HUDMessage.newQuest_type));
                    }, who);
                    break;
                case "water":
                    RequestChoreExecution(npc, () =>
                    {
                        if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                            Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.WaterCrops, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks });
                        ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.WaterCrops, farmerId, farmerName);
                        Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("hud.help_water", new { npcName = npc.displayName }), HUDMessage.newQuest_type));
                    }, who);
                    break;
                case "harvest":
                    if (HasReadyFlowerCropsForHarvest(who))
                    {
                        DelayedAction.functionAfterDelay(() => ShowHarvestFlowersSubmenu(npc, farmerId, farmerName, who), 100);
                    }
                    else
                    {
                        RequestChoreExecution(npc, () =>
                        {
                            var job = new NpcJob { NpcName = npc.Name, Kind = JobKind.HarvestCrops, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks };
                            if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                                Enqueue(job);
                            ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.HarvestCrops, farmerId, farmerName);
                            Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("hud.help_harvest", new { npcName = npc.displayName }), HUDMessage.newQuest_type));
                        }, who);
                    }
                    break;
                case "pet":
                    RequestChoreExecution(npc, () =>
                    {
                        if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                            Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.PetAnimals, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks });
                        ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.PetAnimals, farmerId, farmerName);
                        Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("hud.help_pet", new { npcName = npc.displayName }), HUDMessage.newQuest_type));
                    }, who);
                    break;
                case "fetch_chest":
                    DelayedAction.functionAfterDelay(() => ShowFetchChestSubmenu(npc, farmerId, farmerName), 120);
                    break;
                case "back":
                    DelayedAction.functionAfterDelay(() => ShowFollowerActionMenu(npc, who), 100);
                    break;
                default:
                    ResumeCompanionDefaultJob(npc, who);
                    break;
            }
        };

        Game1.currentLocation.createQuestionDialogue(
            Utilities.I18n.Get("menu.tasks.title", new { npcName = npc.displayName }),
            workResponses.ToArray(),
            "afterQuestion");
    }

    private void ShowActivitiesSubmenu(NPC npc, long farmerId, string farmerName)
    {
        var actResponses = new List<Response>
        {
            new Response("coffee",  Utilities.I18n.Get("menu.activities.coffee")),
            new Response("photo",   Utilities.I18n.Get("menu.activities.photo")),
            new Response("picnic",  Utilities.I18n.Get("menu.activities.picnic")),
            new Response("admire",  Utilities.I18n.Get("menu.activities.admire"))
        };
        if (_config?.EnableCinematicOutings != false)
            actResponses.Add(new Response("cinematic_moment", Utilities.I18n.Get("menu.activities.cinematic_moment")));
        actResponses.Add(new Response("back", Utilities.I18n.Get("menu.activities.back")));

        Game1.currentLocation.afterQuestion = (Farmer who, string answer) =>
        {
            var activeFarmer = who ?? Game1.player;
            if (answer != "back" && !string.IsNullOrEmpty(answer))
            {
                _companions.TryGetValue(npc.Name, out var cAct);
                if (npc.isSleeping.Value || (npc.currentLocation != null && IsNpcInsideBedFootprint(npc, npc.currentLocation, out _)))
                {
                    WakeAndExtractNpcFromBed(npc, npc.currentLocation, cAct, activeFarmer);
                }
                if (cAct != null)
                {
                    cAct.WokeUpByPlayerTonight = true;
                    cAct.NightState = NightLifecycleState.DaytimeActive;
                }
            }

            switch (answer)
            {
                case "coffee":
                    if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                        Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.TakeBreak, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks });
                    ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.TakeBreak, farmerId, farmerName);
                    break;
                case "photo":
                    if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                        Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.TakePhoto, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks });
                    ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.TakePhoto, farmerId, farmerName);
                    break;
                case "picnic":
                    if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                        Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.Picnic, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks });
                    ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.Picnic, farmerId, farmerName);
                    break;
                case "admire":
                    if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                        Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.AdmireFarm, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks });
                    ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.AdmireFarm, farmerId, farmerName);
                    break;
                case "cinematic_moment":
                    DelayedAction.functionAfterDelay(() =>
                    {
                        bool started = activeFarmer != null
                            && _config?.EnableCinematicOutings != false
                            && ValleyBuddies.Systems.LivingEvents.CinematicDirectorService.Instance.TryStartCinematicFromHub(npc, activeFarmer);
                        if (!started)
                            ResumeCompanionDefaultJob(npc, activeFarmer);
                    }, 120);
                    break;
                case "back":
                    DelayedAction.functionAfterDelay(() => ShowFollowerActionMenu(npc, activeFarmer), 100);
                    break;
                default:
                    ResumeCompanionDefaultJob(npc, activeFarmer);
                    break;
            }
        };

        Game1.currentLocation.createQuestionDialogue(
            Utilities.I18n.Get("menu.activities.title", new { npcName = npc.displayName }),
            actResponses.ToArray(),
            "afterQuestion");
    }

    private readonly Dictionary<string, ulong> _nightActivityLastClickTick = new(StringComparer.OrdinalIgnoreCase);

    private void ShowNightActivitiesSubmenu(NPC npc, long farmerId, string farmerName)
    {
        var nightResponses = new List<Response>
        {
            new Response("coffee",      Utilities.I18n.Get("menu.night.coffee")),
            new Response("photo",       Utilities.I18n.Get("menu.night.photo")),
            new Response("fetch_chest", Utilities.I18n.Get("menu.tasks.fetch_chest"))
        };
        if (_config?.EnableCinematicOutings != false)
            nightResponses.Add(new Response("cinematic_moment", Utilities.I18n.Get("menu.activities.cinematic_moment")));
        nightResponses.Add(new Response("back", Utilities.I18n.Get("menu.tasks.back")));

        Game1.currentLocation.afterQuestion = (Farmer who, string answer) =>
        {
            var activeFarmer = who ?? Game1.player;
            if (answer != "back" && !string.IsNullOrEmpty(answer))
            {
                ulong nowTick = ModEntry.CurrentTick;
                if (_nightActivityLastClickTick.TryGetValue(npc.Name, out ulong lastClick) && nowTick < lastClick + 30)
                    return;
                _nightActivityLastClickTick[npc.Name] = nowTick;

                _companions.TryGetValue(npc.Name, out var cNight);
                if (npc.isSleeping.Value || (npc.currentLocation != null && IsNpcInsideBedFootprint(npc, npc.currentLocation, out _)))
                {
                    WakeAndExtractNpcFromBed(npc, npc.currentLocation, cNight, activeFarmer);
                }
                if (cNight != null)
                {
                    cNight.WokeUpByPlayerTonight = true;
                    if (cNight.NightState != NightLifecycleState.CampOutdoorIdle)
                    {
                        cNight.NightState = NightLifecycleState.DaytimeActive;
                    }
                }
            }

            switch (answer)
            {
                case "coffee":
                    if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                        Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.TakeBreak, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks });
                    else
                        ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.TakeBreak, farmerId, farmerName);
                    break;
                case "photo":
                    if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                        Enqueue(new NpcJob { NpcName = npc.Name, Kind = JobKind.TakePhoto, FarmerId = farmerId, FarmerName = farmerName, CreatedTick = Game1.ticks });
                    else
                        ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.TakePhoto, farmerId, farmerName);
                    break;
                case "fetch_chest":
                    DelayedAction.functionAfterDelay(() => ShowFetchChestSubmenu(npc, farmerId, farmerName), 120);
                    break;
                case "cinematic_moment":
                    DelayedAction.functionAfterDelay(() =>
                    {
                        bool started = activeFarmer != null
                            && _config?.EnableCinematicOutings != false
                            && ValleyBuddies.Systems.LivingEvents.CinematicDirectorService.Instance.TryStartCinematicFromHub(npc, activeFarmer);
                        if (!started)
                            ResumeCompanionDefaultJob(npc, activeFarmer);
                    }, 120);
                    break;
                case "back":
                    DelayedAction.functionAfterDelay(() => ShowFollowerActionMenu(npc, activeFarmer), 100);
                    break;
                default:
                    ResumeCompanionDefaultJob(npc, activeFarmer);
                    break;
            }
        };

        Game1.currentLocation.createQuestionDialogue(
            Utilities.I18n.Get("menu.night.activities_title", new { npcName = npc.displayName }),
            nightResponses.ToArray(),
            "afterQuestion");
    }

    private void ExecuteBeachPebbleSkimming(NPC npc, Farmer who)
    {
        if (npc == null || who == null) return;
        var loc = who.currentLocation;
        if (loc == null) return;

        npc.controller = null;
        npc.Halt();
        who.Halt();

        bool hasStone = who.Items.Any(i => i != null && (i.QualifiedItemId == "(O)390" || i.ItemId == "390"));
        if (hasStone)
        {
            who.removeFirstOfThisItemFromInventory("390");
        }

        loc.playSound("throw");
        who.doEmote(20);

        DelayedAction.playSoundAfterDelay("waterDrop", 320, loc);
        DelayedAction.playSoundAfterDelay("waterDrop", 540, loc);
        DelayedAction.playSoundAfterDelay("waterDrop", 760, loc);

        DelayedAction.functionAfterDelay(() =>
        {
            npc.doEmote(32);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.pebbles_my_turn"), duration: 1500);
            loc.playSound("throw");

            DelayedAction.playSoundAfterDelay("waterDrop", 280, loc);
            DelayedAction.playSoundAfterDelay("waterDrop", 460, loc);
            DelayedAction.playSoundAfterDelay("waterDrop", 640, loc);
            DelayedAction.playSoundAfterDelay("waterDrop", 820, loc);
            DelayedAction.playSoundAfterDelay("waterDrop", 1000, loc);

            DelayedAction.functionAfterDelay(() =>
            {
                npc.doEmote(20);
                npc.faceTowardFarmerForPeriod(3000, 3, false, who);
                if (TryConsumeDailyRewardOnce(who, npc, "pebble_skimming"))
                {
                    who.changeFriendship(30, npc);
                }
                npc.showTextAboveHead(Utilities.I18n.Get("bubble.pebbles_win"), duration: 4000);
                RecordMemory(npc.Name, "We skipped stones across the ocean waves at the beach together, laughing as the pebbles bounced over the water.");
            }, 1200);
        }, 1100);
    }

    private void ExecuteSecretWoodsSpirits(NPC npc, Farmer who)
    {
        if (npc == null || who == null) return;
        var loc = who.currentLocation;
        if (loc == null) return;

        npc.controller = null;
        npc.Halt();
        who.Halt();

        loc.playSound("crystal");
        DelayedAction.playSoundAfterDelay("healSound", 500, loc);

        npc.doEmote(32);
        DelayedAction.functionAfterDelay(() =>
        {
            npc.faceTowardFarmerForPeriod(3500, 3, false, who);
            if (TryConsumeDailyRewardOnce(who, npc, "woods_spirits"))
            {
                var gift = ItemRegistry.Create("(O)257", 1); // Morel from the Secret Woods
                if (gift != null)
                {
                    if (who.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
                        GiveItemToFarmerDirectly(who, gift);
                    else
                        _multiplayerSync?.SendSpouseGift(who.UniqueMultiplayerID, "(O)257", 1, npc.Name);
                }

                who.changeFriendship(35, npc);
                loc.playSound("coin");
            }
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.spirits_gift"), duration: 4200);
            RecordMemory(npc.Name, "We stood together in the misty Secret Woods under the moonlight, listening to the gentle whispers of the forest spirits.");
        }, 800);
    }

    private void ShowTreeSelectionSubmenu(NPC npc, long farmerId, string farmerName)
    {
        var treeResponses = new List<Response>
        {
            new Response("all",      Utilities.I18n.Get("menu.trees.all")),
            new Response("oak",      Utilities.I18n.Get("menu.trees.oak")),
            new Response("maple",    Utilities.I18n.Get("menu.trees.maple")),
            new Response("pine",     Utilities.I18n.Get("menu.trees.pine")),
            new Response("mahogany", Utilities.I18n.Get("menu.trees.mahogany")),
            new Response("cancel",   Utilities.I18n.Get("menu.trees.cancel"))
        };

        Game1.currentLocation.afterQuestion = (Farmer who, string answer) =>
        {
            if (string.IsNullOrEmpty(answer))
            {
                ResumeCompanionDefaultJob(npc, who);
                return;
            }
            if (answer == "cancel")
            {
                DelayedAction.functionAfterDelay(() =>
                {
                    ShowFollowerActionMenu(npc, who);
                }, 120);
                return;
            }

            RequestChoreExecution(npc, () =>
            {
                var job = new NpcJob
                {
                    NpcName = npc.Name,
                    Kind = JobKind.ChopTrees,
                    FarmerId = farmerId,
                    FarmerName = farmerName,
                    CreatedTick = Game1.ticks
                };
                job.Data["treeFilter"] = answer;
                if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                    Enqueue(job);
                ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.ChopTrees, farmerId, farmerName, answer);
                Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("hud.chop-trees", new { name = npc.displayName }), HUDMessage.newQuest_type));
            }, who);
        };

        Game1.currentLocation.createQuestionDialogue(
            Utilities.I18n.Get("menu.trees.title", new { npcName = npc.displayName }),
            treeResponses.ToArray(),
            "afterQuestion");
    }

    private void ShowFetchChestSubmenu(NPC npc, long farmerId, string farmerName)
    {
        var fetchResponses = new List<Response>
        {
            new Response("fetch_wood",   Utilities.I18n.Get("menu.fetch.wood")),
            new Response("fetch_stone",  Utilities.I18n.Get("menu.fetch.stone")),
            new Response("fetch_fiber",  Utilities.I18n.Get("menu.fetch.fiber")),
            new Response("fetch_crops",  Utilities.I18n.Get("menu.fetch.crops")),
            new Response("fetch_last",   Utilities.I18n.Get("menu.fetch.last")),
            new Response("cancel",       Utilities.I18n.Get("menu.fetch.cancel"))
        };

        Game1.currentLocation.afterQuestion = (Farmer who, string answer) =>
        {
            if (string.IsNullOrEmpty(answer))
            {
                ResumeCompanionDefaultJob(npc, who);
                return;
            }
            if (answer == "cancel")
            {
                DelayedAction.functionAfterDelay(() =>
                {
                    ShowFollowerActionMenu(npc, who);
                }, 120);
                return;
            }

            RequestChoreExecution(npc, () =>
            {
                var job = new NpcJob
                {
                    NpcName = npc.Name,
                    Kind = JobKind.FetchFromChest,
                    FarmerId = farmerId,
                    FarmerName = farmerName,
                    CreatedTick = Game1.ticks
                };
                job.Data["fetchCategory"] = answer;
                if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                    Enqueue(job);
                ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.FetchFromChest, farmerId, farmerName, answer);
                Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("hud.fetch-chest", new { name = npc.displayName }), HUDMessage.newQuest_type));
            }, who);
        };

        Game1.currentLocation.createQuestionDialogue(
            Utilities.I18n.Get("menu.fetch.title", new { npcName = npc.displayName }),
            fetchResponses.ToArray(),
            "afterQuestion");
    }

    private static bool HasReadyFlowerCropsForHarvest(Farmer? who)
    {
        var checkedLocations = new HashSet<GameLocation>();
        void AddIfNotNull(GameLocation? loc)
        {
            if (loc != null) checkedLocations.Add(loc);
        }

        AddIfNotNull(Game1.getFarm());
        AddIfNotNull(Game1.getLocationFromName("Greenhouse"));
        if (who?.currentLocation != null && (who.currentLocation.IsGreenhouse || who.currentLocation.NameOrUniqueName.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase) || who.currentLocation is Farm))
        {
            AddIfNotNull(who.currentLocation);
        }

        foreach (var loc in checkedLocations)
        {
            foreach (var kv in loc.terrainFeatures.Pairs)
            {
                if (kv.Value is StardewValley.TerrainFeatures.HoeDirt hd
                    && hd.readyForHarvest()
                    && ValleyBuddies.AgencyCore.FarmTaskHelper.IsFlowerCrop(hd.crop))
                {
                    return true;
                }
            }
            foreach (var kv in loc.objects.Pairs)
            {
                if (kv.Value is StardewValley.Objects.IndoorPot pot
                    && pot.hoeDirt.Value is StardewValley.TerrainFeatures.HoeDirt phd
                    && phd.readyForHarvest()
                    && ValleyBuddies.AgencyCore.FarmTaskHelper.IsFlowerCrop(phd.crop))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void ShowHarvestFlowersSubmenu(NPC npc, long farmerId, string farmerName, Farmer? interactingFarmer)
    {
        var location = Game1.currentLocation;
        if (location == null)
        {
            ResumeCompanionDefaultJob(npc, interactingFarmer);
            return;
        }

        var responses = new List<Response>
        {
            new Response("harvest_with_flowers", Utilities.I18n.Get("menu.harvest.with_flowers")),
            new Response("harvest_no_flowers",   Utilities.I18n.Get("menu.harvest.no_flowers")),
            new Response("cancel",               Utilities.I18n.Get("menu.harvest.cancel"))
        };

        location.afterQuestion = (Farmer who, string answer) =>
        {
            var activeFarmer = who ?? interactingFarmer ?? Game1.player;
            if (answer == "harvest_with_flowers" || answer == "harvest_no_flowers")
            {
                bool includeFlowers = answer == "harvest_with_flowers";
                RequestChoreExecution(npc, () =>
                {
                    var job = new NpcJob
                    {
                        NpcName = npc.Name,
                        Kind = JobKind.HarvestCrops,
                        FarmerId = farmerId,
                        FarmerName = farmerName,
                        CreatedTick = Game1.ticks
                    };
                    job.Data["harvestFlowers"] = includeFlowers;
                    if (!Game1.IsMultiplayer || Context.IsMainPlayer)
                        Enqueue(job);
                    ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.HarvestCrops, farmerId, farmerName, includeFlowers ? "true" : "false");
                    Game1.addHUDMessage(new HUDMessage(Utilities.I18n.Get("hud.help_harvest", new { npcName = npc.displayName }), HUDMessage.newQuest_type));
                }, activeFarmer);
                return;
            }

            ResumeCompanionDefaultJob(npc, activeFarmer);
        };

        location.createQuestionDialogue(
            Utilities.I18n.Get("menu.harvest.flowers.title", new { npcName = npc.displayName }),
            responses.ToArray(),
            "afterQuestion");
    }

    private void RequestChoreExecution(NPC npc, Action startChoreAction, Farmer? interactingFarmer = null)
    {
        var initialFarmer = interactingFarmer ?? Game1.player;
        Action<Farmer?> choreWithWake = (Farmer? actingFarmer) =>
        {
            var resolvedFarmer = actingFarmer ?? initialFarmer;
            bool preserveOvernightCamp = GetEveningResolution(npc.Name) == EveningResolutionState.Camping
                || (_overnightGuests.TryGetValue(npc.Name, out var ogInfo) && ogInfo.DidCampfire);
            AbortCurrentJob(npc.Name, preserveOvernightCamp: preserveOvernightCamp);
            npc.controller = null;
            npc.Halt();
            if (npc.isSleeping.Value || (npc.currentLocation != null && IsNpcInsideBedFootprint(npc, npc.currentLocation, out _)))
            {
                WakeAndExtractNpcFromBed(npc, npc.currentLocation, _companions.TryGetValue(npc.Name, out var cChore) ? cChore : null, resolvedFarmer);
            }
            if (!preserveOvernightCamp && _companions.TryGetValue(npc.Name, out var cChore2))
            {
                cChore2.WokeUpByPlayerTonight = true;
                cChore2.NightState = NightLifecycleState.DaytimeActive;
            }
            startChoreAction();
        };

        int hearts = initialFarmer != null ? initialFarmer.getFriendshipHeartLevelForNPC(npc.Name) : 0;
        if (hearts < 4)
        {
            var responses = new List<Response>
            {
                new Response("pay",    Utilities.I18n.Get("menu.hire.pay")),
                new Response("cancel", Utilities.I18n.Get("menu.hire.cancel"))
            };

            DelayedAction.functionAfterDelay(() =>
            {
                if (Game1.currentLocation == null)
                {
                    ResumeCompanionDefaultJob(npc, initialFarmer);
                    return;
                }
                Game1.currentLocation.afterQuestion = (Farmer who, string answer) =>
                {
                    var payer = who ?? initialFarmer;
                    if (answer == "pay")
                    {
                        if (payer != null
                            && TryGetCompanionInfo(npc.Name, out var ownerCheck)
                            && ownerCheck.FarmerId != 0
                            && ownerCheck.FarmerId != payer.UniqueMultiplayerID
                            && Game1.GetPlayer(ownerCheck.FarmerId, onlyOnline: true) != null
                            && _config?.AllowHostOverrideCompanion != true)
                        {
                            string ownerName = !string.IsNullOrWhiteSpace(ownerCheck.FarmerName)
                                ? ownerCheck.FarmerName
                                : (Game1.GetPlayer(ownerCheck.FarmerId, true)?.displayName ?? "Player");
                            Game1.showRedMessage(Utilities.I18n.Get("mp.not_owner", new { npcName = npc.displayName, ownerName = ownerName }));
                            return;
                        }

                        if (payer != null && payer.Money >= 100)
                        {
                            payer.Money -= 100;
                            Game1.currentLocation?.playSound("coin");
                            npc.doEmote(20);
                            npc.showTextAboveHead(Utilities.I18n.Get("bubble.hire_deal"), duration: 2500);
                            RecordMemory(npc.Name, "The farmer paid me 100g to help with chores on the farm today, and I agreed to help them.");
                            choreWithWake(payer);
                        }
                        else
                        {
                            npc.doEmote(28);
                            npc.showTextAboveHead(Utilities.I18n.Get("bubble.hire_no_money"), duration: 2500);
                            ResumeCompanionDefaultJob(npc, payer);
                        }
                    }
                    else
                    {
                        npc.doEmote(32);
                        npc.showTextAboveHead(Utilities.I18n.Get("bubble.hire_declined"), duration: 2500);
                        ResumeCompanionDefaultJob(npc, payer);
                    }
                };

                Game1.currentLocation.createQuestionDialogue(
                    Utilities.I18n.Get("menu.hire.title", new { npcName = npc.displayName }),
                    responses.ToArray(),
                    "afterQuestion");
            }, 120);
        }
        else
        {
            npc.doEmote(20);
            npc.showTextAboveHead(Utilities.I18n.Get($"bubble.friend_help_{Game1.random.Next(1, 5)}"), duration: 2500);
            RecordMemory(npc.Name, "I happily helped my friend the farmer with chores on the farm today.");
            choreWithWake(initialFarmer);
        }
    }
}
