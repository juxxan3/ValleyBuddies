namespace ValleyBuddies.Systems.LivingEvents;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using ValleyBuddies.Execution;
using ValleyBuddies.Multiplayer;
using ValleyBuddies.Utilities;

public enum RelationshipBondTier
{
    ChildSafe,
    Friend,
    CloseFriend,
    Romance
}

/// <summary>
/// Motor Universal de Eventos Cinemáticos Nativos (StardewValley.Event):
/// Soporta 20 escenarios (Mapa + Clima + Hora), coreografía dinámica por escenario,
/// 97 cinemáticas personales interactivas vía CharacterVoicePackService + 6 arquetipos para mods custom,
/// 4 niveles de relación (Friend / CloseFriend / Romance-Spouse / ChildSafe),
/// anclaje adaptable (horizontal, vertical y adyacente 2-tiles, permitiendo alfombras) y Co-op seguro.
/// </summary>
public sealed class CinematicDirectorService
{
    public static CinematicDirectorService Instance { get; } = new();

    private static readonly string[] AllScenarioNames =
    {
        "museum", "beach", "forest", "mountain", "saloon", "town", "farm",
        "home", "spa", "greenhouse", "mines", "desert", "night_stars", "rainy_shelter", "scenic",
        "secret_woods", "island", "railroad", "sewer", "sunset_golden"
    };

    private static readonly HashSet<string> ValidScenes = new(AllScenarioNames, StringComparer.OrdinalIgnoreCase);

    private sealed class ScenarioQuestionEntry
    {
        public string Id { get; set; } = string.Empty;
        public string QuestionKey { get; set; } = string.Empty;
        public string Opt1Key { get; set; } = string.Empty;
        public string Opt2Key { get; set; } = string.Empty;
        public string Reply1Key { get; set; } = string.Empty;
        public string Reply2Key { get; set; } = string.Empty;
    }

    private sealed class ScenarioWeatherVariant
    {
        public string? Line1Key { get; set; }
        public List<ScenarioQuestionEntry>? Questions { get; set; }
    }

