using System;
using Microsoft.Xna.Framework;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using ValleyBuddies.Actions;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Bridge;
using ValleyBuddies.Config;
using ValleyBuddies.Execution;
using ValleyBuddies.Memory;
using ValleyBuddies.Systems;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;

namespace ValleyBuddies;

public sealed class ActionSyncMessage
{
    public string NpcName { get; set; } = string.Empty;
    public JobKind Kind { get; set; }
    public long FarmerId { get; set; }
    public string FarmerName { get; set; } = string.Empty;
    public string Extra { get; set; } = string.Empty;
}

public sealed class ModEntry : Mod
{
    private static IModHelper? _staticHelper;
    private static IManifest? _staticManifest;
    private static int _lastMenuTick = 0;

    private static ModEntry? _instance;
    private static ActionsConfig _config = null!;
    public static ActionsConfig Config => _config ?? new();
    public static ulong CurrentTick { get; private set; }
    public static Multiplayer.MultiplayerSyncService? MultiplayerSync => _instance?._multiplayerSync;
    public static IConversationBridge ConversationBridge { get; private set; } = new NullConversationBridge();
    public static IMonitor? ModMonitor => _instance?.Monitor;

    // ── Modo Dual Harmony (v4.0) ──
    public static bool IsAliveNpcsInstalled { get; private set; }
    public static bool IsOfflineModeActive =>
        _config?.ForceOfflineMode == true ||
        !IsAliveNpcsInstalled ||
        ConversationBridge?.Mode == ConversationBridgeMode.OfflineStandalone;
    public static bool IsAliveNpcsModeActive => !IsOfflineModeActive;

    private ActionRegistry _registry = null!;
    private ActionContext _context = null!;
    private ActionMemory _memory = null!;
    private Harmony _coreHarmony = null!;
    private Harmony _aliveNpcsHarmony = null!;
    private DynamicMailService _mailService = null!;
    private Multiplayer.MultiplayerSyncService _multiplayerSync = null!;
    private bool _active;
    private readonly ValleyAgencyApi _publicApi = new();

    public override object GetApi() => _publicApi;

