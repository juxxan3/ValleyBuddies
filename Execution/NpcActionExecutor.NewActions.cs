using System;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using ValleyBuddies.Systems;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    private void TickStopFollowing(NpcJob job, NPC npc, ulong gameTick)
    {
        if (IsMarriedToAnyFarmer(npc))
        {
            DismissCompanion(npc.Name);
            CompleteJob(job, npc);
            return;
        }

        var loc = npc.currentLocation;
        if (loc is Farm || loc is StardewValley.Locations.FarmHouse || (loc != null && loc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase)))
        {
            DismissCompanion(npc.Name);
            job.Done = true;
        }
        else
        {
            RestoreNpcToVanillaState(npc, resumeSchedule: true);
            npc.doEmote(32);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.leave_farewell"), duration: 3000);
            CompleteJob(job, npc);
        }
    }

    private void TickFeedAnimals(NpcJob job, NPC npc, ulong gameTick)
    {
        var loc = npc.currentLocation ?? Game1.getFarm();
        if (loc == null) { CompleteJob(job, npc); return; }
        var player = GetJobFarmer(job);
        var hayFarm = loc as Farm ?? Game1.getFarm();
        int fedCount = 0;

        if (loc is AnimalHouse ah)
        {
            for (int x = 0; x < ah.map.Layers[0].LayerWidth; x++)
            {
                for (int y = 0; y < ah.map.Layers[0].LayerHeight; y++)
                {
                    if (hayFarm == null || hayFarm.piecesOfHay.Value <= 0) break;
                    if (ah.doesTileHaveProperty(x, y, "Trough", "Back") != null)
                    {
                        var v = new Vector2(x, y);
                        if (!ah.Objects.ContainsKey(v))
                        {
                            ah.Objects.Add(v, ItemRegistry.Create<StardewValley.Object>("(O)178"));
                            hayFarm.piecesOfHay.Value--;
                            fedCount++;
                        }
                    }
                }
                if (hayFarm == null || hayFarm.piecesOfHay.Value <= 0) break;
            }
        }
        else if (loc is Farm farm)
        {
            foreach (var b in farm.buildings)
            {
                if (hayFarm == null || hayFarm.piecesOfHay.Value <= 0) break;
                if (b.indoors.Value is AnimalHouse bAh)
                {
                    for (int x = 0; x < bAh.map.Layers[0].LayerWidth; x++)
                    {
                        for (int y = 0; y < bAh.map.Layers[0].LayerHeight; y++)
                        {
                            if (hayFarm.piecesOfHay.Value <= 0) break;
                            if (bAh.doesTileHaveProperty(x, y, "Trough", "Back") != null)
                            {
                                var v = new Vector2(x, y);
                                if (!bAh.Objects.ContainsKey(v))
                                {
                                    bAh.Objects.Add(v, ItemRegistry.Create<StardewValley.Object>("(O)178"));
                                    hayFarm.piecesOfHay.Value--;
                                    fedCount++;
                                }
                            }
                        }
                        if (hayFarm.piecesOfHay.Value <= 0) break;
                    }
                }
            }
        }

        loc.playSound("dirtyHit");
        npc.doEmote(20);
        npc.showTextAboveHead(fedCount > 0
            ? Utilities.I18n.Get("bubble.feed_done", new { count = fedCount })
            : Utilities.I18n.Get("bubble.feed_already_full"), duration: 3000);
        if (fedCount > 0)
        {
            try { TryAwardDailyActivityFriendship(player, npc, "feed_animals", 15, 15); } catch { }
        }
        CompleteJob(job, npc);
    }

    private void TickCollectAnimalProducts(NpcJob job, NPC npc, ulong gameTick)
    {
        var loc = npc.currentLocation ?? Game1.getFarm();
        if (loc == null) { CompleteJob(job, npc); return; }
        var player = GetJobFarmer(job) ?? Game1.player;
        int collected = 0;

        var harvestable = loc.Objects.Pairs
            .Where(p => ValleyBuddies.AgencyCore.FarmTaskHelper.IsAnimalProductObject(loc, p.Value))
            .ToList();

        foreach (var pair in harvestable)
        {
            var item = pair.Value;
            loc.Objects.Remove(pair.Key);
            GiveItemToFarmerDirectly(player, item);
            collected++;
        }

        if (loc is Farm fHarvest)
        {
            foreach (var animal in fHarvest.animals.Values.ToList())
            {
                if (ValleyBuddies.AgencyCore.FarmTaskHelper.TryHarvestAnimalProduce(animal, loc) is Item prod)
                {
                    GiveItemToFarmerDirectly(player, prod);
                    collected++;
                }
            }
        }
        else if (loc is AnimalHouse ahHarvest)
        {
            foreach (var animal in ahHarvest.animals.Values.ToList())
            {
                if (ValleyBuddies.AgencyCore.FarmTaskHelper.TryHarvestAnimalProduce(animal, loc) is Item prod)
                {
                    GiveItemToFarmerDirectly(player, prod);
                    collected++;
                }
            }
        }

        if (collected > 0)
        {
            loc.playSound("coin");
            npc.doEmote(32);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.collect_done", new { count = collected }), duration: 3000);
            try { TryAwardDailyActivityFriendship(player, npc, "collect_animal_products", 15, 15); } catch { }
        }
        else
        {
            npc.doEmote(8);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.collect_none"), duration: 2500);
        }
        CompleteJob(job, npc);
    }

    private void TickWaterPetBowl(NpcJob job, NPC npc, ulong gameTick)
    {
        var loc = npc.currentLocation ?? Game1.getFarm();
        if (loc == null) { CompleteJob(job, npc); return; }
        var player = GetJobFarmer(job);
        PetBowl? targetBowl = null;

        if (loc.buildings != null)
            targetBowl = loc.buildings.OfType<PetBowl>().FirstOrDefault();

        if (targetBowl == null && Game1.getFarm()?.buildings != null)
            targetBowl = Game1.getFarm().buildings.OfType<PetBowl>().FirstOrDefault();

        if (targetBowl != null && !targetBowl.watered.Value)
        {
            targetBowl.watered.Value = true;
            loc.playSound("glug");
            npc.doEmote(20);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.petbowl_filled"), duration: 3000);
            try { TryAwardDailyActivityFriendship(player, npc, "water_pet_bowl", 10, 10); } catch { }
        }
        else
        {
            npc.doEmote(32);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.petbowl_already_full"), duration: 2500);
        }
        CompleteJob(job, npc);
    }

    private static bool IsPersistentCycleMachine(StardewValley.Object machine)
    {
        if (machine is StardewValley.Objects.Cask || machine is StardewValley.Objects.CrabPot)
            return true;
        if (machine.heldObject.Value is StardewValley.Objects.Chest)
            return true;
        if (machine.QualifiedItemId is "(BC)21" or "(BC)105" or "(BC)264" or "(BC)10" or "(BC)163"
            or "(BC)154" or "(BC)DeluxeWormBin" or "(BC)MushroomLog" or "(BC)128"
            or "(BC)117" or "(BC)127" or "(BC)160" or "(BC)165" or "(BC)231" or "(BC)246" or "(BC)9")
            return true;

        var data = machine.GetMachineData();
        if (data?.OutputRules != null)
        {
            foreach (var rule in data.OutputRules)
            {
                if (rule?.Triggers == null) continue;
                foreach (var trig in rule.Triggers)
                {
                    if (trig.Trigger == StardewValley.GameData.Machines.MachineOutputTrigger.OutputCollected
                        || trig.Trigger == StardewValley.GameData.Machines.MachineOutputTrigger.DayUpdate)
                        return true;
                }
            }
        }
        return false;
    }

    private void TickCollectMachines(NpcJob job, NPC npc, ulong gameTick)
    {
        var loc = npc.currentLocation ?? Game1.getFarm();
        if (loc == null) { CompleteJob(job, npc); return; }
        var player = GetJobFarmer(job) ?? Game1.player;
        int count = 0;

        foreach (var pair in loc.Objects.Pairs.ToList())
        {
            var machine = pair.Value;
            if (machine == null || !machine.readyForHarvest.Value || machine.heldObject.Value == null)
                continue;
            if (machine is StardewValley.Objects.CrabPot || machine.heldObject.Value is StardewValley.Objects.Chest)
                continue;

            if (player == Game1.player)
            {
                if (!player.couldInventoryAcceptThisItem(machine.heldObject.Value))
                    continue;

                bool wasReady = machine.readyForHarvest.Value;
                try
                {
                    machine.checkForAction(player, justCheckingForActivity: false);
                }
                catch { }

                if (!machine.readyForHarvest.Value && wasReady)
                {
                    count++;
                }
            }
            else
            {
                if (IsPersistentCycleMachine(machine))
                    continue;

                var held = machine.heldObject.Value;
                machine.heldObject.Value = null;
                machine.readyForHarvest.Value = false;
                GiveItemToFarmerDirectly(player, held);
                count++;
            }
        }

        if (count > 0)
        {
            loc.playSound("coin");
            npc.doEmote(32);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.machines_done", new { count = count }), duration: 3000);
            try { player?.changeFriendship(15, npc); } catch { }
        }
        else
        {
            npc.doEmote(8);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.machines_none"), duration: 2500);
        }
        CompleteJob(job, npc);
    }

    private void TickPlantSeeds(NpcJob job, NPC npc, ulong gameTick)
    {
        var loc = npc.currentLocation ?? Game1.getFarm();
        if (loc == null) { CompleteJob(job, npc); return; }
        var player = GetJobFarmer(job) ?? Game1.player;
        int planted = 0;

        string? wantedSeed = job.Data.TryGetValue("seedType", out var stObj) ? stObj?.ToString()?.Trim() : null;
        string? wantedSeedQid = !string.IsNullOrWhiteSpace(wantedSeed)
            ? SafeItemResolver.ResolveObjectQualifiedId(wantedSeed)
            : null;

        if (player != null)
        {
            foreach (var pair in loc.terrainFeatures.Pairs)
            {
                if (pair.Value is HoeDirt hd && hd.crop == null)
                {
                    int tileX = (int)pair.Key.X;
                    int tileY = (int)pair.Key.Y;

                    Item? seedItem = null;
                    foreach (var item in player.Items)
                    {
                        if (item != null && item.Stack > 0 && item.Category == StardewValley.Object.SeedsCategory)
                        {
                            if (!string.IsNullOrWhiteSpace(wantedSeed))
                            {
                                bool matches = (!string.IsNullOrWhiteSpace(wantedSeedQid) && string.Equals(item.QualifiedItemId, wantedSeedQid, StringComparison.OrdinalIgnoreCase))
                                    || item.Name.Contains(wantedSeed, StringComparison.OrdinalIgnoreCase)
                                    || item.DisplayName.Contains(wantedSeed, StringComparison.OrdinalIgnoreCase);
                                if (!matches) continue;
                            }

                            if (Crop.TryGetData(item.ItemId, out _) && loc.CanPlantSeedsHere(item.ItemId, tileX, tileY, false, out _))
                            {
                                seedItem = item;
                                break;
                            }
                        }
                    }

                    if (seedItem != null && hd.plant(seedItem.ItemId, player, isFertilizer: false))
                    {
                        loc.playSound("dirtyHit");
                        planted++;
                        seedItem.Stack--;
                        if (seedItem.Stack <= 0)
                        {
                            player.removeItemFromInventory(seedItem);
                        }
                        if (planted >= 15) break;
                    }
                }
            }
        }

        if (planted > 0)
        {
            npc.doEmote(32);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.plant_done", new { count = planted }), duration: 3000);
            try { player?.changeFriendship(15, npc); } catch { }
        }
        else
        {
            npc.doEmote(8);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.plant_none"), duration: 2500);
        }
        CompleteJob(job, npc);
    }

    private void TickRepairFences(NpcJob job, NPC npc, ulong gameTick)
    {
        var loc = npc.currentLocation ?? Game1.getFarm();
        if (loc == null) { CompleteJob(job, npc); return; }
        var player = GetJobFarmer(job);
        int repaired = 0;

        foreach (var pair in loc.Objects.Pairs)
        {
            if (pair.Value is Fence fence && fence.health.Value < fence.maxHealth.Value)
            {
                fence.repair();
                repaired++;
                if (repaired >= 10) break;
            }
        }

        if (repaired > 0)
        {
            loc.playSound("axchop");
            npc.doEmote(32);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.fence_repaired", new { count = repaired }), duration: 3000);
            try { TryAwardDailyActivityFriendship(player, npc, "repair_fences", 15, 15); } catch { }
        }
        else
        {
            npc.doEmote(32);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.fence_none"), duration: 2500);
        }
        CompleteJob(job, npc);
    }

    private void TickCombatAssist(NpcJob job, NPC npc, ulong gameTick)
    {
        var loc = npc.currentLocation;
        if (loc == null) { CompleteJob(job, npc); return; }
        var player = GetJobFarmer(job) ?? Game1.player;

        npc.doEmote(16);
        loc.playSound("swordswipe");
        npc.showTextAboveHead(Utilities.I18n.Get("bubble.combat_assist"), duration: 2500);
        try { TryAwardDailyActivityFriendship(player, npc, "combat_assist", 10, 10); } catch { }
        CompleteJob(job, npc);
    }

    private void TickFishTogether(NpcJob job, NPC npc, ulong gameTick)
    {
        var loc = npc.currentLocation;
        var player = GetJobFarmer(job) ?? Game1.player;
        if (loc == null || player == null) { CompleteJob(job, npc); return; }

        _companions.TryGetValue(npc.Name, out var comp);
        if (comp == null)
        {
            loc.playSound("cast");
            loc.playSound("dropItemInWater");
            npc.doEmote(56);
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.fish_together"), duration: 3500);
            try { TryAwardDailyActivityFriendship(player, npc, "fish_together", 15, 15); } catch { }
            CompleteJob(job, npc);
            return;
        }

        if (!job.Data.ContainsKey("fishStartTick"))
        {
            var spot = FindSeparatedFishingSpot(loc, player, npc);
            if (spot.standTile == Point.Zero || spot.waterTile == Point.Zero)
            {
                npc.doEmote(12);
                npc.showTextAboveHead(Utilities.I18n.Get("hangout.fish.no_water"), duration: 3500);
                CompleteJob(job, npc);
                return;
            }

            job.Data["fishStartTick"] = gameTick.ToString();
            comp.IsWalkingToFishingSpot = true;
            comp.FishingStandTile = spot.standTile;
            comp.FishingWaterTile = spot.waterTile;
            comp.OwnerFishingTicks = 180;
            comp.ShownFishingStartBubble = false;
        }

        TickCompanionFishing(job, npc, comp, player, gameTick);

        ulong startTick = ulong.TryParse(job.Data["fishStartTick"]?.ToString(), out var st) ? st : gameTick;
        ulong elapsed = gameTick >= startTick ? (gameTick - startTick) : 0;

        bool isPlayerFishing = player.UsingTool && player.CurrentTool is StardewValley.Tools.FishingRod;
        if (elapsed >= 1200 && !isPlayerFishing && !comp.IsWalkingToFishingSpot)
        {
            comp.IsFishingTogether = false;
            comp.IsWalkingToFishingSpot = false;
            comp.FishingStandTile = Point.Zero;
            comp.FishingWaterTile = Point.Zero;
            npc.Halt();
            npc.controller = null;
            CompleteJob(job, npc);
        }
    }

    private void TickScoutMines(NpcJob job, NPC npc, ulong gameTick)
    {
        var loc = npc.currentLocation;
        if (loc == null) { CompleteJob(job, npc); return; }
        var player = GetJobFarmer(job) ?? Game1.player;

        npc.doEmote(16);
        loc.playSound("discoverMineral");
        npc.showTextAboveHead(Utilities.I18n.Get("bubble.scout_mines"), duration: 3000);
        try { TryAwardDailyActivityFriendship(player, npc, "scout_mines", 15, 15); } catch { }
        CompleteJob(job, npc);
    }

    private void TickCookMeal(NpcJob job, NPC npc)
    {
        var player = GetJobFarmer(job) ?? Game1.player;
        npc.currentLocation?.playSound("furnace");
        npc.doEmote(32);

        if (TryConsumeDailyRewardOnce(player, npc, "cook_meal"))
        {
            string dishQid = "(O)196"; // Salad fallback
            if (job.Data.TryGetValue("dishName", out var dishObj) && dishObj != null)
            {
                string? resolvedQid = SafeItemResolver.ResolveObjectQualifiedId(dishObj.ToString());
                if (!string.IsNullOrWhiteSpace(resolvedQid))
                {
                    var probe = ItemRegistry.Create(resolvedQid, 1, allowNull: true);
                    if (probe is StardewValley.Object cookObj
                        && cookObj.Edibility > 0
                        && cookObj.Category == StardewValley.Object.CookingCategory)
                    {
                        dishQid = resolvedQid;
                    }
                }
            }

            var snack = ItemRegistry.Create(dishQid, 1, allowNull: true) ?? ItemRegistry.Create("(O)196", 1);
            if (snack != null)
                GiveItemToFarmerDirectly(player, snack);
            try { TryAwardDailyActivityFriendship(player, npc, "cook_meal", 20, 20); } catch { }
        }

        npc.showTextAboveHead(Utilities.I18n.Get("bubble.cook_meal"), duration: 3500);
        CompleteJob(job, npc);
    }

    private void TickPlayMusic(NpcJob job, NPC npc)
    {
        var loc = npc.currentLocation;
        if (loc == null) { CompleteJob(job, npc); return; }
        var player = GetJobFarmer(job) ?? Game1.player;

        loc.playSound("flute");
        npc.doEmote(56);
        npc.showTextAboveHead(Utilities.I18n.Get("bubble.music"), duration: 3500);
        try { TryAwardDailyActivityFriendship(player, npc, "play_music", 15, 15); } catch { }
        CompleteJob(job, npc);
    }

    private void TickDanceTogether(NpcJob job, NPC npc)
    {
        var loc = npc.currentLocation;
        if (loc == null) { CompleteJob(job, npc); return; }
        var player = GetJobFarmer(job) ?? Game1.player;

        loc.playSound("yoba");
        npc.doEmote(32);
        npc.showTextAboveHead(Utilities.I18n.Get("bubble.dance"), duration: 3500);
        try { TryAwardDailyActivityFriendship(player, npc, "dance_together", 20, 20); } catch { }
        CompleteJob(job, npc);
    }

    private void TickStargaze(NpcJob job, NPC npc)
    {
        var player = GetJobFarmer(job) ?? Game1.player;
        npc.faceDirection(0);
        npc.doEmote(20);
        npc.showTextAboveHead(Utilities.I18n.Get("bubble.stargaze"), duration: 4000);
        try { TryAwardDailyActivityFriendship(player, npc, "stargaze", 20, 20); } catch { }
        CompleteJob(job, npc);
    }

    private void TickSendLetterTomorrow(NpcJob job, NPC npc)
    {
        var player = GetJobFarmer(job) ?? Game1.player;
        string title = job.Data.TryGetValue("title", out var t) ? t.ToString()! : Utilities.I18n.Get("mail.default_friend_title");
        string message = job.Data.TryGetValue("message", out var m) ? m.ToString()! : Utilities.I18n.Get("mail.default_friend_message");
        string? item = job.Data.TryGetValue("item", out var it) ? it.ToString() : null;

        DynamicMailService.Instance?.QueueLetter(npc.Name, title, message, item, player.UniqueMultiplayerID, _multiplayerSync);

        npc.doEmote(20);
        npc.showTextAboveHead(Utilities.I18n.Get("bubble.letter_tomorrow"), duration: 3000);
        try { TryAwardDailyActivityFriendship(player, npc, "send_letter_tomorrow", 15, 15); } catch { }
        CompleteJob(job, npc);
    }
}
