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

public sealed class VignetteBeatData
{
    public string Speaker { get; set; } = "A"; // "A" o "B"
    public int DelayTicks { get; set; } = 0;
    public int Emote { get; set; } = 32;
    public int? OtherEmote { get; set; }
    public string? Sound { get; set; }
    public string? Fx { get; set; } // sparkle | music | hearts | leaf | splash | dust
    public string En { get; set; } = string.Empty;
    public string Es { get; set; } = string.Empty;
}

public sealed class VignetteDefinitionData
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string NpcA { get; set; } = string.Empty;
    public string? NpcB { get; set; }
    public bool ChildSafe { get; set; } = true;
    public bool RequirePet { get; set; } = false;
    public string[]? Locations { get; set; }
    public string[]? Seasons { get; set; }
    public string[]? Weather { get; set; }
    public int? MinTime { get; set; }
    public int? MaxTime { get; set; }
    public List<VignetteBeatData> Beats { get; set; } = new();
}

public sealed class VignettePackFile
{
    public List<VignetteDefinitionData> Vignettes { get; set; } = new();
}

internal sealed class ActiveVignetteSession
{
    public VignetteDefinitionData Definition { get; init; } = null!;
    public NPC NpcA { get; init; } = null!;
    public NPC? NpcB { get; init; }
    public Pet? NearPet { get; init; }
    public Farmer WitnessFarmer { get; init; } = null!;
    public string LocationName { get; init; } = string.Empty;
    public int BeatIndex { get; set; } = 0;
    public ulong NextBeatTick { get; set; } = 0UL;
}

/// <summary>
/// Servicio "Valle Vivo": mini-escenas escenificadas en tiempo real (viñetas del pueblo)
/// con diálogo secuencial, sonidos seguros y partículas sincronizadas vía broadcastSprites,
/// sin teleports y respetando puestos de tienda, acompañantes y rutinas del cónyuge.
/// </summary>
public sealed class LivingTownVignetteService
{
    public static LivingTownVignetteService Instance { get; } = new();

    public static readonly string[] SupportedCategories =
    {
        "work_mishap_help",
        "dropped_items",
        "workplace_banter",
        "rain_shelter_laugh",
        "seasonal_wonder",
        "street_music_cheer",
        "farmer_praise",
        "pet_admire"
    };

    private const ulong ScanIntervalTicks = 90UL;
    private const ulong PerNpcCooldownTicks = 10800UL; // 3 minutos
    private const float MaxWitnessDistanceTiles = 16.0f;
    private const float MaxPairDistanceTiles = 8.5f;
    private const float BreakAwayDistanceTiles = 10.0f;
    private const float MaxPetDistanceTiles = 6.5f;
    private const int MaxActiveGlobal = 2;
    private const int RecentBufferCapacity = 25;
    private const int DefaultBeatDurationMs = 4200;

    private static readonly Color VignetteChatColor = new(245, 224, 175);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private IModHelper? _helper;
    private IMonitor? _monitor;

    private readonly List<VignetteDefinitionData> _vignettes = new();
    private readonly List<ActiveVignetteSession> _activeSessions = new();
    private readonly Dictionary<string, ulong> _npcCooldownUntilTick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentVignetteIds = new();
    private ulong _globalCooldownUntilTick = 0UL;

    public int LoadedVignetteCount => _vignettes.Count;
    public int ActiveVignetteCount => _activeSessions.Count;

    private LivingTownVignetteService() { }

    public void Initialize(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
        LoadVignettePacks();
    }

    public void ResetDaily()
    {
        _activeSessions.Clear();
        _npcCooldownUntilTick.Clear();
        _globalCooldownUntilTick = 0UL;
    }

    public void ResetAll()
    {
        ResetDaily();
        _recentVignetteIds.Clear();
    }

