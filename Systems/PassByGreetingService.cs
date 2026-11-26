using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Monsters;
using ValleyBuddies.Execution;
using ValleyBuddies.Systems.LivingEvents;

namespace ValleyBuddies.Systems;

public sealed class GreetingLineData
{
    public string Id { get; set; } = string.Empty;
    public int Emote { get; set; } = 32;
    public string En { get; set; } = string.Empty;
    public string Es { get; set; } = string.Empty;
}

public sealed class GreetingPackFile
{
    public Dictionary<string, Dictionary<string, List<GreetingLineData>>> Characters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, List<GreetingLineData>>> Archetypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Servicio "Valle Vivo": saludos contextuales breves al pasar cerca (<= 4.5 casillas)
/// de cualquier aldeano para todos los granjeros conectados en Co-op, sin interrumpir
/// rutinas ni acompañantes activos. Corre exclusivamente en el Host con el reloj unificado gameTick.
/// </summary>
public sealed class PassByGreetingService
{
    public static PassByGreetingService Instance { get; } = new();

    private const ulong ScanIntervalTicks = 30UL;
    private const ulong FarmerGlobalCooldownTicks = 900UL;
    private const float MaxGreetingDistanceTiles = 4.5f;
    private const float CoopChatEchoMaxDistanceTiles = 10.0f;
    private const int BubbleDurationMs = 3800;
    private const int RecentLineBufferCapacity = 8;

    private static readonly Color GreetingChatColor = new(215, 240, 220);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private IModHelper? _helper;
    private IMonitor? _monitor;

    // characterName -> categoryKey -> lines
    private readonly Dictionary<string, Dictionary<string, List<GreetingLineData>>> _characterBanks = new(StringComparer.OrdinalIgnoreCase);
    // archetype -> categoryKey -> lines
    private readonly Dictionary<string, Dictionary<string, List<GreetingLineData>>> _archetypeBanks = new(StringComparer.OrdinalIgnoreCase);

    // Estado diario y anti-spam por jugador (solo en RAM del Host)
    private readonly HashSet<string> _greetedFarmerNpcToday = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, ulong> _farmerGlobalCooldownUntilTick = new();
    private readonly Dictionary<string, Queue<string>> _recentLinesByPair = new(StringComparer.OrdinalIgnoreCase);

    public int LoadedCharacterCount => _characterBanks.Count;
    public int LoadedArchetypeCount => _archetypeBanks.Count;
    public int LoadedTotalLines { get; private set; }

    private PassByGreetingService() { }

    public void Initialize(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
        LoadAllGreetingBanks();
    }

    public void ResetDaily()
    {
        _greetedFarmerNpcToday.Clear();
        _farmerGlobalCooldownUntilTick.Clear();
    }

    public void ResetAll()
    {
        ResetDaily();
        _recentLinesByPair.Clear();
    }

