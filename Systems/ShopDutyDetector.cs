using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.Characters;
using StardewValley.GameData.Shops;
using StardewValley.Locations;
using ValleyBuddies.Config;

namespace ValleyBuddies.Systems;

public static class ShopDutyDetector
{
    private static IMonitor? _monitor;
    private static Dictionary<string, ShopData>? _cachedShops;
    private static readonly HashSet<string> _knownShopOwners = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize(IModHelper helper, IMonitor monitor)
    {
        _monitor = monitor;
        LoadShops(helper);
    }

    public static void LoadShops(IModHelper helper)
    {
        try
        {
            _cachedShops = helper.GameContent.Load<Dictionary<string, ShopData>>("Data/Shops");
            _knownShopOwners.Clear();

            if (_cachedShops != null)
            {
                foreach (var shopKvp in _cachedShops)
                {
                    var shop = shopKvp.Value;
                    if (shop?.Owners != null)
                    {
                        foreach (var owner in shop.Owners)
                        {
                            if (!string.IsNullOrWhiteSpace(owner?.Name))
                            {
                                _knownShopOwners.Add(owner.Name.Trim());
                            }
                        }
                    }
                }
            }
            _monitor?.Log($"[ShopDutyDetector] Data/Shops cargado: {_cachedShops?.Count ?? 0} tiendas y {_knownShopOwners.Count} propietarios registrados.", LogLevel.Trace);
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[ShopDutyDetector] Error al cargar Data/Shops: {ex.Message}", LogLevel.Warn);
            _cachedShops = null;
        }
    }

    public static void OnAssetsInvalidated(AssetsInvalidatedEventArgs e, IModHelper helper)
    {
        if (e.NamesWithoutLocale.Any(name => name.IsEquivalentTo("Data/Shops")))
        {
            _monitor?.Log("[ShopDutyDetector] Data/Shops invalidado. Recargando caché de tiendas...", LogLevel.Trace);
            LoadShops(helper);
        }
    }

    public static void ClearCache()
    {
        _cachedShops = null;
        _knownShopOwners.Clear();
        _monitor?.Log("[ShopDutyDetector] Caché de tiendas limpiada.", LogLevel.Trace);
    }

    public static bool IsShopkeeperOnDuty(NPC npc, GameLocation? location)
    {
        if (npc == null) return false;
        var loc = location ?? npc.currentLocation;
        int time = Game1.timeOfDay;
        string name = npc.Name;
        string locName = loc?.Name ?? string.Empty;

        // Specific vanilla shopkeeper schedule & location checks
        switch (name.ToLowerInvariant())
        {
            case "pierre":
                return locName.Equals("SeedShop", StringComparison.OrdinalIgnoreCase) && time >= 900 && time < 1700;
            case "robin":
                return locName.Equals("ScienceHouse", StringComparison.OrdinalIgnoreCase) && time >= 900 && time < 1700;
            case "clint":
                return locName.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase) && time >= 900 && time < 1600;
            case "marnie":
                return locName.Equals("AnimalShop", StringComparison.OrdinalIgnoreCase) && time >= 900 && time < 1600;
            case "willy":
                return locName.Equals("FishShop", StringComparison.OrdinalIgnoreCase) && time >= 900 && time < 1700;
            case "gus":
                return locName.Equals("Saloon", StringComparison.OrdinalIgnoreCase) && time >= 1200 && time <= 2400;
            case "harvey":
                return locName.Equals("Hospital", StringComparison.OrdinalIgnoreCase) && time >= 900 && time < 1500;
            case "sandy":
                return locName.Contains("Sandy", StringComparison.OrdinalIgnoreCase) || locName.Contains("Oasis", StringComparison.OrdinalIgnoreCase);
            case "krobus":
                return locName.Equals("Sewer", StringComparison.OrdinalIgnoreCase);
            case "dwarf":
                return locName.Contains("Mine", StringComparison.OrdinalIgnoreCase) || locName.Contains("Volcano", StringComparison.OrdinalIgnoreCase);
        }

        // Generic / Modded shopkeepers via Data/Shops cache: only on duty indoors (not outdoors/FarmHouse/Cabin)
        if (loc == null || loc.IsOutdoors || loc is FarmHouse || locName.Contains("Cabin", StringComparison.OrdinalIgnoreCase))
            return false;

        if (_cachedShops != null && _knownShopOwners.Contains(name))
        {
            foreach (var shop in _cachedShops.Values)
            {
                if (shop?.Owners == null) continue;
                foreach (var owner in shop.Owners)
                {
                    if (owner == null || !string.Equals(owner.Name, name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool condOk = string.IsNullOrWhiteSpace(owner.Condition)
                        || GameStateQuery.CheckConditions(owner.Condition, loc, player: Game1.player);
                    if (!condOk)
                        continue;

                    if (!string.IsNullOrWhiteSpace(owner.ClosedMessage))
                        break;

                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsDirectClickShopkeeperOnDuty(NPC npc, GameLocation? location)
    {
        if (npc == null) return false;
        var loc = location ?? npc.currentLocation;
        string locName = loc?.Name ?? string.Empty;
        string name = npc.Name;

        if (string.Equals(name, "krobus", StringComparison.OrdinalIgnoreCase))
        {
            if (locName.Equals("Sewer", StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (string.Equals(name, "dwarf", StringComparison.OrdinalIgnoreCase))
        {
            if (locName.Contains("Mine", StringComparison.OrdinalIgnoreCase) || locName.Contains("Volcano", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool IsNpcEligibleForOfflineRecruitment(NPC? npc, ActionsConfig config)
    {
        if (npc == null) return false;

        // Discard custom creatures / companions inheriting from NPC (e.g. CustomCompanions)
        if (npc.GetType() != typeof(NPC))
            return false;

        // Villager must be social
        if (!npc.IsVillager || !npc.CanSocialize)
            return false;

        // Safe CharacterData inspection
        var data = npc.GetData();
        if (data != null)
        {
            if (data.Age == NpcAge.Child && !config.AllowRecruitingChildren)
                return false;
        }

        // Excluded NPC names check (comma-separated, OrdinalIgnoreCase, Trimmed)
        if (!string.IsNullOrWhiteSpace(config.ExcludedNpcNames))
        {
            string[] excluded = config.ExcludedNpcNames.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string ex in excluded)
            {
                if (string.Equals(ex.Trim(), npc.Name, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }

        // Hard global context blocks
        if (Game1.eventUp || Game1.CurrentEvent != null)
            return false;

        if (Game1.isFestival())
            return false;

        if (npc.currentLocation is MovieTheater)
            return false;

        if (npc.isSleeping.Value)
            return false;

        return true;
    }

    public static void CheckCoexistenceWarnings(IModHelper helper, IMonitor monitor)
    {
        try
        {
            var loadedMods = helper.ModRegistry.GetAll().ToList();
            var conflictingModIds = new[]
            {
                "aedenthorn.FollowMe",
                "PurrplingCat.NPCAdventures"
            };

            foreach (var modId in conflictingModIds)
            {
                if (loadedMods.Any(m => string.Equals(m.Manifest.UniqueID, modId, StringComparison.OrdinalIgnoreCase)))
                {
                    monitor.Log(
                        $"[Valley Buddies] Se detectó el mod '{modId}' instalado. Se recomienda no reclutar al mismo aldeano simultáneamente en ambos mods para evitar conflictos de comportamiento.",
                        LogLevel.Warn);
                }
            }
        }
        catch (Exception ex)
        {
            monitor.Log($"Error checking coexistence warnings: {ex.Message}", LogLevel.Trace);
        }
    }
}