    public bool IsNpcInActiveVignette(string? npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName) || _activeSessions.Count == 0)
            return false;

        foreach (var s in _activeSessions)
        {
            if (string.Equals(s.NpcA.Name, npcName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (s.NpcB != null && string.Equals(s.NpcB.Name, npcName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void LoadVignettePacks()
    {
        _vignettes.Clear();
        if (_helper == null) return;

        string dir = Path.Combine(_helper.DirectoryPath, "assets", "living_valley");
        if (!Directory.Exists(dir))
        {
            _monitor?.Log($"[LivingTownVignettes] Directory not found: {dir}", LogLevel.Trace);
            return;
        }

        bool hasSve = _helper.ModRegistry.IsLoaded("FlashShifter.StardewValleyExpandedCP")
            || _helper.ModRegistry.IsLoaded("FlashShifter.SVECode");
        bool hasRsv = _helper.ModRegistry.IsLoaded("Rafseazz.RidgesideVillage")
            || _helper.ModRegistry.IsLoaded("Rafseazz.RSVCP");

        var filesToLoad = new List<string> { "vignettes_vanilla.json" };
        if (hasSve) filesToLoad.Add("vignettes_sve.json");
        if (hasRsv) filesToLoad.Add("vignettes_rsv.json");

        try
        {
            foreach (string extra in Directory.GetFiles(dir, "vignettes_custom_*.json", SearchOption.TopDirectoryOnly))
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
                var pack = JsonSerializer.Deserialize<VignettePackFile>(raw, JsonOpts);
                if (pack?.Vignettes == null) continue;

                foreach (var v in pack.Vignettes)
                {
                    if (v == null || string.IsNullOrWhiteSpace(v.NpcA) || v.Beats == null || v.Beats.Count == 0)
                        continue;

                    if (string.IsNullOrWhiteSpace(v.Id))
                        v.Id = $"vig_{_vignettes.Count + 1}";

                    if (string.IsNullOrWhiteSpace(v.Category))
                        v.Category = "workplace_banter";

                    foreach (var beat in v.Beats)
                    {
                        if (string.IsNullOrWhiteSpace(beat.En)) beat.En = beat.Es;
                        if (string.IsNullOrWhiteSpace(beat.Es)) beat.Es = beat.En;
                    }

                    _vignettes.Add(v);
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[LivingTownVignettes] Failed to load '{fileName}': {ex.Message}", LogLevel.Warn);
            }
        }

        _monitor?.Log(
            $"[LivingTownVignettes] Loaded {_vignettes.Count} staged vignettes across {SupportedCategories.Length} categories (SVE={hasSve}, RSV={hasRsv}).",
            LogLevel.Debug);
    }

    public void Update(ulong gameTick)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer) return;

        // Primero avanzar sesiones activas o cancelarlas limpiamente si el contexto cambió
        if (_activeSessions.Count > 0)
        {
            TickActiveSessions(gameTick);
        }

        if (ModEntry.Config?.EnableLivingTownVignettes == false) return;
        if (gameTick % ScanIntervalTicks != 0) return;

        if (_vignettes.Count == 0 || _activeSessions.Count >= MaxActiveGlobal) return;
        if (gameTick < _globalCooldownUntilTick) return;

        if (Utility.isFestivalDay() || Game1.isFestival() || Game1.weddingToday || Game1.eventUp)
            return;

        if (Game1.CurrentEvent != null && !Game1.CurrentEvent.isWedding)
            return;

        TryStartContextualVignette(gameTick);
    }

    private void TickActiveSessions(ulong gameTick)
    {
        bool worldInterrupted = Utility.isFestivalDay() || Game1.isFestival() || Game1.weddingToday || Game1.eventUp;

        for (int i = _activeSessions.Count - 1; i >= 0; i--)
        {
            var session = _activeSessions[i];
            if (worldInterrupted || !IsSessionStillValid(session))
            {
                _activeSessions.RemoveAt(i);
                continue;
            }

            if (gameTick < session.NextBeatTick) continue;

            if (session.BeatIndex >= session.Definition.Beats.Count)
            {
                _activeSessions.RemoveAt(i);
                continue;
            }

            var beat = session.Definition.Beats[session.BeatIndex];
            ExecuteBeat(session, beat);
            session.BeatIndex++;

            if (session.BeatIndex >= session.Definition.Beats.Count)
            {
                _activeSessions.RemoveAt(i);
            }
            else
            {
                int nextDelay = Math.Max(90, session.Definition.Beats[session.BeatIndex].DelayTicks);
                session.NextBeatTick = gameTick + (ulong)nextDelay;
            }
        }
    }

    private static bool IsSessionStillValid(ActiveVignetteSession session)
    {
        GameLocation? loc = session.NpcA.currentLocation;
        if (loc == null || !string.Equals(loc.NameOrUniqueName, session.LocationName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (loc.currentEvent != null) return false;
        if (!IsNpcGenerallyAvailable(session.NpcA, ignoreVignetteCheck: true))
            return false;

        if (session.NpcB != null)
        {
            if (session.NpcB.currentLocation != loc) return false;
            if (!IsNpcGenerallyAvailable(session.NpcB, ignoreVignetteCheck: true)) return false;

            // Cancelación limpia si los participantes se separan > 10 casillas
            float distSq = Vector2.DistanceSquared(session.NpcA.Tile, session.NpcB.Tile);
            if (distSq > BreakAwayDistanceTiles * BreakAwayDistanceTiles)
                return false;
        }

        return true;
    }

    private void TryStartContextualVignette(ulong gameTick)
    {
        // Agrupamos testigos online por mapa
        var farmersByMap = new Dictionary<string, List<Farmer>>(StringComparer.OrdinalIgnoreCase);
        foreach (Farmer farmer in Game1.getOnlineFarmers())
        {
            if (farmer == null) continue;
            GameLocation? loc = farmer.currentLocation;
            if (loc == null || loc.currentEvent != null) continue;
            if (loc is MovieTheater or MineShaft or VolcanoDungeon) continue;
            if (farmer == Game1.player && (Game1.activeClickableMenu != null || Game1.dialogueUp)) continue;

            string mapKey = loc.NameOrUniqueName;
            if (!farmersByMap.TryGetValue(mapKey, out var list))
            {
                list = new List<Farmer>();
                farmersByMap[mapKey] = list;
            }
            list.Add(farmer);
        }

        foreach (var (mapKey, witnessFarmers) in farmersByMap)
        {
            if (_activeSessions.Count >= MaxActiveGlobal) break;

            // Máximo 1 viñeta activa por mapa
            if (_activeSessions.Any(s => string.Equals(s.LocationName, mapKey, StringComparison.OrdinalIgnoreCase)))
                continue;

            GameLocation loc = witnessFarmers[0].currentLocation;
            if (loc?.characters == null || loc.characters.Count == 0) continue;

            // Recolectar NPCs disponibles cercanos (<= 16 casillas de algún granjero en el mapa)
            var eligibleNpcs = new Dictionary<string, (NPC Npc, Farmer Witness)>(StringComparer.OrdinalIgnoreCase);
            float maxWitnessSq = MaxWitnessDistanceTiles * MaxWitnessDistanceTiles;

            foreach (NPC ch in loc.characters)
            {
                if (ch == null || string.IsNullOrWhiteSpace(ch.Name)) continue;
                if (!IsNpcGenerallyAvailable(ch, ignoreVignetteCheck: false)) continue;
                if (_npcCooldownUntilTick.TryGetValue(ch.Name, out ulong untilTick) && gameTick < untilTick) continue;

                Farmer? closestWitness = null;
                float bestSq = maxWitnessSq;
                foreach (Farmer wf in witnessFarmers)
                {
                    float dSq = Vector2.DistanceSquared(wf.Tile, ch.Tile);
                    if (dSq <= bestSq)
                    {
                        bestSq = dSq;
                        closestWitness = wf;
                    }
                }

                if (closestWitness != null)
                    eligibleNpcs[ch.Name] = (ch, closestWitness);
            }

            if (eligibleNpcs.Count == 0) continue;

            List<Pet> petsInLoc = loc.characters.OfType<Pet>().ToList();

            var candidates = new List<(VignetteDefinitionData Def, NPC NpcA, NPC? NpcB, Pet? Pet, Farmer Witness, int Score)>();

            foreach (var v in _vignettes)
            {
                if (!eligibleNpcs.TryGetValue(v.NpcA, out var entryA)) continue;

                NPC? npcB = null;
                if (!string.IsNullOrWhiteSpace(v.NpcB))
                {
                    if (!eligibleNpcs.TryGetValue(v.NpcB, out var entryB)) continue;
                    float pairDistSq = Vector2.DistanceSquared(entryA.Npc.Tile, entryB.Npc.Tile);
                    if (pairDistSq > MaxPairDistanceTiles * MaxPairDistanceTiles) continue;
                    npcB = entryB.Npc;
                }

                bool hasChild = entryA.Npc.Age == NPC.child || (npcB != null && npcB.Age == NPC.child);
                if (hasChild && !v.ChildSafe) continue;

                Pet? nearPet = null;
                if (v.RequirePet)
                {
                    float petSq = MaxPetDistanceTiles * MaxPetDistanceTiles;
                    nearPet = petsInLoc.FirstOrDefault(p => Vector2.DistanceSquared(entryA.Npc.Tile, p.Tile) <= petSq);
                    if (nearPet == null) continue;
                }

                if (!MatchesContextFilters(v, loc)) continue;

                int score = ComputeSpecificityScore(v);
                candidates.Add((v, entryA.Npc, npcB, nearPet, entryA.Witness, score));
            }

            if (candidates.Count == 0) continue;

            // Evitar las últimas 25 viñetas vistas cuando existan alternativas frescas
            var fresh = candidates.Where(c => !_recentVignetteIds.Contains(c.Def.Id)).ToList();
            var pool = fresh.Count > 0 ? fresh : candidates;

            int topScore = pool.Max(c => c.Score);
            var topPool = pool.Where(c => c.Score >= topScore - 1).ToList();
            var chosen = topPool[Game1.random.Next(topPool.Count)];

            StartSession(chosen.Def, chosen.NpcA, chosen.NpcB, chosen.Pet, chosen.Witness, gameTick);
            return;
        }
    }

    public bool TryTriggerTestVignette(string? category, string? npcAName, string? npcBName, out string report)
    {
        report = string.Empty;
        if (!Context.IsWorldReady || !Context.IsMainPlayer || Game1.player?.currentLocation == null)
        {
            report = "You must be loaded into a world as the host to run vb_test_vignette.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(category)
            && !SupportedCategories.Contains(category.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            report = $"Unknown category '{category}'. Valid: {string.Join(", ", SupportedCategories)}";
            return false;
        }

        Farmer farmer = Game1.player;
        GameLocation loc = farmer.currentLocation;
        var locNpcs = new Dictionary<string, NPC>(StringComparer.OrdinalIgnoreCase);
        if (loc.characters != null)
        {
            foreach (NPC n in loc.characters)
            {
                if (n != null && !string.IsNullOrWhiteSpace(n.Name) && n.IsVillager && n is not Pet and not Horse and not Junimo and not Monster)
                {
                    locNpcs.TryAdd(n.Name, n);
                }
            }
        }

        var matching = _vignettes.Where(v =>
        {
            if (!string.IsNullOrWhiteSpace(category)
                && !string.Equals(v.Category, category.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(npcAName)
                && !string.Equals(v.NpcA, npcAName.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrWhiteSpace(npcBName)
                && !string.Equals(v.NpcB ?? string.Empty, npcBName.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }).ToList();

        // Primero intentar match con NPCs presentes actualmente en el mapa sin teleports
        var onMapMatches = matching.Where(v =>
            locNpcs.ContainsKey(v.NpcA)
            && (string.IsNullOrWhiteSpace(v.NpcB) || locNpcs.ContainsKey(v.NpcB))
            && MatchesContextFilters(v, loc))
            .ToList();

        if (onMapMatches.Count == 0)
        {
            onMapMatches = matching.Where(v =>
                locNpcs.ContainsKey(v.NpcA)
                && (string.IsNullOrWhiteSpace(v.NpcB) || locNpcs.ContainsKey(v.NpcB)))
                .ToList();
        }

        VignetteDefinitionData? picked = null;
        NPC? npcA = null;
        NPC? npcB = null;

        if (onMapMatches.Count > 0)
        {
            var fresh = onMapMatches.Where(v => !_recentVignetteIds.Contains(v.Id)).ToList();
            var list = fresh.Count > 0 ? fresh : onMapMatches;
            picked = list[Game1.random.Next(list.Count)];
            npcA = locNpcs[picked.NpcA];
            if (!string.IsNullOrWhiteSpace(picked.NpcB)) npcB = locNpcs[picked.NpcB];
        }
        else if (matching.Count > 0 && locNpcs.Count > 0)
        {
            // Fallback de prueba en consola: usa actores naturales del mapa actual si el jugador pidió categoría o estaba en otro mapa
            picked = matching[Game1.random.Next(matching.Count)];
            npcA = !string.IsNullOrWhiteSpace(npcAName) && locNpcs.TryGetValue(npcAName.Trim(), out var explicitA)
                ? explicitA
                : Game1.getCharacterFromName(picked.NpcA);
            npcB = !string.IsNullOrWhiteSpace(picked.NpcB)
                ? (!string.IsNullOrWhiteSpace(npcBName) && locNpcs.TryGetValue(npcBName.Trim(), out var explicitB)
                    ? explicitB
                    : Game1.getCharacterFromName(picked.NpcB))
                : null;
        }

        if (picked == null || npcA?.currentLocation == null)
        {
            report = $"No suitable vignette matched on '{loc.NameOrUniqueName}'. Try standing near NPCs or specify: vb_test_vignette [{string.Join("|", SupportedCategories)}] [npcA] [npcB]";
            return false;
        }

        Pet? nearPet = npcA.currentLocation.characters.OfType<Pet>().FirstOrDefault();
        StartSession(picked, npcA, npcB, nearPet, farmer, ModEntry.CurrentTick, isTestOverride: true);
        report = $"Triggered vignette '{picked.Id}' ({picked.Category}) with {npcA.Name}"
            + (npcB != null ? $" & {npcB.Name}" : string.Empty)
            + $" on {npcA.currentLocation.NameOrUniqueName}.";
        return true;
    }

    private void StartSession(
        VignetteDefinitionData def,
        NPC npcA,
        NPC? npcB,
        Pet? nearPet,
        Farmer witness,
        ulong gameTick,
        bool isTestOverride = false)
    {
        string locName = npcA.currentLocation?.NameOrUniqueName ?? string.Empty;
        if (string.IsNullOrEmpty(locName)) return;

        // Giro sutil hacia compañero/mascota/granjero solo si están quietos, sin ruta y no en mostrador de tienda
        if (npcB != null)
        {
            if (!npcA.isMoving() && npcA.controller == null && !ShopDutyDetector.IsShopkeeperOnDuty(npcA, npcA.currentLocation))
                npcA.faceGeneralDirection(npcB.Position);
            if (!npcB.isMoving() && npcB.controller == null && !ShopDutyDetector.IsShopkeeperOnDuty(npcB, npcB.currentLocation))
                npcB.faceGeneralDirection(npcA.Position);
        }
        else if (nearPet != null)
        {
            if (!npcA.isMoving() && npcA.controller == null && !ShopDutyDetector.IsShopkeeperOnDuty(npcA, npcA.currentLocation))
                npcA.faceGeneralDirection(nearPet.Position);
        }
        else if (!npcA.isMoving() && npcA.controller == null && !ShopDutyDetector.IsShopkeeperOnDuty(npcA, npcA.currentLocation))
        {
            npcA.faceGeneralDirection(witness.Position);
        }

        var session = new ActiveVignetteSession
        {
            Definition = def,
            NpcA = npcA,
            NpcB = npcB,
            NearPet = nearPet,
            WitnessFarmer = witness,
            LocationName = locName,
            BeatIndex = 0,
            NextBeatTick = gameTick
        };

        _activeSessions.Add(session);
        RecordRecent(def.Id);

        if (!isTestOverride)
        {
            int cooldownSeconds = Math.Clamp(ModEntry.Config?.LivingTownVignetteCooldownSeconds ?? 45, 20, 120);
            _globalCooldownUntilTick = gameTick + (ulong)cooldownSeconds * 60UL;
            _npcCooldownUntilTick[npcA.Name] = gameTick + PerNpcCooldownTicks;
            if (npcB != null)
                _npcCooldownUntilTick[npcB.Name] = gameTick + PerNpcCooldownTicks;
        }

        // Disparar inmediatamente el primer beat si DelayTicks == 0
        if (def.Beats[0].DelayTicks <= 0)
        {
            TickActiveSessions(gameTick);
        }
        else
        {
            session.NextBeatTick = gameTick + (ulong)def.Beats[0].DelayTicks;
        }
    }

    private void ExecuteBeat(ActiveVignetteSession session, VignetteBeatData beat)
    {
        NPC speaker = string.Equals(beat.Speaker, "B", StringComparison.OrdinalIgnoreCase) && session.NpcB != null
            ? session.NpcB
            : session.NpcA;
        NPC? listener = speaker == session.NpcA ? session.NpcB : session.NpcA;
        GameLocation? loc = speaker.currentLocation;
        if (loc == null) return;

        // Solo rotar suavemente si están quietos, sin ruta y sin puesto de tienda
        if (listener != null)
        {
            if (!speaker.isMoving() && speaker.controller == null && !ShopDutyDetector.IsShopkeeperOnDuty(speaker, loc))
                speaker.faceGeneralDirection(listener.Position);
        }

        if (beat.Emote > 0)
        {
            speaker.doEmote(beat.Emote);
        }

        if (beat.OtherEmote.HasValue && beat.OtherEmote.Value > 0)
        {
            if (listener != null)
                listener.doEmote(beat.OtherEmote.Value);
            else if (session.NearPet != null && !session.NearPet.IsEmoting)
                session.NearPet.doEmote(beat.OtherEmote.Value);
        }

        if (!string.IsNullOrWhiteSpace(beat.Sound))
        {
            try { loc.playSound(beat.Sound); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(beat.Fx))
        {
            SpawnSynchronizedParticles(loc, speaker, listener, session.NearPet, beat.Fx);
        }

        Farmer wf = session.WitnessFarmer ?? Game1.player;
        string farmNameEn = string.IsNullOrWhiteSpace(wf?.farmName.Value) ? "the farm" : wf.farmName.Value;
        string farmNameEs = string.IsNullOrWhiteSpace(wf?.farmName.Value) ? "la granja" : wf.farmName.Value;
        string farmerName = wf?.Name ?? "Farmer";
        string petName = session.NearPet?.displayName ?? session.NearPet?.Name ?? "buddy";

        string enText = FormatTokens(beat.En, farmerName, farmNameEn, petName);
        string esText = FormatTokens(beat.Es, farmerName, farmNameEs, petName);

        bool hostIsSpanish = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.es;
        string localText = hostIsSpanish ? esText : enText;

        if (!string.IsNullOrWhiteSpace(localText))
        {
            speaker.showTextAboveHead(localText, duration: DefaultBeatDurationMs);
            AmbientTownBanterService.Instance.RecordExternalTranscriptLine(
                $"[Vignette:{session.Definition.Category}] {speaker.Name}: {localText}");

            EchoVignetteToNearbyFarmers(loc, speaker, localText, enText, esText);
        }
    }

    private static void SpawnSynchronizedParticles(
        GameLocation loc,
        NPC speaker,
        NPC? listener,
        Pet? pet,
        string fxKey)
    {
        try
        {
            Vector2 origin = speaker.Position;
            if (listener != null)
                origin = (speaker.Position + listener.Position) / 2f;
            else if (pet != null)
                origin = (speaker.Position + pet.Position) / 2f;

            Color tint = fxKey.ToLowerInvariant() switch
            {
                "music" => Color.CornflowerBlue,
                "hearts" => Color.HotPink,
                "leaf" => Color.Orange,
                "splash" => Color.DeepSkyBlue,
                "dust" => Color.LightGray,
                _ => Color.Gold
            };

            int count = fxKey.Equals("dust", StringComparison.OrdinalIgnoreCase) ? 3 : 4;
            for (int i = 0; i < count; i++)
            {
                Vector2 offset = new Vector2(Game1.random.Next(-48, 49), Game1.random.Next(-64, 17));
                var sprite = new TemporaryAnimatedSprite(
                    "LooseSprites\\Cursors",
                    new Rectangle(294, 1432, 16, 16),
                    90f,
                    4,
                    2,
                    origin + offset,
                    false,
                    false,
                    1f,
                    0.015f,
                    tint,
                    2.2f,
                    0.01f,
                    0f,
                    0f);

                if (Game1.IsMultiplayer && Game1.Multiplayer != null)
                    Game1.Multiplayer.broadcastSprites(loc, sprite);
                else
                    loc.TemporarySprites.Add(sprite);
            }
        }
        catch { }
    }

    private static void EchoVignetteToNearbyFarmers(
        GameLocation loc,
        NPC speaker,
        string localText,
        string enText,
        string esText)
    {
        float maxSq = MaxWitnessDistanceTiles * MaxWitnessDistanceTiles;

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
                    Game1.chatBox?.addMessage($"{speaker.displayName}: {localText}", VignetteChatColor);
            }
            else if (Context.IsMultiplayer)
            {
                ModEntry.MultiplayerSync?.SendBanterChatEcho(
                    peer.UniqueMultiplayerID,
                    speaker,
                    localText,
                    enText,
                    esText,
                    isCompanionChat: false,
                    isPersonalChatter: true,
                    maxDistanceTiles: MaxWitnessDistanceTiles);
            }
        }
    }

    private static bool IsNpcGenerallyAvailable(NPC npc, bool ignoreVignetteCheck)
    {
        if (npc == null || !npc.IsVillager) return false;
        if (npc is Pet or Horse or Junimo or Monster) return false;
        if (npc.isSleeping.Value || npc.IsInvisible) return false;
        if (string.IsNullOrWhiteSpace(npc.Name)) return false;

        if (!ignoreVignetteCheck && Instance.IsNpcInActiveVignette(npc.Name))
            return false;

        if (AmbientTownBanterService.Instance.IsNpcInActiveBanter(npc.Name)) return false;
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

    private static bool MatchesContextFilters(VignetteDefinitionData v, GameLocation loc)
    {
        if (v.MinTime.HasValue && Game1.timeOfDay < v.MinTime.Value)
            return false;
        if (v.MaxTime.HasValue && Game1.timeOfDay > v.MaxTime.Value)
            return false;

        if (v.Locations != null && v.Locations.Length > 0)
        {
            string locName = loc.Name ?? string.Empty;
            string locUnique = loc.NameOrUniqueName ?? string.Empty;
            bool locMatched = v.Locations.Any(req =>
                !string.IsNullOrWhiteSpace(req)
                && (string.Equals(locName, req, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(locUnique, req, StringComparison.OrdinalIgnoreCase)
                    || locName.Contains(req, StringComparison.OrdinalIgnoreCase)));
            if (!locMatched) return false;
        }

        if (v.Seasons != null && v.Seasons.Length > 0)
        {
            string curSeason = (loc.GetSeason().ToString() ?? Game1.currentSeason ?? "spring").ToLowerInvariant();
            if (!v.Seasons.Any(s => string.Equals(s, curSeason, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (v.Weather != null && v.Weather.Length > 0)
        {
            bool greenRain = loc.IsOutdoors && loc is not Desert && Game1.isGreenRain;
            bool storm = loc.IsOutdoors && loc is not Desert && loc.IsLightningHere();
            bool snow = loc.IsOutdoors && loc.IsSnowingHere();
            bool rain = loc.IsOutdoors && loc.IsRainingHere();
            bool sun = !greenRain && !storm && !snow && !rain;

            bool weatherMatch = v.Weather.Any(w => w.ToLowerInvariant() switch
            {
                "green_rain" => greenRain,
                "storm" => storm,
                "snow" => snow,
                "rain" => rain || storm,
                "sun" or "clear" => sun,
                _ => false
            });

            if (!weatherMatch) return false;
        }

        return true;
    }

    private static int ComputeSpecificityScore(VignetteDefinitionData v)
    {
        int score = 0;
        if (v.Locations != null && v.Locations.Length > 0) score += 3;
        if (v.Weather != null && v.Weather.Length > 0) score += 3;
        if (v.Seasons != null && v.Seasons.Length > 0) score += 2;
        if (v.RequirePet) score += 2;
        if (v.MinTime.HasValue || v.MaxTime.HasValue) score += 1;
        return score;
    }

    private void RecordRecent(string vignetteId)
    {
        if (string.IsNullOrWhiteSpace(vignetteId) || _recentVignetteIds.Contains(vignetteId))
            return;

        _recentVignetteIds.Enqueue(vignetteId);
        while (_recentVignetteIds.Count > RecentBufferCapacity)
            _recentVignetteIds.Dequeue();
    }

    private static string FormatTokens(string template, string farmerName, string farmName, string petName)
        => (template ?? string.Empty)
            .Replace("{farmer}", farmerName, StringComparison.OrdinalIgnoreCase)
            .Replace("{farm}", farmName, StringComparison.OrdinalIgnoreCase)
            .Replace("{pet}", petName, StringComparison.OrdinalIgnoreCase);
}