    public override void Entry(IModHelper helper)
    {
        _instance = this;
        _staticHelper = helper;
        _staticManifest = ModManifest;

        _config = helper.ReadConfig<ActionsConfig>();
        _memory = new ActionMemory(helper, Monitor);
        _registry = new ActionRegistry();
        _context = new ActionContext(_config, Monitor, _memory, helper);
        _mailService = new DynamicMailService(helper);

        Utilities.I18n.Init(helper.Translation);
        ShopDutyDetector.Initialize(helper, Monitor);
        PostDialogueInviteMonitor.Initialize(helper, Monitor, _config);
        _multiplayerSync = new Multiplayer.MultiplayerSyncService(
            helper,
            ModManifest,
            CompanionSynergyManager.Instance,
            _mailService,
            dto =>
            {
                var farmhand = Game1.GetPlayer(dto.OwnerPlayerId, onlyOnline: true);
                string farmhandName = farmhand?.displayName ?? (farmhand?.Name ?? "Farmhand");
                Monitor.Log($"[Actions] Host received farmhand action '{dto.ActionName}' for {dto.NpcName} from {farmhandName} [{dto.OwnerPlayerId}] at '{dto.LocationName}'.", LogLevel.Info);

                bool canOverride = _config.AllowHostOverrideCompanion && dto.OwnerPlayerId == Game1.MasterPlayer.UniqueMultiplayerID;

                if (NpcActionExecutor.Instance.TryGetCompanionInfo(dto.NpcName, out var compInfo) && compInfo.FarmerId != 0 && compInfo.FarmerId != dto.OwnerPlayerId)
                {
                    var existingOwner = Game1.GetPlayer(compInfo.FarmerId, onlyOnline: true);
                    if (existingOwner != null && !canOverride)
                    {
                        string npcDisplay = Game1.getCharacterFromName(dto.NpcName)?.displayName ?? dto.NpcName;
                        string ownerDisplay = existingOwner.displayName ?? existingOwner.Name;
                        _multiplayerSync?.SendPrivateNotice(dto.OwnerPlayerId, Utilities.I18n.Get("mp.not_owner", new { npcName = npcDisplay, ownerName = ownerDisplay }));
                        return;
                    }
                }
                else if (NpcActionExecutor.Instance.HasActiveJob(dto.NpcName))
                {
                    var existing = NpcActionExecutor.Instance.GetAllActiveJobsSnapshot()
                        .FirstOrDefault(j => j.NpcName.Equals(dto.NpcName, StringComparison.OrdinalIgnoreCase));
                    if (existing != null && existing.OwnerPlayerId != dto.OwnerPlayerId)
                    {
                        var existingOwner = Game1.GetPlayer(existing.OwnerPlayerId, onlyOnline: true);
                        if (existingOwner != null && !canOverride)
                        {
                            string npcDisplay = Game1.getCharacterFromName(dto.NpcName)?.displayName ?? dto.NpcName;
                            string ownerDisplay = existingOwner.displayName ?? existingOwner.Name;
                            _multiplayerSync?.SendPrivateNotice(dto.OwnerPlayerId, Utilities.I18n.Get("mp.not_owner", new { npcName = npcDisplay, ownerName = ownerDisplay }));
                            return;
                        }
                    }
                }

                try
                {
                    System.Text.Json.JsonElement clonedPayload = default;
                    if (!string.IsNullOrWhiteSpace(dto.PayloadJson))
                    {
                        using var payloadDoc = System.Text.Json.JsonDocument.Parse(dto.PayloadJson);
                        clonedPayload = payloadDoc.RootElement.Clone();
                    }

                    var req = new Actions.ActionRequest
                    {
                        Action = dto.ActionName,
                        NpcName = dto.NpcName,
                        OwnerPlayerId = dto.OwnerPlayerId,
                        Payload = clonedPayload
                    };
                    if (_registry.TryGet(req.Action, out var act) && act != null)
                    {
                        int hearts = farmhand?.getFriendshipHeartLevelForNPC(dto.NpcName) ?? 0;
                        if (!act.IsEnabled(_context))
                        {
                            _multiplayerSync?.SendPrivateNotice(dto.OwnerPlayerId, Utilities.I18n.Get("mp.action.disabled", new { action = act.Name }));
                            return;
                        }
                        if (hearts < act.GetMinHearts(_context))
                        {
                            _multiplayerSync?.SendPrivateNotice(dto.OwnerPlayerId, Utilities.I18n.Get("mp.action.needs_hearts", new { npcName = dto.NpcName, hearts = act.GetMinHearts(_context), action = act.Name }));
                            return;
                        }
                        var validation = act.Validate(req, _context, hearts);
                        if (!validation.IsSuccess)
                        {
                            if (!string.IsNullOrWhiteSpace(validation.Message))
                                _multiplayerSync?.SendPrivateNotice(dto.OwnerPlayerId, validation.Message);
                            return;
                        }

                        var job = act.BuildJob(req, _context);
                        if (job != null)
                        {
                            job.OwnerPlayerId = dto.OwnerPlayerId;
                            job.FarmerName = farmhand?.Name ?? string.Empty;

                            if ((job.Kind is JobKind.VisitFarm or JobKind.HangOut) && !string.IsNullOrWhiteSpace(dto.LocationName))
                            {
                                job.Data["locationName"] = dto.LocationName;
                            }

                            if (job.Kind is JobKind.Follow or JobKind.VisitFarm or JobKind.HangOut)
                            {
                                NpcActionExecutor.Instance.SetCompanion(
                                    job.NpcName,
                                    dto.OwnerPlayerId,
                                    isVisitingFarm: job.Kind is JobKind.VisitFarm,
                                    isFollowing: job.Kind is JobKind.Follow,
                                    farmerName: job.FarmerName);
                            }

                            NpcActionExecutor.Instance.Enqueue(job);
                            string extra = job.Data.TryGetValue("locationName", out var loc) ? (loc?.ToString() ?? "") : "";
                            BroadcastJob(job.NpcName, job.Kind, dto.OwnerPlayerId, job.FarmerName, extra);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Monitor.Log($"Failed to process farmhand action {dto.ActionName}: {ex.Message}", LogLevel.Warn);
                }
            }
        );
        NpcActionExecutor.Instance.Configure(Monitor, _memory, _config);
        NpcActionExecutor.Instance.SetMultiplayerSync(_multiplayerSync);
        NpcActionExecutor.Instance.SetMailService(_mailService);
        ReactionInterceptor.MultiplayerSync = _multiplayerSync;

        // Register all actions
        RegisterActions();

        // Wire SMAPI events
        helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        helper.Events.GameLoop.SaveLoaded   += OnSaveLoaded;
        helper.Events.GameLoop.DayEnding    += OnDayEnding;
        helper.Events.GameLoop.DayStarted   += OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.GameLoop.Saving       += OnSaving;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        helper.Events.Player.Warped         += OnPlayerWarped;
        helper.Events.Input.ButtonPressed   += OnButtonPressed;
        helper.Events.Input.ButtonsChanged  += OnButtonsChanged;
        helper.Events.Content.AssetRequested += _mailService.OnAssetRequested;
        helper.Events.Content.AssetsInvalidated += (s, e) => ShopDutyDetector.OnAssetsInvalidated(e, Helper);
        helper.Events.Multiplayer.ModMessageReceived += OnModMessageReceived;
        helper.Events.Multiplayer.PeerConnected += _multiplayerSync.OnPeerConnected;
        helper.Events.Multiplayer.PeerDisconnected += _multiplayerSync.OnPeerDisconnected;
        helper.Events.Display.RenderedStep += OnRenderedStep;
        helper.Events.World.ObjectListChanged += (s, e) => NpcActionExecutor.InvalidateWalkabilityCache(e.Location.NameOrUniqueName);
        helper.Events.World.TerrainFeatureListChanged += (s, e) => NpcActionExecutor.InvalidateWalkabilityCache(e.Location.NameOrUniqueName);
        helper.Events.World.BuildingListChanged += (s, e) => NpcActionExecutor.InvalidateWalkabilityCache(e.Location.NameOrUniqueName);
        helper.Events.World.FurnitureListChanged += (s, e) => NpcActionExecutor.InvalidateWalkabilityCache(e.Location.NameOrUniqueName);

        ValleyBuddies.Systems.LivingEvents.ValleyPacingDirector.Instance.Initialize(helper);
        ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.Initialize(helper);
        RegisterConsoleCommands(helper);
    }

    // ─────────────────────────── Setup ────────────────────────────

    private void RegisterActions()
    {
        // 1. Movement & company (8)
        _registry.Register(new FollowPlayerAction());
        _registry.Register(new StopFollowingAction());
        _registry.Register(new ComeHereAction());
        _registry.Register(new GoToLocationAction());
        _registry.Register(new VisitFarmAction());
        _registry.Register(new SitOrRestAction());
        _registry.Register(new GuardOrWaitAction());
        _registry.Register(new EscortHomeAction());

        // 2. Farm help & resource gathering (10 existing + 6 new = 16)
        _registry.Register(new WaterCropsAction());
        _registry.Register(new HarvestCropsAction());
        _registry.Register(new HarvestForageAction());
        _registry.Register(new ClearDebrisAction());
        _registry.Register(new PetAnimalsAction());
        _registry.Register(new MineNearbyRockAction());
        _registry.Register(new ChopWoodAction());
        _registry.Register(new ChopTreesAction());
        _registry.Register(new TakeFromChestAction());
        _registry.Register(new FetchFromChestAction());
        _registry.Register(new FeedAnimalsAction());
        _registry.Register(new CollectAnimalProductsAction());
        _registry.Register(new WaterPetBowlAction());
        _registry.Register(new CollectMachinesAction());
        _registry.Register(new PlantSeedsAction());
        _registry.Register(new RepairFencesAction());

        // 3. Social & emotional (10 existing + 4 new = 14)
        _registry.Register(new GiveItemToPlayerAction());
        _registry.Register(new ShowEmoteAction());
        _registry.Register(new FacePlayerAction());
        _registry.Register(new DoAnimationAction());
        _registry.Register(new HugOrCelebrateAction());
        _registry.Register(new TakeBreakAction());
        _registry.Register(new TakePhotoAction());
        _registry.Register(new PicnicAction());
        _registry.Register(new AdmireFarmAction());
        _registry.Register(new PlayMusicAction());
        _registry.Register(new DanceTogetherAction());
        _registry.Register(new StargazeAction());
        _registry.Register(new SendLetterAction());
        _registry.Register(new MeetAtTimeAction());

        // 4. Adventure & Homestead (4 new)
        _registry.Register(new CombatAssistAction());
        _registry.Register(new FishTogetherAction());
        _registry.Register(new ScoutMinesAction());
        _registry.Register(new CookMealAction());
        _registry.Register(new SetupCampAction());
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        RegisterGmcm();

        if (!_config.EnableMod)
        {
            Monitor.Log("ValleyBuddies disabled by config.", LogLevel.Info);
            return;
        }

        _coreHarmony = new Harmony(ModManifest.UniqueID);
        _aliveNpcsHarmony = new Harmony(ModManifest.UniqueID + ".AliveNpcsBridge");
        IsAliveNpcsInstalled = Helper.ModRegistry.IsLoaded("Lucas.AliveNpcs");

        // Check coexistence warnings with other companion mods
        ShopDutyDetector.CheckCoexistenceWarnings(Helper, Monitor);

        // 1. ALWAYS ACTIVE Harmony Patches (Offline & Integrated)
        PreventObjectDestructionPatches.Apply(_coreHarmony, Monitor);
        TextAboveHeadPatches.Apply(_coreHarmony, Monitor);
        PathfindingSafetyPatches.Apply(_coreHarmony, Monitor);
        WarpTracePatches.Apply(_coreHarmony, Monitor);

        DialogueBypassPatches.Apply(_coreHarmony, Monitor);
        if (_config.BypassDailyLimitWhenFollowing)
        {
            TryHookDialogueManager();
        }

        // 2. Conversation Bridge (con soporte a runtime toggle via GMCM)
        ApplyConversationBridgeMode();

        // 3. Cargar paquetes modulares de voces cinemáticas, escenarios climáticos, charlas del pueblo y frases personales (assets/)
        ValleyBuddies.Systems.LivingEvents.CharacterVoicePackService.Instance.Initialize(Helper, Monitor);
        ValleyBuddies.Systems.LivingEvents.CinematicDirectorService.Instance.Initialize(Helper, Monitor);
        ValleyBuddies.Systems.AmbientTownBanterService.Instance.Initialize(Helper, Monitor);
        ValleyBuddies.Systems.CharacterAmbientChatterService.Instance.Initialize(Helper, Monitor);
        ValleyBuddies.Systems.PassByGreetingService.Instance.Initialize(Helper, Monitor);
        ValleyBuddies.Systems.LivingTownVignetteService.Instance.Initialize(Helper, Monitor);
        ValleyBuddies.Systems.LivingEvents.AmbientCutsceneDirectorService.Instance.Initialize(Helper, Monitor);

        _active = true;
        Monitor.Log($"ValleyBuddies active ✔ (Mode: {ConversationBridge.Mode})", LogLevel.Info);
    }

    // ─────────────────── Dual Harmony Bridge Switch ───────────────────

    /// <summary>
    /// Recrea el puente de conversación en función de ForceOfflineMode y AliveNpcs.
    /// Se llama al lanzar el juego Y cada vez que el usuario guarda config en GMCM.
    /// </summary>
    private void ApplyConversationBridgeMode()
    {
        bool useOffline = _config.ForceOfflineMode || !IsAliveNpcsInstalled;

        // Cierre limpio del puente anterior
        try { ConversationBridge?.Shutdown(); } catch { }

        // Desparchar sólo los parches del bridge de AliveNpcs (no los Core)
        try { _aliveNpcsHarmony?.UnpatchAll(_aliveNpcsHarmony.Id); } catch { }
        _aliveNpcsHarmony = new Harmony($"{ModManifest.UniqueID}.AliveNpcsBridge");

        if (useOffline)
        {
            var offline = new OfflineConversationBridge();
            offline.Initialize(Helper, Monitor, _aliveNpcsHarmony);
            ConversationBridge = offline;
            Monitor.Log(
                _config.ForceOfflineMode
                    ? "Valley Buddies: Modo Offline Standalone activo (forzado por config)."
                    : "Valley Buddies: Modo Offline Standalone activo (AliveNpcs no instalado).",
                LogLevel.Info);
        }
        else
        {
            try
            {
                ConversationBridge = ConversationBridgeFactory.CreateIsolated(
                    Helper, Monitor, _aliveNpcsHarmony,
                    _registry, _context, _memory, BuildPromptBlock, _multiplayerSync);
                Monitor.Log("Valley Buddies: Modo Integrado con AliveNpcs activo.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                Monitor.Log($"AliveNpcs bridge falló ({ex.GetType().Name}: {ex.Message}). Degradando a Offline.", LogLevel.Warn);
                var fallback = new OfflineConversationBridge();
                fallback.Initialize(Helper, Monitor, _aliveNpcsHarmony);
                ConversationBridge = fallback;
            }
        }
    }

    private void TryHookDialogueManager()
    {
        try
        {
            var aliveMod = Helper.ModRegistry.Get("Lucas.AliveNpcs");
            if (aliveMod != null)
            {
                var modInstanceProp = aliveMod.GetType().GetProperty("Mod");
                var modInstance = modInstanceProp?.GetValue(aliveMod);
                if (modInstance != null)
                {
                    var field = AccessTools.Field(modInstance.GetType(), "_dialogues");
                    var dm = field?.GetValue(modInstance);
                    if (dm != null)
                    {
                        DialogueBypassPatches.SetDialogueManager(dm);
                        Monitor.Log("DialogueManager hooked for unlimited follower conversation.", LogLevel.Debug);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Monitor.Log("TryHookDialogueManager error (ignored): " + ex.Message, LogLevel.Trace);
        }
    }

    // ──────────────────────── Prompt block ────────────────────────

    private string BuildPromptBlock(string npcName)
    {
        if (!_active || !_config.EnableMod) return string.Empty;

        int hearts = 0;
        try { hearts = Game1.player.getFriendshipHeartLevelForNPC(npcName); }
        catch { /* NPC not found */ }

        var sb = new StringBuilder();
        sb.AppendLine("[NPC PHYSICAL ACTIONS]");
        sb.AppendLine($"Current friendship with {npcName}: {hearts}/10 hearts.");
        sb.AppendLine();
        sb.AppendLine("You can perform REAL physical actions in the game world by adding an");
        sb.AppendLine("\"npc_action\" field (null or array) to the JSON you already return.");
        sb.AppendLine("Use null when you decide not to do anything physical.");
        sb.AppendLine();

        // Heart-tier rules
        AppendHeartRules(sb, hearts, npcName);
        sb.AppendLine();

        // Available actions for this NPC at current heart level
        var enabled = _registry.EnabledActions(_context, npcName, hearts).ToList();
        if (enabled.Count == 0)
        {
            sb.AppendLine("At this friendship level no physical actions are available yet.");
        }
        else
        {
            sb.AppendLine("Available physical actions (use the exact \"action\" name in the JSON):");
            foreach (var a in enabled)
                sb.AppendLine($"  - {a.Name} | Schema: {a.PromptSchema} | When: {a.PromptDescription}");
        }

        sb.AppendLine();
        sb.AppendLine("CONCISE DIALOGUE STYLE RULE:");
        sb.AppendLine("Keep your spoken dialogue natural, concise, and punchy (1 to 2 short sentences, maximum 25-30 words).");
        sb.AppendLine("Do NOT write long paragraphs, monologues, or speeches. Speak like a real person talking face-to-face!");
        sb.AppendLine();
        sb.AppendLine("CRITICAL ACTION SELECTION RULES:");
        sb.AppendLine("1. If the player asks you to follow, accompany them, or go somewhere together (e.g. 'sigueme', 'ven conmigo', 'vamos') and you accept, you MUST emit [{\"action\":\"follow_player\"}]. DO NOT emit 'face_player' when you agree to walk with them!");
        sb.AppendLine("2. 'face_player' is ONLY for standing still and turning your head/body towards the player.");
        sb.AppendLine("3. If the player asks for farm work (watering, weeding, petting) or mining, and you agree, emit the appropriate action (e.g. 'water_crops', 'harvest_crops', 'clear_debris', 'pet_animals', 'mine_nearby_rock'). The game will walk/travel you to the farm or location to do it.");
        sb.AppendLine("4. If you need materials (e.g. wood, stone, resources) for your hobbies or projects, or the farmer told you 'agarra madera', 'toma lo que necesites del cofre', etc., you can emit [{\"action\":\"chop_wood\"}] (branches), [{\"action\":\"chop_trees\", \"tree_type\":\"all\"}] (to chop trees on the farm), or [{\"action\":\"take_from_chest\", \"item\":\"wood\"}].");
        sb.AppendLine("5. If the player asks you to bring them items from the chest (e.g. 'traeme madera del cofre', 'traeme piedra', 'traeme lo que guardaste'), emit [{\"action\":\"fetch_from_chest\", \"category\":\"wood\"|\"stone\"|\"crops\"|\"anything\"}]. You will walk to the chest, pick it up, and deliver it to their hands!");
        sb.AppendLine("6. If you or the player want to relax together or take a break, emit [{\"action\":\"take_break\"}]. If taking a picture together, emit [{\"action\":\"take_photo\"}].");
        sb.AppendLine("7. If the player says goodbye, tells you to stop following, to go back to what you were doing, or that's all for now (e.g. 'adios', 'puedes volver a tu rutina', 'nos vemos luego', 'ya no me sigas'), emit [{\"action\":\"stop_following\"}]. You will wave and resume your normal daily schedule.");
        sb.AppendLine("8. MESSENGER / INVITATIONS: If the player invites you (in person OR via farm computer Messenger text — e.g. 'ven', 'puedes venir', 'ven a mi granja', 'ven a mi casa', 'te espero', 'come over', 'visit') and you say you are coming or on your way, you MUST emit [{\"action\":\"visit_player_farm\"}] (or [{\"action\":\"go_to_location\", \"location\":\"<name>\"}]). NEVER say you are coming over while emitting \"npc_action\": null!");
        sb.AppendLine("9. If you refuse or decline, explain your reason naturally in your dialogue and emit \"npc_action\": null.");
        sb.AppendLine();
        sb.AppendLine("SCHEMA: \"npc_action\": [{\"action\":\"<name>\", ... extra fields per action ...}]");
        sb.AppendLine("If you are doing nothing physical, write: \"npc_action\": null");
        sb.AppendLine();
        sb.AppendLine("FREE WILL RULE: You are a living person with your own desires. Use your personality, mood,");
        sb.AppendLine("and friendship level to decide whether you WANT to do the action. If you accept, the game will physically move you!");

        if (NpcActionExecutor.Instance.TryGetCompanionInfo(npcName, out var compInfo) && compInfo.FarmerId != 0)
        {
            sb.AppendLine();
            sb.AppendLine("[CURRENT SOCIAL CONTEXT]");
            string owner = !string.IsNullOrWhiteSpace(compInfo.FarmerName) ? compInfo.FarmerName : "the host farmer";
            if (compInfo.IsFollowing)
                sb.AppendLine($"You are currently accompanying and following {owner} today.");
            else if (compInfo.IsVisitingFarm)
                sb.AppendLine($"You were invited by {owner} to visit their farm today, and you are happily spending time at the farm helping out or enjoying the surroundings.");
            else
                sb.AppendLine($"You are spending time with {owner} today.");
            sb.AppendLine($"If another player talks to you, kindly let them know that {owner} invited you to the farm or asked you to accompany them, and mention how nice everything is!");
        }

        var recentEvents = _memory.GetRecentEvents(npcName, 6);
        if (recentEvents.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[RECENT SHARED EXPERIENCES & MEMORIES WITH THE FARMER]");
            sb.AppendLine("You remember sharing these recent real activities and moments with the farmer:");
            foreach (var ev in recentEvents)
                sb.AppendLine($"  - {ev}");
            sb.AppendLine("You can naturally mention, reminisce, or comment about these shared moments in your dialogue if appropriate!");
        }

        return sb.ToString();
    }

    private static void AppendHeartRules(StringBuilder sb, int hearts, string npcName)
    {
        sb.AppendLine("FRIENDSHIP & FREE WILL RULES (follow them or explain in dialogue why you don't):");
        if (hearts <= 3)
        {
            sb.AppendLine($"  {npcName} and the farmer are only acquaintances ({hearts}/10 hearts).");
            sb.AppendLine("  If the player asks you to help out with farm work (clearing weeds, watering crops, chopping wood, mining, etc.), you are willing to help if offered or paid a modest fee (100g), or you can ask: 'Está bien, te ayudo pero págame 100g por mi tiempo'.");
            sb.AppendLine("  Decline heavy free favors unless they offer compensation or a compelling reason.");
        }
        else if (hearts <= 6)
        {
            sb.AppendLine($"  {npcName} and the farmer are good friends ({hearts}/10 hearts).");
            sb.AppendLine("  You happily help out for free because you are friends: '¡Claro que sí! Con gusto te ayudo, para eso están los amigos'.");
            sb.AppendLine("  You are happy to follow the player, visit the farm, help out, or hang out.");
        }
        else if (hearts <= 8)
        {
            sb.AppendLine($"  {npcName} and the farmer are good friends ({hearts}/10 hearts).");
            sb.AppendLine("  You are happy to follow the player, visit the farm, help out, or hang out.");
            sb.AppendLine("  You still keep your own personality — refuse if it violates who you are (e.g. Haley hating dirt).");
        }
        else
        {
            sb.AppendLine($"  {npcName} deeply trusts and cares for the farmer ({hearts}/10 hearts).");
            sb.AppendLine("  You accept almost any proposal with warmth and enthusiasm.");
            sb.AppendLine("  You readily follow the player anywhere, help on the farm, and show deep affection.");
        }
        sb.AppendLine("  LATE NIGHT & CAMPING (After 9:00 PM on Farm): If you are on the farm late at night, you notice how dark it is and can suggest lighting a campfire to watch the stars, staying over at the farm, or heading home to rest. If you stay over, you wake up early, make coffee or breakfast for the farmer, and leave in the morning for your daily routine.");
    }

    // ──────────────────────── SMAPI events ────────────────────────

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        _memory.Load();
        _mailService.LoadFromSave();
        DialogueBypassPatches.ResetDaily();
        NpcActionExecutor.Instance.OnSaveLoaded();
        ValleyBuddies.Systems.LivingEvents.ValleyPacingDirector.Instance.OnSaveLoaded();
        ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.OnSaveLoaded();
        ValleyBuddies.Systems.LivingEvents.AmbientCutsceneDirectorService.Instance.ResetDaily();
        if (Game1.IsMasterGame && Game1.IsMultiplayer)
        {
            Helper.Multiplayer.SendMessage("ResetAllDayState", "AliveNpcsResetState", modIDs: new[] { ModManifest.UniqueID });
        }
    }

    private void OnDayEnding(object? sender, DayEndingEventArgs e)
    {
        ValleyBuddies.Systems.PicnicSceneService.Instance.Reset();
        ValleyBuddies.Systems.AmbientTownBanterService.Instance.ResetDaily();
        ValleyBuddies.Systems.CharacterAmbientChatterService.Instance.ResetDaily();
        ValleyBuddies.Systems.PassByGreetingService.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingTownVignetteService.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingEvents.AmbientCutsceneDirectorService.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.ClearAll(includeLastDestinationHistory: false);
        NpcActionExecutor.Instance.CaptureLateNightGuestsBeforeDayEnd();
        _mailService.ScheduleMorningSleepoverLetters(NpcActionExecutor.Instance.GetActiveSleepoverGuests(), _multiplayerSync);
        foreach (var job in NpcActionExecutor.Instance.GetAllActiveJobsSnapshot())
        {
            var npc = Game1.getCharacterFromName(job.NpcName);
            if (npc != null)
                NpcActionExecutor.RestoreNpcToVanillaState(npc, job.OwnerPlayerId, resumeSchedule: false);
        }
        NpcActionExecutor.Instance.ClearSleepoverGuests();

        NpcActionExecutor.Instance.EvacuateAllBedsBeforeSave();
        NpcActionExecutor.Instance.CleanupCampBeforeSave();
        NpcActionExecutor.Instance.DepositPendingHarvestsBeforeSave();
        NpcActionExecutor.Instance.RemoveAllNightTorches();
        ValleyBuddies.Systems.CompanionSynergyManager.Instance.ResetDaily();
        ValleyBuddies.Systems.EasterEggEngine.ResetDaily();
        NpcActionExecutor.InvalidateWalkabilityCache();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        _publicApi.ResetNavJobs();
        NpcAnimationService.ResetDaily();
        ValleyBuddies.Bridge.TextAboveHeadPatches.Reset();
        ValleyBuddies.Bridge.ReactionInterceptor.ClearQueue();
        ValleyBuddies.Systems.PicnicSceneService.Instance.Reset();
        ValleyBuddies.Systems.AmbientTownBanterService.Instance.ResetAll();
        ValleyBuddies.Systems.CharacterAmbientChatterService.Instance.ResetAll();
        ValleyBuddies.Systems.PassByGreetingService.Instance.ResetAll();
        ValleyBuddies.Systems.LivingTownVignetteService.Instance.ResetAll();
        ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.OnReturnedToTitle();
        ValleyBuddies.Systems.LivingEvents.CinematicDirectorService.Instance.ResetAll();
        ValleyBuddies.Systems.LivingEvents.AmbientCutsceneDirectorService.Instance.ResetAll();
        ValleyBuddies.Systems.LivingEvents.LivingEventManager.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingEvents.Events.ShinyFarmFindEvent.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingEvents.Events.MischievousAnimalEvent.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingEvents.ValleyPacingDirector.Instance.ResetAll();
        ValleyBuddies.Systems.CompanionSynergyManager.Instance.ResetDaily();
        ValleyBuddies.Systems.EasterEggEngine.ResetDaily();
        _mailService.ClearState();
        NpcActionExecutor.InvalidateWalkabilityCache();
        NpcActionExecutor.ClearAllFollowSlots();
        NpcActionExecutor.ClearUnattendedStepCache();
        NpcActionExecutor.Instance.RemoveAllCaveLights();
        NpcActionExecutor.Instance.RemoveAllNightTorches();
        NpcActionExecutor.Instance.ResetCaveFloorState();
        NpcActionExecutor.Instance.ClearPendingMorningNote();
        NpcActionExecutor.Instance.ResetAllCompanionsAndJobsToDefault();
        PostDialogueInviteMonitor.ResetAllScreens();
        ShopDutyDetector.ClearCache();
        ConversationBridge.Shutdown();
        ValleyBuddies.Bridge.PathfindingSafetyPatches.ClearAllBedExitNoClip();
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        _publicApi.ResetNavJobs();
        NpcAnimationService.ResetDaily();
        ValleyBuddies.Systems.PicnicSceneService.Instance.Reset();
        ValleyBuddies.Systems.AmbientTownBanterService.Instance.ResetDaily();
        ValleyBuddies.Systems.CharacterAmbientChatterService.Instance.ResetDaily();
        ValleyBuddies.Systems.PassByGreetingService.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingTownVignetteService.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingEvents.AmbientCutsceneDirectorService.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingEvents.ValleyPacingDirector.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.OnDayStarted();
        ValleyBuddies.Systems.LivingEvents.CinematicDirectorService.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingEvents.LivingEventManager.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingEvents.Events.ShinyFarmFindEvent.Instance.ResetDaily();
        ValleyBuddies.Systems.LivingEvents.Events.MischievousAnimalEvent.Instance.ResetDaily();
        _memory.ResetDaily();
        ConversationBridge.ResetDaily();
        DialogueBypassPatches.ResetDaily();
        ShopDutyDetector.LoadShops(Helper);
        PostDialogueInviteMonitor.ResetAllScreens();
        ValleyBuddies.Systems.CompanionSynergyManager.Instance.ResetDaily();
        ValleyBuddies.Systems.EasterEggEngine.ResetDaily();
        NpcActionExecutor.InvalidateWalkabilityCache();
        NpcActionExecutor.ClearUnattendedStepCache();
        NpcActionExecutor.Instance.RemoveAllCaveLights();
        NpcActionExecutor.Instance.RemoveAllNightTorches();
        NpcActionExecutor.Instance.ResetCaveFloorState();
        NpcActionExecutor.Instance.OnDayStarted();
        ValleyBuddies.Bridge.PathfindingSafetyPatches.ClearAllBedExitNoClip();
        if (Game1.IsMasterGame && Game1.IsMultiplayer)
        {
            Helper.Multiplayer.SendMessage("ResetAllDayState", "AliveNpcsResetState", modIDs: new[] { ModManifest.UniqueID });
        }
    }

    private void OnSaving(object? sender, SavingEventArgs e)
    {
        ValleyBuddies.Systems.PicnicSceneService.Instance.Reset();
        NpcActionExecutor.Instance.RemoveAllCaveLights();
        NpcActionExecutor.Instance.EvacuateAllBedsBeforeSave();
        NpcActionExecutor.Instance.CleanupCampBeforeSave();
        NpcActionExecutor.Instance.DepositPendingHarvestsBeforeSave();
        NpcActionExecutor.Instance.RemoveAllNightTorches();
        ValleyBuddies.Systems.LivingEvents.ValleyPacingDirector.Instance.Save();
        _memory.Save();
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        CurrentTick = e.Ticks;
        if (!_active || !Context.IsWorldReady) return;
        ConversationBridge.DrainQueue();
        if (!Context.IsMultiplayer && (Game1.paused || Game1.activeClickableMenu != null || Game1.eventUp))
            return;
        ValleyBuddies.Systems.LivingEvents.LivingEventManager.Instance.Update(e.Ticks);
        ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.Update(e.Ticks);
        ValleyBuddies.Systems.AmbientTownBanterService.Instance.Update(e.Ticks);
        ValleyBuddies.Systems.CharacterAmbientChatterService.Instance.Update(e.Ticks);
        ValleyBuddies.Systems.PassByGreetingService.Instance.Update(e.Ticks);
        ValleyBuddies.Systems.LivingTownVignetteService.Instance.Update(e.Ticks);
        ValleyBuddies.Systems.LivingEvents.AmbientCutsceneDirectorService.Instance.Update(e.Ticks);
        NpcActionExecutor.Instance.Tick(e.Ticks);

        if (e.IsMultipleOf(300) && _config.EnableCompanionBuffs)
        {
            long localId = Game1.player?.UniqueMultiplayerID ?? 0;
            var follower = NpcActionExecutor.Instance.GetActiveFollowerForPlayer(localId);
            if (follower != null && Game1.player != null)
            {
                ValleyBuddies.Systems.CompanionSynergyManager.Instance.RefreshCompanionAura(follower, Game1.player, true);
            }
        }
    }

    private void OnPlayerWarped(object? sender, WarpedEventArgs e)
    {
        if (!_active) return;
        NpcActionExecutor.InvalidateWalkabilityCache();
        NpcActionExecutor.Instance.OnPlayerWarped(e);
        if (e.IsLocalPlayer)
            ValleyBuddies.Systems.LivingEvents.AmbientCutsceneDirectorService.Instance.OnPlayerWarped(e.NewLocation, CurrentTick);
    }

    private void OnRenderedStep(object? sender, RenderedStepEventArgs e)
    {
        if (e.Step == StardewValley.Mods.RenderSteps.World_Background)
        {
            ValleyBuddies.Systems.PicnicSceneService.Instance.DrawActivePicnics(e.SpriteBatch, Game1.currentLocation);
        }
    }

    private void OnButtonsChanged(object? sender, ButtonsChangedEventArgs e)
    {
        if (!_active || !Context.IsPlayerFree || Game1.activeClickableMenu != null) return;

        if (_config.OfflineCompanionMenuKey.JustPressed())
        {
            Helper.Input.SuppressActiveKeybinds(_config.OfflineCompanionMenuKey);
            HandleOfflineCompanionHotKey();
        }
    }

    private void HandleOfflineCompanionHotKey()
    {
        var loc = Game1.player?.currentLocation;
        if (loc == null || Game1.player == null) return;

        Vector2 cursorTile = Game1.currentCursorTile;
        Vector2 playerTile = Game1.player.Tile;

        NPC? bestNpc = null;
        float bestScore = float.MaxValue;

        foreach (var character in loc.characters)
        {
            if (character is NPC npc && !npc.IsInvisible && !npc.isSleeping.Value)
            {
                float cursorDist = Vector2.Distance(npc.Tile, cursorTile);
                float playerDist = Vector2.Distance(npc.Tile, playerTile);

                if (cursorDist <= 2.5f || playerDist <= 4.0f)
                {
                    bool isManaged = NpcActionExecutor.Instance.IsManagedCompanion(npc.Name);
                    bool isEligible = isManaged || ShopDutyDetector.IsNpcEligibleForOfflineRecruitment(npc, _config);
                    if (!isEligible)
                        continue;

                    float score = cursorDist <= 2.5f ? cursorDist : (playerDist + 2.0f);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestNpc = npc;
                    }
                }
            }
        }

        if (bestNpc == null)
        {
            try
            {
                var nearby = loc.characters
                    .Where(c => c is NPC n && Vector2.Distance(n.Tile, playerTile) <= 6.0f)
                    .Select(c => $"{c.Name} ({c.GetType().Name})");
                ModMonitor?.Log($"[OfflineHotKey] No eligible NPC nearby. Found nearby: [{string.Join(", ", nearby)}]", LogLevel.Debug);
            }
            catch { }

            Game1.showRedMessage(Utilities.I18n.Get("offline.invite.no_npc_nearby"));
            return;
        }

        if (NpcActionExecutor.Instance.IsManagedCompanion(bestNpc.Name))
        {
            NpcActionExecutor.Instance.HandlePlayerInteraction(bestNpc, Game1.player);
            return;
        }

        PostDialogueInviteMonitor.ShowOfflineInviteQuestion(bestNpc, Game1.player);
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!_active || !Context.IsPlayerFree) return;

        // Hotkey dismissal
        if (!string.IsNullOrWhiteSpace(_config.DismissKey) &&
            Enum.TryParse<SButton>(_config.DismissKey, true, out var dismissBtn) &&
            e.Button == dismissBtn)
        {
            long localId = Game1.player?.UniqueMultiplayerID ?? 0;
            var follower = NpcActionExecutor.Instance.GetActiveFollowerForPlayer(localId);
            if (follower != null)
            {
                NpcActionExecutor.Instance.DismissCompanion(follower.Name);
                if (Game1.IsMultiplayer)
                {
                    BroadcastJob(follower.Name, JobKind.Done, localId, Game1.player?.Name ?? string.Empty);
                }
                if (_config.ShowHudMessages)
                {
                    Game1.addHUDMessage(new HUDMessage(
                        Helper.Translation.Get("hud.dismissed", new { name = follower.displayName }),
                        HUDMessage.newQuest_type));
                }
                return;
            }
        }

        // Action menu when right-clicking / interacting with follower or visitor
        if (ConversationBridge.Mode == ConversationBridgeMode.AliveNpcsIntegrated && e.Button.IsActionButton() && _config.EnableFollowerActionMenu && Game1.currentLocation != null && Game1.player != null)
        {
            if (Game1.activeClickableMenu != null || Game1.dialogueUp || Game1.currentSpeaker != null)
                return;
            if (Game1.ticks - _lastMenuTick < 30)
                return;

            // Never intercept clicks on chests, crafting machines, or placed objects
            var loc = Game1.currentLocation;
            if (loc.objects.TryGetValue(e.Cursor.GrabTile, out var grabObj) && (grabObj is StardewValley.Objects.Chest || grabObj.bigCraftable.Value || grabObj.Name.Contains("Chest", StringComparison.OrdinalIgnoreCase)))
                return;
            if (loc.objects.TryGetValue(e.Cursor.Tile, out var cursorObj) && (cursorObj is StardewValley.Objects.Chest || cursorObj.bigCraftable.Value || cursorObj.Name.Contains("Chest", StringComparison.OrdinalIgnoreCase)))
                return;

            var targetNpc = NpcActionExecutor.Instance.GetActiveFollowerOrVisitor(Game1.currentLocation, e.Cursor.Tile, e.Cursor.GrabTile);
            if (targetNpc != null)
            {
                float playerDist = Vector2.Distance(targetNpc.Tile, Game1.player.Tile);
                if (playerDist <= 3.5f)
                {
                    _lastMenuTick = Game1.ticks;
                    Helper.Input.Suppress(e.Button);
                    NpcActionExecutor.Instance.HandlePlayerInteraction(targetNpc, Game1.player);
                }
            }
        }
    }

    private void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != ModManifest.UniqueID) return;

        _multiplayerSync.OnModMessageReceived(sender, e);

        if (e.Type == "AliveNpcsActionSync")
        {
            var message = e.ReadAs<ActionSyncMessage>();
            if (message != null && !string.IsNullOrEmpty(message.NpcName))
            {
                bool applied = NpcActionExecutor.Instance.ApplyRemoteJob(message.NpcName, message.Kind, message.FarmerId, message.FarmerName, message.Extra);

                // If we are the Host, relay to other farmhands only when accepted/applied!
                if (applied && Game1.IsMasterGame)
                {
                    long senderId = e.FromPlayerID;
                    long hostId = Game1.player?.UniqueMultiplayerID ?? 0;
                    var otherFarmhandIds = Game1.getOnlineFarmers()
                        .Where(f => f != null && f.UniqueMultiplayerID != senderId && f.UniqueMultiplayerID != hostId)
                        .Select(f => f.UniqueMultiplayerID)
                        .ToArray();

                    if (otherFarmhandIds.Length > 0)
                    {
                        Helper.Multiplayer.SendMessage(message, "AliveNpcsActionSync", modIDs: new[] { ModManifest.UniqueID }, playerIDs: otherFarmhandIds);
                    }
                }
            }
        }
        else if (e.Type == "AliveNpcsResetState")
        {
            NpcActionExecutor.Instance.ResetAllCompanionsAndJobsToDefault();
        }
        else if (e.Type == "AliveNpcsNightPrompt")
        {
            string npcName = e.ReadAs<string>();
            if (!string.IsNullOrEmpty(npcName))
            {
                var npc = NpcActionExecutor.FindNpc(npcName);
                if (npc != null)
                {
                    NpcActionExecutor.Instance.PromptNighttimeActivity(npc);
                }
            }
        }
    }

    public static void SendNightPromptToPlayer(long farmerId, string npcName)
    {
        try
        {
            if (_staticHelper == null || _staticManifest == null) return;
            _staticHelper.Multiplayer.SendMessage(npcName, "AliveNpcsNightPrompt", modIDs: new[] { _staticManifest.UniqueID }, playerIDs: new[] { farmerId });
        }
        catch { }
    }

    public static void BroadcastJob(string npcName, JobKind kind, long farmerId, string farmerName = "", string extra = "", long? specificPeerId = null)
    {
        try
        {
            if (_staticHelper == null || _staticManifest == null) return;
            var message = new ActionSyncMessage
            {
                NpcName = npcName,
                Kind = kind,
                FarmerId = farmerId,
                FarmerName = farmerName,
                Extra = extra
            };
            _staticHelper.Multiplayer.SendMessage(
                message,
                "AliveNpcsActionSync",
                modIDs: new[] { _staticManifest.UniqueID },
                playerIDs: specificPeerId.HasValue ? new[] { specificPeerId.Value } : null);
        }
        catch { }
    }

    public static void BroadcastReset()
    {
        try
        {
            if (_staticHelper == null || _staticManifest == null) return;
            _staticHelper.Multiplayer.SendMessage("reset", "AliveNpcsResetState", modIDs: new[] { _staticManifest.UniqueID });
        }
        catch { }
    }

    // ──────────────────── Console commands ───────────────────────

    private void RegisterConsoleCommands(IModHelper helper)
    {
        helper.ConsoleCommands.Add("actions_status",
            "Show ValleyBuddies status and active NPC jobs.",
            (_, _) =>
            {
                Monitor.Log($"Active: {_active}", LogLevel.Info);
                NpcActionExecutor.Instance.LogStatus(Monitor);
            });

        helper.ConsoleCommands.Add("actions_cancel",
            "Cancel all active NPC physical jobs. Usage: actions_cancel [npcName]",
            (_, args) =>
            {
                string? name = args.Length > 0 ? args[0] : null;
                NpcActionExecutor.Instance.CancelAll(name);
                Monitor.Log(name != null ? $"Cancelled jobs for {name}." : "Cancelled all NPC jobs.", LogLevel.Info);
            });

        helper.ConsoleCommands.Add("actions_dismiss",
            "Dismiss follower so they wave goodbye and resume their normal schedule. Usage: actions_dismiss [npcName]",
            (_, args) =>
            {
                string? name = args.Length > 0 ? args[0] : NpcActionExecutor.Instance.GetActiveFollowerName();
                if (!string.IsNullOrEmpty(name))
                {
                    NpcActionExecutor.Instance.DismissCompanion(name);
                    Monitor.Log($"Dismissed {name} — returned to normal schedule.", LogLevel.Info);
                }
                else
                {
                    Monitor.Log("No active follower found to dismiss.", LogLevel.Info);
                }
            });

        helper.ConsoleCommands.Add("vb_test_event",
            "Trigger a living micro-event immediately for testing. Usage: vb_test_event <eventId> [npcName]",
            (_, args) =>
            {
                if (args.Length == 0)
                {
                    string eventsList = string.Join(", ", ValleyBuddies.Systems.LivingEvents.LivingEventManager.SupportedEventIds);
                    Monitor.Log($"Usage: vb_test_event <eventId> [npcName]\n{ValleyBuddies.Systems.LivingEvents.LivingEventManager.SupportedEventIds.Count} Events: {eventsList}", LogLevel.Info);
                    return;
                }
                string eventId = args[0];
                string? npcName = args.Length > 1 ? args[1] : null;
                if (ValleyBuddies.Systems.LivingEvents.LivingEventManager.Instance.TestEvent(eventId, npcName, out string msg))
                {
                    Monitor.Log($"[LivingEvents] {msg}", LogLevel.Info);
                }
                else
                {
                    Monitor.Log($"[LivingEvents] {msg}", LogLevel.Warn);
                }
            });

        helper.ConsoleCommands.Add("vb_test_invite",
            "Trigger an inverse NPC outing invitation for testing. Usage: vb_test_invite [npcName]",
            (_, args) =>
            {
                string? npcName = args.Length > 0 ? args[0] : null;
                if (ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.TestInvite(npcName, out string statusMsg))
                {
                    Monitor.Log($"[OutingInvite] {statusMsg}", LogLevel.Info);
                }
                else
                {
                    Monitor.Log($"[OutingInvite] {statusMsg}", LogLevel.Warn);
                }
            });

        helper.ConsoleCommands.Add("vb_test_picnic",
            "Start a picnic scene with follower for testing. Usage: vb_test_picnic [npcName]",
            (_, args) =>
            {
                string? npcName = args.Length > 0 ? args[0] : NpcActionExecutor.Instance.GetActiveFollowerName();
                if (string.IsNullOrEmpty(npcName))
                {
                    Monitor.Log("No active follower found. Specify an NPC name or recruit a companion first.", LogLevel.Warn);
                    return;
                }
                var npc = Game1.getCharacterFromName(npcName);
                if (npc == null)
                {
                    Monitor.Log($"NPC '{npcName}' not found.", LogLevel.Warn);
                    return;
                }
                NpcActionExecutor.Instance.Enqueue(new NpcJob
                {
                    NpcName = npc.Name,
                    Kind = JobKind.Picnic,
                    FarmerId = Game1.player.UniqueMultiplayerID,
                    FarmerName = Game1.player.Name,
                    CreatedTick = Game1.ticks
                });
                BroadcastJob(npc.Name, JobKind.Picnic, Game1.player.UniqueMultiplayerID, Game1.player.Name);
                Monitor.Log($"Starting picnic job for {npcName}...", LogLevel.Info);
            });

        helper.ConsoleCommands.Add("vb_test_cutscene",
            "Play a native cinematic event immediately for testing. Usage: vb_test_cutscene [npcName] [sceneType|personal]\n20 Scenes: museum, beach, forest, mountain, saloon, town, farm, home, spa, greenhouse, mines, desert, night_stars, rainy_shelter, scenic, secret_woods, island, railroad, sewer, sunset_golden (+ 'personal')",
            (_, args) =>
            {
                string? npcName = args.Length > 0 ? args[0] : null;
                string? sceneType = args.Length > 1 ? args[1] : null;
                if (ValleyBuddies.Systems.LivingEvents.CinematicDirectorService.Instance.TestCutscene(npcName, sceneType, out string statusMsg))
                {
                    Monitor.Log($"[CinematicDirector] {statusMsg}", LogLevel.Info);
                }
                else
                {
                    Monitor.Log($"[CinematicDirector] {statusMsg}", LogLevel.Warn);
                }
            });

        helper.ConsoleCommands.Add("vb_list_cinematics",
            "List loaded cinematic voice packs, character counts, all 20 supported scenarios, and all 18 living micro-events.",
            (_, _) =>
            {
                var vp = ValleyBuddies.Systems.LivingEvents.CharacterVoicePackService.Instance;
                var scenarios = string.Join(", ", ValleyBuddies.Systems.LivingEvents.CinematicDirectorService.SupportedScenarios);
                var eventsList = string.Join(", ", ValleyBuddies.Systems.LivingEvents.LivingEventManager.SupportedEventIds);
                Monitor.Log(
                    $"=== Valley Buddies Cinematic & Living World Engine v{ModManifest.Version} ===\n" +
                    $"Loaded Voice Packs: Total={vp.TotalLoadedCount} characters | Vanilla={vp.VanillaLoadedCount} | SVE={vp.SveLoadedCount} (detected={vp.IsSveDetected}) | RSV={vp.RsvLoadedCount} (detected={vp.IsRsvDetected}) | Custom={vp.CustomLoadedCount}\n" +
                    $"20 Supported Scenarios: {scenarios} (+ 'personal')\n" +
                    $"18 Supported Living Micro-Events (vb_test_event): {eventsList}",
                    LogLevel.Info);
            });

        helper.ConsoleCommands.Add("vb_pacing_status",
            "Inspect ValleyPacingDirector counters, cooldowns, preset, and current pity-timer chances.",
            (_, _) =>
            {
                if (!Context.IsWorldReady || Game1.player == null)
                {
                    Monitor.Log("You must be in-game to inspect pacing status.", LogLevel.Warn);
                    return;
                }
                Monitor.Log(ValleyBuddies.Systems.LivingEvents.ValleyPacingDirector.Instance.FormatStatusReport(Game1.player.UniqueMultiplayerID), LogLevel.Info);
            });

        helper.ConsoleCommands.Add("vb_test_spouse_date",
            "Trigger a surprise spouse date prompt immediately for testing. Usage: vb_test_spouse_date [npcName]",
            (_, args) =>
            {
                string? npcName = args.Length > 0 ? args[0] : null;
                if (ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.TestSpouseDate(npcName, out string statusMsg))
                {
                    Monitor.Log($"[SpouseDate] {statusMsg}", LogLevel.Info);
                }
                else
                {
                    Monitor.Log($"[SpouseDate] {statusMsg}", LogLevel.Warn);
                }
            });

        helper.ConsoleCommands.Add("vb_test_banter",
            "Trigger a multi-turn Living Town banter conversation immediately for testing. Usage: vb_test_banter [npcA] [npcB]",
            (_, args) =>
            {
                string? npcA = args.Length > 0 ? args[0] : null;
                string? npcB = args.Length > 1 ? args[1] : null;
                if (ValleyBuddies.Systems.AmbientTownBanterService.Instance.TryTriggerTestBanter(npcA, npcB, out string report))
                {
                    Monitor.Log($"[LivingTownBanter] {report}", LogLevel.Info);
                }
                else
                {
                    Monitor.Log($"[LivingTownBanter] {report}", LogLevel.Warn);
                }
            });

        helper.ConsoleCommands.Add("vb_banter_log",
            "Inspect the last 50 Ambient Town & Group Banter transcript lines.",
            (_, _) =>
            {
                var svc = ValleyBuddies.Systems.AmbientTownBanterService.Instance;
                var lines = svc.GetRecentTranscript();
                Monitor.Log(
                    $"=== Living Town Banter Stats: Pairs={svc.LoadedPairsCount} | Pools={svc.LoadedPoolCount} | Convos={svc.LoadedConversationsCount} | Turns={svc.LoadedTurnsCount} (SVE={svc.IsSveDetected}, RSV={svc.IsRsvDetected}) ===",
                    LogLevel.Info);
                if (lines.Count == 0)
                {
                    Monitor.Log("(No ambient banter lines recorded yet in this session.)", LogLevel.Info);
                    return;
                }
                foreach (string entry in lines)
                {
                    Monitor.Log(entry, LogLevel.Info);
                }
            });

        helper.ConsoleCommands.Add("vb_test_chatter",
            "Trigger a personal ambient character chatter line immediately. Usage: vb_test_chatter [npcName] [category]",
            (_, args) =>
            {
                string? npcName = args.Length > 0 ? args[0] : null;
                string? category = args.Length > 1 ? args[1] : null;
                if (ValleyBuddies.Systems.CharacterAmbientChatterService.Instance.TryTriggerTestChatter(npcName, category, out string report))
                {
                    Monitor.Log($"[CharacterChatter] {report}", LogLevel.Info);
                }
                else
                {
                    Monitor.Log($"[CharacterChatter] {report}", LogLevel.Warn);
                }
            });

        helper.ConsoleCommands.Add("vb_test_greeting",
            "Trigger a Living Valley pass-by neighbor greeting immediately for testing. Usage: vb_test_greeting [npcName]",
            (_, args) =>
            {
                string? npcName = args.Length > 0 ? args[0] : null;
                if (ValleyBuddies.Systems.PassByGreetingService.Instance.TryTriggerTestGreeting(npcName, out string report))
                {
                    Monitor.Log($"[PassByGreeting] {report}", LogLevel.Info);
                }
                else
                {
                    Monitor.Log($"[PassByGreeting] {report}", LogLevel.Warn);
                }
            });

        helper.ConsoleCommands.Add("vb_test_vignette",
            "Trigger a staged Living Town micro-vignette immediately for testing. Usage: vb_test_vignette [category] [npcA] [npcB]",
            (_, args) =>
            {
                string? category = args.Length > 0 ? args[0] : null;
                string? npcA = args.Length > 1 ? args[1] : null;
                string? npcB = args.Length > 2 ? args[2] : null;
                if (ValleyBuddies.Systems.LivingTownVignetteService.Instance.TryTriggerTestVignette(category, npcA, npcB, out string report))
                {
                    Monitor.Log($"[LivingTownVignettes] {report}", LogLevel.Info);
                }
                else
                {
                    Monitor.Log($"[LivingTownVignettes] {report}", LogLevel.Warn);
                }
            });

        helper.ConsoleCommands.Add("vb_test_ambient_scene",
            "List or trigger an ambient indoor cutscene immediately for testing. Usage: vb_test_ambient_scene [sceneId|list] [scriptIndex]",
            (_, args) =>
            {
                string? sceneId = args.Length > 0 ? args[0] : null;
                int scriptIdx = (args.Length > 1 && int.TryParse(args[1], out int parsed)) ? parsed : -1;
                if (ValleyBuddies.Systems.LivingEvents.AmbientCutsceneDirectorService.Instance.TryTriggerTestScene(sceneId, scriptIdx, out string report))
                {
                    Monitor.Log($"[AmbientCutscenes] {report}", LogLevel.Info);
                }
                else
                {
                    Monitor.Log($"[AmbientCutscenes] {report}", LogLevel.Warn);
                }
            });

        helper.ConsoleCommands.Add("vb_test_easter_egg",
            "Trigger any of the 10 easy Easter Eggs immediately for testing. Usage: vb_test_easter_egg <id> [npcName]",
            (_, args) =>
            {
                string? eggId = args.Length > 0 ? args[0] : null;
                string? npcName = args.Length > 1 ? args[1] : null;
                if (ValleyBuddies.Systems.EasterEggEngine.TestEasterEgg(eggId, npcName, out string report))
                {
                    Monitor.Log($"[EasterEgg] {report}", LogLevel.Info);
                }
                else
                {
                    Monitor.Log($"[EasterEgg] {report}", LogLevel.Warn);
                }
            });

        helper.ConsoleCommands.Add("vb_dedup_npcs",
            "Scan world and cabins to remove duplicate villager clones on Host.",
            (_, _) =>
            {
                if (!Context.IsWorldReady || !Context.IsMainPlayer)
                {
                    Monitor.Log("[DedupGuard] Must be Host in an active world to deduplicate villagers.", LogLevel.Warn);
                    return;
                }
                int removed = NpcActionExecutor.Instance.DeduplicateVillagersIfNeeded();
                Monitor.Log($"[DedupGuard] Scan complete. Removed {removed} duplicate villager instance(s).", LogLevel.Info);
            });

        helper.ConsoleCommands.Add("vb_restore_qi",
            "Restore Mister Qi to QiNutRoom if he is missing (Host only).",
            (_, _) =>
            {
                if (!Context.IsWorldReady || !Context.IsMainPlayer)
                {
                    Monitor.Log("[DedupGuard] Must be Host in an active world to restore Mister Qi.", LogLevel.Warn);
                    return;
                }
                NpcActionExecutor.Instance.EnsureMisterQiInNutRoom(out string qiReport);
                Monitor.Log($"[DedupGuard] {qiReport}", LogLevel.Info);
            });

        helper.ConsoleCommands.Add("vb_warp_trace",
            "Forensic warp/teleport stack tracer. Usage: vb_warp_trace <on|off|npcName|dump|clear>",
            (_, args) =>
            {
                if (args.Length == 0)
                {
                    Monitor.Log(
                        $"[WarpTrace] Status: Enabled={WarpTracePatches.IsEnabled} | Filter={WarpTracePatches.FilterNpcName ?? "ALL"} | Buffered={WarpTracePatches.GetDump().Count}/50\n" +
                        "Usage: vb_warp_trace on | off | <npcName> | dump | clear",
                        LogLevel.Info);
                    return;
                }

                string sub = args[0].Trim();
                switch (sub.ToLowerInvariant())
                {
                    case "on":
                        WarpTracePatches.EnableAll();
                        Monitor.Log("[WarpTrace] Live warp & tile-jump tracing ENABLED for ALL NPCs.", LogLevel.Info);
                        break;
                    case "off":
                        WarpTracePatches.Disable();
                        Monitor.Log("[WarpTrace] Live warp tracing DISABLED (0 overhead).", LogLevel.Info);
                        break;
                    case "dump":
                        var entries = WarpTracePatches.GetDump();
                        Monitor.Log($"=== [WarpTrace] Buffer Dump ({entries.Count} entries) ===", LogLevel.Info);
                        if (entries.Count == 0)
                        {
                            Monitor.Log("(Buffer is empty. Run 'vb_warp_trace on' or 'vb_warp_trace <npcName>' first.)", LogLevel.Info);
                        }
                        else
                        {
                            for (int idx = 0; idx < entries.Count; idx++)
                                Monitor.Log($"#{idx + 1}: {entries[idx]}", LogLevel.Info);
                        }
                        break;
                    case "clear":
                        WarpTracePatches.Clear();
                        Monitor.Log("[WarpTrace] Circular trace buffer cleared.", LogLevel.Info);
                        break;
                    default:
                        WarpTracePatches.EnableForNpc(sub);
                        Monitor.Log($"[WarpTrace] Live warp tracing ENABLED filtered for NPC '{sub}'.", LogLevel.Info);
                        break;
                }
            });
    }

    // ──────────────────── GMCM registration ──────────────────────

    private static KeybindList SafeParseKeybind(string? raw, string fallback)
    {
        try
        {
            return KeybindList.Parse(string.IsNullOrWhiteSpace(raw) ? fallback : raw);
        }
        catch
        {
            return KeybindList.Parse(fallback);
        }
    }

    private void RegisterGmcm()
    {
        try
        {
            var gmcm = Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (gmcm == null) return;

            gmcm.Register(ModManifest,
                reset: () => _config.ResetToDefaults(),
                save:  () =>
                {
                    Helper.WriteConfig(_config);
                    NpcActionExecutor.Instance.ApplyLiveConfigChanges(_config);
                    if (_active) ApplyConversationBridgeMode();
                });

            // ════════════════════════════════════════════════════════════════
            // PÁGINA PRINCIPAL: MODO + PANEL RÁPIDO + NAVEGACIÓN MODULAR
            // ════════════════════════════════════════════════════════════════
            gmcm.AddSectionTitle(
                ModManifest,
                () => Utilities.I18n.Get("config.main.section.general.title"),
                () => Utilities.I18n.Get("config.main.section.general.tooltip")
            );
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableMod,
                setValue: v => _config.EnableMod = v,
                name: () => Helper.Translation.Get("config.EnableMod.label"),
                tooltip: () => Helper.Translation.Get("config.EnableMod.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.ForceOfflineMode,
                setValue: v => _config.ForceOfflineMode = v,
                name: () => Utilities.I18n.Get("config.offline.force_offline.name"),
                tooltip: () => Utilities.I18n.Get("config.offline.force_offline.tooltip"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnablePostDialogueInvitePrompt,
                setValue: v => _config.EnablePostDialogueInvitePrompt = v,
                name: () => Utilities.I18n.Get("config.offline.post_dialogue_invite.name"),
                tooltip: () => Utilities.I18n.Get("config.offline.post_dialogue_invite.tooltip"));
            gmcm.AddKeybindList(ModManifest,
                getValue: () => _config.OfflineCompanionMenuKey,
                setValue: v => _config.OfflineCompanionMenuKey = v,
                name: () => Utilities.I18n.Get("config.offline.hotkey.name"),
                tooltip: () => Utilities.I18n.Get("config.offline.hotkey.tooltip"));
            gmcm.AddKeybindList(ModManifest,
                getValue: () => string.IsNullOrWhiteSpace(_config.DismissKey) ? new KeybindList() : SafeParseKeybind(_config.DismissKey, "End"),
                setValue: v => _config.DismissKey = v.IsBound ? v.Keybinds[0].Buttons[0].ToString() : string.Empty,
                name: () => Helper.Translation.Get("config.DismissKey.label"),
                tooltip: () => Helper.Translation.Get("config.DismissKey.desc"));

            gmcm.AddSectionTitle(
                ModManifest,
                () => Utilities.I18n.Get("config.main.section.quick_modules.title"),
                () => Utilities.I18n.Get("config.main.section.quick_modules.tooltip")
            );
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableSpouseLivingHome,
                setValue: v => _config.EnableSpouseLivingHome = v,
                name: () => Helper.Translation.Get("config.spouse_living_home.label"),
                tooltip: () => Helper.Translation.Get("config.spouse_living_home.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableLivingMicroEvents,
                setValue: v => _config.EnableLivingMicroEvents = v,
                name: () => Helper.Translation.Get("config.EnableLivingMicroEvents.label"),
                tooltip: () => Helper.Translation.Get("config.EnableLivingMicroEvents.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableCinematicOutings,
                setValue: v => _config.EnableCinematicOutings = v,
                name: () => Helper.Translation.Get("config.EnableCinematicOutings.label"),
                tooltip: () => Helper.Translation.Get("config.EnableCinematicOutings.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableNpcOutingInvitations,
                setValue: v => _config.EnableNpcOutingInvitations = v,
                name: () => Helper.Translation.Get("config.EnableNpcOutingInvitations.label"),
                tooltip: () => Helper.Translation.Get("config.EnableNpcOutingInvitations.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableTownHangouts,
                setValue: v => _config.EnableTownHangouts = v,
                name: () => Helper.Translation.Get("config.EnableTownHangouts.label"),
                tooltip: () => Helper.Translation.Get("config.EnableTownHangouts.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableAmbientTownConversations,
                setValue: v => _config.EnableAmbientTownConversations = v,
                name: () => Helper.Translation.Get("config.EnableAmbientTownConversations.label"),
                tooltip: () => Helper.Translation.Get("config.EnableAmbientTownConversations.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableCharacterAmbientChatter,
                setValue: v => _config.EnableCharacterAmbientChatter = v,
                name: () => Helper.Translation.Get("config.EnableCharacterAmbientChatter.label"),
                tooltip: () => Helper.Translation.Get("config.EnableCharacterAmbientChatter.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnablePassByGreetings,
                setValue: v => _config.EnablePassByGreetings = v,
                name: () => Helper.Translation.Get("config.EnablePassByGreetings.label"),
                tooltip: () => Helper.Translation.Get("config.EnablePassByGreetings.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableLivingTownVignettes,
                setValue: v => _config.EnableLivingTownVignettes = v,
                name: () => Helper.Translation.Get("config.EnableLivingTownVignettes.label"),
                tooltip: () => Helper.Translation.Get("config.EnableLivingTownVignettes.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableAmbientCutscenes,
                setValue: v => _config.EnableAmbientCutscenes = v,
                name: () => Helper.Translation.Get("config.EnableAmbientCutscenes.label"),
                tooltip: () => Helper.Translation.Get("config.EnableAmbientCutscenes.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.AllowFarmWork,
                setValue: v => _config.AllowFarmWork = v,
                name: () => Helper.Translation.Get("config.AllowFarmWork.label"),
                tooltip: () => Helper.Translation.Get("config.AllowFarmWork.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableCompanionCombat,
                setValue: v => _config.EnableCompanionCombat = v,
                name: () => Helper.Translation.Get("config.EnableCompanionCombat.label"),
                tooltip: () => Helper.Translation.Get("config.EnableCompanionCombat.desc"));

            gmcm.AddSectionTitle(
                ModManifest,
                () => Utilities.I18n.Get("config.main.section.pages.title"),
                () => Utilities.I18n.Get("config.main.section.pages.tooltip")
            );
            gmcm.AddPageLink(ModManifest, "page_immersion",
                text: () => Utilities.I18n.Get("config.page.immersion.link"),
                tooltip: () => Utilities.I18n.Get("config.page.immersion.tooltip"));
            gmcm.AddPageLink(ModManifest, "page_spouse",
                text: () => Utilities.I18n.Get("config.page.spouse.link"),
                tooltip: () => Utilities.I18n.Get("config.page.spouse.tooltip"));
            gmcm.AddPageLink(ModManifest, "page_hangouts",
                text: () => Utilities.I18n.Get("config.page.hangouts.link"),
                tooltip: () => Utilities.I18n.Get("config.page.hangouts.tooltip"));
            gmcm.AddPageLink(ModManifest, "page_farm_combat",
                text: () => Utilities.I18n.Get("config.page.farm_combat.link"),
                tooltip: () => Utilities.I18n.Get("config.page.farm_combat.tooltip"));
            gmcm.AddPageLink(ModManifest, "page_advanced",
                text: () => Utilities.I18n.Get("config.page.advanced.link"),
                tooltip: () => Utilities.I18n.Get("config.page.advanced.tooltip"));

            // ════════════════════════════════════════════════════════════════
            // SUBPÁGINA 1: INMERSIÓN, BURBUJAS Y MICRO-EVENTOS
            // ════════════════════════════════════════════════════════════════
            gmcm.AddPage(ModManifest, "page_immersion", () => Utilities.I18n.Get("config.page.immersion.title"));

            gmcm.AddSectionTitle(ModManifest, () => Utilities.I18n.Get("config.section.bubbles"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.BubbleTextScale,
                setValue: v => _config.BubbleTextScale = v,
                name: () => Helper.Translation.Get("config.BubbleTextScale.label"),
                tooltip: () => Helper.Translation.Get("config.BubbleTextScale.desc"),
                min: 0.70f,
                max: 1.00f,
                interval: 0.05f,
                formatValue: v => $"{Math.Round(v * 100):0}%");
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.BubbleMaxWidthPx,
                setValue: v => _config.BubbleMaxWidthPx = v,
                name: () => Helper.Translation.Get("config.BubbleMaxWidthPx.label"),
                tooltip: () => Helper.Translation.Get("config.BubbleMaxWidthPx.desc"),
                min: 140,
                max: 360,
                interval: 10);
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.BubbleDurationMultiplier,
                setValue: v => _config.BubbleDurationMultiplier = v,
                name: () => Helper.Translation.Get("config.BubbleDurationMultiplier.label"),
                tooltip: () => Helper.Translation.Get("config.BubbleDurationMultiplier.desc"),
                min: 0.8f,
                max: 2.0f,
                interval: 0.1f);
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.ShowHudMessages,
                setValue: v => _config.ShowHudMessages = v,
                name: () => Helper.Translation.Get("config.ShowHudMessages.label"),
                tooltip: () => Helper.Translation.Get("config.ShowHudMessages.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableToolVisualOverlays,
                setValue: v => _config.EnableToolVisualOverlays = v,
                name: () => Helper.Translation.Get("config.tool_overlays.label"),
                tooltip: () => Helper.Translation.Get("config.tool_overlays.desc"));

            gmcm.AddSectionTitle(ModManifest, () => Helper.Translation.Get("config.section.living_immersion"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableLivingMicroEvents,
                setValue: v => _config.EnableLivingMicroEvents = v,
                name: () => Helper.Translation.Get("config.EnableLivingMicroEvents.label"),
                tooltip: () => Helper.Translation.Get("config.EnableLivingMicroEvents.desc"));
            gmcm.AddTextOption(ModManifest,
                getValue: () => _config.LivingPacingPreset,
                setValue: v => _config.LivingPacingPreset = v,
                name: () => Helper.Translation.Get("config.LivingPacingPreset.label"),
                tooltip: () => Helper.Translation.Get("config.LivingPacingPreset.desc"),
                allowedValues: new[] { "Relaxed", "Balanced", "Lively" },
                formatAllowedValue: v => Helper.Translation.Get($"config.LivingPacingPreset.{v.ToLowerInvariant()}"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MicroEventFrequencyPercent,
                setValue: v => _config.MicroEventFrequencyPercent = v,
                name: () => Helper.Translation.Get("config.MicroEventFrequencyPercent.label"),
                tooltip: () => Helper.Translation.Get("config.MicroEventFrequencyPercent.desc"),
                min: 25,
                max: 200,
                interval: 25,
                formatValue: v => $"{v}%");
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableEasterEggs,
                setValue: v => _config.EnableEasterEggs = v,
                name: () => Helper.Translation.Get("config.EnableEasterEggs.label"),
                tooltip: () => Helper.Translation.Get("config.EnableEasterEggs.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableCompanionTenderMoments,
                setValue: v => _config.EnableCompanionTenderMoments = v,
                name: () => Helper.Translation.Get("config.companion_tender.label"),
                tooltip: () => Helper.Translation.Get("config.companion_tender.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableRareFindReactions,
                setValue: v => _config.EnableRareFindReactions = v,
                name: () => Helper.Translation.Get("config.EnableRareFindReactions.label"),
                tooltip: () => Helper.Translation.Get("config.EnableRareFindReactions.desc"));

            // ════════════════════════════════════════════════════════════════
            // SUBPÁGINA 2: HOGAR, CÓNYUGES Y PIJAMADAS
            // ════════════════════════════════════════════════════════════════
            gmcm.AddPage(ModManifest, "page_spouse", () => Utilities.I18n.Get("config.page.spouse.title"));

            gmcm.AddSectionTitle(ModManifest, () => Helper.Translation.Get("config.section.home_heart"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableSpouseLivingHome,
                setValue: v => _config.EnableSpouseLivingHome = v,
                name: () => Helper.Translation.Get("config.spouse_living_home.label"),
                tooltip: () => Helper.Translation.Get("config.spouse_living_home.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableSpouseMorningKiss,
                setValue: v => _config.EnableSpouseMorningKiss = v,
                name: () => Helper.Translation.Get("config.spouse_morning_kiss.label"),
                tooltip: () => Helper.Translation.Get("config.spouse_morning_kiss.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableSpouseMorningBreakfast,
                setValue: v => _config.EnableSpouseMorningBreakfast = v,
                name: () => Helper.Translation.Get("config.spouse_breakfast.label"),
                tooltip: () => Helper.Translation.Get("config.spouse_breakfast.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableSpouseFarmChores,
                setValue: v => _config.EnableSpouseFarmChores = v,
                name: () => Helper.Translation.Get("config.spouse_chores.label"),
                tooltip: () => Helper.Translation.Get("config.spouse_chores.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableSpouseAutoHarvest,
                setValue: v => _config.EnableSpouseAutoHarvest = v,
                name: () => Helper.Translation.Get("config.spouse_harvest.label"),
                tooltip: () => Helper.Translation.Get("config.spouse_harvest.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableSpouseSurpriseDates,
                setValue: v => _config.EnableSpouseSurpriseDates = v,
                name: () => Helper.Translation.Get("config.EnableSpouseSurpriseDates.label"),
                tooltip: () => Helper.Translation.Get("config.EnableSpouseSurpriseDates.desc"));

            gmcm.AddSectionTitle(ModManifest, () => Utilities.I18n.Get("config.section.sleepover"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableCozyIndoorEvening,
                setValue: v => _config.EnableCozyIndoorEvening = v,
                name: () => Helper.Translation.Get("config.EnableCozyIndoorEvening.label"),
                tooltip: () => Helper.Translation.Get("config.EnableCozyIndoorEvening.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableSleepoverCoffeeLetter,
                setValue: v => _config.EnableSleepoverCoffeeLetter = v,
                name: () => Helper.Translation.Get("config.EnableSleepoverCoffeeLetter.label"),
                tooltip: () => Helper.Translation.Get("config.EnableSleepoverCoffeeLetter.desc"));

            // ════════════════════════════════════════════════════════════════
            // SUBPÁGINA 3: SALIDAS, INVITACIONES Y CINEMÁTICAS
            // ════════════════════════════════════════════════════════════════
            gmcm.AddPage(ModManifest, "page_hangouts", () => Utilities.I18n.Get("config.page.hangouts.title"));

            gmcm.AddSectionTitle(ModManifest, () => Utilities.I18n.Get("config.section.outings"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableNpcOutingInvitations,
                setValue: v => _config.EnableNpcOutingInvitations = v,
                name: () => Helper.Translation.Get("config.EnableNpcOutingInvitations.label"),
                tooltip: () => Helper.Translation.Get("config.EnableNpcOutingInvitations.desc"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.NpcOutingInviteChancePercent,
                setValue: v => _config.NpcOutingInviteChancePercent = v,
                name: () => Helper.Translation.Get("config.NpcOutingInviteChancePercent.label"),
                tooltip: () => Helper.Translation.Get("config.NpcOutingInviteChancePercent.desc"),
                min: 0,
                max: 100,
                interval: 5,
                formatValue: v => $"{v}%");
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.NpcOutingCooldownDays,
                setValue: v => _config.NpcOutingCooldownDays = v,
                name: () => Helper.Translation.Get("config.NpcOutingCooldownDays.label"),
                tooltip: () => Helper.Translation.Get("config.NpcOutingCooldownDays.desc"),
                min: 1,
                max: 14,
                interval: 1);
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableCinematicOutings,
                setValue: v => _config.EnableCinematicOutings = v,
                name: () => Helper.Translation.Get("config.EnableCinematicOutings.label"),
                tooltip: () => Helper.Translation.Get("config.EnableCinematicOutings.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableFavoriteSpotPrompts,
                setValue: v => _config.EnableFavoriteSpotPrompts = v,
                name: () => Helper.Translation.Get("config.EnableFavoriteSpotPrompts.label"),
                tooltip: () => Helper.Translation.Get("config.EnableFavoriteSpotPrompts.desc"));

            gmcm.AddSectionTitle(ModManifest, () => Helper.Translation.Get("config.section.hangouts"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableTownHangouts,
                setValue: v => _config.EnableTownHangouts = v,
                name: () => Helper.Translation.Get("config.EnableTownHangouts.label"),
                tooltip: () => Helper.Translation.Get("config.EnableTownHangouts.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableCompanionFishing,
                setValue: v => _config.EnableCompanionFishing = v,
                name: () => Helper.Translation.Get("config.EnableCompanionFishing.label"),
                tooltip: () => Helper.Translation.Get("config.EnableCompanionFishing.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableDailyBondingBonus,
                setValue: v => _config.EnableDailyBondingBonus = v,
                name: () => Helper.Translation.Get("config.EnableDailyBondingBonus.label"),
                tooltip: () => Helper.Translation.Get("config.EnableDailyBondingBonus.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableOfflineGroupBanter,
                setValue: v => _config.EnableOfflineGroupBanter = v,
                name: () => Helper.Translation.Get("config.EnableOfflineGroupBanter.label"),
                tooltip: () => Helper.Translation.Get("config.EnableOfflineGroupBanter.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableAmbientTownConversations,
                setValue: v => _config.EnableAmbientTownConversations = v,
                name: () => Helper.Translation.Get("config.EnableAmbientTownConversations.label"),
                tooltip: () => Helper.Translation.Get("config.EnableAmbientTownConversations.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableCharacterAmbientChatter,
                setValue: v => _config.EnableCharacterAmbientChatter = v,
                name: () => Helper.Translation.Get("config.EnableCharacterAmbientChatter.label"),
                tooltip: () => Helper.Translation.Get("config.EnableCharacterAmbientChatter.desc"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.AmbientTownBanterCooldownSeconds,
                setValue: v => _config.AmbientTownBanterCooldownSeconds = v,
                name: () => Helper.Translation.Get("config.AmbientTownBanterCooldownSeconds.label"),
                tooltip: () => Helper.Translation.Get("config.AmbientTownBanterCooldownSeconds.desc"),
                min: 10,
                max: 90,
                interval: 5,
                formatValue: v => $"{v}s");
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnablePassByGreetings,
                setValue: v => _config.EnablePassByGreetings = v,
                name: () => Helper.Translation.Get("config.EnablePassByGreetings.label"),
                tooltip: () => Helper.Translation.Get("config.EnablePassByGreetings.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableLivingTownVignettes,
                setValue: v => _config.EnableLivingTownVignettes = v,
                name: () => Helper.Translation.Get("config.EnableLivingTownVignettes.label"),
                tooltip: () => Helper.Translation.Get("config.EnableLivingTownVignettes.desc"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.LivingTownVignetteCooldownSeconds,
                setValue: v => _config.LivingTownVignetteCooldownSeconds = v,
                name: () => Helper.Translation.Get("config.LivingTownVignetteCooldownSeconds.label"),
                tooltip: () => Helper.Translation.Get("config.LivingTownVignetteCooldownSeconds.desc"),
                min: 20,
                max: 120,
                interval: 5,
                formatValue: v => $"{v}s");
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableAmbientCutscenes,
                setValue: v => _config.EnableAmbientCutscenes = v,
                name: () => Helper.Translation.Get("config.EnableAmbientCutscenes.label"),
                tooltip: () => Helper.Translation.Get("config.EnableAmbientCutscenes.desc"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.AmbientCutsceneChancePercent,
                setValue: v => _config.AmbientCutsceneChancePercent = v,
                name: () => Helper.Translation.Get("config.AmbientCutsceneChancePercent.label"),
                tooltip: () => Helper.Translation.Get("config.AmbientCutsceneChancePercent.desc"),
                min: 5,
                max: 100,
                interval: 5,
                formatValue: v => $"{v}%");
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MaxAmbientCutscenesPerDay,
                setValue: v => _config.MaxAmbientCutscenesPerDay = v,
                name: () => Helper.Translation.Get("config.MaxAmbientCutscenesPerDay.label"),
                tooltip: () => Helper.Translation.Get("config.MaxAmbientCutscenesPerDay.desc"),
                min: 1,
                max: 6,
                interval: 1);
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EchoNpcConversationsToChat,
                setValue: v => _config.EchoNpcConversationsToChat = v,
                name: () => Helper.Translation.Get("config.EchoNpcConversationsToChat.label"),
                tooltip: () => Helper.Translation.Get("config.EchoNpcConversationsToChat.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableSpontaneousVisits,
                setValue: v => _config.EnableSpontaneousVisits = v,
                name: () => Helper.Translation.Get("config.EnableSpontaneousVisits.label"),
                tooltip: () => Helper.Translation.Get("config.EnableSpontaneousVisits.desc"));

            // ════════════════════════════════════════════════════════════════
            // SUBPÁGINA 4: GRANJA, SEGUIMIENTO Y COMBATE EN MINAS
            // ════════════════════════════════════════════════════════════════
            gmcm.AddPage(ModManifest, "page_farm_combat", () => Utilities.I18n.Get("config.page.farm_combat.title"));

            gmcm.AddSectionTitle(ModManifest, () => Utilities.I18n.Get("config.section.follow"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.FollowDistanceTiles,
                setValue: v => _config.FollowDistanceTiles = v,
                name: () => Helper.Translation.Get("config.FollowDistanceTiles.label"),
                tooltip: () => Helper.Translation.Get("config.FollowDistanceTiles.desc"),
                min: 1, max: 8);
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.ComeHereTimeoutMinutes,
                setValue: v => _config.ComeHereTimeoutMinutes = v,
                name: () => Helper.Translation.Get("config.ComeHereTimeoutMinutes.label"),
                tooltip: () => Helper.Translation.Get("config.ComeHereTimeoutMinutes.desc"),
                min: 10, max: 120, interval: 5);
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.AllowCrossMapFollow,
                setValue: v => _config.AllowCrossMapFollow = v,
                name: () => Helper.Translation.Get("config.AllowCrossMapFollow.label"),
                tooltip: () => Helper.Translation.Get("config.AllowCrossMapFollow.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableFollowerActionMenu,
                setValue: v => _config.EnableFollowerActionMenu = v,
                name: () => Helper.Translation.Get("config.EnableFollowerActionMenu.label"),
                tooltip: () => Helper.Translation.Get("config.EnableFollowerActionMenu.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.AllowHostOverrideCompanion,
                setValue: v => _config.AllowHostOverrideCompanion = v,
                name: () => Helper.Translation.Get("config.AllowHostOverrideCompanion.label"),
                tooltip: () => Helper.Translation.Get("config.AllowHostOverrideCompanion.desc"));

            gmcm.AddSectionTitle(ModManifest, () => Utilities.I18n.Get("config.section.farm_work"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.AllowFarmWork,
                setValue: v => _config.AllowFarmWork = v,
                name: () => Helper.Translation.Get("config.AllowFarmWork.label"),
                tooltip: () => Helper.Translation.Get("config.AllowFarmWork.desc"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.FarmWorkRadius,
                setValue: v => _config.FarmWorkRadius = v,
                name: () => Helper.Translation.Get("config.FarmWorkRadius.label"),
                tooltip: () => Helper.Translation.Get("config.FarmWorkRadius.desc"),
                min: 5, max: 100, interval: 5);
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.AllowFullMapFarmScan,
                setValue: v => _config.AllowFullMapFarmScan = v,
                name: () => Helper.Translation.Get("config.AllowFullMapFarmScan.label"),
                tooltip: () => Helper.Translation.Get("config.AllowFullMapFarmScan.desc"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MaxCropsPerWaterJob,
                setValue: v => _config.MaxCropsPerWaterJob = v,
                name: () => Helper.Translation.Get("config.MaxCropsPerWaterJob.label"),
                tooltip: () => Helper.Translation.Get("config.MaxCropsPerWaterJob.desc"),
                min: 10, max: 500, interval: 10);
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.ProtectFlowersNearBeeHouses,
                setValue: v => _config.ProtectFlowersNearBeeHouses = v,
                name: () => Helper.Translation.Get("config.ProtectFlowersNearBeeHouses.label"),
                tooltip: () => Helper.Translation.Get("config.ProtectFlowersNearBeeHouses.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.StoreHarvestInChests,
                setValue: v => _config.StoreHarvestInChests = v,
                name: () => Helper.Translation.Get("config.StoreHarvestInChests.label"),
                tooltip: () => Helper.Translation.Get("config.StoreHarvestInChests.desc"));

            gmcm.AddSectionTitle(ModManifest, () => Utilities.I18n.Get("config.section.combat"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableCompanionCombat,
                setValue: v => _config.EnableCompanionCombat = v,
                name: () => Helper.Translation.Get("config.EnableCompanionCombat.label"),
                tooltip: () => Helper.Translation.Get("config.EnableCompanionCombat.desc"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableCompanionBuffs,
                setValue: v => _config.EnableCompanionBuffs = v,
                name: () => Helper.Translation.Get("config.EnableCompanionBuffs.label"),
                tooltip: () => Helper.Translation.Get("config.EnableCompanionBuffs.desc"));

            // ════════════════════════════════════════════════════════════════
            // SUBPÁGINA 5: REGISTROS, ECONOMÍA, CORAZONES Y DEPURACIÓN
            // ════════════════════════════════════════════════════════════════
            gmcm.AddPage(ModManifest, "page_advanced", () => Utilities.I18n.Get("config.page.advanced.title"));

            gmcm.AddSectionTitle(ModManifest, () => Utilities.I18n.Get("config.section.roster_memory"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.AllowRecruitingChildren,
                setValue: v => _config.AllowRecruitingChildren = v,
                name: () => Utilities.I18n.Get("config.offline.allow_children.name"),
                tooltip: () => Utilities.I18n.Get("config.offline.allow_children.tooltip"));
            gmcm.AddTextOption(ModManifest,
                getValue: () => _config.ExcludedNpcNames,
                setValue: v => _config.ExcludedNpcNames = v,
                name: () => Utilities.I18n.Get("config.offline.excluded_npcs.name"),
                tooltip: () => Utilities.I18n.Get("config.offline.excluded_npcs.tooltip"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.EnableMemoryFeed,
                setValue: v => _config.EnableMemoryFeed = v,
                name: () => Helper.Translation.Get("config.EnableMemoryFeed.label"),
                tooltip: () => Helper.Translation.Get("config.EnableMemoryFeed.desc"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MemoryDailyLimit,
                setValue: v => _config.MemoryDailyLimit = v,
                name: () => Helper.Translation.Get("config.MemoryDailyLimit.label"),
                tooltip: () => Helper.Translation.Get("config.MemoryDailyLimit.desc"),
                min: 1, max: 20, interval: 1);
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.BypassDailyLimitWhenFollowing,
                setValue: v => _config.BypassDailyLimitWhenFollowing = v,
                name: () => Helper.Translation.Get("config.BypassDailyLimitWhenFollowing.label"),
                tooltip: () => Helper.Translation.Get("config.BypassDailyLimitWhenFollowing.desc"));

            gmcm.AddSectionTitle(ModManifest, () => Utilities.I18n.Get("config.section.hearts_gifts"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MaxGiftQuantity,
                setValue: v => _config.MaxGiftQuantity = v,
                name: () => Helper.Translation.Get("config.MaxGiftQuantity.label"),
                tooltip: () => Helper.Translation.Get("config.MaxGiftQuantity.desc"),
                min: 1, max: 10);
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MaxGiftItemValue,
                setValue: v => _config.MaxGiftItemValue = v,
                name: () => Helper.Translation.Get("config.MaxGiftItemValue.label"),
                tooltip: () => Helper.Translation.Get("config.MaxGiftItemValue.desc"),
                min: 50, max: 5000, interval: 50);
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.AllowRareNpcGifts,
                setValue: v => _config.AllowRareNpcGifts = v,
                name: () => Helper.Translation.Get("config.AllowRareNpcGifts.label"),
                tooltip: () => Helper.Translation.Get("config.AllowRareNpcGifts.desc"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MinHeartsFollow,
                setValue: v => _config.MinHeartsFollow = v,
                name: () => Helper.Translation.Get("config.MinHeartsFollow.label"),
                min: 0, max: 10);
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MinHeartsVisitFarm,
                setValue: v => _config.MinHeartsVisitFarm = v,
                name: () => Helper.Translation.Get("config.MinHeartsVisitFarm.label"),
                min: 0, max: 10);
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MinHeartsHug,
                setValue: v => _config.MinHeartsHug = v,
                name: () => Helper.Translation.Get("config.MinHeartsHug.label"),
                min: 0, max: 10);
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MinHeartsFarmWork,
                setValue: v => _config.MinHeartsFarmWork = v,
                name: () => Helper.Translation.Get("config.MinHeartsFarmWork.label"),
                min: 0, max: 10);
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MinHeartsChestAccess,
                setValue: v => _config.MinHeartsChestAccess = v,
                name: () => Helper.Translation.Get("config.MinHeartsChestAccess.label"),
                min: 0, max: 10);
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MinHeartsEscort,
                setValue: v => _config.MinHeartsEscort = v,
                name: () => Helper.Translation.Get("config.MinHeartsEscort.label"),
                min: 0, max: 10);
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MinHeartsGiveItem,
                setValue: v => _config.MinHeartsGiveItem = v,
                name: () => Helper.Translation.Get("config.MinHeartsGiveItem.label"),
                min: 0, max: 10);
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MinHeartsEmote,
                setValue: v => _config.MinHeartsEmote = v,
                name: () => Helper.Translation.Get("config.MinHeartsEmote.label"),
                min: 0, max: 10);
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MinHeartsMeetAtTime,
                setValue: v => _config.MinHeartsMeetAtTime = v,
                name: () => Helper.Translation.Get("config.MinHeartsMeetAtTime.label"),
                min: 0, max: 10);

            gmcm.AddSectionTitle(ModManifest, () => Utilities.I18n.Get("config.section.safety_debug"));
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.BlockDuringEvents,
                setValue: v => _config.BlockDuringEvents = v,
                name: () => Helper.Translation.Get("config.BlockDuringEvents.label"),
                tooltip: () => Helper.Translation.Get("config.BlockDuringEvents.desc"));
            gmcm.AddNumberOption(ModManifest,
                getValue: () => _config.MaxActionsPerNpcPerDay,
                setValue: v => _config.MaxActionsPerNpcPerDay = v,
                name: () => Helper.Translation.Get("config.MaxActionsPerNpcPerDay.label"),
                tooltip: () => Helper.Translation.Get("config.MaxActionsPerNpcPerDay.desc"),
                min: 5, max: 200, interval: 5);
            gmcm.AddBoolOption(ModManifest,
                getValue: () => _config.DebugLogging,
                setValue: v => _config.DebugLogging = v,
                name: () => Helper.Translation.Get("config.DebugLogging.label"),
                tooltip: () => Helper.Translation.Get("config.DebugLogging.desc"));
        }
        catch (Exception ex)
        {
            Monitor.Log($"GMCM registration failed: {ex.Message}", LogLevel.Trace);
        }
    }
}