    private sealed class ScenarioWeatherPack
    {
        public string Scenario { get; set; } = string.Empty;
        public Dictionary<string, ScenarioWeatherVariant> Variants { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FarmerCinematicState
    {
        public HashSet<string> CompletedScenesToday { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FriendshipAwardedToday { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> VisitedScenarioLocations { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Queue<string> RecentQuestionHistory { get; } = new();
        public string? ActiveNpcName { get; set; }
    }

    private static readonly JsonSerializerOptions ScenarioJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly string[] ExteriorScenarioJsonNames = { "beach", "forest", "mountain", "town", "farm" };
    private readonly Dictionary<string, ScenarioWeatherPack> _scenarioPacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, FarmerCinematicState> _stateByFarmer = new();

    public static IReadOnlyList<string> SupportedScenarios => AllScenarioNames;
    public int LoadedScenarioPackCount => _scenarioPacks.Count;

    public void Initialize(IModHelper helper, IMonitor monitor)
    {
        _scenarioPacks.Clear();
        if (helper == null) return;

        string cinematicsDir = Path.Combine(helper.DirectoryPath, "assets", "cinematics");
        if (!Directory.Exists(cinematicsDir))
        {
            monitor?.Log($"[CinematicDirector] Cinematics folder not found: {cinematicsDir}", LogLevel.Trace);
            return;
        }

        foreach (string mapKey in ExteriorScenarioJsonNames)
        {
            string filePath = Path.Combine(cinematicsDir, $"{mapKey}.json");
            if (!File.Exists(filePath)) continue;

            try
            {
                string raw = File.ReadAllText(filePath);
                var pack = JsonSerializer.Deserialize<ScenarioWeatherPack>(raw, ScenarioJsonOpts);
                if (pack != null && pack.Variants != null && pack.Variants.Count > 0)
                {
                    string key = string.IsNullOrWhiteSpace(pack.Scenario) ? mapKey : pack.Scenario.Trim();
                    _scenarioPacks[key] = pack;
                }
            }
            catch (Exception ex)
            {
                monitor?.Log($"[CinematicDirector] Failed to load scenario JSON '{filePath}': {ex.Message}", LogLevel.Warn);
            }
        }

        monitor?.Log($"[CinematicDirector] Loaded {_scenarioPacks.Count} weather scenario packs.", LogLevel.Debug);
    }

    private FarmerCinematicState GetOrCreateFarmerState(long farmerId)
    {
        if (!_stateByFarmer.TryGetValue(farmerId, out var st))
        {
            st = new FarmerCinematicState();
            _stateByFarmer[farmerId] = st;
        }
        return st;
    }

    public void ClearForPlayer(long farmerId)
    {
        _stateByFarmer.Remove(farmerId);
    }

    public void ResetDaily()
    {
        foreach (var st in _stateByFarmer.Values)
        {
            st.CompletedScenesToday.Clear();
            st.FriendshipAwardedToday.Clear();
            st.ActiveNpcName = null;
        }
    }

    public void ResetAll()
    {
        _stateByFarmer.Clear();
    }

    public bool HasCompletedSceneToday(long farmerId, string npcName, string scene)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(scene)) return false;
        return _stateByFarmer.TryGetValue(farmerId, out var st)
            && st.CompletedScenesToday.Contains(BuildSceneDailyKey(farmerId, npcName, scene));
    }

    public static string ResolveSceneType(GameLocation? loc)
    {
        if (loc == null) return "scenic";

        string name = loc.NameOrUniqueName ?? loc.Name ?? string.Empty;
        string baseName = loc.Name ?? string.Empty;
        bool isDesertLoc = loc is StardewValley.Locations.Desert ||
                           name.Contains("Desert", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Calico", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Oasis", StringComparison.OrdinalIgnoreCase);

        bool isIslandLoc = name.Contains("Island", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Resort", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("Lagoon", StringComparison.OrdinalIgnoreCase);

        // 1. Escena acogedora bajo la lluvia (exteriores con lluvia, salvo desierto)
        if (loc.IsOutdoors && loc.IsRainingHere() && !isDesertLoc)
        {
            return "rainy_shelter";
        }

        // 2. Mirando las estrellas de noche al aire libre (>= 19:30, sin lluvia)
        if (loc.IsOutdoors && !loc.IsRainingHere() && Game1.timeOfDay >= 1930)
        {
            return "night_stars";
        }

        // 3. Atardecer dorado al aire libre (17:00 - 19:29, sin lluvia, fuera de Desierto/Isla)
        if (loc.IsOutdoors && !loc.IsRainingHere() &&
            Game1.timeOfDay >= 1700 && Game1.timeOfDay < 1930 &&
            !isDesertLoc && !isIslandLoc)
        {
            return "sunset_golden";
        }

        // 4. Hogar (FarmHouse / Cabin)
        if (loc is StardewValley.Locations.FarmHouse ||
            loc is StardewValley.Locations.Cabin ||
            name.Contains("FarmHouse", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Cabin", StringComparison.OrdinalIgnoreCase))
        {
            return "home";
        }

        // 5. Invernadero
        if (loc.IsGreenhouse || name.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase))
        {
            return "greenhouse";
        }

        // 6. Biblioteca / Museo
        if (loc is StardewValley.Locations.LibraryMuseum ||
            name.Contains("ArchaeologyHouse", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Museum", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Library", StringComparison.OrdinalIgnoreCase))
        {
            return "museum";
        }

        // 7. Aguas Termales / Spa
        if (name.Contains("BathHouse", StringComparison.OrdinalIgnoreCase))
        {
            return "spa";
        }

        // 8. Alcantarillas / Guarida sombría
        if (loc is StardewValley.Locations.Sewer ||
            name.Contains("Sewer", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("VoidDen", StringComparison.OrdinalIgnoreCase))
        {
            return "sewer";
        }

        // 9. Minas / Cuevas / Volcán
        if (loc is StardewValley.Locations.MineShaft ||
            loc is StardewValley.Locations.VolcanoDungeon ||
            baseName.Equals("Mine", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Mine", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Cave", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Cavern", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Volcano", StringComparison.OrdinalIgnoreCase))
        {
            return "mines";
        }

        // 10. Desierto / Oasis
        if (isDesertLoc)
        {
            return "desert";
        }

        // 11. Estación de Tren / Teleférico
        if (name.Equals("Railroad", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Railroad", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("TrainStation", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("CableCar", StringComparison.OrdinalIgnoreCase))
        {
            return "railroad";
        }

        // 12. Isla Jengibre / Resort tropical (prioridad antes de beach)
        if (isIslandLoc)
        {
            return "island";
        }

        // 13. Playa / Costa
        if (loc is StardewValley.Locations.Beach ||
            name.Contains("Beach", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Coast", StringComparison.OrdinalIgnoreCase))
        {
            return "beach";
        }

        // 14. Bosque Secreto / Arboleda mágica (prioridad antes de forest)
        if (loc is StardewValley.Locations.Woods ||
            name.Contains("Woods", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("SecretForest", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Grove", StringComparison.OrdinalIgnoreCase))
        {
            return "secret_woods";
        }

        // 15. Bosque / Viñedo / Naturaleza
        if (loc is StardewValley.Locations.Forest ||
            baseName.Equals("Backwoods", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Forest", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Vineyard", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Orchard", StringComparison.OrdinalIgnoreCase))
        {
            return "forest";
        }

        // 16. Montaña / Lago / Mirador / Ridge
        if (loc is StardewValley.Locations.Mountain ||
            name.Contains("Mountain", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Ridge", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Summit", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Cliff", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Falls", StringComparison.OrdinalIgnoreCase))
        {
            return "mountain";
        }

        // 17. Saloon / Cafés / Tabernas / Posadas
        if (baseName.Equals("Saloon", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Saloon", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Tavern", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Cafe", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Diner", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Inn", StringComparison.OrdinalIgnoreCase))
        {
            return "saloon";
        }

        // 18. Tu Granja al aire libre
        if (loc is Farm || name.Contains("Farm", StringComparison.OrdinalIgnoreCase))
        {
            return "farm";
        }

        // 19. Pueblo (Pelican Town / Ridgeside Village / Sunberry Village)
        if (loc is StardewValley.Locations.Town ||
            baseName.Equals("Town", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Town", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Village", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Plaza", StringComparison.OrdinalIgnoreCase))
        {
            return "town";
        }

        // 20. Respaldo universal para cualquier otro mapa custom
        return "scenic";
    }

    public static string NormalizeSceneType(string? sceneType, GameLocation? fallbackLoc = null)
    {
        if (!string.IsNullOrWhiteSpace(sceneType))
        {
            string trimmed = sceneType.Trim().ToLowerInvariant();
            if (ValidScenes.Contains(trimmed))
                return trimmed;
        }
        return ResolveSceneType(fallbackLoc);
    }

    /// <summary>
    /// Lanza la cinemática a petición desde el menú Hub de acompañante ("Compartir un momento especial").
    /// Limita a 1 vez al día por pareja NPC + escenario.
    /// </summary>
    public bool TryStartCinematicFromHub(NPC npc, Farmer farmer)
    {
        if (npc == null || farmer == null) return false;
        var loc = farmer.currentLocation ?? Game1.currentLocation;
        if (loc == null) return false;

        string scene = ResolveSceneType(loc);
        string dailyKey = BuildSceneDailyKey(farmer.UniqueMultiplayerID, npc.Name, scene);
        var farmerState = GetOrCreateFarmerState(farmer.UniqueMultiplayerID);

        if (farmerState.CompletedScenesToday.Contains(dailyKey))
        {
            string msg = I18n.Get("cinematic.already_shared_today", new
            {
                playerName = farmer.displayName ?? farmer.Name,
                npcName = npc.displayName
            });
            ModEntry.ConversationBridge.ShowCompanionBubble(npc, msg, 3500);
            return false;
        }

        return TryStartCinematic(loc, npc, farmer, scene, fromOutingInvite: false, ignoreDailyCooldown: false);
    }

    /// <summary>
    /// Inicia una cinemática nativa (o su respaldo con retratos expresivos si el mapa/estado actual lo requiere).
    /// </summary>
    public bool TryStartCinematic(
        GameLocation loc,
        NPC npc,
        Farmer owner,
        string? sceneType = null,
        bool fromOutingInvite = false,
        bool ignoreDailyCooldown = false,
        bool forcePersonalQuestion = false)
    {
        if (loc == null || npc == null || owner == null) return false;

        if (owner.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID)
            return false;

        // Seguridad crítica: esperar si el mapa sigue en fundido de transición o menú activo
        if (Game1.fadeToBlack || Game1.isWarping || Game1.eventUp || Game1.CurrentEvent != null ||
            loc.currentEvent != null || Game1.activeClickableMenu != null || Game1.dialogueUp || Utility.isFestivalDay())
        {
            return false;
        }

        string scene = NormalizeSceneType(sceneType, loc);
        string dailyKey = BuildSceneDailyKey(owner.UniqueMultiplayerID, npc.Name, scene);
        var farmerState = GetOrCreateFarmerState(owner.UniqueMultiplayerID);

        if (!ignoreDailyCooldown && farmerState.CompletedScenesToday.Contains(dailyKey))
            return false;

        if (string.IsNullOrWhiteSpace(npc.Name) || npc.Name.Any(char.IsWhiteSpace))
        {
            var tokens = BuildTokens(loc, npc, owner, scene);
            string cozyMsg = I18n.Get("cinematic.space_name_fallback", tokens);
            npc.Halt();
            npc.controller = null;
            npc.faceGeneralDirection(owner.Position);
            npc.doEmote(20);
            ModEntry.ConversationBridge.ShowCompanionBubble(npc, cozyMsg, 4200);
            CompleteCinematicCommon(loc, npc, owner, scene, fromOutingInvite);
            return true;
        }

        farmerState.ActiveNpcName = npc.Name;

        if (CanRunNativeEvent(loc, npc, owner, out Point anchor, out Point camTile, out Point partnerTile, out int farmerDir, out int npcDir, out int stepDx, out int stepDy, out bool hasStep))
        {
            try
            {
                string script = BuildNativeEventScript(loc, npc, owner, scene, anchor, camTile, partnerTile, farmerDir, npcDir, stepDx, stepDy, hasStep, fromOutingInvite, forcePersonalQuestion);
                var evt = new Event(script, null, "-1", owner);
                evt.markEventSeen = false;
                evt.ignoreTileOffsets = true;
                evt.onEventFinished = () => OnCinematicFinished(evt, loc, npc, owner, scene, fromOutingInvite);

                loc.startEvent(evt);
                ModEntry.ModMonitor?.Log($"[CinematicDirector] Started native '{scene}' event with {npc.Name} at {loc.NameOrUniqueName} (anchor={anchor}, partner={partnerTile}, step={hasStep}, personal={forcePersonalQuestion}).", LogLevel.Debug);
                return true;
            }
            catch (Exception ex)
            {
                ModEntry.ModMonitor?.Log($"[CinematicDirector] Native event start failed for {npc.Name} ({scene}): {ex.Message}. Falling back to portraits.", LogLevel.Warn);
            }
        }
        else
        {
            ModEntry.ModMonitor?.Log($"[CinematicDirector] Native anchor unavailable for {npc.Name} in '{loc.NameOrUniqueName}' ({scene}); running PortraitFallbackSequence.", LogLevel.Debug);
        }

        return RunPortraitFallbackSequence(loc, npc, owner, scene, fromOutingInvite, forcePersonalQuestion);
    }

    public bool TestCutscene(string? npcName, string? rawScene, out string statusMsg)
    {
        if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
        {
            statusMsg = "You must be in-game to run vb_test_cutscene.";
            return false;
        }

        Farmer owner = Game1.player;
        GameLocation loc = owner.currentLocation;

        NPC? target = null;
        if (!string.IsNullOrWhiteSpace(npcName))
        {
            target = Game1.getCharacterFromName(npcName);
        }

        if (target == null)
        {
            string? followerName = NpcActionExecutor.Instance.GetActiveFollowerName();
            if (!string.IsNullOrEmpty(followerName))
                target = Game1.getCharacterFromName(followerName);
        }

        if (target == null && !string.IsNullOrWhiteSpace(owner.spouse))
        {
            target = Game1.getCharacterFromName(owner.spouse);
        }

        if (target == null)
        {
            target = loc.characters.FirstOrDefault(c => c.IsVillager) ?? Game1.getCharacterFromName("Penny");
        }

        if (target == null)
        {
            statusMsg = "Could not find a suitable NPC for cutscene test.";
            return false;
        }

        bool forcePersonal = string.Equals(rawScene?.Trim(), "personal", StringComparison.OrdinalIgnoreCase);
        string scene = forcePersonal ? ResolveSceneType(loc) : NormalizeSceneType(rawScene, loc);
        bool started = TryStartCinematic(loc, target, owner, scene, fromOutingInvite: false, ignoreDailyCooldown: true, forcePersonalQuestion: forcePersonal);
        if (started)
        {
            statusMsg = $"Started '{scene}'{(forcePersonal ? " [personal]" : "")} cinematic with {target.Name} in {loc.NameOrUniqueName}.";
            return true;
        }

        statusMsg = $"Could not start cutscene '{scene}' with {target.Name} right now (busy fade/menu/event/festival).";
        return false;
    }

    public void HandleRemoteCinematicCompletedOnHost(CinematicCompletedMessage msg)
    {
        if (!Context.IsMainPlayer || msg == null || string.IsNullOrWhiteSpace(msg.NpcName)) return;

        var npc = Game1.getCharacterFromName(msg.NpcName);
        var owner = Game1.GetPlayer(msg.FarmerId, onlyOnline: false);
        if (npc == null || owner == null) return;

        ApplyPostCinematicCompletionOnHost(npc, owner, msg.SceneType, msg.FromOutingInvite);
    }

    private void OnCinematicFinished(Event evt, GameLocation loc, NPC npc, Farmer owner, string scene, bool fromOutingInvite)
    {
        try
        {
            if (!string.IsNullOrEmpty(evt.id))
            {
                Game1.player.eventsSeen.Remove(evt.id);
                Game1.eventsSeenSinceLastLocationChange.Remove(evt.id);
            }
            Game1.player.eventsSeen.Remove("-1");
            Game1.eventsSeenSinceLastLocationChange.Remove("-1");
        }
        catch { }

        CompleteCinematicCommon(loc, npc, owner, scene, fromOutingInvite);
    }

    private void CompleteCinematicCommon(GameLocation? loc, NPC npc, Farmer owner, string scene, bool fromOutingInvite)
    {
        string dailyKey = BuildSceneDailyKey(owner.UniqueMultiplayerID, npc.Name, scene);
        var farmerState = GetOrCreateFarmerState(owner.UniqueMultiplayerID);
        farmerState.CompletedScenesToday.Add(dailyKey);
        string visitKey = ResolveBaseExteriorScenario(loc, scene) ?? scene;
        farmerState.VisitedScenarioLocations.Add(visitKey);
        farmerState.ActiveNpcName = null;

        if (Context.IsMainPlayer)
        {
            ApplyPostCinematicCompletionOnHost(npc, owner, scene, fromOutingInvite);
        }
        else
        {
            ModEntry.MultiplayerSync?.SendCinematicCompletedToHost(new CinematicCompletedMessage
            {
                FarmerId = owner.UniqueMultiplayerID,
                NpcName = npc.Name,
                SceneType = scene,
                FromOutingInvite = fromOutingInvite
            });
        }
    }

    private void ApplyPostCinematicCompletionOnHost(NPC npc, Farmer owner, string scene, bool fromOutingInvite)
    {
        string rewardKey = $"{owner.UniqueMultiplayerID}_{npc.Name}";
        var farmerState = GetOrCreateFarmerState(owner.UniqueMultiplayerID);
        farmerState.CompletedScenesToday.Add(BuildSceneDailyKey(owner.UniqueMultiplayerID, npc.Name, scene));
        if (farmerState.FriendshipAwardedToday.Add(rewardKey))
        {
            owner.changeFriendship(40, npc);
        }

        NpcActionExecutor.Instance.RecordMemory(npc.Name, $"We shared a memorable moment together during our {scene} outing.");

        if (fromOutingInvite)
        {
            NpcOutingInvitationService.Instance.MarkOutingCompletedFromCinematic(npc.Name);
        }

        if ((fromOutingInvite || NpcActionExecutor.Instance.IsManagedCompanion(npc.Name))
            && !NpcActionExecutor.Instance.IsNpcInOvernightAnchorState(npc.Name))
        {
            NpcActionExecutor.Instance.StartFollowDirectly(npc, owner);
        }
    }

    private bool CanRunNativeEvent(
        GameLocation loc,
        NPC npc,
        Farmer owner,
        out Point anchor,
        out Point camTile,
        out Point partnerTile,
        out int farmerDir,
        out int npcDir,
        out int stepDx,
        out int stepDy,
        out bool hasStep)
    {
        anchor = Point.Zero;
        camTile = Point.Zero;
        partnerTile = Point.Zero;
        farmerDir = 1;
        npcDir = 3;
        stepDx = 0;
        stepDy = 0;
        hasStep = false;

        if (string.IsNullOrWhiteSpace(npc.Name) ||
            npc.Name.Contains(' ') ||
            npc.Name.Contains('/') ||
            npc.Name.Contains('"') ||
            npc.Name.Contains('#'))
        {
            return false;
        }

        try
        {
            if (npc.Sprite?.Texture == null)
            {
                return false;
            }

            string texName = NPC.getTextureNameForCharacter(npc.Name);
            if (!Game1.content.DoesAssetExist<Texture2D>("Characters\\" + texName))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        return TryFindCinematicAnchor(loc, owner.TilePoint, npc, out anchor, out camTile, out partnerTile, out farmerDir, out npcDir, out stepDx, out stepDy, out hasStep);
    }

    private static bool TryFindCinematicAnchor(
        GameLocation loc,
        Point center,
        NPC npc,
        out Point anchor,
        out Point camTile,
        out Point partnerTile,
        out int farmerDir,
        out int npcDir,
        out int stepDx,
        out int stepDy,
        out bool hasStep)
    {
        anchor = center;
        camTile = center;
        partnerTile = center;
        farmerDir = 1;
        npcDir = 3;
        stepDx = 0;
        stepDy = 0;
        hasStep = false;

        int edgeMarginX = loc.IsOutdoors ? 2 : 1;
        int edgeMarginY = loc.IsOutdoors ? 2 : 2;
        int mapW = loc.map?.Layers != null && loc.map.Layers.Count > 0 ? loc.map.Layers[0].LayerWidth : 999;
        int mapH = loc.map?.Layers != null && loc.map.Layers.Count > 0 ? loc.map.Layers[0].LayerHeight : 999;

        bool IsClearTile(Point pt)
        {
            if (pt.X < edgeMarginX || pt.Y < edgeMarginY) return false;
            if (pt.X >= mapW - edgeMarginX || pt.Y >= mapH - edgeMarginY) return false;

            if (!NpcActionExecutor.Instance.Navigator.IsTileWalkable(loc, pt, npc))
                return false;

            var tileVec = pt.ToVector2();
            if (loc.objects != null && loc.objects.ContainsKey(tileVec))
                return false;

            try
            {
                var furn = loc.GetFurnitureAt(tileVec);
                if (furn != null && furn.furniture_type.Value != StardewValley.Objects.Furniture.rug)
                    return false;
            }
            catch { }

            var box = new Rectangle(pt.X * 64 + 8, pt.Y * 64 + 8, 48, 48);
            if (loc.isCollidingWithWarp(box, npc) != null)
                return false;

            return true;
        }

        // Fase 1: buscar tríos con paso de acercamiento (Horizontal X±2 y Vertical Y±2) dentro de radio 12
        for (int radius = 0; radius <= 12; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;

                    Point cand = new Point(center.X + dx, center.Y + dy);
                    if (!IsClearTile(cand)) continue;

                    // 1A. Horizontal derecha: farmer(X), step(X+1), npc(X+2)
                    Point r1 = new Point(cand.X + 1, cand.Y);
                    Point r2 = new Point(cand.X + 2, cand.Y);
                    if (IsClearTile(r1) && IsClearTile(r2))
                    {
                        anchor = cand; camTile = r1; partnerTile = r2;
                        farmerDir = 1; npcDir = 3; stepDx = -1; stepDy = 0; hasStep = true;
                        return true;
                    }

                    // 1B. Horizontal izquierda: npc(X-2), step(X-1), farmer(X)
                    Point l1 = new Point(cand.X - 1, cand.Y);
                    Point l2 = new Point(cand.X - 2, cand.Y);
                    if (IsClearTile(l1) && IsClearTile(l2))
                    {
                        anchor = cand; camTile = l1; partnerTile = l2;
                        farmerDir = 3; npcDir = 1; stepDx = 1; stepDy = 0; hasStep = true;
                        return true;
                    }

                    // 1C. Vertical arriba: npc(Y-2), step(Y-1), farmer(Y)
                    Point u1 = new Point(cand.X, cand.Y - 1);
                    Point u2 = new Point(cand.X, cand.Y - 2);
                    if (IsClearTile(u1) && IsClearTile(u2))
                    {
                        anchor = cand; camTile = u1; partnerTile = u2;
                        farmerDir = 0; npcDir = 2; stepDx = 0; stepDy = 1; hasStep = true;
                        return true;
                    }

                    // 1D. Vertical abajo: farmer(Y), step(Y+1), npc(Y+2)
                    Point d1 = new Point(cand.X, cand.Y + 1);
                    Point d2 = new Point(cand.X, cand.Y + 2);
                    if (IsClearTile(d1) && IsClearTile(d2))
                    {
                        anchor = cand; camTile = d1; partnerTile = d2;
                        farmerDir = 2; npcDir = 0; stepDx = 0; stepDy = -1; hasStep = true;
                        return true;
                    }
                }
            }
        }

        // Fase 2: Parejas adyacentes directas (2 casillas) para pasillos/interiores muy estrechos sin move
        (int ox, int oy, int fDir, int nDir)[] adjDirs =
        {
            (1, 0, 1, 3),
            (-1, 0, 3, 1),
            (0, -1, 0, 2),
            (0, 1, 2, 0)
        };

        for (int radius = 0; radius <= 12; radius++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;

                    Point cand = new Point(center.X + dx, center.Y + dy);
                    if (!IsClearTile(cand)) continue;

                    foreach (var (ox, oy, fDir, nDir) in adjDirs)
                    {
                        Point adj = new Point(cand.X + ox, cand.Y + oy);
                        if (IsClearTile(adj))
                        {
                            anchor = cand;
                            camTile = cand;
                            partnerTile = adj;
                            farmerDir = fDir;
                            npcDir = nDir;
                            stepDx = 0;
                            stepDy = 0;
                            hasStep = false;
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private string BuildNativeEventScript(
        GameLocation loc,
        NPC npc,
        Farmer owner,
        string scene,
        Point anchor,
        Point camTile,
        Point partnerTile,
        int farmerDir,
        int npcDir,
        int stepDx,
        int stepDy,
        bool hasStep,
        bool fromOutingInvite,
        bool forcePersonalQuestion)
    {
        var tokens = BuildTokens(loc, npc, owner, scene);
        var (tier, isSpouse) = ResolveRelationshipTier(owner, npc);

        string musicTrack = ResolveMusicForScene(loc, scene);
        bool isScenicLook = scene is "beach" or "island" or "mountain" or "night_stars" or "sunset_golden" or "farm" or "desert" or "secret_woods" or "scenic";
        bool isCozyIndoor = scene is "museum" or "home" or "greenhouse" or "saloon" or "spa";

        int scenicLookDir = (scene == "beach" || scene == "island" || scene == "farm" || scene == "desert") ? 2 : 0;
        int initFarmerDir = isScenicLook ? scenicLookDir : farmerDir;
        int initNpcDir = isScenicLook ? scenicLookDir : npcDir;

        string line1 = SanitizeEventText(ResolveScenarioOpeningLine(loc, scene, tokens)) + "$h";
        string personalLine = SanitizeEventText(GetLayerPersonalityLine(npc, owner, tokens));
        string personalTag = tier == RelationshipBondTier.Romance ? "$l" : "$h";
        string? romanceExtra = tier == RelationshipBondTier.Romance ? GetRomanceExtraLine(npc, owner) : null;

        var (qPromptRaw, opt1Raw, opt2Raw, rep1Raw, rep2Raw) = ResolveInteractiveQuestionData(loc, npc, owner, scene, forcePersonalQuestion, tokens);
        string qPrompt = SanitizeEventText(qPromptRaw);
        string opt1 = SanitizeEventText(opt1Raw);
        string opt2 = SanitizeEventText(opt2Raw);
        string rep1 = SanitizeEventText(rep1Raw) + "$h";
        string rep2 = SanitizeEventText(rep2Raw) + (tier == RelationshipBondTier.Romance ? "$l" : "$h");

        string closing = SanitizeEventText(GetClosingLine(npc, owner, scene, tier, isSpouse, fromOutingInvite, tokens));
        string closingTag = tier == RelationshipBondTier.Romance ? "$l" : "$h";
        string qTag = (scene is "mines" or "sewer" or "rainy_shelter") ? "$u" : "$h";

        int rep2Emote = scene is "mines" or "sewer" or "rainy_shelter" ? 56 : 32;

        var cmds = new List<string>
        {
            musicTrack,
            $"{camTile.X} {camTile.Y}",
            $"farmer {anchor.X} {anchor.Y} {initFarmerDir} {npc.Name} {partnerTile.X} {partnerTile.Y} {initNpcDir}",
            "ignoreEventTileOffset",
            "skippable"
        };

        // Coreografía diferenciada por familia de escenario
        if (isScenicLook)
        {
            cmds.Add("pause 600");
            cmds.Add($"emote {npc.Name} 32");
            cmds.Add($"speak {npc.Name} \"{line1}\"");
            cmds.Add("pause 300");
            if (hasStep)
            {
                cmds.Add($"move {npc.Name} {stepDx} {stepDy} {npcDir}");
            }
            else
            {
                cmds.Add($"faceDirection {npc.Name} {npcDir}");
            }
            cmds.Add($"faceDirection farmer {farmerDir}");
        }
        else if (isCozyIndoor)
        {
            cmds.Add("pause 400");
            if (hasStep)
            {
                cmds.Add($"move {npc.Name} {stepDx} {stepDy} {npcDir}");
            }
            cmds.Add($"faceDirection farmer {farmerDir}");
            cmds.Add($"emote {npc.Name} 32");
            cmds.Add($"speak {npc.Name} \"{line1}\"");
        }
        else
        {
            // Aventura / Clima / Especiales (mines, forest, rainy_shelter, town, railroad, sewer)
            int glanceDir = (npcDir + 1) % 4;
            cmds.Add("pause 300");
            cmds.Add($"faceDirection {npc.Name} {glanceDir}");
            cmds.Add("pause 260");
            cmds.Add($"faceDirection {npc.Name} {npcDir}");
            cmds.Add($"emote {npc.Name} {(scene == "rainy_shelter" ? 28 : 16)}");
            if (hasStep)
            {
                cmds.Add($"move {npc.Name} {stepDx} {stepDy} {npcDir}");
            }
            cmds.Add($"faceDirection farmer {farmerDir}");
            cmds.Add($"speak {npc.Name} \"{line1}\"");
        }

        // Animación nativa SOLO de pie validada (sin animaciones de silla/mueble que sienten en el aire)
        if (NpcAnimationService.TryGetValidatedAnimationFrames(npc, GetCandidateAnimations(npc, scene), out int[] frames, standingOnly: true))
        {
            cmds.Add($"animate {npc.Name} false true 160 {string.Join(" ", frames)}");
            cmds.Add("pause 1200");
            cmds.Add($"stopAnimation {npc.Name}");
            cmds.Add($"faceDirection {npc.Name} {npcDir}");
        }
        else
        {
            int gestureEmote = tier == RelationshipBondTier.Romance ? 20 : (scene == "saloon" ? 32 : 56);
            cmds.Add($"emote {npc.Name} {gestureEmote}");
            cmds.Add("pause 350");
        }

        cmds.Add($"speak {npc.Name} \"{personalLine}{personalTag}\"");

        if (!string.IsNullOrWhiteSpace(romanceExtra))
        {
            cmds.Add($"emote {npc.Name} 20");
            cmds.Add($"speak {npc.Name} \"{SanitizeEventText(romanceExtra)}$l\"");
        }

        // Pregunta hablada primero con retrato y luego quickQuestion de EXACTAMENTE 2 opciones = 2 ramas
        cmds.Add($"speak {npc.Name} \"{qPrompt}{qTag}\"");
        cmds.Add($"quickQuestion #{opt1}#{opt2}(break)speak {npc.Name} \"{rep1}\"\\emote {npc.Name} 20(break)speak {npc.Name} \"{rep2}\"\\emote {npc.Name} {rep2Emote}");
        cmds.Add($"speak {npc.Name} \"{closing}{closingTag}\"");
        cmds.Add($"emote {npc.Name} {(tier == RelationshipBondTier.ChildSafe ? 32 : 20)}");
        cmds.Add("pause 400");
        cmds.Add("end");

        return string.Join("/", cmds);
    }

    private bool RunPortraitFallbackSequence(GameLocation loc, NPC npc, Farmer owner, string scene, bool fromOutingInvite, bool forcePersonalQuestion)
    {
        var tokens = BuildTokens(loc, npc, owner, scene);
        var (tier, isSpouse) = ResolveRelationshipTier(owner, npc);

        string line1 = ResolveScenarioOpeningLine(loc, scene, tokens);
        if (string.IsNullOrWhiteSpace(line1)) line1 = "...";
        string line2 = GetLayerPersonalityLine(npc, owner, tokens);
        string? romanceExtra = tier == RelationshipBondTier.Romance ? GetRomanceExtraLine(npc, owner) : null;
        if (!string.IsNullOrWhiteSpace(romanceExtra))
        {
            line2 = string.IsNullOrWhiteSpace(line2) ? romanceExtra : $"{line2} {romanceExtra}";
        }
        if (string.IsNullOrWhiteSpace(line2)) line2 = "...";

        string line2Tag = tier == RelationshipBondTier.Romance ? "$l" : "$h";
        var (qPrompt, opt1, opt2, rep1, rep2) = ResolveInteractiveQuestionData(loc, npc, owner, scene, forcePersonalQuestion, tokens);
        string closing = GetClosingLine(npc, owner, scene, tier, isSpouse, fromOutingInvite, tokens);
        if (string.IsNullOrWhiteSpace(closing)) closing = "...";
        string closingTag = tier == RelationshipBondTier.Romance ? "$l" : "$h";

        ModEntry.ModMonitor?.Log($"[CinematicDirector] Launching portrait fallback '{scene}' with {npc.Name} in '{loc.NameOrUniqueName}'.", LogLevel.Debug);

        PortraitDialogueHelper.ShowPortraitDialogue(npc, line1, "$h", onClosed: () =>
        {
            PortraitDialogueHelper.ShowPortraitThenQuestion(
                npc,
                line2,
                line2Tag,
                qPrompt,
                new Response[] { new("opt1", opt1), new("opt2", opt2) },
                (who, ans) =>
                {
                    bool first = string.Equals(ans, "opt1", StringComparison.OrdinalIgnoreCase);
                    string repText = first ? rep1 : rep2;
                    string repTag = first ? "$h" : (tier == RelationshipBondTier.Romance ? "$l" : "$h");

                    PortraitDialogueHelper.ShowPortraitDialogue(npc, repText, repTag, onClosed: () =>
                    {
                        PortraitDialogueHelper.ShowPortraitDialogue(npc, closing, closingTag, onClosed: () =>
                        {
                            npc.doEmote(tier == RelationshipBondTier.ChildSafe ? 32 : 20);
                            CompleteCinematicCommon(loc, npc, owner, scene, fromOutingInvite);
                        });
                    });
                });
        });

        return true;
    }

    private static string? ResolveBaseExteriorScenario(GameLocation? loc, string scene)
    {
        if (loc != null)
        {
            if (loc is StardewValley.Locations.Beach ||
                (loc.NameOrUniqueName?.Equals("Beach", StringComparison.OrdinalIgnoreCase) == true))
                return "beach";
            if (loc is StardewValley.Locations.Woods)
                return null;
            if (loc is StardewValley.Locations.Forest ||
                (loc.Name?.Equals("Backwoods", StringComparison.OrdinalIgnoreCase) == true))
                return "forest";
            if (loc is StardewValley.Locations.Mountain)
                return "mountain";
            if (loc is StardewValley.Locations.Town)
                return "town";
            if (loc is Farm)
                return "farm";
        }

        return ExteriorScenarioJsonNames.Contains(scene, StringComparer.OrdinalIgnoreCase) ? scene.ToLowerInvariant() : null;
    }

    private static IReadOnlyList<string> ResolveWeatherVariantOrder(GameLocation? loc)
    {
        if (loc == null) return new[] { "day_clear", "default" };
        bool isRaining = loc.IsRainingHere() || loc.IsLightningHere();
        bool isSnowing = loc.IsSnowingHere();

        if (isRaining && Game1.timeOfDay >= 1900)
            return new[] { "rainy_night", "rainy", "default" };
        if (isSnowing)
            return new[] { "winter_snow", "day_clear", "default" };
        if (isRaining)
            return new[] { "rainy", "default" };
        return new[] { "day_clear", "default" };
    }

    private string ResolveScenarioOpeningLine(GameLocation? loc, string scene, object tokens)
    {
        string? exteriorKey = ResolveBaseExteriorScenario(loc, scene);
        if (exteriorKey != null && _scenarioPacks.TryGetValue(exteriorKey, out var pack))
        {
            var order = ResolveWeatherVariantOrder(loc);
            bool forceSpecialWeather = order[0] is "rainy_night" or "winter_snow";
            if (forceSpecialWeather || string.Equals(scene, exteriorKey, StringComparison.OrdinalIgnoreCase))
            {
                foreach (string variantName in order)
                {
                    if (pack.Variants.TryGetValue(variantName, out var variant) && !string.IsNullOrWhiteSpace(variant.Line1Key))
                    {
                        return I18n.Get(variant.Line1Key, tokens);
                    }
                }
            }
        }

        return I18n.Get($"cinematic.{scene}.line1", tokens);
    }

    private (string QPrompt, string Opt1, string Opt2, string Rep1, string Rep2) ResolveInteractiveQuestionData(
        GameLocation? loc,
        NPC npc,
        Farmer owner,
        string scene,
        bool forcePersonalQuestion,
        object tokens)
    {
        var farmerState = GetOrCreateFarmerState(owner.UniqueMultiplayerID);
        string visitKey = ResolveBaseExteriorScenario(loc, scene) ?? scene;
        bool isFirstVisit = !farmerState.VisitedScenarioLocations.Contains(visitKey);
        bool tryPersonal = forcePersonalQuestion || (!isFirstVisit && Game1.random.NextDouble() < 0.50);

        if (tryPersonal && CharacterVoicePackService.Instance.TryGetVoice(npc.Name, out var voice) && voice != null)
        {
            string? pq = CharacterVoicePackService.Instance.ResolveLocalized(voice.PersonalQuestion, owner, npc);
            string? po1 = CharacterVoicePackService.Instance.ResolveLocalized(voice.PersonalOpt1, owner, npc);
            string? po2 = CharacterVoicePackService.Instance.ResolveLocalized(voice.PersonalOpt2, owner, npc);
            string? pr1 = CharacterVoicePackService.Instance.ResolveLocalized(voice.PersonalReply1, owner, npc);
            string? pr2 = CharacterVoicePackService.Instance.ResolveLocalized(voice.PersonalReply2, owner, npc);

            bool hasCompletePersonal = !string.IsNullOrWhiteSpace(pq) &&
                                       !string.IsNullOrWhiteSpace(po1) &&
                                       !string.IsNullOrWhiteSpace(po2) &&
                                       !string.IsNullOrWhiteSpace(pr1) &&
                                       !string.IsNullOrWhiteSpace(pr2);

            if (hasCompletePersonal)
            {
                return (pq!, po1!, po2!, pr1!, pr2!);
            }
        }

        string? packKey = ResolveBaseExteriorScenario(loc, scene);
        if (packKey != null && _scenarioPacks.TryGetValue(packKey, out var pack))
        {
            var candidateQuestions = new List<ScenarioQuestionEntry>();
            var order = ResolveWeatherVariantOrder(loc);
            bool isSpecialWeather = order[0] is "rainy_night" or "winter_snow";

            foreach (string variantName in order)
            {
                if (pack.Variants.TryGetValue(variantName, out var variant) && variant.Questions != null)
                {
                    candidateQuestions.AddRange(variant.Questions.Where(q =>
                        q != null && !string.IsNullOrWhiteSpace(q.QuestionKey)));
                    if (isSpecialWeather && candidateQuestions.Count > 0 && variantName == order[0])
                        break;
                }
            }

            if (candidateQuestions.Count > 0)
            {
                var fresh = candidateQuestions
                    .Where(q => string.IsNullOrWhiteSpace(q.Id) || !farmerState.RecentQuestionHistory.Contains(q.Id))
                    .ToList();
                var pool = fresh.Count > 0 ? fresh : candidateQuestions;
                var picked = pool[Game1.random.Next(pool.Count)];

                if (!string.IsNullOrWhiteSpace(picked.Id))
                {
                    farmerState.RecentQuestionHistory.Enqueue(picked.Id);
                    while (farmerState.RecentQuestionHistory.Count > 6)
                        farmerState.RecentQuestionHistory.Dequeue();
                }

                return (
                    I18n.Get(picked.QuestionKey, tokens),
                    I18n.Get(picked.Opt1Key, tokens),
                    I18n.Get(picked.Opt2Key, tokens),
                    I18n.Get(picked.Reply1Key, tokens),
                    I18n.Get(picked.Reply2Key, tokens)
                );
            }
        }

        string fallbackQId = $"cinematic.{scene}.question";
        farmerState.RecentQuestionHistory.Enqueue(fallbackQId);
        while (farmerState.RecentQuestionHistory.Count > 6)
            farmerState.RecentQuestionHistory.Dequeue();

        return (
            I18n.Get($"cinematic.{scene}.question", tokens),
            I18n.Get($"cinematic.{scene}.opt1", tokens),
            I18n.Get($"cinematic.{scene}.opt2", tokens),
            I18n.Get($"cinematic.{scene}.reply1", tokens),
            I18n.Get($"cinematic.{scene}.reply2", tokens)
        );
    }

    private static object BuildTokens(GameLocation loc, NPC npc, Farmer owner, string scene)
    {
        string destDisplay = I18n.Get($"outing.loc.{scene}");
        return new
        {
            playerName = owner.displayName ?? owner.Name,
            npcName = npc.displayName,
            itemName = destDisplay
        };
    }

    private static string GetLayerPersonalityLine(NPC npc, Farmer owner, object tokens)
    {
        if (CharacterVoicePackService.Instance.TryGetVoice(npc.Name, out var voice) && voice != null)
        {
            int slot = Math.Abs(Game1.Date.TotalDays + Game1.timeOfDay / 100 + npc.Name.Length) % 3;
            Dictionary<string, string>? pickedMap = slot switch
            {
                0 => voice.Story,
                1 => voice.Story2,
                _ => voice.Opening
            };

            string? primary = CharacterVoicePackService.Instance.ResolveLocalized(pickedMap, owner, npc)
                           ?? CharacterVoicePackService.Instance.ResolveLocalized(voice.Story, owner, npc)
                           ?? CharacterVoicePackService.Instance.ResolveLocalized(voice.Opening, owner, npc);
            if (!string.IsNullOrWhiteSpace(primary))
            {
                return primary;
            }
        }

        string arch = NpcPersonalityDialogueService.GetPersonalityArchetype(npc);
        return I18n.Get($"cinematic.arch.{arch}.opening", tokens);
    }

    private static string? GetRomanceExtraLine(NPC npc, Farmer owner)
    {
        if (CharacterVoicePackService.Instance.TryGetVoice(npc.Name, out var voice) && voice != null)
        {
            return CharacterVoicePackService.Instance.ResolveLocalized(voice.Romance, owner, npc);
        }
        return null;
    }

    private static string GetClosingLine(NPC npc, Farmer owner, string scene, RelationshipBondTier tier, bool isSpouse, bool fromOutingInvite, object tokens)
    {
        if (tier == RelationshipBondTier.ChildSafe)
        {
            return I18n.Get("cinematic.closing.child", tokens);
        }

        if (tier == RelationshipBondTier.Romance)
        {
            return isSpouse
                ? I18n.Get("cinematic.closing.spouse", tokens)
                : I18n.Get("cinematic.closing.romance", tokens);
        }

        if (fromOutingInvite)
        {
            return I18n.Get("outing.thanks_complete", tokens);
        }

        if (CharacterVoicePackService.Instance.TryGetVoice(npc.Name, out var voice) && voice != null)
        {
            if (tier == RelationshipBondTier.CloseFriend && Game1.random.NextDouble() < 0.45)
            {
                return I18n.Get("cinematic.closing.close_friend", tokens);
            }

            string? voicedClosing = CharacterVoicePackService.Instance.ResolveLocalized(voice.Closing, owner, npc);
            if (!string.IsNullOrWhiteSpace(voicedClosing))
            {
                return voicedClosing;
            }
        }

        if (tier == RelationshipBondTier.CloseFriend)
        {
            return I18n.Get("cinematic.closing.close_friend", tokens);
        }

        string arch = NpcPersonalityDialogueService.GetPersonalityArchetype(npc);
        return I18n.Get($"cinematic.arch.{arch}.closing", tokens);
    }

    private static (RelationshipBondTier Tier, bool IsSpouse) ResolveRelationshipTier(Farmer owner, NPC npc)
    {
        if (owner == null || npc == null) return (RelationshipBondTier.Friend, false);

        if (CavePersonalityRegistry.ResolveProfile(npc).IsChildProtected || npc.Age == NPC.child)
            return (RelationshipBondTier.ChildSafe, false);

        bool isSpouse = string.Equals(owner.spouse, npc.Name, StringComparison.OrdinalIgnoreCase);
        bool hasFriendship = owner.friendshipData.TryGetValue(npc.Name, out var fd);
        if (isSpouse || (hasFriendship && fd != null && fd.IsDating()))
            return (RelationshipBondTier.Romance, isSpouse);

        int hearts = owner.getFriendshipHeartLevelForNPC(npc.Name);
        if (hearts >= 8)
            return (RelationshipBondTier.CloseFriend, false);

        return (RelationshipBondTier.Friend, false);
    }

    private static string ResolveMusicForScene(GameLocation loc, string scene)
    {
        return scene switch
        {
            "museum" => "libraryTheme",
            "beach" or "island" => loc.IsRainingHere() ? "continue" : "ocean",
            "saloon" => "Saloon1",
            _ => "continue"
        };
    }

    private static string[] GetCandidateAnimations(NPC npc, string scene)
    {
        var candidates = new List<string>();
        foreach (string anim in CharacterVoicePackService.Instance.GetPreferredAnimations(npc.Name))
        {
            if (!NpcAnimationService.IsSittingOrFurnitureAnimation(anim))
                candidates.Add(anim);
        }

        string n = npc.Name.ToLowerInvariant();
        switch (scene)
        {
            case "saloon":
                candidates.AddRange(new[] { $"{n}_laugh", $"{n}_drink", $"{n}_guitar", $"{n}_dance", $"{n}_wave", $"{n}_idle" });
                break;
            case "spa":
                candidates.AddRange(new[] { $"{n}_relax", $"{n}_laugh", $"{n}_idle" });
                break;
            case "beach":
            case "island":
            case "sunset_golden":
            case "night_stars":
                candidates.AddRange(new[] { $"{n}_laugh", $"{n}_photo", $"{n}_flute", $"{n}_guitar", $"{n}_wave", $"{n}_idle" });
                break;
            default:
                candidates.AddRange(new[] { $"{n}_laugh", $"{n}_dance", $"{n}_guitar", $"{n}_flute", $"{n}_photo", $"{n}_exercise", $"{n}_wave", $"{n}_idle" });
                break;
        }

        return candidates.Where(k => !NpcAnimationService.IsSittingOrFurnitureAnimation(k))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .ToArray();
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

    private static string BuildSceneDailyKey(long farmerId, string npcName, string scene)
    {
        return $"{farmerId}_{npcName}_{scene}";
    }
}
