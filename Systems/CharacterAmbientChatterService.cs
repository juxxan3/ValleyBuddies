namespace ValleyBuddies.Systems;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using ValleyBuddies.Execution;
using ValleyBuddies.Systems.LivingEvents;

public sealed class CharacterChatterLineData
{
    public string Id { get; set; } = string.Empty;
    public int Emote { get; set; } = 0;
    public string En { get; set; } = string.Empty;
    public string Es { get; set; } = string.Empty;
}

public sealed class ChatterPackFile
{
    public Dictionary<string, Dictionary<string, List<CharacterChatterLineData>>> Characters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, List<CharacterChatterLineData>>> Archetypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Per-character personal ambient commentary engine ("assets/chatter/*.json").
/// Handles both intimate companion stroll reflections (~55-85s) and spontaneous solo town observations
/// when the farmer passes nearby (<= 11 tiles), with anti-repeat memory, Co-op chat echo, and vb_test_chatter.
/// </summary>
public sealed class CharacterAmbientChatterService
{
    public static CharacterAmbientChatterService Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private const float TownChatterWitnessTiles = 11.0f;
    private const float ChatterChatEchoMaxTiles = 12.0f;
    private const ulong GlobalTownChatterCooldownTicks = 35 * 60; // 35s
    private const ulong PerNpcTownCooldownTicks = 150 * 60;       // 150s
    private const int RecentLineHistoryCap = 24;

    private static readonly Color ChatterChatColor = new(220, 235, 215);

    private readonly Dictionary<string, Dictionary<string, List<CharacterChatterLineData>>> _characterBank = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, List<CharacterChatterLineData>>> _archetypeBank = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> _companionNextChatterTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ulong> _npcLastChatterTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentLineIds = new();

    private ulong _lastTownChatterTick = 0;
    private ulong _lastUpdateTick = 0;
    private IMonitor? _monitor;

    public bool IsSveDetected { get; private set; }
    public bool IsRsvDetected { get; private set; }
    public int LoadedCharacterCount => _characterBank.Count;
    public int LoadedArchetypeCount => _archetypeBank.Count;
    public int TotalLinesLoaded { get; private set; }
    public int VanillaCharactersLoaded { get; private set; }
    public int SveCharactersLoaded { get; private set; }
    public int RsvCharactersLoaded { get; private set; }
    public int CustomCharactersLoaded { get; private set; }

