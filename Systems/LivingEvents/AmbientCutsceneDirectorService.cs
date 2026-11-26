using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Monsters;
using ValleyBuddies.Execution;

namespace ValleyBuddies.Systems.LivingEvents;

public sealed class CutsceneLineData
{
    public string Speaker { get; set; } = string.Empty;
    public string En { get; set; } = string.Empty;
    public string Es { get; set; } = string.Empty;
    public int? Emote { get; set; }
    public int? Dir { get; set; }
    public int? PauseMs { get; set; }
    public string? Sound { get; set; }
    public bool? Jump { get; set; }
    public int? ShakeMs { get; set; }
}

public sealed class CutsceneFarmerNoticeData
{
    public string Noticer { get; set; } = string.Empty;
    public string IntroEn { get; set; } = string.Empty;
    public string IntroEs { get; set; } = string.Empty;
    public string Opt1En { get; set; } = string.Empty;
    public string Opt1Es { get; set; } = string.Empty;
    public string Opt2En { get; set; } = string.Empty;
    public string Opt2Es { get; set; } = string.Empty;
    public string Reply1Speaker { get; set; } = string.Empty;
    public string Reply1En { get; set; } = string.Empty;
    public string Reply1Es { get; set; } = string.Empty;
    public int? Reply1Emote { get; set; }
    public string Reply2Speaker { get; set; } = string.Empty;
    public string Reply2En { get; set; } = string.Empty;
    public string Reply2Es { get; set; } = string.Empty;
    public int? Reply2Emote { get; set; }
}

public sealed class CutsceneScriptData
{
    public string Id { get; set; } = string.Empty;
    public bool NoticeFarmer { get; set; } = false;
    public string? Music { get; set; }
    public List<CutsceneLineData> Lines { get; set; } = new();
    public CutsceneFarmerNoticeData? FarmerNotice { get; set; }
}

public sealed class CutsceneSceneData
{
    public string Id { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string[]? Locations { get; set; }
    public string[] RequiredActors { get; set; } = Array.Empty<string>();
    public string[]? OptionalActors { get; set; }
    public int? MinTime { get; set; }
    public int? MaxTime { get; set; }
    public string[]? Seasons { get; set; }
    public string[]? Weather { get; set; }
    public List<CutsceneScriptData> Scripts { get; set; } = new();
}

public sealed class CutscenePackFile
{
    public List<CutsceneSceneData> Scenes { get; set; } = new();
}

/// <summary>
/// Director de cinemáticas ambientales aleatorias al entrar a interiores y lugares del valle.
/// Corre localmente en el cliente que entra a la escena, sin congelar el tiempo en Co-op,
/// validando actores reales, textura nativa y espacio seguro de cámara y jugador.
/// </summary>
public sealed class AmbientCutsceneDirectorService
{
    public static AmbientCutsceneDirectorService Instance { get; } = new();

    private const ulong WarpDelayTicks = 75UL;
    private const int SceneCooldownDays = 3;
    private const int MaxActorsPerScene = 5;
    private const int RecentScriptsCapacity = 30;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private IModHelper? _helper;
    private IMonitor? _monitor;

    private readonly List<CutsceneSceneData> _scenes = new();
    private readonly Dictionary<string, int> _lastPlayedDayByScene = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentScriptIds = new();

    private GameLocation? _pendingWarpLocation;
    private ulong _pendingCheckTick;
    private int _todayCutsceneCount;

    public int LoadedSceneCount => _scenes.Count;
    public int LoadedScriptCount => _scenes.Sum(s => s.Scripts?.Count ?? 0);

    private AmbientCutsceneDirectorService() { }

    public void Initialize(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;
        LoadCutscenePacks();
    }

    public void ResetDaily()
    {
        _todayCutsceneCount = 0;
        _pendingWarpLocation = null;
        _pendingCheckTick = 0UL;
    }

    public void ResetAll()
    {
        ResetDaily();
        _lastPlayedDayByScene.Clear();
        _recentScriptIds.Clear();
    }