    private void LoadAllGreetingBanks()
    {
        _characterBanks.Clear();
        _archetypeBanks.Clear();
        LoadedTotalLines = 0;

        if (_helper == null) return;

        string dir = Path.Combine(_helper.DirectoryPath, "assets", "living_valley");
        if (!Directory.Exists(dir))
        {
            _monitor?.Log($"[PassByGreeting] Directory not found: {dir}", LogLevel.Trace);
            return;
        }

        bool hasSve = _helper.ModRegistry.IsLoaded("FlashShifter.StardewValleyExpandedCP")
            || _helper.ModRegistry.IsLoaded("FlashShifter.SVECode");
        bool hasRsv = _helper.ModRegistry.IsLoaded("Rafseazz.RidgesideVillage")
            || _helper.ModRegistry.IsLoaded("Rafseazz.RSVCP");

        var filesToLoad = new List<string>
        {
            "greetings_vanilla.json",
            "greetings_archetypes.json"
        };
        if (hasSve) filesToLoad.Add("greetings_sve.json");
        if (hasRsv) filesToLoad.Add("greetings_rsv.json");

        try
        {
            foreach (string extra in Directory.GetFiles(dir, "greetings_custom_*.json", SearchOption.TopDirectoryOnly))
            {
                string fileName = Path.GetFileName(extra);
                if (!filesToLoad.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                    filesToLoad.Add(fileName);
            }
        }
        catch { }

        foreach (string fileName in filesToLoad)
        {
            string fullPath = Path.Combine(dir, fileName);
            if (!File.Exists(fullPath)) continue;

            try
            {
                string raw = File.ReadAllText(fullPath);
                var pack = JsonSerializer.Deserialize<GreetingPackFile>(raw, JsonOpts);
                if (pack == null) continue;

                MergeBuckets(_characterBanks, pack.Characters);
                MergeBuckets(_archetypeBanks, pack.Archetypes);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[PassByGreeting] Failed to parse '{fileName}': {ex.Message}", LogLevel.Warn);
            }
        }

        _monitor?.Log(
            $"[PassByGreeting] Loaded {LoadedTotalLines} greetings across {_characterBanks.Count} villagers and {_archetypeBanks.Count} archetypes (SVE={hasSve}, RSV={hasRsv}).",
            LogLevel.Debug);
    }

    private void MergeBuckets(
        Dictionary<string, Dictionary<string, List<GreetingLineData>>> target,
        Dictionary<string, Dictionary<string, List<GreetingLineData>>>? source)
    {
        if (source == null) return;

        foreach (var (ownerKey, categories) in source)
        {
            if (string.IsNullOrWhiteSpace(ownerKey) || categories == null) continue;

            if (!target.TryGetValue(ownerKey, out var destCats))
            {
                destCats = new Dictionary<string, List<GreetingLineData>>(StringComparer.OrdinalIgnoreCase);
                target[ownerKey] = destCats;
            }

            foreach (var (catKey, lines) in categories)
            {
                if (string.IsNullOrWhiteSpace(catKey) || lines == null) continue;

                if (!destCats.TryGetValue(catKey, out var destLines))
                {
                    destLines = new List<GreetingLineData>();
                    destCats[catKey] = destLines;
                }

                foreach (var line in lines)
                {
                    if (line == null || (string.IsNullOrWhiteSpace(line.En) && string.IsNullOrWhiteSpace(line.Es)))
                        continue;

                    if (string.IsNullOrWhiteSpace(line.Id))
                        line.Id = $"{ownerKey}_{catKey}_{destLines.Count + 1}".ToLowerInvariant();

                    if (string.IsNullOrWhiteSpace(line.En)) line.En = line.Es;
                    if (string.IsNullOrWhiteSpace(line.Es)) line.Es = line.En;
                    if (line.Emote != 20 && line.Emote != 32) line.Emote = 32;

                    destLines.Add(line);
                    LoadedTotalLines++;
                }
            }
        }
    }

    public void Update(ulong gameTick)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer) return;
        if (ModEntry.Config?.EnablePassByGreetings == false) return;
        if (gameTick % ScanIntervalTicks != 0) return;

        if (Utility.isFestivalDay() || Game1.isFestival() || Game1.weddingToday || Game1.eventUp)
            return;

        if (Game1.CurrentEvent != null && !Game1.CurrentEvent.isWedding)
            return;

        foreach (Farmer farmer in Game1.getOnlineFarmers())
        {
            if (farmer == null || farmer.currentLocation == null) continue;
            if (!IsFarmerEligible(farmer, gameTick)) continue;

            NPC? candidate = FindNearestEligibleNpc(farmer);
            if (candidate == null) continue;

            if (TryTriggerGreetingForPair(farmer, candidate, gameTick, isTestOverride: false, out _))
            {
                _farmerGlobalCooldownUntilTick[farmer.UniqueMultiplayerID] = gameTick + FarmerGlobalCooldownTicks;
                MarkGreetedToday(farmer.UniqueMultiplayerID, candidate.Name);
            }
        }
    }

