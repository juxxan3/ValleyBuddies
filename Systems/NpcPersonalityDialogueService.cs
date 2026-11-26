using System;
using System.Collections.Generic;
using StardewValley;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Systems;

/// <summary>
/// Dedicated service delivering rich, unique NPC dialogue personalities across:
/// 1. Spouse private rooms (SpouseRoom) in FarmHouse
/// 2. Specific town hangouts (Fishing, Saloon, Spa, Museum, Beach)
/// 3. Organic seasonal & weather outdoor reflections
/// </summary>
public static class NpcPersonalityDialogueService
{
    private static readonly HashSet<string> KnownSpousesVanillaAndKrobus = new(StringComparer.OrdinalIgnoreCase)
    {
        "Penny", "Abigail", "Sebastian", "Sam", "Alex", "Leah", "Elliott",
        "Harvey", "Maru", "Haley", "Emily", "Shane", "Krobus"
    };

    private static readonly HashSet<string> KnownSpousesSve = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sophia", "Claire", "Victor", "Olivia", "Lance"
    };

    private static readonly HashSet<string> FishingStartNpcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Willy", "Elliott", "Pam", "Linus", "Leo", "Penny", "Abigail",
        "Sebastian", "Haley", "Sam", "Leah", "Shane", "Harvey", "Maru",
        "Alex", "Emily", "Krobus"
    };

    private static readonly HashSet<string> SaloonNpcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gus", "Shane", "Pam", "Abigail", "Sebastian", "Sam", "Emily",
        "Leah", "Elliott", "Clint", "Penny", "Harvey", "Haley", "Alex", "Maru"
    };

    private static readonly HashSet<string> SpaNpcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Penny", "Abigail", "Sebastian", "Sam", "Alex", "Leah",
        "Elliott", "Harvey", "Maru", "Haley", "Emily", "Shane", "Krobus"
    };

    private static readonly HashSet<string> MuseumNpcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Penny", "Maru", "Elliott", "Harvey", "Demetrius", "Abigail",
        "Sebastian", "Leah", "Sam", "Haley", "Emily", "Alex", "Shane", "Krobus"
    };

    private static readonly HashSet<string> BeachNpcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Haley", "Elliott", "Willy", "Abigail", "Sam", "Alex",
        "Penny", "Leah", "Sebastian", "Harvey", "Maru", "Emily", "Shane", "Krobus"
    };

    // ─────────────────────────────────────────────────────────────────────────
    // 1. Spouse Room Activity Key
    // ─────────────────────────────────────────────────────────────────────────

    public static string GetSpouseRoomActivityKey(NPC spouse)
    {
        if (spouse == null) return "spouse.room.general.v1";
        string name = spouse.Name;
        int v = Game1.random.Next(1, 4);

        if (KnownSpousesVanillaAndKrobus.Contains(name) || KnownSpousesSve.Contains(name))
        {
            return $"spouse.room.{name.ToLowerInvariant()}.v{v}";
        }

        // Fallback for custom / Ridgeside / East Scarp NPCs by archetype
        string archetype = GetPersonalityArchetype(spouse);
        int vArch = Game1.random.Next(1, 3);
        return archetype switch
        {
            "adventurous" => $"spouse.room.active.v{vArch}",
            "poetic" => $"spouse.room.creative.v{vArch}",
            "cozy" => $"spouse.room.reading.v{vArch}",
            "technical" => $"spouse.room.hobby.v{vArch}",
            "shy" => $"spouse.room.chill.v{vArch}",
            _ => $"spouse.room.general.v{vArch}"
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. Hangout Custom Dialogue Lines
    // ─────────────────────────────────────────────────────────────────────────

    public static string GetFishingStartLine(NPC npc, Farmer owner)
    {
        string name = npc.Name;
        string key = FishingStartNpcs.Contains(name)
            ? $"hangout.fish.start.{name.ToLowerInvariant()}"
            : $"hangout.fish.start.general.v{Game1.random.Next(1, 4)}";

        return I18n.Get(key, new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
    }

    public static string GetSaloonLine(NPC npc, Farmer owner)
    {
        string name = npc.Name;
        if (SaloonNpcs.Contains(name))
        {
            return I18n.Get($"hangout.saloon.{name.ToLowerInvariant()}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
        }

        int v = Game1.random.Next(1, 4);
        return I18n.Get($"hangout.saloon.general.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
    }

    public static string GetSpaLine(NPC npc, Farmer owner)
    {
        string name = npc.Name;
        if (SpaNpcs.Contains(name))
        {
            return I18n.Get($"hangout.spa.{name.ToLowerInvariant()}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
        }

        int v = Game1.random.Next(1, 4);
        return I18n.Get($"hangout.spa.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
    }

    public static string GetMuseumLine(NPC npc, Farmer owner)
    {
        string name = npc.Name;
        if (MuseumNpcs.Contains(name))
        {
            return I18n.Get($"hangout.museum.{name.ToLowerInvariant()}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
        }

        int v = Game1.random.Next(1, 4);
        return I18n.Get($"hangout.museum.general.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
    }

    public static string GetBeachLine(NPC npc, Farmer owner)
    {
        string name = npc.Name;
        if (BeachNpcs.Contains(name))
        {
            return I18n.Get($"hangout.beach.{name.ToLowerInvariant()}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
        }

        int v = Game1.random.Next(1, 5);
        return I18n.Get($"hangout.beach.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. Outdoor Season & Weather Flavor Reflection
    // ─────────────────────────────────────────────────────────────────────────

    public static string GetSeasonWeatherFlavorLine(NPC npc, Farmer owner, GameLocation loc)
    {
        string seasonKey = loc.IsRainingHere()
            ? "storm"
            : (Game1.currentSeason?.ToLowerInvariant() ?? "spring");

        string archetype = GetPersonalityArchetype(npc);
        int variant = Game1.random.Next(1, 3);
        string key = $"season.{seasonKey}.{archetype}.v{variant}";

        return I18n.Get(key, new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4. Personality Archetype Resolution (SDV 1.6+ API Clean)
    // ─────────────────────────────────────────────────────────────────────────

    public static string GetPersonalityArchetype(NPC npc)
    {
        if (npc == null) return "chill";
        string name = npc.Name;

        // Known named characters
        if (name is "Abigail" or "Alex" or "Lance" or "Sam") return "adventurous";
        if (name is "Elliott" or "Leah" or "Emily" or "Sophia") return "poetic";
        if (name is "Penny" or "Harvey" or "Jodi" or "Caroline" or "Claire") return "cozy";
        if (name is "Maru" or "Demetrius" or "Sebastian" or "Victor") return "technical";
        if (name is "Krobus" or "Shane" or "Clint" or "Linus") return "shy";
        if (name is "Haley" or "Willy" or "Pam" or "Gus" or "Olivia" or "Leo") return "chill";

        // Dynamic SDV 1.6 CharacterData inspection (Zero SDV 1.5 legacy fields)
        try
        {
            var data = npc.GetData();
            if (data != null)
            {
                if (data.SocialAnxiety == StardewValley.GameData.Characters.NpcSocialAnxiety.Outgoing)
                {
                    return (data.Optimism == StardewValley.GameData.Characters.NpcOptimism.Positive) ? "adventurous" : "chill";
                }
                if (data.SocialAnxiety == StardewValley.GameData.Characters.NpcSocialAnxiety.Shy)
                {
                    return "shy";
                }
                if (data.Manner == StardewValley.GameData.Characters.NpcManner.Polite)
                {
                    return "cozy";
                }
            }
        }
        catch { }

        return "chill";
    }
}
