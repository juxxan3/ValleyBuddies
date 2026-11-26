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

public sealed class BanterTurnData
{
    public string Speaker { get; set; } = "A";
    public int Emote { get; set; } = 0;
    public string En { get; set; } = string.Empty;
    public string Es { get; set; } = string.Empty;
}

public sealed class BanterConversationData
{
    public string Id { get; set; } = string.Empty;
    public bool ChildSafe { get; set; } = true;
    public string[]? Locations { get; set; }
    public string[]? Seasons { get; set; }
    public string[]? Weather { get; set; }
    public List<BanterTurnData> Turns { get; set; } = new();
}

public sealed class BanterPairData
{
    public string NpcA { get; set; } = string.Empty;
    public string NpcB { get; set; } = string.Empty;
    public List<BanterConversationData> Conversations { get; set; } = new();
}

public sealed class BanterPackFile
{
    public List<BanterPairData> Pairs { get; set; } = new();
    public Dictionary<string, List<BanterConversationData>> Pools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Living Town ("Pueblo Vivo") ambient conversation engine.
/// Pairs nearby town villagers and recruited companions organically using modular JSON packs,
/// shows multi-turn speech bubbles via tick-based progression (pausing cleanly in single-player menus),
/// echoes nearby banter to the chat box with Co-op sync, and records a 50-line diagnostic transcript.
/// </summary>
public sealed class AmbientTownBanterService
{
    public static AmbientTownBanterService Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private sealed class PairBucket
    {
        public string NpcA { get; init; } = string.Empty;
        public string NpcB { get; init; } = string.Empty;
        public List<BanterConversationData> Conversations { get; } = new();
    }

    private sealed class ActiveBanterSession
    {
        public string ConversationId { get; init; } = string.Empty;
        public string LocationName { get; init; } = string.Empty;
        public string NpcAName { get; init; } = string.Empty;
        public string NpcBName { get; init; } = string.Empty;
        public bool IsCompanionChat { get; init; }
        public string WitnessFarmerName { get; init; } = string.Empty;
        public List<BanterTurnData> Turns { get; init; } = new();
        public int CurrentTurnIndex { get; set; }
        public ulong NextTurnTick { get; set; }
    }

    private const int RecentHistoryCapacity = 25;
    private const int TranscriptCapacity = 50;
    private const ulong PerNpcCooldownTicks = 90 * 60; // 90 seconds at 60 FPS
    private const float TownPairMaxDistanceTiles = 5.5f;
    private const float CompanionPairMaxDistanceTiles = 6.5f;
    private const float FarmerWitnessMaxDistanceTiles = 16.0f;
    private const float ChatEchoMaxDistanceTiles = 15.0f;
    private const float BreakAwayDistanceTiles = 9.0f;

    private readonly Dictionary<string, PairBucket> _pairBuckets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<BanterConversationData>> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ActiveBanterSession> _activeSessions = new();
    private readonly Dictionary<string, ulong> _npcLastSpokeTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentConversationIds = new();
    private readonly List<string> _transcriptLog = new();

    private IMonitor? _monitor;
    private ulong _lastBanterStartedTick = 0;
    private ulong _lastUpdateTick = 0;

    public bool IsSveDetected { get; private set; }
    public bool IsRsvDetected { get; private set; }
    public int LoadedPairsCount => _pairBuckets.Count;
    public int LoadedPoolCount => _pools.Count;
    public int LoadedConversationsCount { get; private set; }
    public int LoadedTurnsCount { get; private set; }
    public int VanillaLoadedPairs { get; private set; }
    public int SveLoadedPairs { get; private set; }
    public int RsvLoadedPairs { get; private set; }
    public int CustomLoadedPairs { get; private set; }