    public bool TryTriggerTestGreeting(string? npcName, out string report)
    {
        report = string.Empty;
        if (!Context.IsWorldReady || !Context.IsMainPlayer || Game1.player?.currentLocation == null)
        {
            report = "You must be loaded into a world as the host to run vb_test_greeting.";
            return false;
        }

        Farmer farmer = Game1.player;
        GameLocation loc = farmer.currentLocation;
        NPC? target = null;

        if (!string.IsNullOrWhiteSpace(npcName))
        {
            target = loc.characters.FirstOrDefault(n =>
                n != null && n.IsVillager && string.Equals(n.Name, npcName.Trim(), StringComparison.OrdinalIgnoreCase));

            target ??= Game1.getCharacterFromName(npcName.Trim());
        }
        else
        {
            target = loc.characters
                .Where(n => n != null && IsEligibleNpcObject(n))
                .OrderBy(n => Vector2.DistanceSquared(farmer.Tile, n.Tile))
                .FirstOrDefault();
        }

        if (target == null)
        {
            report = string.IsNullOrWhiteSpace(npcName)
                ? $"No villagers found on '{loc.NameOrUniqueName}'. Pass an NPC name: vb_test_greeting Abigail"
                : $"Villager '{npcName}' not found on current map or world.";
            return false;
        }

        if (TryTriggerGreetingForPair(farmer, target, ModEntry.CurrentTick, isTestOverride: true, out string detail))
        {
            report = $"Pass-by greeting fired for {target.Name}: {detail}";
            return true;
        }

        report = $"No greeting line available for {target.Name}.";
        return false;
    }

    private bool IsFarmerEligible(Farmer farmer, ulong gameTick)
    {
        if (_farmerGlobalCooldownUntilTick.TryGetValue(farmer.UniqueMultiplayerID, out ulong untilTick)
            && gameTick < untilTick)
        {
            return false;
        }

        GameLocation loc = farmer.currentLocation;
        if (loc == null) return false;
        if (loc.currentEvent != null) return false;
        if (loc is MovieTheater or MineShaft or VolcanoDungeon) return false;

        if (farmer == Game1.player)
        {
            if (Game1.activeClickableMenu != null || Game1.dialogueUp)
                return false;
        }

        return true;
    }

    private NPC? FindNearestEligibleNpc(Farmer farmer)
    {
        GameLocation loc = farmer.currentLocation;
        if (loc?.characters == null) return null;

        NPC? best = null;
        float bestDistSq = MaxGreetingDistanceTiles * MaxGreetingDistanceTiles;

        foreach (NPC npc in loc.characters)
        {
            if (npc == null || !IsEligibleNpcObject(npc)) continue;
            if (HasGreetedToday(farmer.UniqueMultiplayerID, npc.Name)) continue;

            float distSq = Vector2.DistanceSquared(farmer.Tile, npc.Tile);
            if (distSq > bestDistSq) continue;

            if (!IsNpcAvailableForGreeting(npc)) continue;

            best = npc;
            bestDistSq = distSq;
        }

        return best;
    }

    private static bool IsEligibleNpcObject(NPC npc)
    {
        if (!npc.IsVillager) return false;
        if (npc is Pet or Horse or Junimo or Monster) return false;
        if (npc.isSleeping.Value || npc.IsInvisible) return false;
        return true;
    }

    private static bool IsNpcAvailableForGreeting(NPC npc)
    {
        if (string.IsNullOrWhiteSpace(npc.Name)) return false;
        if (AmbientTownBanterService.Instance.IsNpcInActiveBanter(npc.Name)) return false;
        if (LivingTownVignetteService.Instance.IsNpcInActiveVignette(npc.Name)) return false;
        if (LivingEventManager.Instance.IsNpcWalkingInApproach(npc.Name)) return false;
        if (NpcOutingInvitationService.Instance.HasActiveOutingForNpc(npc.Name)) return false;

        if (NpcActionExecutor.Instance != null)
        {
            if (NpcActionExecutor.Instance.IsNpcBusyForAmbientBanter(npc.Name, out bool isManaged) || isManaged)
                return false;

            if (NpcActionExecutor.Instance.IsSpouseInHomeRoutine(npc.Name))
                return false;
        }

        return true;
    }