    public void OnPlayerWarped(GameLocation? newLoc, ulong gameTick)
    {
        if (!Context.IsWorldReady || newLoc == null || _scenes.Count == 0)
        {
            _pendingWarpLocation = null;
            return;
        }

        if (ModEntry.Config?.EnableAmbientCutscenes == false)
        {
            _pendingWarpLocation = null;
            return;
        }

        _pendingWarpLocation = newLoc;
        _pendingCheckTick = gameTick + WarpDelayTicks;
    }

    public void Update(ulong gameTick)
    {
        if (!Context.IsWorldReady || Game1.player == null)
            return;

        if (_pendingWarpLocation == null || gameTick < _pendingCheckTick)
            return;

        var loc = _pendingWarpLocation;
        _pendingWarpLocation = null;

        if (ModEntry.Config?.EnableAmbientCutscenes == false)
            return;

        if (Game1.player.currentLocation != loc)
            return;

        if (!IsWorldSafeForCutscene(loc))
            return;

        int maxPerDay = Math.Clamp(ModEntry.Config?.MaxAmbientCutscenesPerDay ?? 2, 1, 6);
        if (_todayCutsceneCount >= maxPerDay)
            return;

        int chancePercent = Math.Clamp(ModEntry.Config?.AmbientCutsceneChancePercent ?? 25, 5, 100);
        if (Game1.random.Next(100) >= chancePercent)
            return;

        TryTriggerSceneInLocation(loc, Game1.player, specificScene: null, specificScriptIndex: -1, ignoreGuards: false, out _);
    }

    public bool TryTriggerTestScene(string? sceneIdOrList, int scriptIndex, out string report)
    {
        if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
        {
            report = "You must be in-game to run vb_test_ambient_scene.";
            return false;
        }

        if (string.Equals(sceneIdOrList?.Trim(), "list", StringComparison.OrdinalIgnoreCase))
        {
            if (_scenes.Count == 0)
            {
                report = "No ambient cutscene scenes loaded.";
                return false;
            }

            string summary = string.Join(", ", _scenes.Select(s => $"{s.Id}@{GetSceneLocationLabel(s)}({s.Scripts.Count})"));
            report = $"Loaded {_scenes.Count} scenes / {LoadedScriptCount} scripts: {summary}";
            return true;
        }

        CutsceneSceneData? targetScene = null;
        if (!string.IsNullOrWhiteSpace(sceneIdOrList))
        {
            targetScene = _scenes.FirstOrDefault(s => string.Equals(s.Id, sceneIdOrList.Trim(), StringComparison.OrdinalIgnoreCase));
            if (targetScene == null)
            {
                report = $"Scene '{sceneIdOrList}' not found. Run 'vb_test_ambient_scene list' to see available IDs.";
                return false;
            }
        }

        return TryTriggerSceneInLocation(
            Game1.player.currentLocation,
            Game1.player,
            targetScene,
            scriptIndex,
            ignoreGuards: true,
            out report);
    }