    public void Initialize(IModHelper helper, IMonitor monitor)
    {
        _monitor = monitor;
        _pairBuckets.Clear();
        _pools.Clear();
        ResetAll();

        LoadedConversationsCount = 0;
        LoadedTurnsCount = 0;
        VanillaLoadedPairs = 0;
        SveLoadedPairs = 0;
        RsvLoadedPairs = 0;
        CustomLoadedPairs = 0;

        if (helper == null) return;

        string banterDir = Path.Combine(helper.DirectoryPath, "assets", "banter");
        if (!Directory.Exists(banterDir))
        {
            monitor?.Log($"[LivingTownBanter] Directory not found: {banterDir}", LogLevel.Warn);
            return;
        }

        // 1. Always load vanilla_*.json packs
        foreach (string vanillaFile in Directory.GetFiles(banterDir, "vanilla_*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            VanillaLoadedPairs += LoadPackFile(vanillaFile, monitor);
        }

        // 2. SVE on-demand
        IsSveDetected = helper.ModRegistry.IsLoaded("FlashShifter.StardewValleyExpandedCP")
                     || helper.ModRegistry.IsLoaded("FlashShifter.SVECode");
        if (IsSveDetected)
        {
            foreach (string sveFile in Directory.GetFiles(banterDir, "sve_*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                SveLoadedPairs += LoadPackFile(sveFile, monitor);
            }
        }

        // 3. Ridgeside Village on-demand
        IsRsvDetected = helper.ModRegistry.IsLoaded("Rafseazz.RidgesideVillage")
                     || helper.ModRegistry.IsLoaded("Rafseazz.RSVCP");
        if (IsRsvDetected)
        {
            foreach (string rsvFile in Directory.GetFiles(banterDir, "rsv_*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                RsvLoadedPairs += LoadPackFile(rsvFile, monitor);
            }
        }

        // 4. Custom packs (custom_*.json)
        try
        {
            foreach (string customFile in Directory.GetFiles(banterDir, "custom_*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                CustomLoadedPairs += LoadPackFile(customFile, monitor);
            }
        }
        catch { }

        monitor?.Log(
            $"[LivingTownBanter] Loaded {LoadedPairsCount} named pairs, {LoadedPoolCount} pools, {LoadedConversationsCount} conversations, {LoadedTurnsCount} turns " +
            $"(Vanilla={VanillaLoadedPairs}, SVE={SveLoadedPairs} [installed={IsSveDetected}], RSV={RsvLoadedPairs} [installed={IsRsvDetected}], Custom={CustomLoadedPairs}).",
            LogLevel.Info);
    }

    private int LoadPackFile(string fullPath, IMonitor? monitor)
    {
        if (!File.Exists(fullPath)) return 0;

        try
        {
            string raw = File.ReadAllText(fullPath);
            var pack = JsonSerializer.Deserialize<BanterPackFile>(raw, JsonOpts);
            if (pack == null) return 0;

            int loadedPairs = 0;

            if (pack.Pairs != null)
            {
                foreach (var p in pack.Pairs)
                {
                    if (p == null || string.IsNullOrWhiteSpace(p.NpcA) || string.IsNullOrWhiteSpace(p.NpcB) || p.Conversations == null)
                        continue;

                    string pairKey = MakePairKey(p.NpcA, p.NpcB);
                    if (!_pairBuckets.TryGetValue(pairKey, out var bucket))
                    {
                        bucket = new PairBucket
                        {
                            NpcA = p.NpcA.Trim(),
                            NpcB = p.NpcB.Trim()
                        };
                        _pairBuckets[pairKey] = bucket;
                        loadedPairs++;
                    }

                    foreach (var convo in p.Conversations)
                    {
                        if (convo == null || convo.Turns == null || convo.Turns.Count == 0) continue;
                        if (string.IsNullOrWhiteSpace(convo.Id))
                            convo.Id = $"{pairKey}_{bucket.Conversations.Count + 1}";

                        // Remap speaker tags if pack appended entries under swapped order
                        bool swapped = !string.Equals(bucket.NpcA, p.NpcA.Trim(), StringComparison.OrdinalIgnoreCase);
                        if (swapped)
                        {
                            foreach (var t in convo.Turns)
                            {
                                t.Speaker = string.Equals(t.Speaker, "B", StringComparison.OrdinalIgnoreCase) ? "A" : "B";
                            }
                        }

                        bucket.Conversations.Add(convo);
                        LoadedConversationsCount++;
                        LoadedTurnsCount += convo.Turns.Count;
                    }
                }
            }

            if (pack.Pools != null)
            {
                foreach (var (poolName, convos) in pack.Pools)
                {
                    if (string.IsNullOrWhiteSpace(poolName) || convos == null) continue;
                    string cleanPool = poolName.Trim().ToLowerInvariant();
                    if (!_pools.TryGetValue(cleanPool, out var list))
                    {
                        list = new List<BanterConversationData>();
                        _pools[cleanPool] = list;
                    }

                    foreach (var convo in convos)
                    {
                        if (convo == null || convo.Turns == null || convo.Turns.Count == 0) continue;
                        if (string.IsNullOrWhiteSpace(convo.Id))
                            convo.Id = $"pool_{cleanPool}_{list.Count + 1}";

                        list.Add(convo);
                        LoadedConversationsCount++;
                        LoadedTurnsCount += convo.Turns.Count;
                    }
                }
            }

            return loadedPairs;
        }
        catch (Exception ex)
        {
            monitor?.Log($"[LivingTownBanter] Failed to parse '{Path.GetFileName(fullPath)}': {ex.Message}", LogLevel.Warn);
            return 0;
        }
    }

    public void ResetDaily()
    {
        _activeSessions.Clear();
        _npcLastSpokeTick.Clear();
        _lastBanterStartedTick = 0;
    }

    public void ResetAll()
    {
        ResetDaily();
        _recentConversationIds.Clear();
        _transcriptLog.Clear();
    }

    public IReadOnlyList<string> GetRecentTranscript() => _transcriptLog.AsReadOnly();

    public bool IsNpcInActiveBanter(string? npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return false;
        return _activeSessions.Any(s =>
            string.Equals(s.NpcAName, npcName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.NpcBName, npcName, StringComparison.OrdinalIgnoreCase));
    }

    public void RecordExternalTranscriptLine(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return;
        _transcriptLog.Add(entry);
        while (_transcriptLog.Count > TranscriptCapacity)
            _transcriptLog.RemoveAt(0);
    }

    public void Update(ulong gameTick)
    {
        _lastUpdateTick = gameTick;
        if (!Context.IsWorldReady) return;
        if (Game1.IsMultiplayer && !Context.IsMainPlayer) return;
        if (ModEntry.Config?.EnableMod == false) return;

        if (Utility.isFestivalDay() || Game1.isFestival() || Game1.weddingToday || Game1.eventUp)
        {
            if (_activeSessions.Count > 0)
                _activeSessions.Clear();
            return;
        }

        // 1. Advance active multi-turn banter sessions (tick-driven so singleplayer pause pauses them automatically)
        ProcessActiveSessions(gameTick);

        // 2. Scan nearby villagers & companions once per second (60 ticks)
        if (gameTick % 60 != 0) return;
        if (_activeSessions.Count >= 2) return;

        bool allowTown = ModEntry.Config?.EnableAmbientTownConversations != false;
        bool allowCompanions = ModEntry.Config?.EnableOfflineGroupBanter != false;
        if (!allowTown && !allowCompanions) return;

        int cooldownSec = Math.Clamp(ModEntry.Config?.AmbientTownBanterCooldownSeconds ?? 25, 10, 90);
        if (_lastBanterStartedTick > 0 && gameTick < _lastBanterStartedTick + (ulong)(cooldownSec * 60))
            return;

        TryStartAmbientBanterScan(gameTick, allowTown, allowCompanions);
    }

    private void ProcessActiveSessions(ulong gameTick)
    {
        for (int i = _activeSessions.Count - 1; i >= 0; i--)
        {
            var session = _activeSessions[i];
            if (gameTick < session.NextTurnTick) continue;

            if (session.CurrentTurnIndex >= session.Turns.Count)
            {
                _activeSessions.RemoveAt(i);
                continue;
            }

            var npcA = Game1.getCharacterFromName(session.NpcAName);
            var npcB = Game1.getCharacterFromName(session.NpcBName);

            if (npcA?.currentLocation == null || npcB?.currentLocation == null
                || !string.Equals(npcA.currentLocation.NameOrUniqueName, session.LocationName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(npcB.currentLocation.NameOrUniqueName, session.LocationName, StringComparison.OrdinalIgnoreCase)
                || npcA.isSleeping.Value || npcB.isSleeping.Value
                || Vector2.Distance(npcA.Tile, npcB.Tile) > BreakAwayDistanceTiles)
            {
                _activeSessions.RemoveAt(i);
                continue;
            }

            var turn = session.Turns[session.CurrentTurnIndex];
            bool speakerIsB = string.Equals(turn.Speaker, "B", StringComparison.OrdinalIgnoreCase);
            NPC speaker = speakerIsB ? npcB : npcA;
            NPC listener = speakerIsB ? npcA : npcB;

            EmitTurn(session, speaker, listener, npcA, npcB, turn);
            session.CurrentTurnIndex++;

            if (session.CurrentTurnIndex >= session.Turns.Count)
            {
                _activeSessions.RemoveAt(i);
            }
            else
            {
                string currentRendered = RenderLocalizedLine(turn, Game1.player, npcA, npcB, session.WitnessFarmerName);
                // Smart spacing between turns: ~2.8s to ~4.2s at 60 FPS
                int delayTicks = Math.Clamp(155 + currentRendered.Length * 2, 168, 252);
                session.NextTurnTick = gameTick + (ulong)delayTicks;
            }
        }
    }

    private void TryStartAmbientBanterScan(ulong gameTick, bool allowTown, bool allowCompanions)
    {
        var farmers = GetOnlineFarmersSafe();
        if (farmers.Count == 0) return;

        var checkedLocations = new HashSet<GameLocation>();
        foreach (var farmer in farmers)
        {
            var loc = farmer.currentLocation;
            if (loc == null || loc.currentEvent != null) continue;
            if (!checkedLocations.Add(loc)) continue;

            // Keep at most 1 active banter per location
            if (_activeSessions.Any(s => string.Equals(s.LocationName, loc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase)))
                continue;

            var candidates = CollectEligibleNpcsInLocation(loc, gameTick);
            if (candidates.Count < 2) continue;

            // Score and gather valid pairs in this map
            var validPairs = new List<(NPC NpcA, NPC NpcB, bool IsCompanionChat, bool HasNamedPair, string WitnessName, int PriorityScore)>();

            for (int i = 0; i < candidates.Count - 1; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    var candA = candidates[i];
                    var candB = candidates[j];

                    bool isCompanionChat = candA.IsManagedCompanion && candB.IsManagedCompanion;
                    if (isCompanionChat && !allowCompanions) continue;
                    if (!isCompanionChat && !allowTown) continue;

                    float maxPairDist = isCompanionChat ? CompanionPairMaxDistanceTiles : TownPairMaxDistanceTiles;
                    if (Vector2.Distance(candA.Npc.Tile, candB.Npc.Tile) > maxPairDist)
                        continue;

                    Vector2 midpoint = (candA.Npc.Tile + candB.Npc.Tile) * 0.5f;
                    Farmer? witness = farmers.FirstOrDefault(f =>
                        f.currentLocation == loc && Vector2.Distance(f.Tile, midpoint) <= FarmerWitnessMaxDistanceTiles);
                    if (witness == null)
                        continue;

                    bool hasNamedPair = _pairBuckets.ContainsKey(MakePairKey(candA.Npc.Name, candB.Npc.Name));
                    int score = (isCompanionChat ? 20 : 10) + (hasNamedPair ? 15 : 0);
                    validPairs.Add((candA.Npc, candB.Npc, isCompanionChat, hasNamedPair, witness.displayName ?? witness.Name ?? "Farmer", score));
                }
            }

            if (validPairs.Count == 0) continue;

            int topScore = validPairs.Max(v => v.PriorityScore);
            var topTier = validPairs.Where(v => v.PriorityScore >= topScore - 5).ToList();
            var chosen = topTier[Game1.random.Next(topTier.Count)];

            if (StartBanterInternal(chosen.NpcA, chosen.NpcB, loc, chosen.IsCompanionChat, chosen.WitnessName, gameTick))
            {
                return;
            }
        }
    }

    private List<(NPC Npc, bool IsManagedCompanion)> CollectEligibleNpcsInLocation(GameLocation loc, ulong gameTick)
    {
        var list = new List<(NPC, bool)>();
        if (loc.characters == null) return list;

        foreach (var ch in loc.characters)
        {
            if (ch == null || !ch.IsVillager || ch.IsInvisible || ch.isSleeping.Value)
                continue;

            if (_activeSessions.Any(s =>
                string.Equals(s.NpcAName, ch.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.NpcBName, ch.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (_npcLastSpokeTick.TryGetValue(ch.Name, out ulong lastSpoke) && gameTick < lastSpoke + PerNpcCooldownTicks)
                continue;

            if (LivingTownVignetteService.Instance.IsNpcInActiveVignette(ch.Name))
                continue;

            if (LivingEventManager.Instance.IsNpcWalkingInApproach(ch.Name))
                continue;

            if (NpcOutingInvitationService.Instance.HasActiveOutingForNpc(ch.Name))
                continue;

            if (NpcActionExecutor.Instance.IsNpcBusyForAmbientBanter(ch.Name, out bool isManagedCompanion))
                continue;

            list.Add((ch, isManagedCompanion));
        }

        return list;
    }

    private bool StartBanterInternal(
        NPC npc1,
        NPC npc2,
        GameLocation loc,
        bool isCompanionChat,
        string witnessName,
        ulong gameTick)
    {
        bool involvesChild = IsChildNpc(npc1) || IsChildNpc(npc2);
        string pairKey = MakePairKey(npc1.Name, npc2.Name);

        NPC orderedA = npc1;
        NPC orderedB = npc2;
        BanterConversationData? selectedConvo = null;

        // 1. Named pair match first (preserving exact A / B speaker binding y priorizando contexto específico!)
        if (_pairBuckets.TryGetValue(pairKey, out var bucket))
        {
            var eligibleNamed = bucket.Conversations
                .Where(c => (!involvesChild || c.ChildSafe) && MatchesBanterContext(c, loc))
                .ToList();

            var contextualNamed = eligibleNamed.Where(HasBanterContextFilter).ToList();
            if (contextualNamed.Count > 0)
            {
                selectedConvo = PickNonRecentConversation(contextualNamed);
            }
            selectedConvo ??= PickNonRecentConversation(eligibleNamed);

            if (selectedConvo != null)
            {
                if (string.Equals(npc2.Name, bucket.NpcA, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(npc1.Name, bucket.NpcB, StringComparison.OrdinalIgnoreCase))
                {
                    orderedA = npc2;
                    orderedB = npc1;
                }
            }
        }

        // 2. Fallback to rich contextual pools
        if (selectedConvo == null)
        {
            selectedConvo = PickContextualPoolConversation(orderedA, orderedB, loc, involvesChild);
        }

        if (selectedConvo == null || selectedConvo.Turns.Count == 0)
            return false;

        RecordRecentConversation(selectedConvo.Id);
        _lastBanterStartedTick = gameTick;
        _npcLastSpokeTick[orderedA.Name] = gameTick;
        _npcLastSpokeTick[orderedB.Name] = gameTick;

        var session = new ActiveBanterSession
        {
            ConversationId = selectedConvo.Id,
            LocationName = loc.NameOrUniqueName ?? string.Empty,
            NpcAName = orderedA.Name,
            NpcBName = orderedB.Name,
            IsCompanionChat = isCompanionChat,
            WitnessFarmerName = witnessName,
            Turns = selectedConvo.Turns,
            CurrentTurnIndex = 0,
            NextTurnTick = gameTick
        };

        _activeSessions.Add(session);
        // Immediately fire turn 0 right now on the start tick
        ProcessActiveSessions(gameTick);
        return true;
    }

    private BanterConversationData? PickContextualPoolConversation(NPC npcA, NPC npcB, GameLocation loc, bool involvesChild)
    {
        var candidatePools = new List<(string PoolName, int Weight)>();
        string locName = loc.NameOrUniqueName ?? string.Empty;

        if (loc is MineShaft || loc is VolcanoDungeon || locName.Contains("UndergroundMine", StringComparison.OrdinalIgnoreCase))
            candidatePools.Add(("caves", 6));
        if (!involvesChild && (locName.Equals("Saloon", StringComparison.OrdinalIgnoreCase) || locName.Contains("Saloon", StringComparison.OrdinalIgnoreCase)))
            candidatePools.Add(("saloon", 6));
        if (locName.Contains("JojaMart", StringComparison.OrdinalIgnoreCase))
            candidatePools.Add(("jojamart", 6));
        if (locName.Contains("Hospital", StringComparison.OrdinalIgnoreCase))
            candidatePools.Add(("hospital", 6));
        if (locName.Equals("Blacksmith", StringComparison.OrdinalIgnoreCase))
            candidatePools.Add(("blacksmith", 6));
        if (locName.Equals("ScienceHouse", StringComparison.OrdinalIgnoreCase))
            candidatePools.Add(("sciencehouse", 6));
        if (locName.Equals("ArchaeologyHouse", StringComparison.OrdinalIgnoreCase))
            candidatePools.Add(("museum", 5));
        if (locName.Equals("SeedShop", StringComparison.OrdinalIgnoreCase))
            candidatePools.Add(("seedshop", 5));
        if (loc is Beach || locName.Contains("Beach", StringComparison.OrdinalIgnoreCase) || locName.Contains("IslandSouth", StringComparison.OrdinalIgnoreCase))
            candidatePools.Add(("beach", 5));
        if (loc is Forest || locName.Equals("Woods", StringComparison.OrdinalIgnoreCase) || locName.Equals("Forest", StringComparison.OrdinalIgnoreCase))
            candidatePools.Add(("forest", 5));
        if (locName.Equals("Mountain", StringComparison.OrdinalIgnoreCase))
            candidatePools.Add(("mountain", 5));
        if (loc is Farm || loc.IsGreenhouse || locName.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase))
            candidatePools.Add(("farm", 5));
        if (locName.Equals("Town", StringComparison.OrdinalIgnoreCase) || locName.Contains("RidgesideVillage", StringComparison.OrdinalIgnoreCase))
            candidatePools.Add(("town", 5));

        if (loc.IsSnowingHere())
            candidatePools.Add(("snow", 4));
        else if (loc is not Desert && loc.IsLightningHere())
        {
            candidatePools.Add(("storm", 5));
            candidatePools.Add(("rain", 3));
        }
        else if (loc is not Desert && (loc.IsRainingHere() || (loc.IsOutdoors && Game1.isGreenRain)))
            candidatePools.Add(("rain", 4));

        if (Game1.timeOfDay >= 1800)
            candidatePools.Add(("evening", 3));

        string seasonKey = (Game1.currentSeason ?? "spring").ToLowerInvariant();
        if (_pools.ContainsKey(seasonKey))
            candidatePools.Add((seasonKey, 3));

        if (Game1.player != null && (Game1.player.getFriendshipHeartLevelForNPC(npcA.Name) >= 2 || Game1.player.getFriendshipHeartLevelForNPC(npcB.Name) >= 2))
            candidatePools.Add(("farmer_gossip", 2));

        candidatePools.Add(("general", 2));

        var available = candidatePools
            .Where(c => _pools.TryGetValue(c.PoolName, out var list)
                && list.Any(x => (!involvesChild || x.ChildSafe) && MatchesBanterContext(x, loc)))
            .ToList();

        if (available.Count == 0) return null;

        int totalWeight = available.Sum(a => a.Weight);
        int roll = Game1.random.Next(totalWeight);
        int running = 0;
        string chosenPool = available[0].PoolName;
        foreach (var item in available)
        {
            running += item.Weight;
            if (roll < running)
            {
                chosenPool = item.PoolName;
                break;
            }
        }

        var poolList = _pools[chosenPool]
            .Where(c => (!involvesChild || c.ChildSafe) && MatchesBanterContext(c, loc))
            .ToList();

        return PickNonRecentConversation(poolList);
    }

    private static bool HasBanterContextFilter(BanterConversationData convo)
        => (convo.Locations != null && convo.Locations.Length > 0)
            || (convo.Seasons != null && convo.Seasons.Length > 0)
            || (convo.Weather != null && convo.Weather.Length > 0);

    private static bool MatchesBanterContext(BanterConversationData convo, GameLocation loc)
    {
        if (convo.Locations != null && convo.Locations.Length > 0)
        {
            string locName = loc.Name ?? string.Empty;
            string locUnique = loc.NameOrUniqueName ?? string.Empty;
            bool locMatched = convo.Locations.Any(req =>
                !string.IsNullOrWhiteSpace(req)
                && (string.Equals(locName, req, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(locUnique, req, StringComparison.OrdinalIgnoreCase)
                    || locName.Contains(req, StringComparison.OrdinalIgnoreCase)));
            if (!locMatched) return false;
        }

        if (convo.Seasons != null && convo.Seasons.Length > 0)
        {
            string curSeason = (loc.GetSeason().ToString() ?? Game1.currentSeason ?? "spring").ToLowerInvariant();
            if (!convo.Seasons.Any(s => string.Equals(s, curSeason, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (convo.Weather != null && convo.Weather.Length > 0)
        {
            bool greenRain = loc.IsOutdoors && loc is not Desert && Game1.isGreenRain;
            bool storm = loc.IsOutdoors && loc is not Desert && loc.IsLightningHere();
            bool snow = loc.IsOutdoors && loc.IsSnowingHere();
            bool rain = loc.IsOutdoors && loc is not Desert && loc.IsRainingHere();
            bool sun = !greenRain && !storm && !snow && !rain;

            bool weatherMatched = convo.Weather.Any(w => w.ToLowerInvariant() switch
            {
                "green_rain" => greenRain,
                "storm" => storm,
                "snow" => snow,
                "rain" => rain || storm,
                "sun" or "clear" => sun,
                _ => false
            });
            if (!weatherMatched) return false;
        }

        return true;
    }

    private BanterConversationData? PickNonRecentConversation(List<BanterConversationData> candidates)
    {
        if (candidates == null || candidates.Count == 0) return null;
        var fresh = candidates.Where(c => !_recentConversationIds.Contains(c.Id)).ToList();
        var pool = fresh.Count > 0 ? fresh : candidates;
        return pool[Game1.random.Next(pool.Count)];
    }

    private void RecordRecentConversation(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        _recentConversationIds.Enqueue(id);
        while (_recentConversationIds.Count > RecentHistoryCapacity)
            _recentConversationIds.Dequeue();
    }

    private void EmitTurn(ActiveBanterSession session, NPC speaker, NPC listener, NPC npcA, NPC npcB, BanterTurnData turn)
    {
        if (speaker?.currentLocation == null) return;

        if (!speaker.isMoving() && speaker.controller == null && !ShopDutyDetector.IsShopkeeperOnDuty(speaker, speaker.currentLocation))
        {
            speaker.faceGeneralDirection(listener.Position);
        }
        if (turn.Emote > 0)
            speaker.doEmote(turn.Emote);

        string enRendered = RenderSpecificLanguage(turn.En, speaker, Game1.player, npcA, npcB, session.WitnessFarmerName, isEs: false);
        string esRendered = RenderSpecificLanguage(turn.Es, speaker, Game1.player, npcA, npcB, session.WitnessFarmerName, isEs: true);
        string localText = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.es
            ? (!string.IsNullOrWhiteSpace(esRendered) ? esRendered : enRendered)
            : (!string.IsNullOrWhiteSpace(enRendered) ? enRendered : esRendered);

        if (string.IsNullOrWhiteSpace(localText)) return;

        int durationMs = Math.Clamp(2200 + localText.Length * 45, 2800, 5600);
        speaker.showTextAboveHead(localText, duration: durationMs);

        // Append transcript line (keep max 50 lines)
        string stamp = Game1.getTimeOfDayString(Game1.timeOfDay);
        string logEntry = $"[{stamp} @ {session.LocationName}] {speaker.displayName}: {localText}";
        _transcriptLog.Add(logEntry);
        while (_transcriptLog.Count > TranscriptCapacity)
            _transcriptLog.RemoveAt(0);

        // Chat echo for nearby farmers (<= 15 tiles)
        var onlineFarmers = GetOnlineFarmersSafe();
        foreach (var farmer in onlineFarmers)
        {
            if (farmer?.currentLocation == null) continue;
            if (!string.Equals(farmer.currentLocation.NameOrUniqueName, session.LocationName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (Vector2.Distance(farmer.Tile, speaker.Tile) > ChatEchoMaxDistanceTiles)
                continue;

            if (farmer.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
            {
                if (ModEntry.Config?.EchoNpcConversationsToChat != false)
                {
                    Color color = session.IsCompanionChat
                        ? new Color(195, 235, 255)
                        : new Color(235, 225, 195);
                    Game1.chatBox?.addMessage($"{speaker.displayName}: {localText}", color);
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
                    session.IsCompanionChat);
            }
        }
    }

    private static string RenderLocalizedLine(BanterTurnData turn, Farmer? viewer, NPC npcA, NPC npcB, string fallbackFarmerName)
    {
        bool isEs = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.es;
        string raw = isEs ? (!string.IsNullOrWhiteSpace(turn.Es) ? turn.Es : turn.En) : (!string.IsNullOrWhiteSpace(turn.En) ? turn.En : turn.Es);
        return RenderSpecificLanguage(raw, npcA, viewer, npcA, npcB, fallbackFarmerName, isEs);
    }

    private static string RenderSpecificLanguage(string? template, NPC speaker, Farmer? viewer, NPC npcA, NPC npcB, string fallbackFarmerName, bool isEs)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;

        string farmerName = !string.IsNullOrWhiteSpace(fallbackFarmerName)
            ? fallbackFarmerName
            : (viewer?.displayName ?? viewer?.Name ?? (isEs ? "granjero" : "Farmer"));

        string rawFarm = viewer?.farmName?.Value ?? Game1.player?.farmName?.Value ?? string.Empty;
        string farmLabel = !string.IsNullOrWhiteSpace(rawFarm)
            ? (isEs ? $"la granja {rawFarm}" : $"{rawFarm} Farm")
            : (isEs ? "la granja" : "the farm");

        return template
            .Replace("{npcA}", npcA.displayName ?? npcA.Name)
            .Replace("{npcB}", npcB.displayName ?? npcB.Name)
            .Replace("{speaker}", speaker.displayName ?? speaker.Name)
            .Replace("{farmer}", farmerName)
            .Replace("{farm}", farmLabel);
    }

    public bool TryTriggerTestBanter(string? npcAName, string? npcBName, out string report)
    {
        if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
        {
            report = "You must be in a loaded game world to test ambient town banter.";
            return false;
        }
        if (Game1.IsMultiplayer && !Context.IsMainPlayer)
        {
            report = "Ambient banter generation runs on the multiplayer host.";
            return false;
        }

        var loc = Game1.player.currentLocation;
        NPC? npcA = !string.IsNullOrWhiteSpace(npcAName) ? Game1.getCharacterFromName(npcAName) : null;
        NPC? npcB = !string.IsNullOrWhiteSpace(npcBName) ? Game1.getCharacterFromName(npcBName) : null;

        if (npcA == null || npcB == null)
        {
            var localVillagers = loc.characters
                .Where(c => c != null && c.IsVillager && !c.IsInvisible && c != npcA)
                .OrderBy(c => Vector2.Distance(c.Tile, Game1.player.Tile))
                .ToList();

            if (npcA == null && localVillagers.Count > 0)
            {
                npcA = localVillagers[0];
                localVillagers.RemoveAt(0);
            }
            if (npcB == null && localVillagers.Count > 0)
            {
                // Prefer one that forms a named pair with npcA
                if (npcA != null)
                {
                    npcB = localVillagers.FirstOrDefault(v => _pairBuckets.ContainsKey(MakePairKey(npcA.Name, v.Name)))
                        ?? localVillagers[0];
                }
                else
                {
                    npcB = localVillagers[0];
                }
            }
        }

        if (npcA == null || npcB == null)
        {
            report = $"Could not find two villagers on '{loc.NameOrUniqueName}'. Pass explicit names (e.g. vb_test_banter Abigail Sebastian) or stand near 2 NPCs.";
            return false;
        }

        var sharedLoc = npcA.currentLocation ?? loc;
        if (npcB.currentLocation != sharedLoc)
        {
            report = $"{npcA.Name} ({npcA.currentLocation?.NameOrUniqueName}) and {npcB.Name} ({npcB.currentLocation?.NameOrUniqueName}) are on different maps.";
            return false;
        }

        // Remove any lingering session involving either test speaker cleanly
        _activeSessions.RemoveAll(s =>
            string.Equals(s.NpcAName, npcA.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.NpcBName, npcA.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.NpcAName, npcB.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.NpcBName, npcB.Name, StringComparison.OrdinalIgnoreCase));

        NpcActionExecutor.Instance.IsNpcBusyForAmbientBanter(npcA.Name, out bool compA);
        NpcActionExecutor.Instance.IsNpcBusyForAmbientBanter(npcB.Name, out bool compB);
        bool bothCompanions = compA && compB;

        ulong tickNow = _lastUpdateTick > 0 ? _lastUpdateTick : ModEntry.CurrentTick;
        string witnessName = Game1.player.displayName ?? Game1.player.Name ?? "Farmer";
        if (!StartBanterInternal(npcA, npcB, sharedLoc, bothCompanions, witnessName, tickNow))
        {
            report = $"No eligible conversation matched between {npcA.Name} and {npcB.Name}.";
            return false;
        }

        var active = _activeSessions.LastOrDefault(s =>
            string.Equals(s.NpcAName, npcA.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.NpcBName, npcA.Name, StringComparison.OrdinalIgnoreCase));
        report = active != null
            ? $"Started banter '{active.ConversationId}' ({active.Turns.Count} turns) between {active.NpcAName} & {active.NpcBName} at {active.LocationName}."
            : $"Started 1-turn banter between {npcA.Name} & {npcB.Name} at {sharedLoc.NameOrUniqueName}.";
        return true;
    }

    private static List<Farmer> GetOnlineFarmersSafe()
    {
        if (Game1.IsMultiplayer)
        {
            return Game1.getOnlineFarmers().Where(f => f != null).ToList();
        }
        return Game1.player != null ? new List<Farmer> { Game1.player } : new List<Farmer>();
    }

    private static bool IsChildNpc(NPC npc)
    {
        if (npc == null) return false;
        return npc.Age == NPC.child || CavePersonalityRegistry.ResolveProfile(npc).IsChildProtected;
    }

    private static string MakePairKey(string name1, string name2)
    {
        string a = name1.Trim();
        string b = name2.Trim();
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{a}|{b}"
            : $"{b}|{a}";
    }
}