    private bool TryTriggerGreetingForPair(
        Farmer farmer,
        NPC npc,
        ulong gameTick,
        bool isTestOverride,
        out string detailReport)
    {
        detailReport = string.Empty;
        GameLocation loc = npc.currentLocation ?? farmer.currentLocation;
        if (loc == null) return false;

        var (line, sourceTag) = SelectGreetingLine(npc, farmer, loc, isTestOverride);
        if (line == null) return false;

        string pairKey = BuildPairKey(farmer.UniqueMultiplayerID, npc.Name);
        RecordRecentLine(pairKey, line.Id);

        string farmNameEn = string.IsNullOrWhiteSpace(farmer.farmName.Value) ? "the farm" : farmer.farmName.Value;
        string farmNameEs = string.IsNullOrWhiteSpace(farmer.farmName.Value) ? "la granja" : farmer.farmName.Value;

        string enRendered = FormatTokens(line.En, farmer.Name, farmNameEn);
        string esRendered = FormatTokens(line.Es, farmer.Name, farmNameEs);

        bool hostIsSpanish = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.es;
        string localText = hostIsSpanish ? esRendered : enRendered;

        // Solo gira suavemente hacia el granjero si está quieto, sin ruta ni atendiendo tienda
        if (!npc.isMoving() && npc.controller == null && !ShopDutyDetector.IsShopkeeperOnDuty(npc, loc))
        {
            npc.faceGeneralDirection(farmer.Position);
        }

        int emote = ResolveGreetingEmote(line, npc, farmer);
        npc.doEmote(emote);
        npc.showTextAboveHead(localText, duration: BubbleDurationMs);

        AmbientTownBanterService.Instance.RecordExternalTranscriptLine(
            $"[PassByGreeting:{sourceTag}] {npc.Name} -> {farmer.Name}: {localText}");

        EchoGreetingToNearbyFarmers(loc, npc, localText, enRendered, esRendered);

        detailReport = $"[{sourceTag}] (emote={emote}) \"{localText}\"";
        return true;
    }

    private static int ResolveGreetingEmote(GreetingLineData line, NPC npc, Farmer farmer)
    {
        if (npc.isBirthday()) return 20;
        if (line.Emote == 20 || line.Emote == 32) return line.Emote;
        return farmer.getFriendshipHeartLevelForNPC(npc.Name) >= 7 ? 20 : 32;
    }