    private void LoadCutscenePacks()
    {
        _scenes.Clear();
        if (_helper == null) return;

        string dir = Path.Combine(_helper.DirectoryPath, "assets", "ambient_cutscenes");
        if (!Directory.Exists(dir))
        {
            _monitor?.Log($"[AmbientCutscenes] Directory not found: {dir}", LogLevel.Trace);
            return;
        }

        bool hasSve = _helper.ModRegistry.IsLoaded("FlashShifter.StardewValleyExpandedCP")
                   || _helper.ModRegistry.IsLoaded("FlashShifter.SVECode");
        bool hasRsv = _helper.ModRegistry.IsLoaded("Rafseazz.RidgesideVillage")
                   || _helper.ModRegistry.IsLoaded("Rafseazz.RSVCP");

        var files = new List<string>();
        files.AddRange(Directory.GetFiles(dir, "vanilla_*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        if (hasSve)
            files.AddRange(Directory.GetFiles(dir, "sve_*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        if (hasRsv)
            files.AddRange(Directory.GetFiles(dir, "rsv_*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        files.AddRange(Directory.GetFiles(dir, "custom_*.json", SearchOption.TopDirectoryOnly).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

        foreach (string fullPath in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string raw = File.ReadAllText(fullPath);
                var pack = JsonSerializer.Deserialize<CutscenePackFile>(raw, JsonOpts);
                if (pack?.Scenes == null) continue;

                foreach (var scene in pack.Scenes)
                {
                    if (scene == null || string.IsNullOrWhiteSpace(scene.Id) || !HasAnyLocation(scene))
                        continue;
                    if (scene.Scripts == null || scene.Scripts.Count == 0)
                        continue;

                    for (int i = 0; i < scene.Scripts.Count; i++)
                    {
                        var sc = scene.Scripts[i];
                        if (string.IsNullOrWhiteSpace(sc.Id))
                            sc.Id = $"{scene.Id}_script_{i + 1}";
                    }

                    _scenes.Add(scene);
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[AmbientCutscenes] Failed to load '{Path.GetFileName(fullPath)}': {ex.Message}", LogLevel.Warn);
            }
        }

        _monitor?.Log($"[AmbientCutscenes] Loaded {LoadedSceneCount} scenes and {LoadedScriptCount} scripts (SVE={hasSve}, RSV={hasRsv}).", LogLevel.Info);
    }

    private bool TryTriggerSceneInLocation(
        GameLocation loc,
        Farmer player,
        CutsceneSceneData? specificScene,
        int specificScriptIndex,
        bool ignoreGuards,
        out string report)
    {
        if (!IsWorldSafeForCutscene(loc))
        {
            report = "World state not safe right now (fade, menu, active event, dialogue or festival).";
            return false;
        }

        var candidates = new List<(CutsceneSceneData Scene, List<(string RawSpec, NPC Actor)> Actors)>();
        int todayTotalDays = Game1.Date?.TotalDays ?? 0;

        IEnumerable<CutsceneSceneData> pool = specificScene != null ? new[] { specificScene } : _scenes;
        foreach (var scene in pool)
        {
            if (!MatchesLocation(scene, loc))
            {
                if (specificScene != null)
                {
                    report = $"Scene '{scene.Id}' expects location '{GetSceneLocationLabel(scene)}', current location is '{loc.NameOrUniqueName}'.";
                    return false;
                }
                continue;
            }

            if (!ignoreGuards)
            {
                if (!MatchesContextFilters(scene, loc))
                    continue;

                if (_lastPlayedDayByScene.TryGetValue(scene.Id, out int lastDay) && (todayTotalDays - lastDay) < SceneCooldownDays)
                    continue;
            }

            if (!TryResolveSceneActors(scene, loc, out var resolvedActors, out string? missingReason))
            {
                if (specificScene != null)
                {
                    report = $"Scene '{scene.Id}' missing required/valid actor(s) in {loc.NameOrUniqueName}: {missingReason}";
                    return false;
                }
                continue;
            }

            candidates.Add((scene, resolvedActors));
        }

        if (candidates.Count == 0)
        {
            report = specificScene != null
                ? $"Could not stage scene '{specificScene.Id}' in '{loc.NameOrUniqueName}'."
                : $"No eligible ambient cutscene available right now in '{loc.NameOrUniqueName}'.";
            return false;
        }

        var chosen = candidates[Game1.random.Next(candidates.Count)];
        CutsceneScriptData? chosenScript = PickScript(chosen.Scene, specificScriptIndex);
        if (chosenScript == null)
        {
            report = $"No valid script found in scene '{chosen.Scene.Id}'.";
            return false;
        }

        if (!BuildAndStartEvent(loc, player, chosen.Scene, chosenScript, chosen.Actors, out string err))
        {
            report = err;
            return false;
        }

        if (!ignoreGuards)
            _todayCutsceneCount++;

        _lastPlayedDayByScene[chosen.Scene.Id] = todayTotalDays;
        RecordRecentScript(chosenScript.Id);

        report = $"Started ambient cutscene '{chosen.Scene.Id}' (script '{chosenScript.Id}', noticeFarmer={chosenScript.NoticeFarmer}) with {chosen.Actors.Count} actors in {loc.NameOrUniqueName}.";
        _monitor?.Log($"[AmbientCutscenes] {report}", LogLevel.Debug);
        return true;
    }

    private CutsceneScriptData? PickScript(CutsceneSceneData scene, int specificScriptIndex)
    {
        if (scene.Scripts == null || scene.Scripts.Count == 0)
            return null;

        if (specificScriptIndex >= 0 && specificScriptIndex < scene.Scripts.Count)
            return scene.Scripts[specificScriptIndex];

        var fresh = scene.Scripts.Where(s => !_recentScriptIds.Contains(s.Id)).ToList();
        var list = fresh.Count > 0 ? fresh : scene.Scripts;
        return list[Game1.random.Next(list.Count)];
    }

    private void RecordRecentScript(string scriptId)
    {
        if (string.IsNullOrWhiteSpace(scriptId)) return;
        _recentScriptIds.Enqueue(scriptId);
        while (_recentScriptIds.Count > RecentScriptsCapacity)
            _recentScriptIds.Dequeue();
    }

    private static bool IsWorldSafeForCutscene(GameLocation loc)
    {
        if (loc == null) return false;
        if (Game1.fadeToBlack || Game1.isWarping || Game1.eventUp || Game1.CurrentEvent != null || loc.currentEvent != null)
            return false;
        if (Game1.activeClickableMenu != null || Game1.dialogueUp)
            return false;
        if (Utility.isFestivalDay() || Game1.isFestival() || Game1.weddingToday)
            return false;
        if (Game1.currentMinigame != null)
            return false;

        return true;
    }

    private static bool HasAnyLocation(CutsceneSceneData scene)
    {
        if (!string.IsNullOrWhiteSpace(scene.Location))
            return true;
        return scene.Locations != null && scene.Locations.Any(l => !string.IsNullOrWhiteSpace(l));
    }

    private static string GetSceneLocationLabel(CutsceneSceneData scene)
    {
        if (scene.Locations != null && scene.Locations.Length > 0)
            return string.Join("|", scene.Locations.Where(l => !string.IsNullOrWhiteSpace(l)));
        return scene.Location ?? string.Empty;
    }

    private static bool MatchesLocation(CutsceneSceneData scene, GameLocation loc)
    {
        string locName = loc.Name ?? string.Empty;
        string locUnique = loc.NameOrUniqueName ?? string.Empty;

        static bool MatchesSingle(string? target, string locName, string locUnique)
        {
            string clean = target?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(clean)) return false;
            return string.Equals(locName, clean, StringComparison.OrdinalIgnoreCase)
                || string.Equals(locUnique, clean, StringComparison.OrdinalIgnoreCase);
        }

        if (MatchesSingle(scene.Location, locName, locUnique))
            return true;

        if (scene.Locations != null)
        {
            foreach (string cand in scene.Locations)
            {
                if (MatchesSingle(cand, locName, locUnique))
                    return true;
            }
        }

        return false;
    }

    private static bool MatchesContextFilters(CutsceneSceneData scene, GameLocation loc)
    {
        if (scene.MinTime.HasValue && Game1.timeOfDay < scene.MinTime.Value)
            return false;
        if (scene.MaxTime.HasValue && Game1.timeOfDay > scene.MaxTime.Value)
            return false;

        if (scene.Seasons != null && scene.Seasons.Length > 0)
        {
            string season = (loc.GetSeason().ToString() ?? Game1.currentSeason ?? "spring").ToLowerInvariant();
            if (!scene.Seasons.Any(s => string.Equals(s?.Trim(), season, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        if (scene.Weather != null && scene.Weather.Length > 0)
        {
            string weather = GetWeatherKey(loc);
            if (!scene.Weather.Any(w => string.Equals(w?.Trim(), weather, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        return true;
    }

    private static string GetWeatherKey(GameLocation loc)
    {
        if (loc.IsOutdoors)
        {
            if (loc.IsSnowingHere()) return "snow";
            if (loc.IsLightningHere()) return "storm";
            if (loc.IsRainingHere()) return "rain";
            return "sun";
        }

        string ctxId = loc.GetLocationContextId();
        if (string.Equals(ctxId, "Desert", StringComparison.OrdinalIgnoreCase))
            return "sun";

        var ctxWeather = Game1.netWorldState?.Value?.GetWeatherForLocation(ctxId);
        if (ctxWeather != null)
        {
            if (ctxWeather.IsSnowing) return "snow";
            if (ctxWeather.IsLightning) return "storm";
            if (ctxWeather.IsRaining) return "rain";
            return "sun";
        }

        if (Game1.isSnowing) return "snow";
        if (Game1.isLightning) return "storm";
        if (Game1.isRaining) return "rain";
        return "sun";
    }

    private static bool TryResolveSceneActors(
        CutsceneSceneData scene,
        GameLocation loc,
        out List<(string RawSpec, NPC Actor)> resolved,
        out string? missingReason)
    {
        resolved = new List<(string, NPC)>();
        missingReason = null;

        if (loc.characters == null || scene.RequiredActors == null || scene.RequiredActors.Length == 0)
        {
            missingReason = "no required actors specified or empty room";
            return false;
        }

        var usedNpcs = new HashSet<NPC>();

        foreach (string reqSpec in scene.RequiredActors)
        {
            NPC? actor = FindMatchingActorInLocation(loc, reqSpec, usedNpcs);
            if (actor == null)
            {
                missingReason = reqSpec;
                return false;
            }
            usedNpcs.Add(actor);
            resolved.Add((reqSpec, actor));
        }

        if (scene.OptionalActors != null)
        {
            foreach (string optSpec in scene.OptionalActors)
            {
                if (resolved.Count >= MaxActorsPerScene)
                    break;

                NPC? actor = FindMatchingActorInLocation(loc, optSpec, usedNpcs);
                if (actor != null)
                {
                    usedNpcs.Add(actor);
                    resolved.Add((optSpec, actor));
                }
            }
        }

        if (resolved.Count > MaxActorsPerScene)
            resolved = resolved.Take(MaxActorsPerScene).ToList();

        return true;
    }

    private static NPC? FindMatchingActorInLocation(GameLocation loc, string spec, HashSet<NPC> alreadyPicked)
    {
        if (string.IsNullOrWhiteSpace(spec) || loc.characters == null)
            return null;

        string[] aliases = spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string alias in aliases)
        {
            foreach (var ch in loc.characters)
            {
                if (ch == null || alreadyPicked.Contains(ch)) continue;
                if (!string.Equals(ch.Name, alias, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsNpcEligibleForCutscene(ch)) continue;
                return ch;
            }
        }
        return null;
    }

    private static bool IsNpcEligibleForCutscene(NPC npc)
    {
        if (npc.GetType() != typeof(NPC) || !npc.IsVillager) return false;
        if (npc is Pet or Horse or Junimo or Monster) return false;
        if (npc.isSleeping.Value || npc.IsInvisible) return false;
        if (string.IsNullOrWhiteSpace(npc.Name)) return false;

        if (npc.Name.Contains(' ') || npc.Name.Contains('/') || npc.Name.Contains('"') || npc.Name.Contains('#'))
            return false;

        // Validar sprite y textura para evitar cualquier NullReference en new Event(...)
        try
        {
            if (npc.Sprite?.Texture == null)
                return false;

            string texName = NPC.getTextureNameForCharacter(npc.Name);
            if (!Game1.content.DoesAssetExist<Texture2D>("Characters\\" + texName))
                return false;
        }
        catch
        {
            return false;
        }

        // Nunca interrumpir acompañantes, salidas o eventos activos de ValleyBuddies
        if (LivingEventManager.Instance.IsNpcWalkingInApproach(npc.Name)) return false;
        if (NpcOutingInvitationService.Instance.HasActiveOutingForNpc(npc.Name)) return false;
        if (LivingTownVignetteService.Instance.IsNpcInActiveVignette(npc.Name)) return false;
        if (AmbientTownBanterService.Instance.IsNpcInActiveBanter(npc.Name)) return false;

        if (NpcActionExecutor.Instance != null)
        {
            if (NpcActionExecutor.Instance.IsNpcBusyForAmbientBanter(npc.Name, out bool isManaged) || isManaged)
                return false;
            if (NpcActionExecutor.Instance.IsSpouseInHomeRoutine(npc.Name))
                return false;
        }

        return true;
    }

    private bool BuildAndStartEvent(
        GameLocation loc,
        Farmer player,
        CutsceneSceneData scene,
        CutsceneScriptData script,
        List<(string RawSpec, NPC Actor)> actors,
        out string error)
    {
        error = string.Empty;
        try
        {
            bool useEs = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.es
                      || (_helper?.Translation.Locale?.StartsWith("es", StringComparison.OrdinalIgnoreCase) == true);

            // 1. Asignar posiciones sin colisiones entre actores ni con farmer
            var occupiedTiles = new HashSet<Point>();
            var placedActors = new List<(string RawSpec, NPC Actor, Point Tile, int Dir)>();

            foreach (var (rawSpec, actor) in actors)
            {
                Point tile = actor.TilePoint;
                if (occupiedTiles.Contains(tile))
                {
                    tile = FindFreeNearbyTile(loc, tile, occupiedTiles, actor);
                }
                occupiedTiles.Add(tile);
                int dir = Math.Clamp(actor.FacingDirection, 0, 3);
                placedActors.Add((rawSpec, actor, tile, dir));
            }

            Point farmerTile = player.TilePoint;
            if (occupiedTiles.Contains(farmerTile))
            {
                farmerTile = FindFreeNearbyTile(loc, farmerTile, occupiedTiles, npc: null);
            }
            occupiedTiles.Add(farmerTile);
            int farmerDir = Math.Clamp(player.FacingDirection, 0, 3);

            // 2. Centroide de cámara clampeado al mapa
            int sumX = 0, sumY = 0;
            foreach (var p in placedActors)
            {
                sumX += p.Tile.X;
                sumY += p.Tile.Y;
            }
            int mapW = loc.map?.Layers != null && loc.map.Layers.Count > 0 ? loc.map.Layers[0].LayerWidth : 30;
            int mapH = loc.map?.Layers != null && loc.map.Layers.Count > 0 ? loc.map.Layers[0].LayerHeight : 30;
            int camX = Math.Clamp(sumX / Math.Max(1, placedActors.Count), 4, Math.Max(4, mapW - 5));
            int camY = Math.Clamp(sumY / Math.Max(1, placedActors.Count), 4, Math.Max(4, mapH - 5));

            // 3. Header de actores: farmer + NPCs presentes
            var actorDefs = new List<string> { $"farmer {farmerTile.X} {farmerTile.Y} {farmerDir}" };
            foreach (var p in placedActors)
            {
                actorDefs.Add($"{p.Actor.Name} {p.Tile.X} {p.Tile.Y} {p.Dir}");
            }

            string music = string.IsNullOrWhiteSpace(script.Music) ? "none" : script.Music.Trim();
            var cmds = new List<string>
            {
                music,
                $"{camX} {camY}",
                string.Join(" ", actorDefs),
                "ignoreEventTileOffset",
                "skippable",
                "pause 450"
            };

            string playerName = player.displayName ?? player.Name ?? "Farmer";
            string farmName = Game1.MasterPlayer?.farmName.Value ?? player.farmName.Value ?? "the farm";

            static string ApplySceneTokens(string text, string pName, string fName)
            {
                return text.Replace("{farmer}", pName, StringComparison.OrdinalIgnoreCase)
                           .Replace("{farm}", fName, StringComparison.OrdinalIgnoreCase);
            }

            // 4. Líneas del guion (sin 'move' para evitar congelamientos)
            int spokenLines = 0;
            foreach (var line in script.Lines)
            {
                NPC? speaker = ResolveSpeaker(line.Speaker, placedActors);
                if (speaker == null) continue;

                if (line.Dir.HasValue)
                    cmds.Add($"faceDirection {speaker.Name} {Math.Clamp(line.Dir.Value, 0, 3)}");
                if (!string.IsNullOrWhiteSpace(line.Sound))
                    cmds.Add($"playSound {line.Sound.Trim()}");
                if (line.Jump == true)
                    cmds.Add($"jump {speaker.Name}");
                if (line.ShakeMs.HasValue && line.ShakeMs.Value > 0)
                    cmds.Add($"shake {speaker.Name} {Math.Clamp(line.ShakeMs.Value, 100, 1500)}");
                if (line.Emote.HasValue)
                    cmds.Add($"emote {speaker.Name} {line.Emote.Value}");

                string rawText = useEs ? (string.IsNullOrWhiteSpace(line.Es) ? line.En : line.Es)
                                       : (string.IsNullOrWhiteSpace(line.En) ? line.Es : line.En);
                rawText = ApplySceneTokens(rawText, playerName, farmName);
                string cleanText = SanitizeEventText(rawText);
                cmds.Add($"speak {speaker.Name} \"{cleanText}\"");
                spokenLines++;

                if (line.PauseMs.HasValue && line.PauseMs.Value > 0)
                    cmds.Add($"pause {Math.Clamp(line.PauseMs.Value, 100, 1500)}");
            }

            if (spokenLines == 0)
            {
                error = "No resolved speaker lines in script.";
                return false;
            }

            // 5. Bloque opcional donde los aldeanos notan al granjero y le hacen quickQuestion
            if (script.NoticeFarmer && script.FarmerNotice != null)
            {
                var fn = script.FarmerNotice;
                NPC? noticer = ResolveSpeaker(fn.Noticer, placedActors) ?? placedActors[0].Actor;
                var noticerEntry = placedActors.First(p => ReferenceEquals(p.Actor, noticer));
                int dirToFarmer = GetDirectionBetween(noticerEntry.Tile, farmerTile);

                cmds.Add("pause 250");
                cmds.Add($"faceDirection {noticer.Name} {dirToFarmer}");
                cmds.Add($"emote {noticer.Name} 16");

                string introRaw = useEs ? (string.IsNullOrWhiteSpace(fn.IntroEs) ? fn.IntroEn : fn.IntroEs)
                                        : (string.IsNullOrWhiteSpace(fn.IntroEn) ? fn.IntroEs : fn.IntroEn);
                introRaw = ApplySceneTokens(introRaw, playerName, farmName);
                cmds.Add($"speak {noticer.Name} \"{SanitizeEventText(introRaw)}\"");

                string opt1Src = useEs ? (string.IsNullOrWhiteSpace(fn.Opt1Es) ? fn.Opt1En : fn.Opt1Es)
                                       : (string.IsNullOrWhiteSpace(fn.Opt1En) ? fn.Opt1Es : fn.Opt1En);
                string opt2Src = useEs ? (string.IsNullOrWhiteSpace(fn.Opt2Es) ? fn.Opt2En : fn.Opt2Es)
                                       : (string.IsNullOrWhiteSpace(fn.Opt2En) ? fn.Opt2Es : fn.Opt2En);
                string opt1 = SanitizeEventText(ApplySceneTokens(opt1Src, playerName, farmName));
                string opt2 = SanitizeEventText(ApplySceneTokens(opt2Src, playerName, farmName));

                NPC rep1Npc = ResolveSpeaker(fn.Reply1Speaker, placedActors) ?? noticer;
                NPC rep2Npc = ResolveSpeaker(fn.Reply2Speaker, placedActors) ?? noticer;

                string rep1Src = useEs ? (string.IsNullOrWhiteSpace(fn.Reply1Es) ? fn.Reply1En : fn.Reply1Es)
                                       : (string.IsNullOrWhiteSpace(fn.Reply1En) ? fn.Reply1Es : fn.Reply1En);
                string rep2Src = useEs ? (string.IsNullOrWhiteSpace(fn.Reply2Es) ? fn.Reply2En : fn.Reply2Es)
                                       : (string.IsNullOrWhiteSpace(fn.Reply2En) ? fn.Reply2Es : fn.Reply2En);
                string rep1Raw = ApplySceneTokens(rep1Src, playerName, farmName);
                string rep2Raw = ApplySceneTokens(rep2Src, playerName, farmName);

                int rep1Emote = fn.Reply1Emote ?? 32;
                int rep2Emote = fn.Reply2Emote ?? 32;

                cmds.Add(
                    $"quickQuestion #{opt1}#{opt2}" +
                    $"(break)speak {rep1Npc.Name} \"{SanitizeEventText(rep1Raw)}$h\"\\emote {rep1Npc.Name} {rep1Emote}" +
                    $"(break)speak {rep2Npc.Name} \"{SanitizeEventText(rep2Raw)}$h\"\\emote {rep2Npc.Name} {rep2Emote}"
                );
            }

            cmds.Add("pause 350");
            cmds.Add("end");

            string eventScript = string.Join("/", cmds);
            var evt = new Event(eventScript, null, "-1", player);
            evt.markEventSeen = false;
            evt.ignoreTileOffsets = true;

            var actorListSnapshot = placedActors.Select(p => p.Actor).ToList();
            evt.onEventFinished = () => OnAmbientEventFinished(player, scene, script, actorListSnapshot);

            loc.startEvent(evt);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to start ambient event '{scene.Id}': {ex.Message}";
            _monitor?.Log($"[AmbientCutscenes] {error}", LogLevel.Warn);
            return false;
        }
    }

    private static void OnAmbientEventFinished(Farmer player, CutsceneSceneData scene, CutsceneScriptData script, List<NPC> actors)
    {
        try
        {
            Game1.player?.eventsSeen.Remove("-1");
            Game1.eventsSeenSinceLastLocationChange?.Remove("-1");

            if (script.NoticeFarmer && player != null)
            {
                foreach (var actor in actors)
                {
                    if (actor != null && actor.CanSocialize)
                    {
                        NpcActionExecutor.TryAwardDailyActivityFriendship(
                            player,
                            actor,
                            $"ambient_cutscene_{scene.Id}",
                            10,
                            10);
                    }
                }
            }
        }
        catch { }
    }

    private static NPC? ResolveSpeaker(string spec, List<(string RawSpec, NPC Actor, Point Tile, int Dir)> placedActors)
    {
        if (string.IsNullOrWhiteSpace(spec))
            return placedActors.Count > 0 ? placedActors[0].Actor : null;

        string[] aliases = spec.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string alias in aliases)
        {
            foreach (var p in placedActors)
            {
                if (string.Equals(p.Actor.Name, alias, StringComparison.OrdinalIgnoreCase))
                    return p.Actor;
            }
        }
        return null;
    }

    private static int GetDirectionBetween(Point from, Point to)
    {
        int dx = to.X - from.X;
        int dy = to.Y - from.Y;
        if (Math.Abs(dx) >= Math.Abs(dy))
            return dx >= 0 ? 1 : 3;
        return dy >= 0 ? 2 : 0;
    }

    private static Point FindFreeNearbyTile(GameLocation loc, Point origin, HashSet<Point> occupied, NPC? npc)
    {
        Point[] offsets =
        {
            new(1, 0), new(-1, 0), new(0, 1), new(0, -1),
            new(1, 1), new(-1, 1), new(1, -1), new(-1, -1),
            new(2, 0), new(-2, 0), new(0, 2), new(0, -2)
        };

        foreach (var off in offsets)
        {
            Point cand = new(origin.X + off.X, origin.Y + off.Y);
            if (occupied.Contains(cand)) continue;
            if (!loc.isTileOnMap(cand.X, cand.Y)) continue;

            if (NpcActionExecutor.Instance?.Navigator != null)
            {
                if (!NpcActionExecutor.Instance.Navigator.IsTileWalkable(loc, cand, npc))
                    continue;
            }
            else if (!loc.isTilePassable(new xTile.Dimensions.Location(cand.X, cand.Y), Game1.viewport))
            {
                continue;
            }

            var box = new Rectangle(cand.X * 64 + 8, cand.Y * 64 + 8, 48, 48);
            if (loc.isCollidingWithWarp(box, npc) != null)
                continue;

            return cand;
        }

        return origin;
    }

    private static string SanitizeEventText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "...";
        string clean = raw.Replace('"', '\'')
                          .Replace('/', ',')
                          .Replace('\\', ' ')
                          .Replace('#', ' ')
                          .Replace('^', ' ')
                          .Replace("$", string.Empty)
                          .Replace("(break)", string.Empty, StringComparison.OrdinalIgnoreCase)
                          .Trim();
        return string.IsNullOrWhiteSpace(clean) ? "..." : clean;
    }
}