    public void Initialize(IModHelper helper, IMonitor monitor)
    {
        _monitor = monitor;
        _characterBank.Clear();
        _archetypeBank.Clear();
        ResetAll();

        TotalLinesLoaded = 0;
        VanillaCharactersLoaded = 0;
        SveCharactersLoaded = 0;
        RsvCharactersLoaded = 0;
        CustomCharactersLoaded = 0;

        if (helper == null) return;

        string chatterDir = Path.Combine(helper.DirectoryPath, "assets", "chatter");
        if (!Directory.Exists(chatterDir))
        {
            monitor?.Log($"[CharacterChatter] Directory not found: {chatterDir}", LogLevel.Warn);
            return;
        }

        foreach (string f in Directory.GetFiles(chatterDir, "vanilla_*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            VanillaCharactersLoaded += LoadPackFile(f, monitor);

        IsSveDetected = helper.ModRegistry.IsLoaded("FlashShifter.StardewValleyExpandedCP")
                     || helper.ModRegistry.IsLoaded("FlashShifter.SVECode");
        if (IsSveDetected)
        {
            foreach (string f in Directory.GetFiles(chatterDir, "sve_*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                SveCharactersLoaded += LoadPackFile(f, monitor);
        }

        IsRsvDetected = helper.ModRegistry.IsLoaded("Rafseazz.RidgesideVillage")
                     || helper.ModRegistry.IsLoaded("Rafseazz.RSVCP");
        if (IsRsvDetected)
        {
            foreach (string f in Directory.GetFiles(chatterDir, "rsv_*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                RsvCharactersLoaded += LoadPackFile(f, monitor);
        }

        try
        {
            foreach (string f in Directory.GetFiles(chatterDir, "custom_*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                CustomCharactersLoaded += LoadPackFile(f, monitor);
        }
        catch { }

        monitor?.Log(
            $"[CharacterChatter] Loaded {LoadedCharacterCount} character banks, {LoadedArchetypeCount} archetypes, {TotalLinesLoaded} personal lines " +
            $"(Vanilla={VanillaCharactersLoaded}, SVE={SveCharactersLoaded} [installed={IsSveDetected}], RSV={RsvCharactersLoaded} [installed={IsRsvDetected}], Custom={CustomCharactersLoaded}).",
            LogLevel.Info);
    }

    private int LoadPackFile(string fullPath, IMonitor? monitor)
    {
        if (!File.Exists(fullPath)) return 0;

        try
        {
            string raw = File.ReadAllText(fullPath);
            var pack = JsonSerializer.Deserialize<ChatterPackFile>(raw, JsonOpts);
            if (pack == null) return 0;

            int newChars = 0;

            if (pack.Characters != null)
            {
                foreach (var (charName, catMap) in pack.Characters)
                {
                    if (string.IsNullOrWhiteSpace(charName) || catMap == null) continue;
                    string key = charName.Trim();
                    if (!_characterBank.TryGetValue(key, out var targetMap))
                    {
                        targetMap = new Dictionary<string, List<CharacterChatterLineData>>(StringComparer.OrdinalIgnoreCase);
                        _characterBank[key] = targetMap;
                        newChars++;
                    }
                    MergeCategoryMap(key, targetMap, catMap);
                }
            }

            if (pack.Archetypes != null)
            {
                foreach (var (archName, catMap) in pack.Archetypes)
                {
                    if (string.IsNullOrWhiteSpace(archName) || catMap == null) continue;
                    string key = archName.Trim();
                    if (!_archetypeBank.TryGetValue(key, out var targetMap))
                    {
                        targetMap = new Dictionary<string, List<CharacterChatterLineData>>(StringComparer.OrdinalIgnoreCase);
                        _archetypeBank[key] = targetMap;
                    }
                    MergeCategoryMap($"arch_{key}", targetMap, catMap);
                }
            }

            return newChars;
        }
        catch (Exception ex)
        {
            monitor?.Log($"[CharacterChatter] Failed to parse '{Path.GetFileName(fullPath)}': {ex.Message}", LogLevel.Warn);
            return 0;
        }
    }

    private void MergeCategoryMap(
        string ownerPrefix,
        Dictionary<string, List<CharacterChatterLineData>> dest,
        Dictionary<string, List<CharacterChatterLineData>> source)
    {
        foreach (var (catName, lines) in source)
        {
            if (string.IsNullOrWhiteSpace(catName) || lines == null) continue;
            string catKey = catName.Trim().ToLowerInvariant();
            if (!dest.TryGetValue(catKey, out var list))
            {
                list = new List<CharacterChatterLineData>();
                dest[catKey] = list;
            }

            foreach (var line in lines)
            {
                if (line == null || (string.IsNullOrWhiteSpace(line.En) && string.IsNullOrWhiteSpace(line.Es)))
                    continue;
                if (string.IsNullOrWhiteSpace(line.Id))
                    line.Id = $"{ownerPrefix}_{catKey}_{list.Count + 1}";
                list.Add(line);
                TotalLinesLoaded++;
            }
        }
    }

    public void ResetDaily()
    {
        _companionNextChatterTick.Clear();
        _npcLastChatterTick.Clear();
        _lastTownChatterTick = 0;
    }

    public void ResetAll()
    {
        ResetDaily();
        _recentLineIds.Clear();
    }

    public void Update(ulong gameTick)
    {
        _lastUpdateTick = gameTick;
        if (!Context.IsWorldReady) return;
        if (Game1.IsMultiplayer && !Context.IsMainPlayer) return;
        if (ModEntry.Config?.EnableMod == false || ModEntry.Config?.EnableCharacterAmbientChatter == false) return;
        if (Utility.isFestivalDay() || Game1.isFestival() || Game1.weddingToday || Game1.eventUp) return;

        // Check every 1 second (60 ticks)
        if (gameTick % 60 != 0) return;

        var farmers = GetOnlineFarmersSafe();
        if (farmers.Count == 0) return;

        // 1. Companion stroll chatter (~55-85s natural reflections toward accompanying player)
        if (TryProcessCompanionChatter(gameTick, farmers))
            return;

        // 2. Solo town ambient chatter when player passes close to villagers (<= 11 tiles)
        TryProcessTownSoloChatter(gameTick, farmers);
    }

    private bool TryProcessCompanionChatter(ulong gameTick, List<Farmer> farmers)
    {
        foreach (var farmer in farmers)
        {
            var loc = farmer.currentLocation;
            if (loc == null || loc.currentEvent != null) continue;

            var follower = NpcActionExecutor.Instance.GetActiveFollowerForPlayer(farmer.UniqueMultiplayerID);
            if (follower == null || follower.currentLocation != loc || follower.isSleeping.Value || follower.IsInvisible)
                continue;

            if (AmbientTownBanterService.Instance.IsNpcInActiveBanter(follower.Name))
                continue;
            if (LivingTownVignetteService.Instance.IsNpcInActiveVignette(follower.Name))
                continue;
            if (LivingEventManager.Instance.IsNpcWalkingInApproach(follower.Name))
                continue;
            if (NpcOutingInvitationService.Instance.HasActiveOutingForNpc(follower.Name))
                continue;
            if (NpcActionExecutor.Instance.IsNpcBusyForAmbientBanter(follower.Name, out _))
                continue;

            if (!_companionNextChatterTick.TryGetValue(follower.Name, out ulong nextTick))
            {
                // Initial stagger 30-55s after recruiting
                _companionNextChatterTick[follower.Name] = gameTick + (ulong)Game1.random.Next(1800, 3300);
                continue;
            }

            if (gameTick < nextTick) continue;
            if (Vector2.Distance(follower.Tile, farmer.Tile) > 8.0f) continue;

            // Next cadence 55-85 seconds (3300-5100 ticks)
            _companionNextChatterTick[follower.Name] = gameTick + (ulong)Game1.random.Next(3300, 5100);
            _npcLastChatterTick[follower.Name] = gameTick;

            string cat = ResolveBestCategory(follower, loc, isCompanionMode: true);
            if (EmitChatterLine(follower, loc, farmer, cat, faceTarget: farmer.Tile, isCompanionMode: true))
                return true;
        }
        return false;
    }

    private void TryProcessTownSoloChatter(ulong gameTick, List<Farmer> farmers)
    {
        if (_lastTownChatterTick > 0 && gameTick < _lastTownChatterTick + GlobalTownChatterCooldownTicks)
            return;

        var checkedLocs = new HashSet<GameLocation>();
        var candidates = new List<(NPC Npc, Farmer Witness, GameLocation Loc)>();

        foreach (var farmer in farmers)
        {
            var loc = farmer.currentLocation;
            if (loc == null || loc.currentEvent != null) continue;
            if (!checkedLocs.Add(loc)) continue;

            foreach (var ch in loc.characters)
            {
                if (ch == null || !ch.IsVillager || ch.IsInvisible || ch.isSleeping.Value)
                    continue;
                if (AmbientTownBanterService.Instance.IsNpcInActiveBanter(ch.Name))
                    continue;
                if (LivingTownVignetteService.Instance.IsNpcInActiveVignette(ch.Name))
                    continue;
                if (LivingEventManager.Instance.IsNpcWalkingInApproach(ch.Name))
                    continue;
                if (NpcOutingInvitationService.Instance.HasActiveOutingForNpc(ch.Name))
                    continue;
                if (NpcActionExecutor.Instance.IsNpcBusyForAmbientBanter(ch.Name, out bool isManaged) || isManaged)
                    continue;
                if (_npcLastChatterTick.TryGetValue(ch.Name, out ulong lastSpoke) && gameTick < lastSpoke + PerNpcTownCooldownTicks)
                    continue;

                Farmer? witness = farmers.FirstOrDefault(f =>
                    f.currentLocation == loc && Vector2.Distance(f.Tile, ch.Tile) <= TownChatterWitnessTiles);
                if (witness == null)
                    continue;

                candidates.Add((ch, witness, loc));
            }
        }

        if (candidates.Count == 0) return;

        var picked = candidates[Game1.random.Next(candidates.Count)];
        string cat = ResolveBestCategory(picked.Npc, picked.Loc, isCompanionMode: false);

        if (EmitChatterLine(picked.Npc, picked.Loc, picked.Witness, cat, faceTarget: null, isCompanionMode: false))
        {
            _lastTownChatterTick = gameTick;
            _npcLastChatterTick[picked.Npc.Name] = gameTick;
        }
    }

    private string ResolveBestCategory(NPC npc, GameLocation loc, bool isCompanionMode)
    {
        var choices = new List<(string Cat, int Weight)>();
        string locName = loc.NameOrUniqueName ?? string.Empty;

        if (loc.IsSnowingHere()) choices.Add(("snow", 5));
        else if (loc is not StardewValley.Locations.Desert && (loc.IsRainingHere() || loc.IsLightningHere())) choices.Add(("rain", 5));

        if (Game1.timeOfDay >= 1800) choices.Add(("evening", 4));

        if (loc is Farm || loc.IsGreenhouse || locName.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase))
            choices.Add(("farm", 5));
        else if (locName.Equals("Saloon", StringComparison.OrdinalIgnoreCase) || locName.Contains("Saloon", StringComparison.OrdinalIgnoreCase))
            choices.Add(("saloon", 5));
        else if (loc is Beach || locName.Contains("Beach", StringComparison.OrdinalIgnoreCase) || locName.Contains("IslandSouth", StringComparison.OrdinalIgnoreCase))
            choices.Add(("beach", 5));

        choices.Add((isCompanionMode ? "companion_stroll" : "town_solo", 6));

        var bank = ResolveBankForNpc(npc);
        if (bank != null)
        {
            var valid = choices.Where(c => bank.ContainsKey(c.Cat) && bank[c.Cat].Count > 0).ToList();
            if (valid.Count > 0)
            {
                int total = valid.Sum(v => v.Weight);
                int roll = Game1.random.Next(total);
                int run = 0;
                foreach (var v in valid)
                {
                    run += v.Weight;
                    if (roll < run) return v.Cat;
                }
            }
            var anyNonEmpty = bank.Keys.Where(k => bank[k].Count > 0).ToList();
            if (anyNonEmpty.Count > 0)
                return anyNonEmpty[Game1.random.Next(anyNonEmpty.Count)];
        }

        return isCompanionMode ? "companion_stroll" : "town_solo";
    }

    private Dictionary<string, List<CharacterChatterLineData>>? ResolveBankForNpc(NPC npc)
    {
        if (_characterBank.TryGetValue(npc.Name, out var exact))
            return exact;

        string archetype = ResolveArchetypeKey(npc);
        if (_archetypeBank.TryGetValue(archetype, out var archMap))
            return archMap;

        return _archetypeBank.Values.FirstOrDefault();
    }

    private static string ResolveArchetypeKey(NPC npc)
    {
        if (npc.Age == NPC.child || CavePersonalityRegistry.ResolveProfile(npc).IsChildProtected)
            return "child_playful";

        return npc.Manners switch
        {
            1 => "warm",
            2 => "reserved",
            _ => npc.SocialAnxiety == 0 ? "cheerful" : "practical"
        };
    }

    private bool EmitChatterLine(
        NPC speaker,
        GameLocation loc,
        Farmer witness,
        string category,
        Vector2? faceTarget,
        bool isCompanionMode)
    {
        var bank = ResolveBankForNpc(speaker);
        if (bank == null) return false;

        List<CharacterChatterLineData>? list = null;
        if (!bank.TryGetValue(category, out list) || list.Count == 0)
        {
            list = bank.Values.FirstOrDefault(v => v.Count > 0);
        }
        if (list == null || list.Count == 0) return false;

        var fresh = list.Where(l => !_recentLineIds.Contains(l.Id)).ToList();
        var pool = fresh.Count > 0 ? fresh : list;
        var chosen = pool[Game1.random.Next(pool.Count)];

        _recentLineIds.Enqueue(chosen.Id);
        while (_recentLineIds.Count > RecentLineHistoryCap)
            _recentLineIds.Dequeue();

        if (faceTarget.HasValue && !speaker.isMoving())
            speaker.faceGeneralDirection(faceTarget.Value * 64f);
        if (chosen.Emote > 0)
            speaker.doEmote(chosen.Emote);

        string enRendered = FormatText(chosen.En, speaker, witness, isEs: false);
        string esRendered = FormatText(chosen.Es, speaker, witness, isEs: true);
        string localText = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.es
            ? (!string.IsNullOrWhiteSpace(esRendered) ? esRendered : enRendered)
            : (!string.IsNullOrWhiteSpace(enRendered) ? enRendered : esRendered);

        if (string.IsNullOrWhiteSpace(localText)) return false;

        int durationMs = Math.Clamp(2300 + localText.Length * 45, 2900, 5600);
        speaker.showTextAboveHead(localText, duration: durationMs);

        string stamp = Game1.getTimeOfDayString(Game1.timeOfDay);
        string modeTag = isCompanionMode ? "Chatter-Buddy" : "Chatter-Solo";
        AmbientTownBanterService.Instance.RecordExternalTranscriptLine(
            $"[{stamp} @ {loc.NameOrUniqueName} ({modeTag}/{category})] {speaker.displayName}: {localText}");

        var onlineFarmers = GetOnlineFarmersSafe();
        foreach (var farmer in onlineFarmers)
        {
            if (farmer?.currentLocation == null) continue;
            if (!string.Equals(farmer.currentLocation.NameOrUniqueName, loc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Vector2.Distance(farmer.Tile, speaker.Tile) > ChatterChatEchoMaxTiles)
                continue;

            if (farmer.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
            {
                if (ModEntry.Config?.EchoNpcConversationsToChat != false)
                {
                    Game1.chatBox?.addMessage($"{speaker.displayName}: {localText}", ChatterChatColor);
                }
            }
            else if (Game1.IsMultiplayer)
            {
                ModEntry.MultiplayerSync?.SendBanterChatEcho(
                    farmer.UniqueMultiplayerID,
                    speaker,
                    localText,
                    enRendered,
                    esRendered,
                    isCompanionChat: false,
                    isPersonalChatter: true,
                    maxDistanceTiles: ChatterChatEchoMaxTiles);
            }
        }

        return true;
    }

    private static string FormatText(string? raw, NPC speaker, Farmer? viewer, bool isEs)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        string farmerName = viewer?.displayName ?? viewer?.Name ?? (isEs ? "granjero" : "Farmer");
        string rawFarm = viewer?.farmName?.Value ?? Game1.player?.farmName?.Value ?? string.Empty;
        string farmLabel = !string.IsNullOrWhiteSpace(rawFarm)
            ? (isEs ? $"la granja {rawFarm}" : $"{rawFarm} Farm")
            : (isEs ? "la granja" : "the farm");

        return raw
            .Replace("{farmer}", farmerName)
            .Replace("{farm}", farmLabel)
            .Replace("{npc}", speaker.displayName ?? speaker.Name);
    }

    public bool TryTriggerTestChatter(string? npcName, string? category, out string report)
    {
        if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
        {
            report = "You must be in a loaded world to test character ambient chatter.";
            return false;
        }
        if (Game1.IsMultiplayer && !Context.IsMainPlayer)
        {
            report = "Character ambient chatter runs on the multiplayer host.";
            return false;
        }

        var loc = Game1.player.currentLocation;
        NPC? target = !string.IsNullOrWhiteSpace(npcName) ? Game1.getCharacterFromName(npcName) : null;
        if (target == null)
        {
            target = NpcActionExecutor.Instance.GetActiveFollowerForPlayer(Game1.player.UniqueMultiplayerID)
                ?? loc.characters
                    .Where(c => c != null && c.IsVillager && !c.IsInvisible)
                    .OrderBy(c => Vector2.Distance(c.Tile, Game1.player.Tile))
                    .FirstOrDefault();
        }

        if (target == null)
        {
            report = $"No eligible villager found near you on '{loc.NameOrUniqueName}'. Pass an NPC name: vb_test_chatter Penny";
            return false;
        }

        var targetLoc = target.currentLocation ?? loc;

        // FIX 3a: Evaluate IsNpcBusyForAmbientBanter separately so isComp is populated before use.
        NpcActionExecutor.Instance.IsNpcBusyForAmbientBanter(target.Name, out bool isComp);
        string cat = !string.IsNullOrWhiteSpace(category)
            ? category.Trim().ToLowerInvariant()
            : ResolveBestCategory(target, targetLoc, isCompanionMode: isComp);

        // Pass isComp so the transcript tag (Chatter-Buddy vs Chatter-Solo) is accurate.
        if (!EmitChatterLine(target, targetLoc, Game1.player, cat, faceTarget: Game1.player.Tile, isCompanionMode: isComp))
        {
            report = $"No chatter line available for {target.Name} (category={cat}).";
            return false;
        }

        _ = _lastUpdateTick;
        report = $"Triggered personal chatter for {target.Name} [{cat}] at {targetLoc.NameOrUniqueName}.";
        return true;
    }

    private static List<Farmer> GetOnlineFarmersSafe()
    {
        if (Game1.IsMultiplayer)
            return Game1.getOnlineFarmers().Where(f => f != null).ToList();
        return Game1.player != null ? new List<Farmer> { Game1.player } : new List<Farmer>();
    }
}