    private (GreetingLineData? Line, string SourceTag) SelectGreetingLine(
        NPC npc,
        Farmer farmer,
        GameLocation loc,
        bool isTestOverride)
    {
        string pairKey = BuildPairKey(farmer.UniqueMultiplayerID, npc.Name);
        _recentLinesByPair.TryGetValue(pairKey, out var recent);

        // Prioridad 1: Cumpleaños / GreenRain / Storm / Snow / Rain
        List<string> priority1Keys = BuildSpecialWeatherKeys(npc, loc);
        // Prioridad 2: Relación específica
        string relKey = ResolveRelationshipKey(npc, farmer);
        // Prioridad 3: Estación + Franja horaria
        List<string> priority3Keys = BuildSeasonTimeKeys(loc);

        bool hasCharBank = _characterBanks.TryGetValue(npc.Name, out var charBank);
        string archetype = ResolveArchetypeKey(npc);
        _archetypeBanks.TryGetValue(archetype, out var archBank);

        // Cumpleaños siempre es estricto si hay línea específica o en arquetipo
        if (npc.isBirthday())
        {
            if (hasCharBank && TryPickFromKeys(charBank!, new[] { "birthday" }, recent, allowRecent: isTestOverride, out var bLine, out _))
                return (bLine, $"{npc.Name}:birthday");
            if (archBank != null && TryPickFromKeys(archBank, new[] { "birthday" }, recent, allowRecent: true, out var archBLine, out _))
                return (archBLine, $"arch:{archetype}:birthday");
        }

        // Prioridad 1 (Clima especial / lluvia): busca línea no reciente en personaje -> arquetipo
        if (priority1Keys.Count > 0)
        {
            if (hasCharBank && TryPickFromKeys(charBank!, priority1Keys, recent, allowRecent: false, out var wLine, out string wCat))
                return (wLine, $"{npc.Name}:{wCat}");
            if (archBank != null && TryPickFromKeys(archBank, priority1Keys, recent, allowRecent: false, out var awLine, out string awCat))
                return (awLine, $"arch:{archetype}:{awCat}");
        }

        // Prioridad 2 (Relación)
        if (hasCharBank && TryPickFromKeys(charBank!, new[] { relKey }, recent, allowRecent: false, out var rLine, out string rCat))
            return (rLine, $"{npc.Name}:{rCat}");

        // Prioridad 3 (Estación + Franja horaria)
        if (hasCharBank && TryPickFromKeys(charBank!, priority3Keys, recent, allowRecent: false, out var stLine, out string stCat))
            return (stLine, $"{npc.Name}:{stCat}");

        // Prioridad 4 (Arquetipo: relación -> estación/franja -> general)
        if (archBank != null)
        {
            var archFallbackKeys = new List<string>();
            archFallbackKeys.AddRange(priority1Keys);
            archFallbackKeys.Add(relKey);
            archFallbackKeys.AddRange(priority3Keys);
            archFallbackKeys.Add("general");

            if (TryPickFromKeys(archBank, archFallbackKeys, recent, allowRecent: false, out var aLine, out string aCat))
                return (aLine, $"arch:{archetype}:{aCat}");
        }

        // Fallback permitiendo recientes si ya rotó todo el repertorio del personaje
        if (hasCharBank)
        {
            var allCharKeys = new List<string>();
            allCharKeys.AddRange(priority1Keys);
            allCharKeys.Add(relKey);
            allCharKeys.AddRange(priority3Keys);
            allCharKeys.Add("general");

            if (TryPickFromKeys(charBank!, allCharKeys, recent, allowRecent: true, out var anyCharLine, out string anyCharCat))
                return (anyCharLine, $"{npc.Name}:{anyCharCat}:repeat");
        }

        if (archBank != null)
        {
            var allArchKeys = new List<string> { relKey };
            allArchKeys.AddRange(priority3Keys);
            allArchKeys.Add("general");

            if (TryPickFromKeys(archBank, allArchKeys, recent, allowRecent: true, out var anyArchLine, out string anyArchCat))
                return (anyArchLine, $"arch:{archetype}:{anyArchCat}:repeat");
        }

        return (null, "none");
    }

    private static List<string> BuildSpecialWeatherKeys(NPC npc, GameLocation loc)
    {
        var keys = new List<string>(5);
        if (npc.isBirthday()) keys.Add("birthday");

        if (loc.IsOutdoors && loc is not Desert)
        {
            if (Game1.isGreenRain)
                keys.Add("green_rain");
            else if (loc.IsLightningHere())
                keys.Add("storm");
            else if (loc.IsSnowingHere())
                keys.Add("snow");
            else if (loc.IsRainingHere())
                keys.Add("rain");
        }

        return keys;
    }

    private static string ResolveRelationshipKey(NPC npc, Farmer farmer)
    {
        if (npc.Age == NPC.child)
            return "child";

        if (farmer.friendshipData.TryGetValue(npc.Name, out var fData))
        {
            if (fData.IsMarried() || fData.IsRoommate())
                return "spouse";
            if (fData.IsDating())
                return "dating";
        }

        int hearts = farmer.getFriendshipHeartLevelForNPC(npc.Name);
        if (hearts >= 7) return "close_friend";
        if (hearts >= 4) return "friend";
        return "acquaintance";
    }

    private static List<string> BuildSeasonTimeKeys(GameLocation loc)
    {
        string season = (loc.GetSeason().ToString() ?? Game1.currentSeason ?? "spring").ToLowerInvariant();
        string timeBand = Game1.timeOfDay < 1200
            ? "morning"
            : (Game1.timeOfDay < 1700 ? "afternoon" : (Game1.timeOfDay < 2000 ? "evening" : "night"));

        return new List<string>(4)
        {
            $"{season}_{timeBand}",
            season,
            timeBand,
            "general"
        };
    }

