using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buffs;
using StardewValley.Characters;
using ValleyBuddies.Execution;

namespace ValleyBuddies.Systems;

public static class EasterEggEngine
{
    private static readonly ConcurrentDictionary<string, int> _easterEggDailyCooldown = new(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<string> SupportedEasyEasterEggIds = new[]
    {
        "loved_gift",
        "morning_coffee",
        "holding_fish",
        "town_fountain",
        "graveyard_night",
        "saloon_jukebox",
        "beach_pier",
        "community_center",
        "greet_pet",
        "birthday_walk"
    };

    public static void ResetDaily()
    {
        _easterEggDailyCooldown.Clear();
    }

    private static bool TryUseDaily(string secretKey)
    {
        int day = Game1.Date?.TotalDays ?? Game1.dayOfMonth;
        if (_easterEggDailyCooldown.TryGetValue(secretKey, out int lastDay) && lastDay == day)
            return false;
        _easterEggDailyCooldown[secretKey] = day;
        return true;
    }

    private static void SpeakEasterEgg(NPC npc, string key, int emote = 32, int durationMs = 3600, object? tokens = null)
    {
        if (npc == null) return;
        if (emote > 0) npc.doEmote(emote);
        string text = Utilities.I18n.Get(key, tokens);
        if (!string.IsNullOrWhiteSpace(text))
            npc.showTextAboveHead(text, duration: durationMs);
    }

    /// <summary>
    /// Evaluates the 10 easy-to-find companion Easter Eggs once every 120 ticks (max 1 per check, 1/day each).
    /// </summary>
    public static void CheckCompanionEasterEggs(NPC npc, Farmer player, GameLocation? loc)
    {
        if (npc == null || player == null || loc == null) return;
        if (Game1.eventUp || Game1.isFestival() || npc.isSleeping.Value) return;

        if (CheckCompanionBirthdayWalk(npc, player, loc)) return;
        if (CheckHoldingLovedGift(npc, player, loc)) return;
        if (CheckMorningCoffeeBoost(npc, player, loc)) return;
        if (CheckHoldingFishReaction(npc, player, loc)) return;
        if (CheckCompanionGreetsPet(npc, player, loc)) return;
        if (CheckRestoredCommunityCenter(npc, player, loc)) return;
        if (CheckTownFountainWish(npc, player, loc)) return;
        if (CheckGraveyardNightSpook(npc, player, loc)) return;
        if (CheckSaloonPeakJukebox(npc, player, loc)) return;
        if (CheckBeachPierBreeze(npc, player, loc)) return;

        if (CheckPurpleShorts(npc, player, loc)) return;
        if (CheckSebastianFrogs(npc, loc)) return;
        if (CheckHaleyGoldenHour(npc, loc, player)) return;
        if (CheckElliottOceanMuse(npc, loc, player)) return;
        if (CheckHarveyBreak(npc, loc)) return;
    }

    /// <summary>Easy Egg #1: Carrying the NPC's loved gift in hand.</summary>
    public static bool CheckHoldingLovedGift(NPC npc, Farmer player, GameLocation loc)
    {
        var item = player.ActiveItem;
        if (item == null) return false;
        try
        {
            if (npc.getGiftTasteForThisItem(item) != NPC.gift_taste_love)
                return false;
        }
        catch { return false; }

        if (!TryUseDaily($"Easy_LovedGift_{npc.Name}")) return false;

        npc.faceGeneralDirection(player.Position);
        SpeakEasterEgg(npc, "easter_egg.loved_gift", emote: 20, tokens: new { itemName = item.DisplayName, playerName = player.displayName });
        return true;
    }

    /// <summary>Easy Egg #2: Holding Coffee or Triple Shot Espresso before 10:30 AM.</summary>
    public static bool CheckMorningCoffeeBoost(NPC npc, Farmer player, GameLocation loc)
    {
        if (Game1.timeOfDay > 1030) return false;
        var item = player.ActiveItem;
        if (item == null || item.QualifiedItemId is not ("(O)395" or "(O)253")) return false;
        if (!TryUseDaily($"Easy_MorningCoffee_{npc.Name}")) return false;

        npc.faceGeneralDirection(player.Position);
        if (npc.Name.Equals("Harvey", StringComparison.OrdinalIgnoreCase))
            SpeakEasterEgg(npc, "easter_egg.morning_coffee_harvey", emote: 32);
        else if (npc.Name.Equals("Sebastian", StringComparison.OrdinalIgnoreCase))
            SpeakEasterEgg(npc, "easter_egg.morning_coffee_sebastian", emote: 32);
        else
            SpeakEasterEgg(npc, "easter_egg.morning_coffee_general", emote: 32, tokens: new { playerName = player.displayName });
        return true;
    }

    /// <summary>Easy Egg #3: Holding any fish in hand near a companion.</summary>
    public static bool CheckHoldingFishReaction(NPC npc, Farmer player, GameLocation loc)
    {
        var item = player.ActiveItem;
        if (item == null || item.Category != StardewValley.Object.FishCategory) return false;
        if (!TryUseDaily($"Easy_HoldingFish_{npc.Name}")) return false;

        npc.faceGeneralDirection(player.Position);
        if (npc.Name.Equals("Willy", StringComparison.OrdinalIgnoreCase) || npc.Name.Equals("Elliott", StringComparison.OrdinalIgnoreCase))
            SpeakEasterEgg(npc, "easter_egg.holding_fish_angler", emote: 20, tokens: new { fishName = item.DisplayName });
        else
            SpeakEasterEgg(npc, "easter_egg.holding_fish_general", emote: 16, tokens: new { fishName = item.DisplayName });
        return true;
    }

    /// <summary>Easy Egg #4: Passing near the Pelican Town fountain (~(23,25) radius 5).</summary>
    public static bool CheckTownFountainWish(NPC npc, Farmer player, GameLocation loc)
    {
        if (loc is not StardewValley.Locations.Town && !loc.Name.Equals("Town", StringComparison.OrdinalIgnoreCase))
            return false;
        if (Vector2.Distance(npc.Tile, new Vector2(23f, 25f)) > 5.0f) return false;
        if (!TryUseDaily($"Easy_TownFountain_{npc.Name}")) return false;

        try
        {
            loc.playSound("coin");
            loc.playSound("waterSlosh");
        }
        catch { }

        SpeakEasterEgg(npc, "easter_egg.town_fountain", emote: 20, tokens: new { playerName = player.displayName });
        return true;
    }

    /// <summary>Easy Egg #5: Crossing the Pelican Town graveyard (~(47,87) radius 6) after 19:00.</summary>
    public static bool CheckGraveyardNightSpook(NPC npc, Farmer player, GameLocation loc)
    {
        if (Game1.timeOfDay < 1900) return false;
        if (loc is not StardewValley.Locations.Town && !loc.Name.Equals("Town", StringComparison.OrdinalIgnoreCase))
            return false;
        if (Vector2.Distance(npc.Tile, new Vector2(47f, 87f)) > 6.0f) return false;
        if (!TryUseDaily($"Easy_GraveyardNight_{npc.Name}")) return false;

        bool brave = npc.Name is "Abigail" or "Sebastian" or "Krobus" or "Wizard";
        SpeakEasterEgg(npc, brave ? "easter_egg.graveyard_brave" : "easter_egg.graveyard_nervous", emote: brave ? 32 : 16, tokens: new { playerName = player.displayName });
        return true;
    }

    /// <summary>Easy Egg #6: Visiting the Saloon during peak evening hours (18:00 - 22:30).</summary>
    public static bool CheckSaloonPeakJukebox(NPC npc, Farmer player, GameLocation loc)
    {
        if (Game1.timeOfDay is < 1800 or > 2230) return false;
        if (!loc.Name.Equals("Saloon", StringComparison.OrdinalIgnoreCase) && !loc.NameOrUniqueName.Contains("Saloon", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!TryUseDaily($"Easy_SaloonJukebox_{npc.Name}")) return false;

        SpeakEasterEgg(npc, "easter_egg.saloon_jukebox", emote: 56);
        return true;
    }

    /// <summary>Easy Egg #7: Walking onto the Beach pier boards (Y >= 24) in clear weather.</summary>
    public static bool CheckBeachPierBreeze(NPC npc, Farmer player, GameLocation loc)
    {
        if (loc is not StardewValley.Locations.Beach && !loc.Name.Equals("Beach", StringComparison.OrdinalIgnoreCase))
            return false;
        if (loc.IsRainingHere() || npc.Tile.Y < 24f) return false;
        if (!TryUseDaily($"Easy_BeachPier_{npc.Name}")) return false;

        SpeakEasterEgg(npc, "easter_egg.beach_pier", emote: 32);
        return true;
    }

    /// <summary>Easy Egg #8: Entering a restored Community Center together.</summary>
    public static bool CheckRestoredCommunityCenter(NPC npc, Farmer player, GameLocation loc)
    {
        if (!loc.Name.Equals("CommunityCenter", StringComparison.OrdinalIgnoreCase)) return false;
        if (Game1.MasterPlayer?.mailReceived == null || !Game1.MasterPlayer.mailReceived.Contains("ccIsComplete"))
            return false;
        if (!TryUseDaily($"Easy_CommunityCenter_{npc.Name}")) return false;

        try { player.changeFriendship(15, npc); } catch { }
        SpeakEasterEgg(npc, "easter_egg.community_center", emote: 20, tokens: new { playerName = player.displayName });
        return true;
    }

    /// <summary>Easy Egg #9: Companion greets your Pet (dog or cat) within 3 tiles on Farm or FarmHouse.</summary>
    public static bool CheckCompanionGreetsPet(NPC npc, Farmer player, GameLocation loc)
    {
        if (loc is not Farm && loc is not StardewValley.Locations.FarmHouse) return false;
        Pet? nearPet = loc.characters.OfType<Pet>().FirstOrDefault(p => p != null && Vector2.Distance(npc.Tile, p.Tile) <= 3.0f);
        if (nearPet == null) return false;
        if (!TryUseDaily($"Easy_GreetPet_{npc.Name}_{nearPet.Name}")) return false;

        npc.faceGeneralDirection(nearPet.Position);
        nearPet.doEmote(20);
        SpeakEasterEgg(npc, "easter_egg.greet_pet", emote: 20, tokens: new { petName = nearPet.displayName ?? nearPet.Name });
        return true;
    }

    /// <summary>Easy Egg #10: Walking together on the companion's birthday.</summary>
    public static bool CheckCompanionBirthdayWalk(NPC npc, Farmer player, GameLocation loc)
    {
        if (!npc.isBirthday()) return false;
        if (!TryUseDaily($"Easy_BirthdayWalk_{npc.Name}")) return false;

        try
        {
            loc.playSound("yoba");
            player.changeFriendship(30, npc);
        }
        catch { }

        SpeakEasterEgg(npc, "easter_egg.birthday_walk", emote: 20, tokens: new { playerName = player.displayName });
        return true;
    }

    /// <summary>Direct console test helper for vb_test_easter_egg [id].</summary>
    public static bool TestEasterEgg(string? eggId, string? npcName, out string report)
    {
        if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
        {
            report = "You must be in-game to test Easter Eggs.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(eggId))
        {
            report = $"Usage: vb_test_easter_egg <id> [npcName]\n10 Easy Easter Eggs: {string.Join(", ", SupportedEasyEasterEggIds)}";
            return false;
        }

        var player = Game1.player;
        var loc = player.currentLocation;
        NPC? npc = !string.IsNullOrWhiteSpace(npcName) ? Game1.getCharacterFromName(npcName) : null;
        npc ??= NpcActionExecutor.Instance.GetActiveFollowerForPlayer(player.UniqueMultiplayerID)
            ?? loc.characters.FirstOrDefault(c => c != null && c.IsVillager);

        if (npc == null)
        {
            report = $"No villager found near you on '{loc.NameOrUniqueName}'. Pass an NPC name (e.g. vb_test_easter_egg {eggId} Penny).";
            return false;
        }

        switch (eggId.Trim().ToLowerInvariant())
        {
            case "loved_gift":
                SpeakEasterEgg(npc, "easter_egg.loved_gift", emote: 20, tokens: new { itemName = player.ActiveItem?.DisplayName ?? "Gift", playerName = player.displayName });
                break;
            case "morning_coffee":
                string cKey = npc.Name.Equals("Harvey", StringComparison.OrdinalIgnoreCase) ? "easter_egg.morning_coffee_harvey"
                    : (npc.Name.Equals("Sebastian", StringComparison.OrdinalIgnoreCase) ? "easter_egg.morning_coffee_sebastian" : "easter_egg.morning_coffee_general");
                SpeakEasterEgg(npc, cKey, emote: 32, tokens: new { playerName = player.displayName });
                break;
            case "holding_fish":
                bool angler = npc.Name is "Willy" or "Elliott";
                SpeakEasterEgg(npc, angler ? "easter_egg.holding_fish_angler" : "easter_egg.holding_fish_general", emote: angler ? 20 : 16, tokens: new { fishName = player.ActiveItem?.DisplayName ?? "Fish" });
                break;
            case "town_fountain":
                try { loc.playSound("coin"); loc.playSound("waterSlosh"); } catch { }
                SpeakEasterEgg(npc, "easter_egg.town_fountain", emote: 20, tokens: new { playerName = player.displayName });
                break;
            case "graveyard_night":
                bool brave = npc.Name is "Abigail" or "Sebastian" or "Krobus" or "Wizard";
                SpeakEasterEgg(npc, brave ? "easter_egg.graveyard_brave" : "easter_egg.graveyard_nervous", emote: brave ? 32 : 16, tokens: new { playerName = player.displayName });
                break;
            case "saloon_jukebox":
                SpeakEasterEgg(npc, "easter_egg.saloon_jukebox", emote: 56);
                break;
            case "beach_pier":
                SpeakEasterEgg(npc, "easter_egg.beach_pier", emote: 32);
                break;
            case "community_center":
                try { player.changeFriendship(15, npc); } catch { }
                SpeakEasterEgg(npc, "easter_egg.community_center", emote: 20, tokens: new { playerName = player.displayName });
                break;
            case "greet_pet":
                SpeakEasterEgg(npc, "easter_egg.greet_pet", emote: 20, tokens: new { petName = loc.characters.OfType<Pet>().FirstOrDefault()?.displayName ?? "Buddy" });
                break;
            case "birthday_walk":
                try { loc.playSound("yoba"); player.changeFriendship(30, npc); } catch { }
                SpeakEasterEgg(npc, "easter_egg.birthday_walk", emote: 20, tokens: new { playerName = player.displayName });
                break;
            default:
                report = $"Unknown Easter Egg '{eggId}'. Valid: {string.Join(", ", SupportedEasyEasterEggIds)}";
                return false;
        }

        report = $"Triggered Easter Egg '{eggId}' for {npc.Name} at {loc.NameOrUniqueName}.";
        return true;
    }

    /// <summary>Easter Egg #1: Abigail eats quartz / amethyst</summary>
    public static bool CheckAbigailCronch(NPC npc, string qualifiedId, GameLocation loc)
    {
        if (!npc.Name.Equals("Abigail", StringComparison.OrdinalIgnoreCase)) return false;
        if (qualifiedId is not ("(O)66" or "(O)80" or "66" or "80")) return false;
        if (!TryUseDaily("AbigailCronch")) return false;

        loc.playSound("eat");
        npc.doEmote(20);
        npc.showTextAboveHead(Utilities.I18n.Get("easter_egg.abigail_cronch"), duration: 3000);
        return true;
    }

    /// <summary>Easter Egg #2: Lucky Purple Shorts panic</summary>
    public static bool CheckPurpleShorts(NPC npc, Farmer player, GameLocation loc)
    {
        if (player.ActiveItem?.QualifiedItemId is not ("(O)789" or "(O)71")) return false;
        if (!npc.Name.Equals("Lewis", StringComparison.OrdinalIgnoreCase) && !npc.Name.Equals("Marnie", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!TryUseDaily($"PurpleShorts_{npc.Name}")) return false;

        if (npc.Name.Equals("Lewis", StringComparison.OrdinalIgnoreCase))
        {
            loc.playSound("dustmeep");
            npc.jump();
            npc.doEmote(16);
            npc.showTextAboveHead(Utilities.I18n.Get("easter_egg.lewis_shorts"), duration: 3500);
            return true;
        }

        npc.doEmote(60);
        npc.showTextAboveHead(Utilities.I18n.Get("easter_egg.marnie_shorts"), duration: 3500);
        return true;
    }

    /// <summary>Easter Egg #3: Sebastian rain & frogs</summary>
    public static bool CheckSebastianFrogs(NPC npc, GameLocation loc)
    {
        if (!npc.Name.Equals("Sebastian", StringComparison.OrdinalIgnoreCase)) return false;
        if (!Game1.IsRainingHere(loc)) return false;
        if (!TryUseDaily("SebastianFrogs")) return false;

        loc.playSound("frog_slap");
        npc.doEmote(32);
        npc.showTextAboveHead(Utilities.I18n.Get("easter_egg.sebastian_frogs"), duration: 3500);
        return true;
    }

    /// <summary>Easter Egg #4: Shane & Charlie</summary>
    public static void CheckShaneCharlie(NPC npc, FarmAnimal animal, GameLocation loc)
    {
        if (!npc.Name.Equals("Shane", StringComparison.OrdinalIgnoreCase)) return;
        if (animal.type.Value.Contains("Chicken", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryUseDaily("ShaneCharlie")) return;
            loc.playSound("cluck");
            npc.doEmote(20);
            npc.showTextAboveHead(Utilities.I18n.Get("easter_egg.shane_charlie"), duration: 3000);
            animal.friendshipTowardFarmer.Value = Math.Min(1000, animal.friendshipTowardFarmer.Value + 20);
        }
    }

    /// <summary>Easter Egg #5: Krobus Friday silence</summary>
    public static bool IsKrobusSilentDay(NPC npc)
    {
        if (!npc.Name.Equals("Krobus", StringComparison.OrdinalIgnoreCase)) return false;
        return (Game1.dayOfMonth % 7) == 5; // Friday
    }

    /// <summary>Easter Egg #6: Trash can check reaction</summary>
    public static void OnCheckTrashCan(NPC npc, GameLocation loc)
    {
        if (npc.Name.Equals("Linus", StringComparison.OrdinalIgnoreCase))
        {
            npc.doEmote(32);
            npc.showTextAboveHead(Utilities.I18n.Get("easter_egg.linus_trash"), duration: 3000);
        }
        else if (npc.Name is "Haley" or "Pierre" or "Alex")
        {
            npc.doEmote(36);
            npc.showTextAboveHead(Utilities.I18n.Get("easter_egg.haley_trash"), duration: 3000);
        }
    }

    /// <summary>Easter Egg #7: Haley Golden Hour photo</summary>
    public static bool CheckHaleyGoldenHour(NPC npc, GameLocation loc, Farmer player)
    {
        if (!npc.Name.Equals("Haley", StringComparison.OrdinalIgnoreCase)) return false;
        if (Game1.timeOfDay is >= 1700 and <= 1930 && !Game1.IsRainingHere(loc) && loc.IsOutdoors)
        {
            if (!TryUseDaily("HaleyGoldenHour")) return false;
            loc.playSound("cameraNoise");
            npc.doEmote(32);
            npc.showTextAboveHead(Utilities.I18n.Get("easter_egg.haley_golden_hour"), duration: 3500);
            try { player.changeFriendship(30, npc); } catch { }
            return true;
        }
        return false;
    }

    /// <summary>Easter Egg #9: Elliott beach ocean muse</summary>
    public static bool CheckElliottOceanMuse(NPC npc, GameLocation loc, Farmer player)
    {
        if (!npc.Name.Equals("Elliott", StringComparison.OrdinalIgnoreCase)) return false;
        if (loc.Name.Equals("Beach", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryUseDaily("ElliottOceanMuse")) return false;
            npc.faceGeneralDirection(new Vector2(npc.Position.X, npc.Position.Y + 200f));
            npc.showTextAboveHead(Utilities.I18n.Get("easter_egg.elliott_ocean"), duration: 4000);
            if (player.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
            {
                var buff = new Buff(
                    id: "ValleyBuddies_ElliottMuse",
                    displayName: Utilities.I18n.Get("easter_egg.elliott_buff_title"),
                    description: Utilities.I18n.Get("easter_egg.elliott_buff_desc"),
                    duration: 60_000,
                    effects: new BuffEffects { LuckLevel = { 1 } }
                );
                player.applyBuff(buff);
            }
            else
            {
                ModEntry.MultiplayerSync?.SendLivingEventBuff(player.UniqueMultiplayerID, "elliott_muse");
            }
            return true;
        }
        return false;
    }

    /// <summary>Easter Egg #12: Harvey coffee break & biplane</summary>
    public static bool CheckHarveyBreak(NPC npc, GameLocation loc)
    {
        if (!npc.Name.Equals("Harvey", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryUseDaily("HarveyBreak")) return false;

        if (Game1.timeOfDay is >= 1200 and <= 1700 && !Game1.IsRainingHere(loc) && loc.IsOutdoors)
        {
            npc.faceDirection(0); // look north at sky
            npc.doEmote(32);
            npc.showTextAboveHead(Utilities.I18n.Get("easter_egg.harvey_plane"), duration: 3500);
        }
        else
        {
            loc.playSound("drink");
            npc.doEmote(20);
            npc.showTextAboveHead(Utilities.I18n.Get("easter_egg.harvey_coffee"), duration: 3500);
        }
        return true;
    }
}
