using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleyBuddies.Utilities;

public static class LocationResolver
{
    private static readonly Dictionary<string, string> CanonicalAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["farm"] = "Farm", ["granja"] = "Farm", ["mi granja"] = "Farm",
        ["farmhouse"] = "FarmHouse", ["casa"] = "FarmHouse", ["house"] = "FarmHouse", ["home"] = "FarmHouse",
        ["greenhouse"] = "Greenhouse", ["invernadero"] = "Greenhouse",
        ["town"] = "Town", ["pueblo"] = "Town", ["pelican town"] = "Town", ["plaza"] = "Town",
        ["saloon"] = "Saloon", ["taberna"] = "Saloon", ["bar"] = "Saloon", ["stardrop saloon"] = "Saloon",
        ["beach"] = "Beach", ["playa"] = "Beach", ["muelle"] = "Beach", ["pier"] = "Beach",
        ["mountain"] = "Mountain", ["montaña"] = "Mountain", ["montana"] = "Mountain", ["lago"] = "Mountain",
        ["forest"] = "Forest", ["bosque"] = "Forest", ["cindersap"] = "Forest",
        ["woods"] = "Woods", ["secret woods"] = "Woods", ["bosque secreto"] = "Woods",
        ["hospital"] = "Hospital", ["clinic"] = "Hospital", ["clinica"] = "Hospital", ["clínica"] = "Hospital",
        ["museum"] = "ArchaeologyHouse", ["museo"] = "ArchaeologyHouse",
        ["library"] = "ArchaeologyHouse", ["biblioteca"] = "ArchaeologyHouse", ["archaeologyhouse"] = "ArchaeologyHouse",
        ["mine"] = "Mine", ["mines"] = "Mine", ["mina"] = "Mine", ["minas"] = "Mine",
        ["manorhouse"] = "ManorHouse", ["manor"] = "ManorHouse", ["alcaldia"] = "ManorHouse", ["alcaldía"] = "ManorHouse",
        ["seedshop"] = "SeedShop", ["pierre"] = "SeedShop", ["tienda"] = "SeedShop", ["general store"] = "SeedShop",
        ["blacksmith"] = "Blacksmith", ["herreria"] = "Blacksmith", ["herrería"] = "Blacksmith", ["clint"] = "Blacksmith",
        ["sciencehouse"] = "ScienceHouse", ["carpenter"] = "ScienceHouse", ["robin"] = "ScienceHouse",
        ["animalshop"] = "AnimalShop", ["ranch"] = "AnimalShop", ["rancho"] = "AnimalShop", ["marnie"] = "AnimalShop",
        ["wizardhouse"] = "WizardHouse", ["torre"] = "WizardHouse", ["tower"] = "WizardHouse", ["mago"] = "WizardHouse",
        ["busstop"] = "BusStop", ["bus"] = "BusStop", ["autobus"] = "BusStop", ["autobús"] = "BusStop",
        ["railroad"] = "Railroad", ["tren"] = "Railroad", ["spa"] = "BathHouse_Entry", ["bathhouse"] = "BathHouse_Entry",
        ["desert"] = "Desert", ["desierto"] = "Desert", ["calico"] = "Desert",
        ["sewer"] = "Sewer", ["alcantarilla"] = "Sewer", ["cloacas"] = "Sewer",
        ["communitycenter"] = "CommunityCenter", ["centro comunitario"] = "CommunityCenter",
        ["movietheater"] = "MovieTheater", ["cine"] = "MovieTheater",
        ["island"] = "IslandSouth", ["ginger island"] = "IslandSouth", ["isla"] = "IslandSouth", ["isla jengibre"] = "IslandSouth",
        ["islandwest"] = "IslandWest", ["granja isla"] = "IslandWest",
        ["guild"] = "AdventureGuild", ["gremio"] = "AdventureGuild", ["adventureguild"] = "AdventureGuild"
    };

    public static GameLocation? Resolve(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;
        string cleaned = rawName.Trim();

        var direct = Game1.getLocationFromName(cleaned);
        if (direct != null) return direct;

        string normalized = cleaned.Replace("_", " ").ToLowerInvariant();
        string compact = cleaned.Replace("_", "").Replace(" ", "").ToLowerInvariant();

        if (CanonicalAliases.TryGetValue(normalized, out string? mapped) ||
            CanonicalAliases.TryGetValue(compact, out mapped))
        {
            var resolved = Game1.getLocationFromName(mapped);
            if (resolved != null) return resolved;
        }

        return Game1.locations.FirstOrDefault(loc =>
            loc != null &&
            (loc.Name.Equals(cleaned, StringComparison.OrdinalIgnoreCase) ||
             loc.NameOrUniqueName.Equals(cleaned, StringComparison.OrdinalIgnoreCase) ||
             (loc.DisplayName != null && loc.DisplayName.Contains(cleaned, StringComparison.OrdinalIgnoreCase))));
    }
}