    private static string ResolveArchetypeKey(NPC npc)
    {
        if (npc.Age == NPC.child)
            return "child";

        if (string.Equals(npc.Name, "Elliott", StringComparison.OrdinalIgnoreCase)
            || string.Equals(npc.Name, "Wizard", StringComparison.OrdinalIgnoreCase)
            || string.Equals(npc.Name, "Leah", StringComparison.OrdinalIgnoreCase)
            || string.Equals(npc.Name, "June", StringComparison.OrdinalIgnoreCase)
            || string.Equals(npc.Name, "Lance", StringComparison.OrdinalIgnoreCase))
        {
            return "poetic";
        }

        if (npc.Manners == 1)
            return npc.SocialAnxiety == 0 ? "warm" : "poetic";

        if (npc.Manners == 2)
            return "reserved";

        return npc.SocialAnxiety == 0 ? "cheerful" : "practical";
    }

    private static bool TryPickFromKeys(
        Dictionary<string, List<GreetingLineData>> bank,
        IEnumerable<string> orderedKeys,
        Queue<string>? recent,
        bool allowRecent,
        out GreetingLineData? picked,
        out string matchedCategory)
    {
        picked = null;
        matchedCategory = string.Empty;

        foreach (string key in orderedKeys)
        {
            if (string.IsNullOrWhiteSpace(key) || !bank.TryGetValue(key, out var list) || list == null || list.Count == 0)
                continue;

            var candidates = allowRecent || recent == null
                ? list
                : list.Where(x => !recent.Contains(x.Id)).ToList();

            if (candidates.Count == 0) continue;

            picked = candidates[Game1.random.Next(candidates.Count)];
            matchedCategory = key;
            return true;
        }

        return false;
    }

    private void RecordRecentLine(string pairKey, string lineId)
    {
        if (string.IsNullOrWhiteSpace(lineId)) return;

        if (!_recentLinesByPair.TryGetValue(pairKey, out var queue))
        {
            queue = new Queue<string>(RecentLineBufferCapacity + 1);
            _recentLinesByPair[pairKey] = queue;
        }

        if (queue.Contains(lineId)) return;

        queue.Enqueue(lineId);
        while (queue.Count > RecentLineBufferCapacity)
            queue.Dequeue();
    }

    private void EchoGreetingToNearbyFarmers(
        GameLocation loc,
        NPC speaker,
        string localText,
        string enRendered,
        string esRendered)
    {
        if (speaker == null || loc == null) return;

        float maxSq = CoopChatEchoMaxDistanceTiles * CoopChatEchoMaxDistanceTiles;

        foreach (Farmer peer in Game1.getOnlineFarmers())
        {
            if (peer?.currentLocation == null) continue;
            if (!string.Equals(peer.currentLocation.NameOrUniqueName, loc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (Vector2.DistanceSquared(peer.Tile, speaker.Tile) > maxSq)
                continue;

            if (peer.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
            {
                if (ModEntry.Config?.EchoNpcConversationsToChat != false)
                    Game1.chatBox?.addMessage($"{speaker.displayName}: {localText}", GreetingChatColor);
            }
            else if (Context.IsMultiplayer)
            {
                ModEntry.MultiplayerSync?.SendBanterChatEcho(
                    peer.UniqueMultiplayerID,
                    speaker,
                    localText,
                    enRendered,
                    esRendered,
                    isCompanionChat: false,
                    isPersonalChatter: true,
                    maxDistanceTiles: CoopChatEchoMaxDistanceTiles);
            }
        }
    }

    private bool HasGreetedToday(long farmerId, string npcName)
        => _greetedFarmerNpcToday.Contains(BuildPairKey(farmerId, npcName));

    private void MarkGreetedToday(long farmerId, string npcName)
        => _greetedFarmerNpcToday.Add(BuildPairKey(farmerId, npcName));

    private static string BuildPairKey(long farmerId, string npcName)
        => $"{farmerId}|{npcName}";

    private static string FormatTokens(string template, string farmerName, string farmName)
        => (template ?? string.Empty)
            .Replace("{farmer}", farmerName, StringComparison.OrdinalIgnoreCase)
            .Replace("{farm}", farmName, StringComparison.OrdinalIgnoreCase);
}
