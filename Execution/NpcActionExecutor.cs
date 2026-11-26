using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Objects;
using StardewValley.Pathfinding;
using StardewValley.Locations;
using StardewValley.Buildings;
using ValleyBuddies.Systems;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Handlers.Farm;
using ValleyBuddies.Handlers.Social;
using ValleyBuddies.Handlers.External;

namespace ValleyBuddies.Execution;


// ─────────────────── NpcActionExecutor (singleton) ──────────────────

/// <summary>
/// Drives physical agency for NPCs on the main thread.
/// Updates movement, pathfinding without clipping walls, cross-map following, and farm help.
/// </summary>
public sealed partial class NpcActionExecutor
{
    public static readonly NpcActionExecutor Instance = new();

    private readonly INpcNavigator _navigator = new NpcNavigatorAdapter();
    private readonly IFarmWorldService _farmWorld = new FarmWorldService();
    private readonly JobHandlerRegistry _jobHandlers = new();

    public INpcNavigator Navigator => _navigator;
    public IFarmWorldService FarmWorld => _farmWorld;
    public JobHandlerRegistry JobHandlers => _jobHandlers;

    private NpcActionExecutor()
    {
        RegisterBuiltInJobHandlers();
    }

    private void RegisterBuiltInJobHandlers()
    {
        _jobHandlers.Register(new HarvestForageHandler());
        _jobHandlers.Register(new TakeFromChestHandler());
        _jobHandlers.Register(new SitOrRestHandler());

        _jobHandlers.Register(new HarvestCropsHandler());
        _jobHandlers.Register(new ClearDebrisHandler());
        _jobHandlers.Register(new PetAnimalsHandler());
        _jobHandlers.Register(new MineNearbyRockHandler());
        _jobHandlers.Register(new ChopWoodHandler());
        _jobHandlers.Register(new ChopTreesHandler());
        _jobHandlers.Register(new FetchFromChestHandler());

        _jobHandlers.Register(new GuardHereHandler());
        _jobHandlers.Register(new TakeBreakHandler());
        _jobHandlers.Register(new TakePhotoHandler());
        _jobHandlers.Register(new PicnicHandler());
        _jobHandlers.Register(new AdmireFarmHandler());
        _jobHandlers.Register(new ShowEmoteHandler());
        _jobHandlers.Register(new FacePlayerHandler());
        _jobHandlers.Register(new DoAnimationHandler());
        _jobHandlers.Register(new HugOrCelebrateHandler());
        _jobHandlers.Register(new GiveItemHandler());
        _jobHandlers.Register(new MeetAtTimeHandler());
        _jobHandlers.Register(new LeaveFarmHandler());
        _jobHandlers.Register(new EscortHomeHandler());
        _jobHandlers.Register(new ExternalTaskHandler());
    }

    internal static void MoveTowardsInternal(NpcJob job, NPC npc, GameLocation loc, Point targetTile, ulong gameTick)
    {
        MoveNpcTowards(job, npc, loc, targetTile, gameTick);
    }

    internal static void StepFollowInternal(NpcJob job, NPC npc, ulong gameTick)
    {
        Instance.TickFollow(job, npc, gameTick);
    }

    private readonly ConcurrentQueue<NpcJob>               _incoming     = new();
    private readonly Dictionary<string, NpcJob>             _active       = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Vector2 Pos, string LocName, ulong LastLogTick)> _teleportGuardTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int>                _dailyCounts  = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string LocationName, Vector2 Tile), ulong> _modOpenedGates = new();
    private readonly Dictionary<string, ulong> _lastGateSoundTickByLoc = new(StringComparer.OrdinalIgnoreCase);

    public void ResetGateTracking()
    {
        _modOpenedGates.Clear();
        _lastGateSoundTickByLoc.Clear();
    }

    private readonly ConcurrentDictionary<string, EveningResolutionState> _eveningResolutions = new(StringComparer.OrdinalIgnoreCase);

    public EveningResolutionState GetEveningResolution(string npcName) =>
        _eveningResolutions.TryGetValue(npcName, out var st) ? st : EveningResolutionState.Undecided;

    public void SetEveningResolution(string npcName, EveningResolutionState state) =>
        _eveningResolutions[npcName] = state;

    public void ResetEveningResolutions() =>
        _eveningResolutions.Clear();


    public bool IsManagedCompanion(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return false;
        if (_companions.TryGetValue(npcName, out var comp) && (comp.IsFollowing || comp.IsVisitingFarm || comp.IsRestingInPlace || comp.NightState != NightLifecycleState.DaytimeActive))
            return true;
        return _active.TryGetValue(npcName, out var job) && !job.Done;
    }

    public IReadOnlyList<string> GetActiveCompanionNames()
    {
        var list = new List<string>();
        foreach (var kvp in _companions)
        {
            if (IsManagedCompanion(kvp.Key))
            {
                list.Add(kvp.Key);
            }
        }
        return list;
    }

    public bool IsSpouseInHomeRoutine(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return false;
        if (_config?.EnableSpouseLivingHome == false) return false;
        var spouseFarmer = Game1.getAllFarmers().FirstOrDefault(f => f.isMarriedOrRoommates() && string.Equals(f.spouse, npcName, StringComparison.OrdinalIgnoreCase));
        if (spouseFarmer == null) return false;

        if (TryGetSpouseHomeState(npcName, out var sState) && sState.YieldedToTownScheduleToday)
        {
            var npc = Game1.getCharacterFromName(npcName);
            var house = GetOwnerHouse(spouseFarmer);
            if (npc != null && house != null && (npc.currentLocation != house || Game1.timeOfDay < 1700))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Helper forense reutilizable: devuelve true si el NPC está casado, es roommate, o es cónyuge registrado de cualquier granjero.
    /// </summary>
    public static bool IsMarriedToAnyFarmer(NPC? npc)
    {
        if (npc == null) return false;
        if (npc.isMarried() || npc.isRoommate()) return true;
        foreach (var f in Game1.getAllFarmers())
        {
            if (f != null && !string.IsNullOrWhiteSpace(f.spouse) && string.Equals(f.spouse, npc.Name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public bool IsNpcInOvernightAnchorState(string? npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return false;

        if (_active.TryGetValue(npcName, out var job) && !job.Done
            && (job.Kind == JobKind.Campfire || job.Kind == JobKind.CampSleepTogether || job.Kind == JobKind.Sleepover))
        {
            return true;
        }

        if (_overnightGuests.ContainsKey(npcName))
            return true;

        if (_companions.TryGetValue(npcName, out var comp))
        {
            if (comp.EveningResolution == EveningResolutionState.Camping || comp.EveningResolution == EveningResolutionState.StayingInHouse)
                return true;
            if (comp.IsSettledInsideHouseForNight || comp.NightState is NightLifecycleState.SleepingInBed or NightLifecycleState.CampOutdoorIdle or NightLifecycleState.IndoorRoaming or NightLifecycleState.IndoorResting or NightLifecycleState.NightSettled)
                return true;
        }

        return false;
    }

    public static void BroadcastTemporarySprite(GameLocation? loc, TemporaryAnimatedSprite? sprite)
    {
        if (loc == null || sprite == null) return;
        if (Game1.IsMultiplayer && Game1.Multiplayer != null)
            Game1.Multiplayer.broadcastSprites(loc, sprite);
        else
            loc.temporarySprites.Add(sprite);
    }

    public static bool IsRestoringNormalClothes = false;
    public static bool IsApplyingBeachOutfit = false;

    public bool IsCompanionWearingBeachOutfit(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return false;
        return _companions.TryGetValue(npcName, out var c) && c.IsWearingBeachOutfit;
    }

    public static bool HasValidBeachOutfit(NPC npc)
    {
        if (npc == null) return false;
        try
        {
            if (Game1.content.DoesAssetExist<Microsoft.Xna.Framework.Graphics.Texture2D>($"Characters/{npc.Name}_Beach"))
                return true;

            var data = npc.GetData();
            if (data?.Appearance != null)
            {
                foreach (var app in data.Appearance)
                {
                    if (app.IsIslandAttire || (app.Id != null && app.Id.Contains("Beach", StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
            }
        }
        catch { }
        return false;
    }


private readonly ConcurrentDictionary<string, CompanionState> _companions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<StoredItemRecord>                 _chestMemory  = new();
    private SpontaneousVisitPlan?                           _pendingSpontaneousVisit;
    private readonly ConcurrentDictionary<string, OvernightGuestInfo> _overnightGuests     = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>                                 _nightPromptedTonight = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>                                 _caveGreetedNPCs      = new(StringComparer.OrdinalIgnoreCase);
    private static string?                                            _pendingMorningNote;

    private static readonly Dictionary<string, int> _dailyActivityFriendship = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _dailyActivityRewardOnce = new(StringComparer.OrdinalIgnoreCase);

    public static void ResetDailySocialCaps()
    {
        _dailyActivityFriendship.Clear();
        _dailyActivityRewardOnce.Clear();
    }

    public static bool TryAwardDailyActivityFriendship(Farmer? player, NPC? npc, string activityKey, int points, int dailyMaxPoints)
    {
        if (player == null || npc == null || points <= 0 || dailyMaxPoints <= 0) return false;
        string key = $"{player.UniqueMultiplayerID}_{npc.Name}_{activityKey}";
        int used = _dailyActivityFriendship.TryGetValue(key, out int cur) ? cur : 0;
        if (used >= dailyMaxPoints) return false;
        int award = Math.Min(points, dailyMaxPoints - used);
        _dailyActivityFriendship[key] = used + award;
        player.changeFriendship(award, npc);
        return true;
    }

    public static bool TryConsumeDailyRewardOnce(Farmer? player, NPC? npc, string activityKey)
    {
        if (player == null || npc == null) return false;
        string key = $"{player.UniqueMultiplayerID}_{npc.Name}_{activityKey}";
        return _dailyActivityRewardOnce.Add(key);
    }

    public void RecordDepositedItem(string npcName, string itemId, string displayName, int qty, Point chestTile)
    {
        var existing = _chestMemory.FirstOrDefault(r => r.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase)
                                                     && r.ItemId == itemId
                                                     && r.ChestTile == chestTile);
        if (existing != null)
        {
            existing.Quantity += qty;
        }
        else
        {
            _chestMemory.Add(new StoredItemRecord
            {
                NpcName = npcName,
                ItemId = itemId,
                ItemDisplayName = displayName,
                Quantity = qty,
                ChestTile = chestTile
            });
        }
    }

    public bool IsLeavingFarm(string name) =>
        _active.TryGetValue(name, out var j) && j.Kind == JobKind.LeaveFarm;

    public bool HasActiveJob(string name) => _active.ContainsKey(name);

    public bool IsIntentionalWarpAllowed(string name)
    {
        if (_active.TryGetValue(name, out var j))
        {
            return j.Kind == JobKind.LeaveFarm || j.Kind == JobKind.GoToLocation || (j.Data.TryGetValue("allow_warp", out var w) && w is true);
        }
        return false;
    }

    public List<NpcJob> GetAllActiveJobsSnapshot() => _active.Values.ToList();

    private IMonitor?                            _monitor;
    private Memory.ActionMemory?                 _memory;
    private Config.ActionsConfig?                _config;
    private Systems.DynamicMailService?          _mailService;
    private Multiplayer.MultiplayerSyncService?  _multiplayerSync;
    private int                                  _maxPerDay       = 50;
    private int                                  _followDistTiles = 2;
    private bool                                 _allowCrossMap   = true;
    private bool                                 _debug           = false;
    private ulong                                _dayStartedTick;

    public void SetMultiplayerSync(Multiplayer.MultiplayerSyncService sync) => _multiplayerSync = sync;
    public void SetMailService(Systems.DynamicMailService mail) => _mailService = mail;

    public void Configure(IMonitor monitor, Memory.ActionMemory memory, Config.ActionsConfig config)
    {
        _monitor         = monitor ?? _monitor;
        _memory          = memory ?? _memory;
        _config          = config;
        _maxPerDay       = Math.Clamp(config.MaxActionsPerNpcPerDay, 5, 200);
        _followDistTiles = config.FollowDistanceTiles;
        _allowCrossMap   = config.AllowCrossMapFollow;
        _debug           = config.DebugLogging;
    }

    public void ClearPendingMorningNote()
    {
        _pendingMorningNote = null;
    }

    /// <summary>
    /// Aplica cambios de GMCM en caliente sobre la sesión activa (cónyuges, combate, límites, etc.).
    /// </summary>
    public void ApplyLiveConfigChanges(Config.ActionsConfig cfg)
    {
        if (cfg == null) return;
        _config          = cfg;
        _maxPerDay       = Math.Clamp(cfg.MaxActionsPerNpcPerDay, 5, 200);
        _followDistTiles = cfg.FollowDistanceTiles;
        _allowCrossMap   = cfg.AllowCrossMapFollow;
        _debug           = cfg.DebugLogging;

        if (!cfg.EnableCompanionCombat)
        {
            foreach (var comp in _companions.Values)
            {
                comp.CurrentCaveCombatTarget = null;
            }
        }

        if (!cfg.EnableSpouseLivingHome)
        {
            foreach (var kvp in _spouseHomeStates.ToList())
            {
                string spouseName = kvp.Key;
                ReleaseSpouseClaims(spouseName);
                var spouse = FindNpc(spouseName);
                if (spouse != null)
                {
                    NpcAnimationService.StopAnimationCleanly(spouse);
                    spouse.Halt();
                    spouse.controller = null;
                    spouse.temporaryController = null;
                    spouse.addedSpeed = 0;

                    if (!IsManagedCompanion(spouseName))
                    {
                        var owner = Game1.GetPlayer(kvp.Value.OwnerFarmerId, true) ?? Game1.player;
                        if (spouse.isSleeping.Value || (spouse.currentLocation != null && IsNpcInsideBedFootprint(spouse, spouse.currentLocation, out _)))
                        {
                            WakeAndExtractNpcFromBed(spouse, spouse.currentLocation, null, owner);
                        }
                        spouse.followSchedule = true;
                        try { spouse.checkSchedule(Game1.timeOfDay); } catch { }
                    }
                }
            }
            _spouseHomeStates.Clear();
            _spouseNavJobs.Clear();
            ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.ClearPendingSpouseDates();
        }
    }

    internal void EndOutingCompanionSilently(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;
        if (_companions.TryGetValue(npcName, out var state))
        {
            state.IsPausedWhileOwnerOutsideHouse = false;
            state.FrozenHousePosition = null;
            state.IsSettledInsideHouseForNight = false;
            state.SettledHouseName = null;
        }
        var npc = FindNpc(npcName);
        _companions.TryRemove(npcName, out _);
        AbortCurrentJob(npcName);
        RemoveCompanionNightTorch(npcName, npc?.currentLocation);
        RemoveCompanionCaveLight(npcName);

        if (npc == null)
        {
            ReleaseNpc(npcName);
            return;
        }

        npc.addedSpeed = 0;
        npc.controller = null;
        npc.temporaryController = null;
        npc.Halt();

        var marriedFarmer = Game1.getAllFarmers().FirstOrDefault(f => f != null && f.isMarriedOrRoommates() && string.Equals(f.spouse, npc.Name, StringComparison.OrdinalIgnoreCase));
        if (marriedFarmer != null && _config?.EnableSpouseLivingHome != false)
        {
            ResumeSpouseHomeAfterDismiss(npc, marriedFarmer, ModEntry.CurrentTick);
            return;
        }

        npc.followSchedule = true;
        try { npc.checkSchedule(Game1.timeOfDay); } catch { }
    }

    public void RecordMemory(string npcName, string description)
    {
        _memory?.RecordEvent(npcName, description);
        if (_config?.EnableMemoryFeed == true)
        {
            ValleyBuddies.Bridge.ValleyBuddiesMemoryFeed.Send(description, _config.MemoryDailyLimit);
        }
    }

    public void Enqueue(NpcJob job) => _incoming.Enqueue(job);

    public void SetCompanion(string name, long farmerId, bool isVisitingFarm, bool isFollowing, string farmerName = "")
    {
        if (string.IsNullOrEmpty(farmerName) && farmerId != 0)
        {
            var f = Game1.GetPlayer(farmerId, true) ?? Game1.MasterPlayer;
            farmerName = f?.Name ?? Game1.player?.Name ?? "el granjero";
        }

        if (!_companions.TryGetValue(name, out var state))
        {
            state = new CompanionState
            {
                NpcName = name,
                FarmerId = farmerId,
                FarmerName = farmerName,
                IsVisitingFarm = isVisitingFarm,
                IsFollowing = isFollowing
            };
            _companions[name] = state;
        }
        else
        {
            if (state.FarmerId != 0)
            {
                if (farmerId == 0)
                {
                    // Conserva state.FarmerId y state.FarmerName
                }
                else if (farmerId != state.FarmerId)
                {
                    var prevOwner = Game1.GetPlayer(state.FarmerId, onlyOnline: true);
                    if (prevOwner != null && _config?.AllowHostOverrideCompanion != true)
                    {
                        // NO sobrescribas state.FarmerId ni state.FarmerName (mantén siempre al jugador que lo invitó primero!)
                    }
                    else
                    {
                        state.FarmerId = farmerId;
                        state.FarmerName = farmerName;
                    }
                }
                else
                {
                    state.FarmerId = farmerId;
                    if (!string.IsNullOrEmpty(farmerName))
                        state.FarmerName = farmerName;
                }
            }
            else
            {
                state.FarmerId = farmerId;
                state.FarmerName = farmerName;
            }

            state.IsVisitingFarm = isVisitingFarm;
            state.IsFollowing = isFollowing;
        }

        if (isFollowing)
        {
            var npc = Game1.getCharacterFromName(name);
            bool wasSleepingOrGoingToBed = (npc != null && npc.isSleeping.Value)
                || state.NightState == NightLifecycleState.SleepingInBed
                || state.NightState == NightLifecycleState.GoingToBed;
            if (wasSleepingOrGoingToBed)
            {
                state.WokeUpByPlayerTonight = true;
            }

            state.NightState = NightLifecycleState.DaytimeActive;
            state.IsRestingInPlace = false;
            state.IsSettledInsideHouseForNight = false;
            state.SettledHouseName = null;
            state.IsPausedWhileOwnerOutsideHouse = false;
            state.FrozenHousePosition = null;
            state.LockedBedPixelPosition = null;
            state.AssignedBedSpot = null;

            if (npc != null && (npc.isSleeping.Value || (npc.currentLocation != null && !npc.currentLocation.isTilePassable(new xTile.Dimensions.Location(npc.TilePoint.X, npc.TilePoint.Y), Game1.viewport))))
            {
                WakeAndExtractNpcFromBed(npc, npc.currentLocation, state, Game1.GetPlayer(farmerId, true) ?? Game1.player);
            }
        }

        ReleaseSpouseClaims(name);
    }

    public bool TryGetCompanionState(string npcName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CompanionState? state)
    {
        return _companions.TryGetValue(npcName, out state);
    }

    public bool TryGetCompanionInfo(string npcName, out (long FarmerId, string FarmerName, bool IsFollowing, bool IsVisitingFarm) info)
    {
        if (_companions.TryGetValue(npcName, out var comp))
        {
            info = (comp.FarmerId, comp.FarmerName, comp.IsFollowing, comp.IsVisitingFarm);
            return true;
        }
        if (_active.TryGetValue(npcName, out var job) && !job.Done)
        {
            info = (job.FarmerId, job.FarmerName, job.Kind == JobKind.Follow, job.Kind == JobKind.VisitFarm || Game1.getFarm() != null);
            return true;
        }
        info = default;
        return false;
    }

    public void DismissCompanion(string name)
    {
        var dismissOwner = GetCompanionOwner(name) ?? Game1.player;

        // v4.3.3: Limpiar anclaje de posición en casa al despedir
        if (_companions.TryGetValue(name, out var stateToClean))
        {
            stateToClean.IsPausedWhileOwnerOutsideHouse = false;
            stateToClean.FrozenHousePosition = null;
            stateToClean.IsSettledInsideHouseForNight = false;
            stateToClean.SettledHouseName = null;
        }
        var npc = FindNpc(name);
        _companions.TryRemove(name, out _);
        AbortCurrentJob(name);

        if (npc == null || npc.currentLocation == null)
        {
            ReleaseNpc(name);
            return;
        }

        var loc = npc.currentLocation;

        // Clean up companion torchlight if any
        RemoveCompanionNightTorch(name, loc);

        RemoveCompanionCaveLight(name);

        npc.addedSpeed = 0;
        npc.controller = null;
        npc.Halt();
        if (dismissOwner != null)
            npc.faceGeneralDirection(dismissOwner.Position);

        // v4.5.1: Si es el cónyuge casado/roommate de algún granjero, devolver el control a SpouseHome mediante ResumeSpouseHomeAfterDismiss
        var marriedFarmer = Game1.getAllFarmers().FirstOrDefault(f => f.isMarriedOrRoommates() && string.Equals(f.spouse, npc.Name, StringComparison.OrdinalIgnoreCase));
        if (marriedFarmer != null)
        {
            ResumeSpouseHomeAfterDismiss(npc, marriedFarmer, ModEntry.CurrentTick);
            return;
        }

        npc.showTextAboveHead(Utilities.I18n.Get("bubble.leave_farewell"), duration: 3800);

        int[] emotes = new[] { 32, 20, 56 };
        npc.doEmote(emotes[Game1.random.Next(emotes.Length)]);

        RecordMemory(npc.Name, "I had a wonderful time hanging out with the farmer today, and then headed back home.");

        // 3-second delay so the user can enjoy the farewell animation and message before the teleport sound & warp
        DelayedAction.functionAfterDelay(() =>
        {
            try
            {
                if (IsManagedCompanion(npc.Name) || _companions.ContainsKey(npc.Name) ||
                    (_active.TryGetValue(npc.Name, out var activeAfterDismiss) && !activeAfterDismiss.Done))
                {
                    return;
                }

                var curLoc = npc.currentLocation ?? loc;
                curLoc.playSound("wand");
                // curLoc.playSound("warp"); - removed non-existent sound

                Vector2 centerPixel = new Vector2(npc.TilePoint.X * 64f + 32f, npc.TilePoint.Y * 64f + 32f);
                for (int i = 0; i < 20; i++)
                {
                    BroadcastTemporarySprite(curLoc, new TemporaryAnimatedSprite(
                        "LooseSprites\\Cursors",
                        new Rectangle(372, 1956, 10, 10),
                        new Vector2(centerPixel.X + Game1.random.Next(-28, 29), centerPixel.Y + Game1.random.Next(-28, 29)),
                        flipped: false,
                        0.002f,
                        Color.LightSkyBlue)
                    {
                        scale = 3.0f,
                        motion = new Vector2((float)(Game1.random.NextDouble() * 3.0 - 1.5), (float)(Game1.random.NextDouble() * -2.5 - 0.5)),
                        acceleration = new Vector2(0f, 0.04f),
                        interval = 90f,
                        totalNumberOfLoops = 2,
                        animationLength = 6
                    });
                }
                BroadcastTemporarySprite(curLoc, new TemporaryAnimatedSprite(
                    28,
                    new Vector2(npc.TilePoint.X * 64f, npc.TilePoint.Y * 64f),
                    Color.MediumPurple)
                {
                    interval = 80f
                });

                string targetMap = !string.IsNullOrWhiteSpace(npc.DefaultMap) ? npc.DefaultMap : "BusStop";
                Vector2 targetTile = (npc.DefaultPosition != Vector2.Zero) ? (npc.DefaultPosition / 64f) : new Vector2(10, 10);

                Game1.warpCharacter(npc, targetMap, targetTile);
                npc.faceDirection(npc.DefaultFacingDirection);
                npc.controller = null;
                npc.Halt();
                npc.speed = 2;
                npc.followSchedule = true;
                npc.swimming.Value = false;
                npc.drawOffset = Vector2.Zero;
                try
                {
                    npc.shouldWearIslandAttire.Value = false;
                    IsRestoringNormalClothes = true;
                    try
                    {
                        npc.wearNormalClothes();
                        npc.ChooseAppearance();
                    }
                    finally
                    {
                        IsRestoringNormalClothes = false;
                    }
                }
                catch { }
                try
                {
                    npc.checkSchedule(Game1.timeOfDay);
                }
                catch { }

                if (_spouseHomeStates.TryGetValue(npc.Name, out var sState))
                {
                    sState.NextActionTick = ModEntry.CurrentTick + 60;
                }

                var destLoc = Game1.getLocationFromName(targetMap);
                if (destLoc != null)
                {
                    Vector2 destPixel = targetTile * 64f;
                    BroadcastTemporarySprite(destLoc, new TemporaryAnimatedSprite(
                        28,
                        destPixel,
                        Color.MediumPurple)
                    {
                        interval = 60f
                    });
                }

                _debug_log($"[Actions] Dismissed and warped {npc.Name} to {targetMap} at {targetTile}.");
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[Actions] Error teleporting {npc.Name} back to schedule: {ex.Message}", LogLevel.Trace);
                ReleaseNpc(name);
            }
        }, 3000);
    }

    public NPC? GetActiveFollower()
    {
        var kv = _active.FirstOrDefault(j => (j.Value.Kind == JobKind.Follow || j.Value.Kind == JobKind.EscortHome) && !j.Value.Done);
        return kv.Key != null ? FindNpc(kv.Key) : null;
    }

    public NPC? GetActiveFollowerForPlayer(long farmerId)
    {
        var kv = _active.FirstOrDefault(j =>
            (j.Value.Kind == JobKind.Follow || j.Value.Kind == JobKind.EscortHome) &&
            !j.Value.Done &&
            (j.Value.OwnerPlayerId == farmerId || (_companions.TryGetValue(j.Key, out var c) && c.FarmerId == farmerId)));
        return kv.Key != null ? FindNpc(kv.Key) : null;
    }

    public NPC? GetActiveFollowerOrVisitor(GameLocation? loc, Vector2 grabTile) =>
        GetActiveFollowerOrVisitor(loc, grabTile, grabTile);

    public NPC? GetActiveFollowerOrVisitor(GameLocation? loc, Vector2 cursorTile, Vector2 grabTile)
    {
        if (loc == null) return null;

        // If clicking on a chest, machine, or container, NEVER intercept! Let the chest open normally!
        if (loc.objects.TryGetValue(cursorTile, out var cObj) && (cObj is StardewValley.Objects.Chest || cObj.bigCraftable.Value || cObj.Name.Contains("Chest", StringComparison.OrdinalIgnoreCase)))
            return null;
        if (loc.objects.TryGetValue(grabTile, out var gObj) && (gObj is StardewValley.Objects.Chest || gObj.bigCraftable.Value || gObj.Name.Contains("Chest", StringComparison.OrdinalIgnoreCase)))
            return null;

        // 1. Direct hit or immediate adjacent click to any mod-active NPC (max 1.25 tiles)
        foreach (var kv in _active.Where(j => !j.Value.Done))
        {
            var npc = FindNpc(kv.Key);
            if (npc != null && npc.currentLocation == loc)
            {
                float distToCursor = Vector2.Distance(npc.Tile, cursorTile);
                float distToGrab   = Vector2.Distance(npc.Tile, grabTile);

                if (distToCursor <= 1.25f || distToGrab <= 1.25f)
                    return npc;
            }
        }

        // 2. Direct hit or immediate adjacent click to any companion NPC
        foreach (var compKey in _companions.Keys)
        {
            var compNpc = FindNpc(compKey);
            if (compNpc != null && compNpc.currentLocation == loc)
            {
                float distToCursor = Vector2.Distance(compNpc.Tile, cursorTile);
                float distToGrab   = Vector2.Distance(compNpc.Tile, grabTile);

                if (distToCursor <= 1.25f || distToGrab <= 1.25f)
                    return compNpc;
            }
        }

        return null;
    }

    public string? GetActiveFollowerName()
    {
        var kv = _active.FirstOrDefault(j => (j.Value.Kind == JobKind.Follow || j.Value.Kind == JobKind.EscortHome) && !j.Value.Done);
        return kv.Key;
    }

    public bool IsNpcFollowingOrVisiting(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return false;
        if (_companions.ContainsKey(npcName)) return true;
        return _active.TryGetValue(npcName, out var job) && !job.Done;
    }

    public Farmer? GetCompanionOwner(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return null;
        if (_companions.TryGetValue(npcName, out var comp) && comp.FarmerId != 0)
        {
            var owner = Game1.GetPlayer(comp.FarmerId, onlyOnline: true) ?? Game1.GetPlayer(comp.FarmerId, onlyOnline: false);
            if (owner != null) return owner;
        }
        if (_active.TryGetValue(npcName, out var job))
            return GetJobFarmer(job);
        return null;
    }

    internal static bool IsTemporaryEveningActivity(JobKind kind) => kind is
        JobKind.TakePhoto or
        JobKind.TakeBreak or
        JobKind.FetchFromChest or
        JobKind.ShowEmote or
        JobKind.FacePlayer or
        JobKind.DoAnimation or
        JobKind.HugOrCelebrate or
        JobKind.GiveItem or
        JobKind.PlayMusic or
        JobKind.DanceTogether or
        JobKind.Stargaze;

    public void AbortCurrentJob(string npcName, bool preserveOvernightCamp = false)
    {
        var npc = FindNpc(npcName);
        if (_active.TryGetValue(npcName, out var job))
        {
            job.Done = true;
            _active.Remove(npcName);

            if (npc != null && _jobHandlers.TryGetHandler(job.Kind, out var handler))
            {
                Farmer? owner = GetJobFarmer(job) ?? GetCompanionOwner(npcName) ?? Game1.player;
                _companions.TryGetValue(npcName, out var companion);
                var ctx = new JobExecutionContext(
                    job,
                    npc,
                    owner,
                    companion,
                    ModEntry.CurrentTick,
                    _navigator,
                    _farmWorld,
                    RecordMemory,
                    RecordDepositedItem,
                    _debug_log);
                handler.OnAbort(ctx);
            }
        }
        if (npc != null)
        {
            npc.controller = null;
            npc.temporaryController = null;
            npc.Halt();
            if (npc.Sprite != null)
            {
                npc.Sprite.CurrentAnimation = null;
                npc.Sprite.StopAnimation();
                npc.Sprite.UpdateSourceRect();
            }
        }
        if (_companions.TryGetValue(npcName, out var cState))
        {
            cState.IsSettledInsideHouseForNight = false;
            cState.SettledHouseName = null;
            cState.IsPausedWhileOwnerOutsideHouse = false;
            cState.FrozenHousePosition = null;
            cState.LockedBedPixelPosition = null;
            cState.AssignedBedSpot = null;
            cState.BedSlideOutTargetPixel = null;
            cState.BedSlideOutTicksRemaining = 0;
            if (!preserveOvernightCamp)
                cState.IsRestingInPlace = false;
            cState.IsFishingTogether = false;
            cState.IsWalkingToFishingSpot = false;
            cState.IsWaitingAtCaveEntrance = false;
            cState.ConsecutiveStuckTicks = 0;
        }
        if (!preserveOvernightCamp)
        {
            _overnightGuests.TryRemove(npcName, out _);
        }
    }


    public void OnSaveLoaded()
    {
        ResetAllCompanionsAndJobsToDefault();
        _dayStartedTick = ModEntry.CurrentTick;
        _hasAutoRescuedThisMorning = false;
        _bedRescueDelayTicks = 8;
        _dedupDelayTicks = 15;
        if (Context.IsMainPlayer)
            EnsureMisterQiInNutRoom(out _);
    }

    private int _bedRescueDelayTicks = -1;
    private int _dedupDelayTicks = -1;
    private bool _hasAutoRescuedThisMorning = false;

    internal static void SafeWarpNpc(NPC? npc, GameLocation? targetLoc, Vector2 tile)
    {
        if (npc == null || targetLoc == null) return;
        try
        {
            var prevLoc = npc.currentLocation;

            // Clean up any pre-existing clones with the same villager name in targetLoc before warping
            if (targetLoc.characters != null && npc.GetType() == typeof(NPC) && npc.IsVillager)
            {
                for (int i = targetLoc.characters.Count - 1; i >= 0; i--)
                {
                    var ch = targetLoc.characters[i];
                    if (ch != null && !ReferenceEquals(ch, npc) && ch.GetType() == typeof(NPC) && ch.IsVillager && string.Equals(ch.Name, npc.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        targetLoc.characters.RemoveAt(i);
                    }
                }
            }

            if (prevLoc != null && prevLoc.characters != null)
            {
                int seenSelf = 0;
                for (int i = prevLoc.characters.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(prevLoc.characters[i], npc))
                    {
                        seenSelf++;
                        if (seenSelf > 1)
                            prevLoc.characters.RemoveAt(i);
                    }
                }
            }

            Game1.warpCharacter(npc, targetLoc, tile);

            // Ensure no leftover duplicate references remain in previous location
            if (prevLoc != null && prevLoc != targetLoc && prevLoc.characters != null && npc.GetType() == typeof(NPC) && npc.IsVillager)
            {
                for (int i = prevLoc.characters.Count - 1; i >= 0; i--)
                {
                    var ch = prevLoc.characters[i];
                    if (ch != null && (ReferenceEquals(ch, npc) || (ch.GetType() == typeof(NPC) && ch.IsVillager && string.Equals(ch.Name, npc.Name, StringComparison.OrdinalIgnoreCase))))
                    {
                        prevLoc.characters.RemoveAt(i);
                    }
                }
            }

            // Ensure strictly single occurrence in targetLoc
            if (targetLoc.characters != null)
            {
                int countInTarget = 0;
                for (int i = targetLoc.characters.Count - 1; i >= 0; i--)
                {
                    var ch = targetLoc.characters[i];
                    if (ReferenceEquals(ch, npc))
                    {
                        countInTarget++;
                        if (countInTarget > 1)
                            targetLoc.characters.RemoveAt(i);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[SafeWarpNpc] Error warping {npc.Name} to {targetLoc.NameOrUniqueName}: {ex.Message}", LogLevel.Warn);
        }
    }

    private static readonly HashSet<string> _dedupExcluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "Mister Qi", "MrQi", "Bouncer", "Henchman", "Gunther", "Marlon", "Gil",
        "Governor", "Grandpa", "Birdie", "Professor Snail", "Fizz", "Bookseller"
    };

    public int DeduplicateVillagersIfNeeded()
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady)
            return 0;

        int removedTotal = 0;
        try
        {
            var locationsToScan = new List<GameLocation>();
            var seenLocs = new HashSet<GameLocation>();

            if (Game1.locations != null)
            {
                foreach (var loc in Game1.locations)
                {
                    if (loc != null && seenLocs.Add(loc))
                        locationsToScan.Add(loc);
                }
            }

            var farm = Game1.getFarm();
            if (farm?.buildings != null)
            {
                foreach (var b in farm.buildings)
                {
                    var indoor = b?.indoors?.Value;
                    if (indoor != null && seenLocs.Add(indoor))
                        locationsToScan.Add(indoor);
                }
            }

            var entriesByName = new Dictionary<string, List<(GameLocation Loc, NPC Npc, int Index)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var loc in locationsToScan)
            {
                string locId = loc.NameOrUniqueName ?? loc.Name ?? string.Empty;
                if (string.Equals(locId, "Club", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(locId, "QiNutRoom", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (loc.characters == null) continue;
                for (int i = 0; i < loc.characters.Count; i++)
                {
                    var ch = loc.characters[i];
                    if (ch != null && ch.GetType() == typeof(NPC) && ch.IsVillager
                        && ch.CanSocialize && !string.IsNullOrWhiteSpace(ch.Name)
                        && !_dedupExcluded.Contains(ch.Name))
                    {
                        if (!entriesByName.TryGetValue(ch.Name, out var list))
                        {
                            list = new List<(GameLocation, NPC, int)>();
                            entriesByName[ch.Name] = list;
                        }
                        list.Add((loc, ch, i));
                    }
                }
            }

            static bool IsFarmOrHomeLocation(GameLocation loc)
            {
                if (loc is Farm || loc is FarmHouse || loc is Cabin || loc is AnimalHouse)
                    return true;
                string name = loc.NameOrUniqueName ?? loc.Name ?? string.Empty;
                return name.Contains("Cabin", StringComparison.OrdinalIgnoreCase);
            }

            foreach (var kv in entriesByName)
            {
                var list = kv.Value;
                if (list.Count <= 1) continue;

                NPC? canonicalGlobal = Game1.getCharacterFromName(kv.Key);
                bool hasSameMapDupes = list.GroupBy(e => e.Loc).Any(g => g.Count() >= 2);
                bool hasDistinctMaps = list.Select(e => e.Loc).Distinct().Count() > 1;
                bool allowCrossMapDedup = IsMarriedToAnyFarmer(canonicalGlobal ?? list[0].Npc)
                    || list.Any(e => IsFarmOrHomeLocation(e.Loc));

                if (!hasSameMapDupes && !(hasDistinctMaps && allowCrossMapDedup))
                    continue;

                GameLocation? preferredHome = null;
                foreach (var f in Game1.getAllFarmers())
                {
                    if (f != null && f.isMarriedOrRoommates() && string.Equals(f.spouse, kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        preferredHome = GetOwnerHouse(f);
                        break;
                    }
                }

                var canonicalEntry = list
                    .OrderByDescending(e => preferredHome != null && e.Loc == preferredHome ? 2 : 0)
                    .ThenByDescending(e => canonicalGlobal != null && ReferenceEquals(e.Npc, canonicalGlobal) ? 1 : 0)
                    .First();

                bool keptCanonicalOnce = false;
                var removalsByLoc = new Dictionary<GameLocation, List<int>>();
                if (allowCrossMapDedup)
                {
                    foreach (var entry in list)
                    {
                        bool isWinner = ReferenceEquals(entry.Npc, canonicalEntry.Npc) && entry.Loc == canonicalEntry.Loc && !keptCanonicalOnce;
                        if (isWinner)
                        {
                            keptCanonicalOnce = true;
                            continue;
                        }

                        if (!removalsByLoc.TryGetValue(entry.Loc, out var idxList))
                        {
                            idxList = new List<int>();
                            removalsByLoc[entry.Loc] = idxList;
                        }
                        idxList.Add(entry.Index);
                    }
                }
                else
                {
                    // Regla 1: solo deduplicar copias multiples dentro del mismo mapa
                    foreach (var grp in list.GroupBy(e => e.Loc).Where(g => g.Count() >= 2))
                    {
                        bool keptFirstInLoc = false;
                        foreach (var entry in grp)
                        {
                            if (!keptFirstInLoc)
                            {
                                keptFirstInLoc = true;
                                continue;
                            }
                            if (!removalsByLoc.TryGetValue(entry.Loc, out var idxList))
                            {
                                idxList = new List<int>();
                                removalsByLoc[entry.Loc] = idxList;
                            }
                            idxList.Add(entry.Index);
                        }
                    }
                }

                foreach (var remKv in removalsByLoc)
                {
                    remKv.Value.Sort();
                    for (int r = remKv.Value.Count - 1; r >= 0; r--)
                    {
                        int removeIdx = remKv.Value[r];
                        if (removeIdx >= 0 && removeIdx < remKv.Key.characters.Count)
                        {
                            remKv.Key.characters.RemoveAt(removeIdx);
                            removedTotal++;
                            ModEntry.ModMonitor?.Log($"[DedupGuard] Removed duplicate clone of {kv.Key} from {remKv.Key.NameOrUniqueName}", LogLevel.Info);
                        }
                    }
                }

                if (canonicalEntry.Npc.currentLocation != canonicalEntry.Loc)
                {
                    canonicalEntry.Npc.currentLocation = canonicalEntry.Loc;
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[DedupGuard] Error during villager deduplication: {ex.Message}", LogLevel.Warn);
        }

        return removedTotal;
    }

    public bool EnsureMisterQiInNutRoom(out string report)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
        {
            report = "Not main player or world not ready.";
            return false;
        }

        var qiRoom = Game1.getLocationFromName("QiNutRoom");
        if (qiRoom == null)
        {
            report = "QiNutRoom location not found.";
            return false;
        }

        // Already present — do nothing
        foreach (var ch in qiRoom.characters)
        {
            if (ch != null && string.Equals(ch.Name, "Mister Qi", StringComparison.OrdinalIgnoreCase))
            {
                report = "Mister Qi already present in QiNutRoom.";
                return false;
            }
        }

        // Try to borrow sprite from Club map first
        NPC? sourceQi = null;
        var club = Game1.getLocationFromName("Club");
        if (club != null)
        {
            foreach (var ch in club.characters)
            {
                if (ch != null && string.Equals(ch.Name, "Mister Qi", StringComparison.OrdinalIgnoreCase))
                {
                    sourceQi = ch;
                    break;
                }
            }
        }

        AnimatedSprite? sprite = sourceQi?.Sprite != null
            ? new AnimatedSprite(sourceQi.Sprite.textureName.Value, 0, sourceQi.Sprite.SpriteWidth, sourceQi.Sprite.SpriteHeight)
            : null;

        if (sprite == null)
        {
            string texName = NPC.getTextureNameForCharacter("MrQi");
            if (!Game1.content.DoesAssetExist<Microsoft.Xna.Framework.Graphics.Texture2D>(texName))
                texName = "Characters\\MrQi";
            sprite = new AnimatedSprite(texName, 0, 16, 32);
        }

        Microsoft.Xna.Framework.Graphics.Texture2D? portrait = null;
        try { portrait = Game1.content.Load<Microsoft.Xna.Framework.Graphics.Texture2D>("Portraits\\MrQi"); } catch { }

        var qi = new NPC(sprite, new Vector2(7 * 64f, 5 * 64f), "QiNutRoom", 2, "Mister Qi", portrait, false);
        qi.IsWalkingTowardPlayer = false;
        qiRoom.characters.Add(qi);

        ModEntry.ModMonitor?.Log("[DedupGuard] Restored Mister Qi to QiNutRoom at (7, 5).", LogLevel.Info);
        report = "Mister Qi restored to QiNutRoom at (7, 5).";
        return true;
    }

    public void EnsureSpousesAtHomeOnDayStart()
    {
        try
        {
            foreach (var farmer in Game1.getAllFarmers())
            {
                if (farmer == null || !farmer.isMarriedOrRoommates()) continue;
                string spouseName = farmer.spouse;
                if (string.IsNullOrWhiteSpace(spouseName)) continue;
                var spouse = FindNpc(spouseName);
                if (spouse == null) continue;

                var house = GetOwnerHouse(farmer);
                if (house == null) continue;

                // Limpiar siempre flags residuales de la jornada previa
                spouse.swimming.Value = false;
                if (spouse.shouldWearIslandAttire.Value)
                {
                    spouse.shouldWearIslandAttire.Value = false;
                    try { spouse.ChooseAppearance(); } catch { }
                }
                spouse.drawOffset = Vector2.Zero;
                spouse.IsInvisible = false;

                // Si es amanecer (< 800) y no es festival ni boda, y el cónyuge NO está dentro de house (o fuera del mapa)
                if (Game1.timeOfDay < 800 && !Utility.isFestivalDay(Game1.Date.DayOfMonth, Game1.Date.Season) && !Game1.weddingToday)
                {
                    bool isOutsideHouse = spouse.currentLocation != house;
                    bool isOutOfBounds = spouse.currentLocation == null || !spouse.currentLocation.isTileOnMap(spouse.TilePoint);

                    if (isOutsideHouse || isOutOfBounds)
                    {
                        var doubleBed = house.furniture?.OfType<BedFurniture>().FirstOrDefault(b => b.bedType == BedFurniture.BedType.Double);
                        Point safeTile = doubleBed != null
                            ? FindGuaranteedFloorTileOutsideBed(house, doubleBed, farmer.TilePoint)
                            : house.getKitchenStandingSpot();

                        if (safeTile.Y < 4) safeTile = new Point(safeTile.X, 4);

                        SafeWarpNpc(spouse, house, safeTile.ToVector2());
                        spouse.Halt();
                        spouse.controller = null;
                        spouse.temporaryController = null;

                        ModEntry.ModMonitor?.Log($"[SpouseHome] Restored {spouse.Name} to {house.NameOrUniqueName} at {safeTile} for morning routine.", LogLevel.Debug);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[SpouseHome] Error in EnsureSpousesAtHomeOnDayStart: {ex}", LogLevel.Error);
        }
    }

    private void EvacuateNpcFromBed(NPC npc, GameLocation loc, Farmer? owner, string logContext, CompanionState? comp = null)
    {
        bool insideBed = IsNpcInsideBedFootprint(npc, loc, out var occupiedBed);
        if (!insideBed && !npc.isSleeping.Value) return;

        var exitBed = occupiedBed ?? loc.furniture?.OfType<BedFurniture>().FirstOrDefault();
        Point exitTile = exitBed != null
            ? FindGuaranteedFloorTileOutsideBed(loc, exitBed, owner?.TilePoint ?? npc.TilePoint)
            : (loc is FarmHouse fh ? fh.getKitchenStandingSpot() : new Point(npc.TilePoint.X, Math.Max(4, npc.TilePoint.Y)));

        npc.setTilePosition(exitTile);
        npc.Position = new Vector2(exitTile.X * 64f, exitTile.Y * 64f);
        npc.isSleeping.Value = false;
        npc.drawOffset = Vector2.Zero;
        try { npc.Sprite?.StopAnimation(); } catch { }
        try { npc.Sprite?.UpdateSourceRect(); } catch { }
        npc.Halt();
        npc.controller = null;
        npc.temporaryController = null;

        if (TryGetSpouseHomeState(npc.Name, out var sState))
        {
            sState.BedSlideOutTargetPixel = null;
        }

        if (comp != null)
        {
            comp.LockedBedPixelPosition = null;
            comp.AssignedBedSpot = null;
            comp.IsPausedWhileOwnerOutsideHouse = false;
            comp.FrozenHousePosition = null;
            comp.IsSettledInsideHouseForNight = false;
            comp.SettledHouseName = null;
            comp.BedSlideOutTargetPixel = null;
        }

        ModEntry.ModMonitor?.Log($"[{logContext}] Evacuated {npc.Name} from bed footprint to {exitTile}", LogLevel.Debug);
    }

    public void AutoRescueTrappedBedOccupants()
    {
        if (_hasAutoRescuedThisMorning) return;
        _hasAutoRescuedThisMorning = true;

        EnsureSpousesAtHomeOnDayStart();

        try
        {
            if (Game1.timeOfDay >= 2200) return;

            // 1. Cónyuges de todos los granjeros
            foreach (var farmer in Game1.getAllFarmers())
            {
                if (farmer == null || !farmer.isMarriedOrRoommates()) continue;
                string spouseName = farmer.spouse;
                if (string.IsNullOrWhiteSpace(spouseName)) continue;
                var spouse = Game1.getCharacterFromName(spouseName);
                if (spouse?.currentLocation == null) continue;

                var loc = spouse.currentLocation;
                if (loc is StardewValley.Locations.FarmHouse || loc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase))
                {
                    EvacuateNpcFromBed(spouse, loc, farmer, "BedRescue");
                }
            }

            // 2. Acompañantes gestionados
            foreach (var (compName, comp) in _companions)
            {
                var npc = Game1.getCharacterFromName(compName);
                if (npc?.currentLocation == null) continue;
                var loc = npc.currentLocation;
                if (loc is StardewValley.Locations.FarmHouse || loc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase))
                {
                    var owner = GetCompanionOwner(npc.Name) ?? Game1.player;
                    EvacuateNpcFromBed(npc, loc, owner, "BedRescue", comp);
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[BedRescue] Error in AutoRescueTrappedBedOccupants: {ex}", LogLevel.Error);
        }
    }

    /// <summary>
    /// Evacua a cualquier cónyuge o acompañante que esté dentro de la huella de la cama antes de guardar el archivo save.
    /// Evita que el archivo de guardado serialice al NPC incrustado dentro de las almohadas o colchón.
    /// </summary>
    public void EvacuateAllBedsBeforeSave()
    {
        if (Game1.IsMultiplayer && !Game1.IsMasterGame) return;

        try
        {
            // 1. Cónyuges de todos los granjeros
            foreach (var farmer in Game1.getAllFarmers())
            {
                if (farmer == null || !farmer.isMarriedOrRoommates()) continue;
                string spouseName = farmer.spouse;
                if (string.IsNullOrWhiteSpace(spouseName)) continue;
                var spouse = Game1.getCharacterFromName(spouseName);
                if (spouse?.currentLocation == null) continue;

                var loc = spouse.currentLocation;
                if (loc is StardewValley.Locations.FarmHouse || loc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase))
                {
                    EvacuateNpcFromBed(spouse, loc, farmer, "BedPreSave");
                }
            }

            // 2. Acompañantes gestionados
            foreach (var (compName, comp) in _companions)
            {
                var npc = Game1.getCharacterFromName(compName);
                if (npc?.currentLocation == null) continue;
                var loc = npc.currentLocation;
                if (loc is StardewValley.Locations.FarmHouse || loc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase))
                {
                    var owner = GetCompanionOwner(npc.Name) ?? Game1.player;
                    EvacuateNpcFromBed(npc, loc, owner, "BedPreSave", comp);
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.ModMonitor?.Log($"[BedPreSave] Error in EvacuateAllBedsBeforeSave: {ex}", LogLevel.Error);
        }
    }

    public void ResetAllCompanionsAndJobsToDefault(bool preserveOvernightGuests = false)
    {
        WakeUpSleepoverGuests();
        RemoveAllCaveLights();
        RemoveAllNightTorches();
        ResetGateTracking();
        _pendingMorningNote = null;
        try
        {
            foreach (var name in _companions.Keys.Concat(_active.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                ReleaseNpc(name);
            }
        }
        catch { }
        _companions.Clear();
        _active.Clear();
        _incoming.Clear();
        if (!preserveOvernightGuests)
        {
            _overnightGuests.Clear();
        }
        _nightPromptedTonight.Clear();
        _caveGreetedNPCs.Clear();
        _dailyCounts.Clear();
        ResetDailySocialCaps();
    }

    public void OnDayStarted()
    {
        _dayStartedTick = ModEntry.CurrentTick;
        ResetDailySocialCaps();
        RemoveAllCaveLights();
        RemoveAllNightTorches();
        _pendingMorningNote = null;
        try
        {
            if (Game1.currentLocation != null)
                PurgeTemporaryCampEntities(Game1.currentLocation);
            foreach (var l in Game1.locations)
                PurgeTemporaryCampEntities(l);
        }
        catch { }
        _dailyCounts.Clear();
        _nightPromptedTonight.Clear();
        _caveGreetedNPCs.Clear();
        ResetEveningResolutions();
        ResetSpouseHomeStatesDaily();
        ResetGateTracking();
        WakeUpSleepoverGuests();

        // Release any remaining companions from previous day so every NPC performs their default schedule
        foreach (var name in _companions.Keys.Concat(_active.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            ReleaseNpc(name);
        }
        _companions.Clear();
        _active.Clear();

        // If Host in multiplayer, broadcast reset state to all clients
        if (Game1.IsMasterGame)
        {
            try
            {
                ValleyBuddies.ModEntry.BroadcastReset();
            }
            catch { }
        }

        // Return any non-guest NPC found inside farm buildings to their home
        try
        {
            var farm = Game1.getFarm();
            if (farm != null)
            {
                foreach (var b in farm.buildings)
                {
                    if (b.indoors.Value != null)
                    {
                        var npcsInside = b.indoors.Value.characters.ToList();
                        foreach (var n in npcsInside)
                        {
                            if (n != null && !_overnightGuests.ContainsKey(n.Name) && !IsMarriedToAnyFarmer(n))
                            {
                                if (!string.IsNullOrEmpty(n.DefaultMap))
                                {
                                    Vector2 defTile = n.DefaultPosition != Vector2.Zero ? (n.DefaultPosition / 64f) : new Vector2(10, 10);
                                    Game1.warpCharacter(n, n.DefaultMap, defTile);
                                }
                                n.followSchedule = true;
                                n.checkSchedule(Game1.timeOfDay);
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // Handle overnight guests who camped or stayed over
        HandleOvernightGuestsMorning();

        // Plan spontaneous friend visit to farm (Host authority only)
        if (Context.IsMainPlayer)
        {
            PlanSpontaneousVisit();
        }

        // Auto rescue spouses and companions stuck in beds upon morning start (tras ~8 ticks para correr tras NPC.dayUpdate vanilla)
        _hasAutoRescuedThisMorning = false;
        _bedRescueDelayTicks = 8;
        _dedupDelayTicks = 15;
    }

    private void PlanSpontaneousVisit()
    {
        _pendingSpontaneousVisit = null;

        if (_config?.EnableSpontaneousVisits != true)
            return;

        if (Game1.isRaining || Utility.isFestivalDay(Game1.dayOfMonth, Game1.season))
            return;

        var player = Game1.player;
        if (player?.friendshipData == null) return;

        // Respetar jerarquía de Plan Grande del ValleyPacingDirector (Cita Cónyuge > Invitación Inversa > Visita Clásica)
        if (!Systems.LivingEvents.ValleyPacingDirector.Instance.CanScheduleClassicVisit(player.UniqueMultiplayerID, Game1.Date.TotalDays))
            return;

        var eligible = new List<(string Name, int Hearts)>();
        foreach (var kv in player.friendshipData.Pairs)
        {
            int hearts = kv.Value.Points / 250;
            if (hearts >= 4)
            {
                var charNpc = FindNpc(kv.Key);
                // Bloqueo A: Excluir cónyuges y roommates de cualquier jugador para visitas espontáneas
                if (charNpc != null && !charNpc.IsInvisible && !IsMarriedToAnyFarmer(charNpc))
                {
                    eligible.Add((kv.Key, hearts));
                }
            }
        }

        if (eligible.Count == 0) return;

        // 35% chance base escalada por el preset de ritmo
        double visitChance = Math.Clamp(0.35 * Systems.LivingEvents.ValleyPacingDirector.Instance.ProbabilityFactor, 0.0, 0.95);
        if (Game1.random.NextDouble() > visitChance) return;

        var chosen = eligible.OrderByDescending(e => e.Hearts + Game1.random.Next(0, 5)).First();
        var friendNpc = FindNpc(chosen.Name);
        if (friendNpc == null) return;

        string giftId = "(O)223"; // Cookies
        if (chosen.Hearts >= 8)
        {
            string[] topGifts = { "(O)211", "(O)395", "(O)731", "(O)340" };
            giftId = topGifts[Game1.random.Next(topGifts.Length)];
        }
        else if (chosen.Hearts >= 6)
        {
            string[] midGifts = { "(O)196", "(O)223", "(O)206", "(O)421" };
            giftId = midGifts[Game1.random.Next(midGifts.Length)];
        }
        else
        {
            string[] lowGifts = { "(O)216", "(O)402", "(O)410", "(O)597" };
            giftId = lowGifts[Game1.random.Next(lowGifts.Length)];
        }

        int visitTime = 800 + Game1.random.Next(0, 3) * 20;

        _pendingSpontaneousVisit = new SpontaneousVisitPlan
        {
            NpcName = chosen.Name,
            ScheduledTime = visitTime,
            GiftItemId = giftId,
            Hearts = chosen.Hearts
        };

        Systems.LivingEvents.ValleyPacingDirector.Instance.RecordClassicVisitScheduled(player.UniqueMultiplayerID, Game1.Date.TotalDays);

        string greeting = chosen.Hearts >= 8
            ? Utilities.I18n.Get("visit.hud.high", new { name = friendNpc.displayName, playerName = player.displayName })
            : chosen.Hearts >= 6
                ? Utilities.I18n.Get("visit.hud.mid", new { name = friendNpc.displayName, playerName = player.displayName })
                : Utilities.I18n.Get("visit.hud.low", new { name = friendNpc.displayName, playerName = player.displayName });

        Game1.addHUDMessage(new HUDMessage(greeting, HUDMessage.newQuest_type));
    }

    public void CancelPendingSpontaneousVisit()
    {
        _pendingSpontaneousVisit = null;
    }

    public void StartFollowDirectly(NPC npc, Farmer owner)
    {
        if (npc == null || owner == null) return;

        if (_active.TryGetValue(npc.Name, out var existingJob) && existingJob.Kind != JobKind.Follow)
        {
            AbortCurrentJob(npc.Name);
        }

        SetCompanion(npc.Name, owner.UniqueMultiplayerID, isVisitingFarm: false, isFollowing: true, farmerName: owner.Name);
        if (_companions.TryGetValue(npc.Name, out var cmp))
        {
            cmp.IsFollowing = true;
            cmp.IsVisitingFarm = false;
            cmp.IsRestingInPlace = false;
            cmp.IsSettledInsideHouseForNight = false;
            cmp.IsPausedWhileOwnerOutsideHouse = false;
            cmp.FrozenHousePosition = null;
        }

        var job = new NpcJob
        {
            NpcName = npc.Name,
            Kind = JobKind.Follow,
            FarmerId = owner.UniqueMultiplayerID,
            FarmerName = owner.Name,
            CreatedTick = Game1.ticks
        };
        Enqueue(job);
        if (Game1.IsMultiplayer && Context.IsMainPlayer)
        {
            ValleyBuddies.ModEntry.BroadcastJob(npc.Name, JobKind.Follow, owner.UniqueMultiplayerID, owner.Name);
        }
    }

    public void OnPlayerWarped(WarpedEventArgs e)
    {
        if (!_allowCrossMap || e.Player == null || e.NewLocation == null) return;
        var player = e.Player;
        GameLocation newLoc = e.NewLocation;

        // Welcome home on player warp into FarmHouse/Cabin (strictly 18:00 - 22:00, max 1 time per day)
        if (e.Player != null && e.NewLocation != null && e.OldLocation != null)
        {
            bool wasOutside = !(e.OldLocation is StardewValley.Locations.FarmHouse || e.OldLocation.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase));
            bool isInsideHome = (newLoc is StardewValley.Locations.FarmHouse || newLoc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase));

            if (wasOutside && isInsideHome && Game1.timeOfDay >= 1800 && Game1.timeOfDay <= 2200)
            {
                if (player.isMarriedOrRoommates() && !string.IsNullOrWhiteSpace(player.spouse))
                {
                    string spouseName = player.spouse;
                    if (_spouseHomeStates.TryGetValue(spouseName, out var sState) && !sState.DidEveningWelcomeToday)
                    {
                        var spouse = newLoc.characters.FirstOrDefault(c => c.Name.Equals(spouseName, StringComparison.OrdinalIgnoreCase));
                        if (spouse != null && !spouse.isSleeping.Value)
                        {
                            sState.DidEveningWelcomeToday = true;
                            spouse.Halt();
                            spouse.faceGeneralDirection(player.Position);
                            if (!spouse.IsEmoting) spouse.doEmote(20); // Heart
                            int idx = Game1.random.Next(1, 4);
                            string welcomeMsg = Utilities.I18n.Get($"spouse.welcome_home.v{idx}", new { playerName = player.displayName ?? player.Name, npcName = spouse.displayName });
                            ModEntry.ConversationBridge.ShowCompanionBubble(spouse, welcomeMsg, 4000);
                            ModEntry.ModMonitor?.Log($"[SpouseHome] Welcomed {player.Name} home by {spouse.Name} on warp into {newLoc.NameOrUniqueName}", LogLevel.Debug);
                        }
                    }
                }
            }

            // v4.5.6: Disparador automático si el jugador regresa a donde está el cónyuge y hay cosecha pendiente
            if (player.isMarriedOrRoommates() && !string.IsNullOrWhiteSpace(player.spouse))
            {
                string spouseName = player.spouse;
                if (_spouseHomeStates.TryGetValue(spouseName, out var sState) && sState.HasPendingHarvestForOwner && sState.HarvestBasket.Count > 0)
                {
                    var spouse = newLoc.characters.FirstOrDefault(c => c.Name.Equals(spouseName, StringComparison.OrdinalIgnoreCase));
                    if (spouse != null && !spouse.isSleeping.Value)
                    {
                        sState.IsDeliveringPendingHarvest = true;
                        sState.PhaseStartedTick = ModEntry.CurrentTick;
                        ModEntry.ModMonitor?.Log($"[SpouseHome] Player returned to {newLoc.NameOrUniqueName}, activating DeliveringPendingHarvest for {spouse.Name}", LogLevel.Debug);
                    }
                }
            }
        }

        // In multiplayer, if a remote player warps on their client, notify Host so the Host immediately authoritatively warps their companion!
        if (Game1.IsMultiplayer && (!Game1.IsMasterGame || !Context.IsMainPlayer))
        {
            if (e.IsLocalPlayer && Game1.timeOfDay >= 600 && Game1.timeOfDay < 2400)
            {
                foreach (var kvp in _companions.Where(c => _active.TryGetValue(c.Key, out var j) && !j.Done && (j.Kind == JobKind.Follow || j.Kind == JobKind.Sleepover || j.Kind == JobKind.VisitFarm || j.Kind == JobKind.HangOut) && c.Value.FarmerId == player.UniqueMultiplayerID))
                {
                    var kindToSend = _active.TryGetValue(kvp.Key, out var j) ? j.Kind : (kvp.Value.IsVisitingFarm ? JobKind.VisitFarm : JobKind.Follow);
                    bool isSettledHome = kvp.Value.IsSettledInsideHouseForNight
                        || kvp.Value.IsRestingInPlace
                        || kvp.Value.NightState is NightLifecycleState.SleepingInBed or NightLifecycleState.IndoorRoaming or NightLifecycleState.IndoorResting or NightLifecycleState.NightSettled;
                    if ((kindToSend == JobKind.Sleepover || kvp.Value.EveningResolution == EveningResolutionState.StayingInHouse) && isSettledHome)
                    {
                        continue;
                    }
                    ValleyBuddies.ModEntry.BroadcastJob(kvp.Key, kindToSend, player.UniqueMultiplayerID, player.Name, newLoc.NameOrUniqueName);
                }
            }
            return;
        }

        foreach (var job in _active.Values.Where(j => (j.Kind == JobKind.Follow || j.Kind == JobKind.EscortHome || j.Kind == JobKind.Sleepover || j.Kind == JobKind.VisitFarm || j.Kind == JobKind.HangOut) && !j.Done))
        {
            long targetFarmerId = job.FarmerId != 0 ? job.FarmerId : (Game1.player?.UniqueMultiplayerID ?? 0);
            if (player.UniqueMultiplayerID != targetFarmerId) continue;

            var npc = FindNpc(job.NpcName);
            // If leaving a cave/mine, grab them from e.OldLocation.characters if not found in global search
            if (npc == null && e.OldLocation != null)
            {
                npc = e.OldLocation.characters.FirstOrDefault(c => c.Name.Equals(job.NpcName, StringComparison.OrdinalIgnoreCase));
            }
            if (npc == null) continue;

            // Si el compañero ya está en el mapa de destino o el evento de warp fue local (intra-mapa), no re-teletransportar
            if (npc.currentLocation == newLoc || string.Equals(npc.currentLocation?.NameOrUniqueName, newLoc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (npc.swimming.Value && newLoc is not StardewValley.Locations.BathHousePool)
            {
                npc.swimming.Value = false;
                npc.drawOffset = Vector2.Zero;
            }

            if (job.Kind == JobKind.VisitFarm || job.Kind == JobKind.HangOut)
            {
                job.Data["locationName"] = newLoc.NameOrUniqueName;
            }

            bool wasInCave = IsCaveOrMineLocation(e.OldLocation);
            bool isNowInCave = IsCaveOrMineLocation(newLoc);

            // ── CAVE FOUNDATIONS HANDLING (v4.2.0) ──
            _companions.TryGetValue(npc.Name, out var compState);

            // Child waiting at cave entrance resuming follow when player returns to lobby
            if (compState?.IsWaitingAtCaveEntrance == true)
            {
                if (IsCaveOrMineEntrance(newLoc))
                {
                    compState.IsWaitingAtCaveEntrance = false;
                    npc.doEmote(20);
                    npc.showTextAboveHead(Utilities.I18n.Get("speech.follow_confirm", new { playerName = player.displayName }), duration: 2500);
                }
                else if (IsUndergroundDangerLocation(newLoc))
                {
                    // Player moved between danger floors, child stays waiting safely at entrance
                    continue;
                }
                else
                {
                    // Player left the caves completely for another surface map, child rejoins
                    compState.IsWaitingAtCaveEntrance = false;
                }
            }

            // Child protection: cannot enter underground danger floors
            var caveProfile = CavePersonalityRegistry.ResolveProfile(npc);
            if (caveProfile.IsChildProtected && IsUndergroundDangerLocation(newLoc))
            {
                var entranceLoc = GetCaveEntranceLocation(newLoc);
                if (entranceLoc != null)
                {
                    if (compState != null) compState.IsWaitingAtCaveEntrance = true;
                    Point freeTile = FindSafeWarpSpawnTile(entranceLoc, new Point(18, 13), 2);
                    Game1.warpCharacter(npc, entranceLoc, new Vector2(freeTile.X, freeTile.Y));
                    npc.controller = null;
                    npc.Halt();
                    npc.doEmote(16);
                    npc.showTextAboveHead(Utilities.I18n.Get("cave.child.stay_entrance", new { playerName = player.displayName, npcName = npc.displayName }), duration: 4500);
                }
                continue;
            }

            // Extreme cave zones confidence (Skull Cavern / Volcano Dungeon):
            // SupportHealer & CautiousFortune need >= 5 hearts to brave these depths
            int hearts = player.getFriendshipHeartLevelForNPC(npc.Name);
            bool isExtremeDanger = (newLoc is MineShaft msExt && msExt.mineLevel >= 121) || (newLoc is VolcanoDungeon vdExt && vdExt.level.Value >= 1);
            if (isExtremeDanger && hearts < 5 && (caveProfile.Archetype == CaveArchetype.SupportHealer || caveProfile.Archetype == CaveArchetype.CautiousFortune))
            {
                var entranceLoc = GetCaveEntranceLocation(newLoc);
                if (entranceLoc != null)
                {
                    if (compState != null) compState.IsWaitingAtCaveEntrance = true;
                    Point freeTile = FindSafeWarpSpawnTile(entranceLoc, new Point(18, 13), 2);
                    Game1.warpCharacter(npc, entranceLoc, new Vector2(freeTile.X, freeTile.Y));
                    npc.controller = null;
                    npc.Halt();
                    npc.doEmote(28); // sweating / nervous
                    npc.showTextAboveHead(Utilities.I18n.Get("cave.danger.need_more_hearts", new { playerName = player.displayName, npcName = npc.displayName }), duration: 4500);
                }
                continue;
            }

            // Exiting cave floor: purge light source and reset floor state
            if (wasInCave && !isNowInCave)
            {
                npc.addedSpeed = 0;
                if (compState != null)
                {
                    compState.IsCaveAutonomyActive = false;
                    compState.CurrentCaveRockTarget = null;
                    compState.CaveWanderTarget = null;
                    compState.IsFleeingBomb = false;
                }
                RemoveCompanionCaveLight(npc.Name, e.OldLocation);
                ResetCaveFloorState(e.OldLocation?.NameOrUniqueName);
            }

            // ── PERMANENCIA EN CASA DURANTE PIJAMADA (v4.3.3 — FrozenHousePosition) ──
            // Detecta si el NPC está dentro de FarmHouse/Cabin en modo pijamada/StayInHouse
            bool isHouseLocation = npc.currentLocation is StardewValley.Locations.FarmHouse
                || (npc.currentLocation != null && npc.currentLocation.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase));
            bool isStayInHouseResolution = (compState?.EveningResolution == EveningResolutionState.StayInHouse
                || compState?.EveningResolution == EveningResolutionState.StayingInHouse
                || job.Kind == JobKind.Sleepover);

            // Detecta si el jugador ACABA DE SALIR de FarmHouse/Cabin (e.OldLocation era la casa)
            bool playerJustLeftHouse = e.OldLocation != null
                && e.OldLocation == npc.currentLocation
                && (e.OldLocation is StardewValley.Locations.FarmHouse || e.OldLocation.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase))
                && !(newLoc is StardewValley.Locations.FarmHouse || newLoc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase));

            // Detecta si el jugador ACABA DE ENTRAR a la casa donde está el NPC
            bool playerJustEnteredSameHouse = isHouseLocation
                && (newLoc == npc.currentLocation
                    || (compState?.SettledHouseName != null && newLoc.NameOrUniqueName == compState.SettledHouseName));

            // 1. Activar anclaje nocturno si ya está dentro de FarmHouse/Cabin
            if (isHouseLocation && isStayInHouseResolution && npc.currentLocation != null)
            {
                string curHouseName = npc.currentLocation.NameOrUniqueName;
                if (compState != null)
                {
                    compState.IsSettledInsideHouseForNight = true;
                    compState.SettledHouseName = curHouseName;
                }
                job.Data["targetHouseName"] = curHouseName;
            }

            // Si el NPC está anclado en casa y el jugador entra/sale de esa casa
            if (compState?.IsSettledInsideHouseForNight == true && isHouseLocation && isStayInHouseResolution)
            {
                // A. Jugador SALIÓ de la casa → congelar posición exacta del NPC, impedir movimiento
                if (playerJustLeftHouse)
                {
                    bool isSleepingInBed = compState.LockedBedPixelPosition.HasValue
                        && (compState.NightState == NightLifecycleState.SleepingInBed || compState.NightState == NightLifecycleState.GoingToBed);

                    if (!isSleepingInBed)
                    {
                        // Detener limpiamente: guardar posición exacta en píxeles y bloquear rutas
                        npc.controller = null;
                        npc.Halt();
                        // Validar que la casilla actual no sea pared norte (Y <= 3) — corregir si es necesario
                        Point currentTile = npc.TilePoint;
                        if (npc.currentLocation != null && currentTile.Y <= 3)
                        {
                            // La casilla ya es pared: buscar una posición segura más abajo
                            Point safeTile = FindSafeIndoorSpawnTile(
                                npc.currentLocation,
                                new Point(currentTile.X, Math.Max(4, currentTile.Y + 2)), null);
                            if (safeTile != Point.Zero)
                                npc.Position = new Vector2(safeTile.X * 64f, safeTile.Y * 64f);
                        }
                        compState.FrozenHousePosition = npc.Position;
                        compState.FrozenHouseFacing = npc.FacingDirection;
                    }
                    else
                    {
                        // Durmiendo en cama: congelar la posición de cama bloqueada
                        compState.FrozenHousePosition = compState.LockedBedPixelPosition;
                        compState.FrozenHouseFacing = npc.FacingDirection;
                    }
                    compState.IsPausedWhileOwnerOutsideHouse = true;
                    continue; // No warpear fuera
                }

                // B. Jugador VOLVIÓ a entrar a la misma casa → restaurar posición exacta (jamás warpCharacter)
                if (playerJustEnteredSameHouse && compState.IsPausedWhileOwnerOutsideHouse)
                {
                    if (compState.FrozenHousePosition.HasValue)
                    {
                        bool isSleepingInBed = compState.LockedBedPixelPosition.HasValue
                            && (compState.NightState == NightLifecycleState.SleepingInBed || compState.NightState == NightLifecycleState.GoingToBed);

                        if (!isSleepingInBed)
                        {
                            npc.controller = null;
                            npc.Halt();
                            npc.Position = compState.FrozenHousePosition.Value;
                            npc.faceDirection(compState.FrozenHouseFacing);
                        }
                        // Si estaba durmiendo, respetar el LockedBedPixelPosition intacto
                    }
                    compState.IsPausedWhileOwnerOutsideHouse = false;
                    compState.FrozenHousePosition = null;
                    // Pausa de reanudación natural (~75 ticks ≈ 1.25 s)
                    compState.IndoorInitPauseUntilTick = ModEntry.CurrentTick + 75;
                    continue; // No warpear
                }

                // C. Cualquier otro caso con NPC anclado en casa: no moverlo
                continue;
            }

            // Entering or advancing between underground danger floors
            if (IsUndergroundDangerLocation(newLoc))
            {
                RemoveCompanionCaveLight(npc.Name, e.OldLocation);
                ResetCaveFloorState(e.OldLocation?.NameOrUniqueName);
                job.Data.Remove("lastCaveMine");

                Point caveSpawn = FindSafeCaveEntryTile(newLoc, player.TilePoint, player.FacingDirection, npc);
                Game1.warpCharacter(npc, newLoc, new Vector2(caveSpawn.X, caveSpawn.Y));
                npc.controller = null;
                npc.Halt();
                npc.farmerPassesThrough = true;
                job.Data["cave_entry_tile"] = player.TilePoint;
                job.Data["cave_pass_through_until"] = ModEntry.CurrentTick + 45;
                job.Data["warp_tick"] = ModEntry.CurrentTick;
                job.Data["warp_pos"] = npc.Position;
                job.Data["follow_last_pos"] = npc.Position;
                job.Data["follow_last_moved_tick"] = ModEntry.CurrentTick;

                if (compState != null)
                {
                    compState.IsCaveAutonomyActive = true;
                    compState.FloorEntryLadderTile = player.TilePoint;
                    compState.FloorSafeWaitTile = FindSafeLadderWaitTile(newLoc, player.TilePoint, npc);
                    compState.CurrentCaveRockTarget = null;
                    compState.CaveWanderTarget = null;
                    compState.IsFleeingBomb = false;
                    compState.CaveStuckSinceTick = 0;
                }

                UpdateCompanionCaveLight(npc, newLoc);
                CheckCaveBiomeReaction(npc, newLoc, player, ModEntry.CurrentTick);
                _debug_log($"[Actions] Warped {job.NpcName} to cave floor {newLoc.NameOrUniqueName} at {caveSpawn} with {player.Name}");
                continue;
            }

            // If it's a sleepover (or pending stay in house) and player entered FarmHouse or Cabin, warp NPC inside safely
            if (job.Kind == JobKind.Sleepover || job.Data.ContainsKey("stay_in_house_on_entry"))
            {
                if (newLoc is StardewValley.Locations.FarmHouse fh && !string.Equals(npc.currentLocation?.NameOrUniqueName, fh.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
                {
                    job.Kind = JobKind.Sleepover;
                    job.Data.Remove("stay_in_house_on_entry");
                    Point entry = fh.getEntryLocation();
                    Point interiorSpawn = FindSafeIndoorSpawnTile(fh, entry, player);
                    LogWarpAudit("OnPlayerWarped_SleepoverEnterHouse", npc, fh, interiorSpawn);
                    Game1.warpCharacter(npc, fh, new Vector2(interiorSpawn.X, interiorSpawn.Y));
                    npc.controller = null;
                    npc.Halt();
                    npc.faceDirection(2);
                    npc.doEmote(20); // Heart emote welcoming each other inside
                    if (_companions.TryGetValue(npc.Name, out var compWarp))
                    {
                        compWarp.IndoorInitPauseUntilTick = ModEntry.CurrentTick + 180; // 3s pause
                        compWarp.HasEmotedAtCurrentPoi = false;
                        compWarp.LastIndoorPoi = null;
                        compWarp.NightState = NightLifecycleState.IndoorRoaming;
                        compWarp.IsSettledInsideHouseForNight = true;
                        compWarp.SettledHouseName = fh.NameOrUniqueName;
                    }
                    job.Data["targetHouseName"] = fh.NameOrUniqueName;
                    continue;
                }
            }

            // If visiting farm, hanging out, or following, and player entered Cabin/Farmhouse, warp inside near entrance safely
            if ((job.Kind == JobKind.VisitFarm || job.Kind == JobKind.HangOut || job.Kind == JobKind.Follow)
                && newLoc is StardewValley.Locations.FarmHouse indoorFh
                && !string.Equals(npc.currentLocation?.NameOrUniqueName, indoorFh.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
            {
                Point entry = indoorFh.getEntryLocation();
                Point interiorSpawn = FindSafeIndoorSpawnTile(indoorFh, entry, player);
                LogWarpAudit("OnPlayerWarped_FollowEnterHouse", npc, indoorFh, interiorSpawn);
                Game1.warpCharacter(npc, indoorFh, new Vector2(interiorSpawn.X, interiorSpawn.Y));
                npc.controller = null;
                npc.Halt();
                continue;
            }

            if (string.Equals(npc.currentLocation?.NameOrUniqueName, newLoc.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
                continue;

            Point spawn = GetCrossMapSpawnTile(newLoc, player.TilePoint, player.FacingDirection);
            LogWarpAudit("OnPlayerWarped_CrossMapFollow", npc, newLoc, spawn);
            Game1.warpCharacter(npc, newLoc, new Vector2(spawn.X, spawn.Y));
            npc.controller = null;
            npc.Halt();
            job.Data["warp_tick"] = ModEntry.CurrentTick;
            job.Data["warp_pos"] = npc.Position;
            job.Data["follow_last_pos"] = npc.Position;
            job.Data["follow_last_moved_tick"] = ModEntry.CurrentTick;

            // Cave exit event: companion safely warps out to surface and cheers!
            if (wasInCave && !isNowInCave)
            {
                npc.doEmote(20);
                npc.showTextAboveHead(Utilities.I18n.Get("bubble.cave_adventure_done"), duration: 4500);
            }

            // Check beach outfit & cave greeting
            CheckBeachOutfit(npc, newLoc);
            CheckCaveArrival(npc, newLoc, player);

            _debug_log($"[Actions] Warped {job.NpcName} to {newLoc.NameOrUniqueName} at {spawn} with {player.Name}");
        }
    }

    // ─────────────────── Main tick ───────────────────────────────

    public void Tick(ulong gameTick)
    {
        // Rescate matutino diferido (~5-10 ticks) para correr tras NPC.dayUpdate vanilla
        if (_bedRescueDelayTicks > 0)
        {
            _bedRescueDelayTicks--;
            if (_bedRescueDelayTicks == 0)
            {
                AutoRescueTrappedBedOccupants();
            }
        }

        if (_dedupDelayTicks > 0)
        {
            _dedupDelayTicks--;
            if (_dedupDelayTicks == 0)
            {
                DeduplicateVillagersIfNeeded();
            }
        }

        // Display pending morning note once day-transition menus close
        if (_pendingMorningNote != null && Game1.activeClickableMenu == null && !Game1.fadeToBlack && Game1.player?.CanMove == true)
        {
            string note = _pendingMorningNote;
            _pendingMorningNote = null;
            try
            {
                Game1.activeClickableMenu = new StardewValley.Menus.LetterViewerMenu(note);
            }
            catch { }
        }

        // Check pending spontaneous visit
        if (_pendingSpontaneousVisit != null && Game1.timeOfDay >= _pendingSpontaneousVisit.ScheduledTime && Context.IsWorldReady)
        {
            var npc = FindNpc(_pendingSpontaneousVisit.NpcName);
            // Bloqueo B: Si el NPC es cónyuge/roommate de algún granjero, cancelar silenciosamente
            if (npc == null || IsMarriedToAnyFarmer(npc))
            {
                _pendingSpontaneousVisit = null;
                return;
            }

            var player = Game1.player;
            var pLoc = player?.currentLocation;

            if (npc != null && player != null && pLoc != null)
            {
                GameLocation targetMap;
                Point spawnTile;

                if (pLoc is StardewValley.Locations.FarmHouse fh)
                {
                    targetMap = fh;
                    spawnTile = fh.getEntryLocation();
                }
                else if (pLoc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase))
                {
                    targetMap = pLoc;
                    spawnTile = GetAdjacentWalkableTile(pLoc, new Point(3, 11), new Point(3, 11), npc);
                }
                else if (pLoc is Farm farmLoc)
                {
                    targetMap = farmLoc;
                    Point porch = GetFarmSafePorchTile(farmLoc);
                    if (Vector2.Distance(player.Tile, porch.ToVector2()) > 18f)
                    {
                        spawnTile = GetAdjacentWalkableTile(farmLoc, player.TilePoint, porch, npc, maxRadius: 5);
                    }
                    else
                    {
                        spawnTile = porch;
                    }
                }
                else
                {
                    targetMap = Game1.getFarm() ?? pLoc;
                    spawnTile = (targetMap is Farm f) ? GetFarmSafePorchTile(f) : new Point(64, 15);
                }

                if (npc.currentLocation?.NameOrUniqueName != targetMap.NameOrUniqueName)
                {
                    Game1.warpCharacter(npc, targetMap, new Vector2(spawnTile.X, spawnTile.Y));
                }
                else
                {
                    npc.setTileLocation(new Vector2(spawnTile.X, spawnTile.Y));
                }

                npc.controller = null;
                npc.Halt();

                SetCompanion(npc.Name, player.UniqueMultiplayerID, isVisitingFarm: true, isFollowing: false, farmerName: player.Name);

                var job = new NpcJob
                {
                    NpcName = npc.Name,
                    Kind = JobKind.VisitFarm,
                    FarmerId = player.UniqueMultiplayerID,
                    FarmerName = player.Name,
                    CreatedTick = Game1.ticks
                };
                job.Data["spontaneous"] = true;
                job.Data["spontaneousGiftId"] = _pendingSpontaneousVisit.GiftItemId;
                job.Data["spontaneousGiftGiven"] = false;
                job.Data["visit_greeting_pending"] = true;

                Enqueue(job);
            }
            _pendingSpontaneousVisit = null;
        }

        // Pull new jobs
        while (_incoming.TryDequeue(out var newJob))
        {
            if (IsBlocked(newJob.NpcName)) continue;

            // Cancel any previous job without releasing to vanilla schedule
            if (_active.TryGetValue(newJob.NpcName, out var old))
            {
                old.Done = true;
                var oldNpc = FindNpc(newJob.NpcName);
                if (oldNpc != null)
                {
                    oldNpc.controller = null;
                    oldNpc.temporaryController = null;
                    oldNpc.Halt();
                    if (oldNpc.Sprite != null)
                    {
                        oldNpc.Sprite.CurrentAnimation = null;
                        oldNpc.Sprite.StopAnimation();
                        oldNpc.Sprite.UpdateSourceRect();
                    }
                }
            }

            // Disable vanilla schedule while under mod control
            var npc = FindNpc(newJob.NpcName);
            if (npc != null)
            {
                npc.followSchedule = false;
            }

            if (_companions.TryGetValue(newJob.NpcName, out var existingComp) && existingComp.FarmerId != 0)
            {
                if (newJob.FarmerId == 0 || (newJob.FarmerId != existingComp.FarmerId && Game1.GetPlayer(existingComp.FarmerId, onlyOnline: true) != null && _config?.AllowHostOverrideCompanion != true))
                {
                    newJob.FarmerId = existingComp.FarmerId;
                    newJob.FarmerName = existingComp.FarmerName;
                }
            }

            bool hasActiveCampGuest = _overnightGuests.TryGetValue(newJob.NpcName, out var ogCamp) && ogCamp.DidCampfire;
            bool keepCamp = (GetEveningResolution(newJob.NpcName) == EveningResolutionState.Camping || hasActiveCampGuest)
                && (IsTemporaryEveningActivity(newJob.Kind) || newJob.Kind == JobKind.Campfire || newJob.Kind == JobKind.CampSleepTogether);

            // Purge residual anchors if new job is daytime task or follow
            if (_companions.TryGetValue(newJob.NpcName, out var cState))
            {
                if (newJob.Kind != JobKind.SitOrRest && newJob.Kind != JobKind.Sleepover)
                {
                    cState.IsSettledInsideHouseForNight = false;
                    cState.SettledHouseName = null;
                    cState.IsPausedWhileOwnerOutsideHouse = false;
                    cState.FrozenHousePosition = null;
                    cState.LockedBedPixelPosition = null;
                    cState.AssignedBedSpot = null;
                    cState.BedSlideOutTargetPixel = null;
                    cState.BedSlideOutTicksRemaining = 0;
                    if (!keepCamp)
                        cState.IsRestingInPlace = false;
                    cState.IsFishingTogether = false;
                    cState.IsWalkingToFishingSpot = false;
                    cState.IsWaitingAtCaveEntrance = false;
                }
                cState.ConsecutiveStuckTicks = 0;
            }

            if (!keepCamp && (newJob.Kind == JobKind.Follow || (newJob.Kind != JobKind.SitOrRest && newJob.Kind != JobKind.Sleepover)))
            {
                _overnightGuests.TryRemove(newJob.NpcName, out _);
            }

            // Maintain companion association state for multiplayer and social context
            if (newJob.Kind == JobKind.Follow)
                SetCompanion(newJob.NpcName, newJob.FarmerId, isVisitingFarm: false, isFollowing: true, farmerName: newJob.FarmerName);
            else if (newJob.Kind == JobKind.VisitFarm || newJob.Kind == JobKind.HangOut)
                SetCompanion(newJob.NpcName, newJob.FarmerId, isVisitingFarm: true, isFollowing: false, farmerName: newJob.FarmerName);
            else if (!_companions.ContainsKey(newJob.NpcName) && newJob.FarmerId != 0)
                SetCompanion(newJob.NpcName, newJob.FarmerId, isVisitingFarm: Game1.getFarm() != null, isFollowing: false, farmerName: newJob.FarmerName);

            _active[newJob.NpcName] = newJob;
            _dailyCounts.TryGetValue(newJob.NpcName, out int cnt);
            _dailyCounts[newJob.NpcName] = cnt + 1;
            _debug_log($"[Actions] Started {newJob.Kind} for {newJob.NpcName}");
        }

        // In multiplayer, the host is the authoritative driver for NPC movement and jobs
        if (Game1.IsMultiplayer && (!Game1.IsMasterGame || !Context.IsMainPlayer))
        {
            UpdateCompanionNightTorches(gameTick);
            return;
        }

        // Subterranean companion job insurance (v4.3.2):
        // Ensure any active companion inside underground danger location ALWAYS has an active job
        foreach (var (compName, comp) in _companions)
        {
            if (!comp.IsFollowing && comp.EveningResolution != EveningResolutionState.StayInHouse) continue;
            var cNpc = FindNpc(compName);
            if (cNpc?.currentLocation != null && IsUndergroundDangerLocation(cNpc.currentLocation))
            {
                if (!_active.TryGetValue(compName, out var actJob) || actJob.Done)
                {
                    var ownerFarmer = GetCompanionOwner(compName) ?? Game1.player;
                    if (ownerFarmer != null)
                    {
                        actJob = new NpcJob
                        {
                            NpcName = compName,
                            Kind = JobKind.Follow,
                            FarmerId = ownerFarmer.UniqueMultiplayerID,
                            FarmerName = ownerFarmer.Name,
                            CreatedTick = Game1.ticks
                        };
                        _active[compName] = actJob;
                    }
                }
            }
        }

        // Tick active jobs with wall-clipping prevention
        foreach (var kv in _active.ToList())
        {
            if (kv.Value.Done) continue;

            var npc = FindNpc(kv.Key);
            if (npc?.controller != null)
            {
                // If stuck against an obstacle for > 5000ms, abort controller so vanilla ghost/wall-phasing mode NEVER activates!
                if (npc.controller.timerSinceLastCheckPoint > 5000)
                {
                    npc.controller = null;
                    npc.Halt();
                }
            }

            TickJob(kv.Value, gameTick);
        }

        CleanDone();

        // Update companion night torches / lanterns when dark
        UpdateCompanionNightTorches(gameTick);

        // Living Home Post-Marriage routine
        if (Context.IsWorldReady && !Game1.eventUp)
        {
            TickSpouseLivingHome(gameTick);
        }

        // Centralized anti-spam gate management for all active companions and spouses
        if (Context.IsWorldReady && !Game1.eventUp)
        {
            TickCompanionGateHandling(gameTick);
        }

        // Check for late-night farm interaction (after 9:00 PM / 2100) and curfew (after 1:00 AM / 2500)
        if (gameTick % 120 == 0 && Context.IsWorldReady && !Game1.eventUp && Game1.timeOfDay >= 2100)
        {
            CheckLateNightCompanions();
            CheckLateNightCurfew(gameTick);
        }
    }

    private void TickJob(NpcJob job, ulong gameTick)
    {
        if (Game1.IsMultiplayer && (!Game1.IsMasterGame || !Context.IsMainPlayer)) return;

        if (!Context.IsWorldReady || Game1.eventUp)
        {
            job.Done = true;
            return;
        }

        NPC? npc = FindNpc(job.NpcName);
        if (npc == null)
        {
            job.Done = true;
            return;
        }

        if (ValleyBuddies.Systems.LivingEvents.LivingEventManager.Instance.IsNpcWalkingInApproach(npc.Name))
        {
            return;
        }

        // Subterranean danger floor handling (v4.3.2):
        // 100% of movement and actions inside MineShaft/VolcanoDungeon are governed by TickCaveAutonomy
        if (IsUndergroundDangerLocation(npc.currentLocation))
        {
            Farmer owner = GetJobFarmer(job) ?? Game1.player;
            TickCaveAutonomy(job, npc, owner, gameTick);
            return;
        }

        _companions.TryGetValue(npc.Name, out var cmp);
        TryGetSpouseHomeState(npc.Name, out var sState);

        // TeleportGuard: monitor anomalous intra-map positional snapping (> 160px / 2.5 tiles)
        string curLocName = npc.currentLocation?.NameOrUniqueName ?? string.Empty;
        Vector2 curPos = npc.Position;
        if (!string.IsNullOrEmpty(curLocName))
        {
            if (_teleportGuardTrackers.TryGetValue(npc.Name, out var track))
            {
                if (track.LocName == curLocName)
                {
                    float jumpDist = Vector2.Distance(track.Pos, curPos);
                    if (jumpDist > 160f)
                    {
                        if (gameTick >= track.LastLogTick + 60UL)
                        {
                            _debug_log($"[TeleportGuard] Npc {npc.Name} jumped {jumpDist:F1}px from {track.Pos} to {curPos} in map {curLocName} during job {job.Kind}");
                            _teleportGuardTrackers[npc.Name] = (curPos, curLocName, gameTick);
                        }
                    }
                    else
                    {
                        _teleportGuardTrackers[npc.Name] = (curPos, curLocName, track.LastLogTick);
                    }
                }
                else
                {
                    _teleportGuardTrackers[npc.Name] = (curPos, curLocName, track.LastLogTick);
                }
            }
            else
            {
                _teleportGuardTrackers[npc.Name] = (curPos, curLocName, 0UL);
            }
        }

        if (TickBedSlideOut(npc, cmp, sState))
        {
            return;
        }

        if (cmp != null && cmp.AffectionAnimationUntilTick > 0)
        {
            if (gameTick < cmp.AffectionAnimationUntilTick)
            {
                return;
            }
            cmp.AffectionAnimationUntilTick = 0;
            try
            {
                if (npc.Sprite != null)
                {
                    npc.Sprite.CurrentAnimation = null;
                    npc.Sprite.StopAnimation();
                    npc.Sprite.UpdateSourceRect();
                }
            }
            catch { }
        }

        if (_jobHandlers.TryGetHandler(job.Kind, out var handler))
        {
            Farmer? owner = GetJobFarmer(job) ?? GetCompanionOwner(job.NpcName) ?? Game1.player;
            var ctx = new JobExecutionContext(
                job,
                npc,
                owner,
                cmp,
                gameTick,
                _navigator,
                _farmWorld,
                RecordMemory,
                RecordDepositedItem,
                _debug_log);
            handler.Tick(ctx);
            return;
        }

        switch (job.Kind)
        {
            case JobKind.ComeHere:       TickComeHere(job, npc, gameTick); break;
            case JobKind.Follow:         TickFollow(job, npc, gameTick); break;
            case JobKind.GoToLocation:   TickHangOut(job, npc, gameTick); break;
            case JobKind.VisitFarm:      TickVisitFarm(job, npc, gameTick); break;
            case JobKind.HangOut:        TickHangOut(job, npc, gameTick); break;
            case JobKind.WaterCrops:     TickWaterCrops(job, npc, gameTick); break;
            case JobKind.Sleepover:      TickSleepover(job, npc, gameTick); break;
            case JobKind.Campfire:
            case JobKind.CampSleepTogether: TickCamping(job, npc, gameTick); break;
            case JobKind.StopFollowing:          TickStopFollowing(job, npc, gameTick); break;
            case JobKind.FeedAnimals:            TickFeedAnimals(job, npc, gameTick); break;
            case JobKind.CollectAnimalProducts:  TickCollectAnimalProducts(job, npc, gameTick); break;
            case JobKind.WaterPetBowl:           TickWaterPetBowl(job, npc, gameTick); break;
            case JobKind.CollectMachines:        TickCollectMachines(job, npc, gameTick); break;
            case JobKind.PlantSeeds:             TickPlantSeeds(job, npc, gameTick); break;
            case JobKind.RepairFences:           TickRepairFences(job, npc, gameTick); break;
            case JobKind.CombatAssist:           TickCombatAssist(job, npc, gameTick); break;
            case JobKind.FishTogether:           TickFishTogether(job, npc, gameTick); break;
            case JobKind.ScoutMines:             TickScoutMines(job, npc, gameTick); break;
            case JobKind.CookMeal:               TickCookMeal(job, npc); break;
            case JobKind.PlayMusic:              TickPlayMusic(job, npc); break;
            case JobKind.DanceTogether:          TickDanceTogether(job, npc); break;
            case JobKind.Stargaze:               TickStargaze(job, npc); break;
            case JobKind.SendLetterTomorrow:     TickSendLetterTomorrow(job, npc); break;
            default:                     job.Done = true; break;
        }
    }

    // ─────────────── Movement implementations ────────────────────

    private void TickComeHere(NpcJob job, NPC npc, ulong gameTick)
    {
        var player = GetJobFarmer(job);
        if (player?.currentLocation == null) return;

        var npcLoc = npc.currentLocation;
        var playerLoc = player.currentLocation;
        if (npcLoc == null || playerLoc == null) return;

        if (npcLoc.NameOrUniqueName != playerLoc.NameOrUniqueName)
        {
            if (_allowCrossMap)
            {
                Point spawn = GetCrossMapSpawnTile(playerLoc, player.TilePoint, player.FacingDirection);
                Game1.warpCharacter(npc, playerLoc, new Vector2(spawn.X, spawn.Y));
                npc.controller = null;
                npc.Halt();
            }
            return;
        }

        float dist = Vector2.Distance(npc.Tile, player.Tile);
        if (dist <= 1.8f)
        {
            npc.controller = null;
            npc.Halt();
            npc.faceTowardFarmerForPeriod(2000, 3, false, player);
            job.Kind = JobKind.HangOut;
            job.Data["locationName"] = npcLoc.NameOrUniqueName;
            npc.showTextAboveHead(Utilities.I18n.Get("bubble.visiting_stay"), duration: 3500);
            return;
        }

        if (!job.Data.TryGetValue("come_here_start_tick", out var startObj) || startObj is not ulong startTick)
        {
            startTick = gameTick;
            job.Data["come_here_start_tick"] = startTick;
        }
        int timeoutMinutes = Math.Clamp(_config?.ComeHereTimeoutMinutes ?? 30, 5, 180);
        ulong timeoutTicks = (ulong)timeoutMinutes * 42UL; // 10 min SDV = ~7s = 420 ticks
        if (gameTick >= startTick + timeoutTicks)
        {
            npc.controller = null;
            npc.Halt();
            CompleteJob(job, npc);
            return;
        }

        Point targetTile = GetAdjacentWalkableTile(npcLoc, player.TilePoint, npc.TilePoint);
        MoveNpcTowards(job, npc, npcLoc, targetTile, gameTick);
    }

    /// <summary>
    /// Checks if the companion is directly in front of the player's face (blocking their facing direction).
    /// </summary>
    private static bool IsDirectlyInFrontOfFarmer(Farmer player, NPC npc)
    {
        if (player == null || npc == null) return false;
        Point pTile = player.TilePoint;
        Point nTile = npc.TilePoint;
        return player.FacingDirection switch
        {
            0 => nTile.X == pTile.X && (nTile.Y == pTile.Y - 1 || nTile.Y == pTile.Y - 2), // Facing Up
            1 => nTile.Y == pTile.Y && (nTile.X == pTile.X + 1 || nTile.X == pTile.X + 2), // Facing Right
            2 => nTile.X == pTile.X && (nTile.Y == pTile.Y + 1 || nTile.Y == pTile.Y + 2), // Facing Down
            3 => nTile.Y == pTile.Y && (nTile.X == pTile.X - 1 || nTile.X == pTile.X - 2), // Facing Left
            _ => false
        };
    }

    /// <summary>
    /// Calculates a target tile prioritizing the companion flanking the player (1 tile to the left or right of facing direction),
    /// or behind them, preventing the companion from standing right in front of the player's face.
    /// Falls back strictly to single-file behind in narrow corridors (like Farm North Entrance).
    /// </summary>
    private static Point GetFlankingFollowerTargetTile(GameLocation loc, Farmer player, Point npcTile, NPC? npc = null)
    {
        Point pTile = player.TilePoint;
        Point facingVec = player.FacingDirection switch
        {
            0 => new Point(0, -1),
            1 => new Point(1, 0),
            2 => new Point(0, 1),
            3 => new Point(-1, 0),
            _ => new Point(0, 1)
        };
        Point perpVec = facingVec.X != 0 ? new Point(0, 1) : new Point(1, 0);

        // Check if lateral flank tiles are walkable
        bool leftWalkable = IsTileWalkable(loc, new Point(pTile.X + perpVec.X, pTile.Y + perpVec.Y), npc);
        bool rightWalkable = IsTileWalkable(loc, new Point(pTile.X - perpVec.X, pTile.Y - perpVec.Y), npc);

        // Pasillo estrecho (paredes a izquierda y derecha, o entrada norte de Farm con Y <= 9):
        bool isNorthEntranceCorridor = loc is Farm && (pTile.Y <= 9 || npcTile.Y <= 9);
        if ((!leftWalkable && !rightWalkable) || (isNorthEntranceCorridor && (!leftWalkable || !rightWalkable)))
        {
            // Fila india estricta detrás del jugador
            Point singleFile1 = new Point(pTile.X - facingVec.X, pTile.Y - facingVec.Y);
            if (IsTileWalkable(loc, singleFile1, npc))
                return singleFile1;

            Point singleFile2 = new Point(pTile.X - facingVec.X * 2, pTile.Y - facingVec.Y * 2);
            if (IsTileWalkable(loc, singleFile2, npc))
                return singleFile2;
        }

        Point[] flankOffsets = player.FacingDirection switch
        {
            0 => new Point[] { new(1, 0), new(-1, 0), new(1, 1), new(-1, 1), new(0, 1), new(0, 2), new(2, 0), new(-2, 0) }, // Up: Flanks, then rear diagonals, then behind
            1 => new Point[] { new(0, 1), new(0, -1), new(-1, 1), new(-1, -1), new(-1, 0), new(-2, 0), new(0, 2), new(0, -2) }, // Right: Flanks, then rear diagonals, then behind
            2 => new Point[] { new(1, 0), new(-1, 0), new(1, -1), new(-1, -1), new(0, -1), new(0, -2), new(2, 0), new(-2, 0) }, // Down: Flanks, then rear diagonals, then behind
            3 => new Point[] { new(0, 1), new(0, -1), new(1, 1), new(1, -1), new(1, 0), new(2, 0), new(0, 2), new(0, -2) }, // Left: Flanks, then rear diagonals, then behind
            _ => new Point[] { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) }
        };

        Point best = Point.Zero;
        float bestDist = float.MaxValue;
        foreach (var off in flankOffsets)
        {
            Point cand = new(pTile.X + off.X, pTile.Y + off.Y);
            if (IsTileWalkable(loc, cand, npc))
            {
                float d = Vector2.Distance(new Vector2(cand.X, cand.Y), new Vector2(npcTile.X, npcTile.Y));
                if (d < bestDist)
                {
                    bestDist = d;
                    best = cand;
                }
            }
        }

        if (best != Point.Zero)
            return best;

        return GetFollowerTargetTile(loc, player, npcTile, npc);
    }

    private void TickFollow(NpcJob job, NPC npc, ulong gameTick)
    {
        var owner = GetJobFarmer(job);
        if (owner == null || owner.currentLocation == null)
        {
            CompleteJob(job, npc);
            return;
        }

        if (_companions.TryGetValue(npc.Name, out var cmp))
        {
            cmp.Hearts = owner.getFriendshipHeartLevelForNPC(npc.Name);

            if (cmp.AffectionAnimationUntilTick > 0)
            {
                if (gameTick < cmp.AffectionAnimationUntilTick)
                {
                    npc.addedSpeed = 0;
                    return;
                }
                cmp.AffectionAnimationUntilTick = 0;
                try { npc.Sprite?.StopAnimation(); } catch { }
                try { npc.Sprite?.UpdateSourceRect(); } catch { }
            }

            TickCompanionTenderMoments(job, npc, cmp, owner, gameTick);
            TickCompanionFishing(job, npc, cmp, owner, gameTick);
            TickTownHangouts(job, npc, cmp, owner, gameTick);
            TickCompanionBonding(job, npc, cmp, owner, gameTick);
            TickRareFindReactions(job, npc, cmp, owner, gameTick);
            TickSeasonWeatherFlavor(job, npc, cmp, owner, gameTick);
            TickCompanionLivingEvents(job, npc, cmp, owner, gameTick);

            if (gameTick % 120 == 0 && _config?.EnableEasterEggs != false && npc.currentLocation == owner.currentLocation && Vector2.Distance(npc.Tile, owner.Tile) <= 6.5f)
            {
                EasterEggEngine.CheckCompanionEasterEggs(npc, owner, npc.currentLocation);
            }

            if (cmp.IsFishingTogether || cmp.IsWalkingToFishingSpot)
            {
                return;
            }

            if (npc.currentLocation is StardewValley.Locations.BathHousePool && cmp.IsWearingBeachOutfit)
            {
                if (TickSpaPoolSwimming(job, npc, cmp, owner, gameTick))
                {
                    return;
                }
            }

            // v4.3.3: Si el acompañante está pausado o anclado en casa mientras el dueño está afuera
            if (cmp.IsPausedWhileOwnerOutsideHouse || (cmp.IsSettledInsideHouseForNight && (npc.currentLocation is FarmHouse || npc.currentLocation?.NameOrUniqueName.Contains("Cabin") == true) && owner.currentLocation != npc.currentLocation))
            {
                return;
            }

            if (cmp.NightState == NightLifecycleState.CampOutdoorIdle)
            {
                TickCampOutdoorIdle(job, npc);
                return;
            }

            // Resumption of follow when player returns to cave entrance lobby
            if (cmp.IsWaitingAtCaveEntrance)
            {
                if (IsCaveOrMineEntrance(owner.currentLocation))
                {
                    cmp.IsWaitingAtCaveEntrance = false;
                    npc.doEmote(20);
                    npc.showTextAboveHead(Utilities.I18n.Get("speech.follow_confirm", new { playerName = owner.displayName }), duration: 2500);
                }
                else
                {
                    // Remain waiting safely at entrance
                    return;
                }
            }
        }

        // Si acordó quedarse a dormir y ya está dentro de la casa/cabaña, transicionar a Sleepover
        if (job.Data.ContainsKey("stay_in_house_on_entry") && (npc.currentLocation is FarmHouse || npc.currentLocation?.NameOrUniqueName.Contains("Cabin") == true))
        {
            job.Kind = JobKind.Sleepover;
            job.Data.Remove("stay_in_house_on_entry");
        }

        // Modo hogareño nocturno (>= 18:00) dentro de la casa/cabaña del dueño
        if (Game1.timeOfDay >= 1800 &&
            owner.currentLocation is FarmHouse fh &&
            npc.currentLocation?.NameOrUniqueName == fh.NameOrUniqueName &&
            (_config == null || _config.EnableCozyIndoorEvening) &&
            !job.Data.ContainsKey("force_tight_follow_indoors"))
        {
            if (cmp?.WokeUpByPlayerTonight == true)
            {
                // El jugador despertó explícitamente al NPC: seguir normalmente sin mandar a dormir
                npc.addedSpeed = 0;
            }
            else
            {
                StartOrTickIndoorEvening(job, npc, fh, owner, gameTick);
                return;
            }
        }

        if (npc.currentLocation is FarmHouse || npc.currentLocation?.NameOrUniqueName.Contains("Cabin") == true)
        {
            npc.addedSpeed = 0;
        }

        // Si el dueño salió de la casa antes de acostarse, limpiar estado nocturno para evitar cartas falsas
        if (job.Data.ContainsKey("evening_phase"))
        {
            job.Data.Remove("evening_phase");
            job.Data.Remove("evening_poi");
            job.Data.Remove("evening_bed_spot");
            job.Data.Remove("evening_bed_announced");
            if (npc.isSleeping.Value) npc.isSleeping.Value = false;
        }

        // Cruce legítimo entre mapas cuando el dueño cambia de ubicación
        if (npc.currentLocation != owner.currentLocation && !string.Equals(npc.currentLocation?.NameOrUniqueName, owner.currentLocation.NameOrUniqueName, StringComparison.OrdinalIgnoreCase))
        {
            if (!_allowCrossMap) return;
            if (job.Data.TryGetValue("last_warp_tick", out var lwtObj) && lwtObj is ulong lwt && gameTick < lwt + 90)
            {
                return; // Cortafuegos: cooldown de mínimo 90 ticks (1.5s) entre teletransportes
            }

            var caveProf = CavePersonalityRegistry.ResolveProfile(npc);
            if (caveProf.IsChildProtected && IsUndergroundDangerLocation(owner.currentLocation))
            {
                var entrance = GetCaveEntranceLocation(owner.currentLocation);
                if (entrance != null && npc.currentLocation != entrance)
                {
                    if (cmp != null) cmp.IsWaitingAtCaveEntrance = true;
                    Point freeTile = FindSafeWarpSpawnTile(entrance, new Point(18, 13), 2);
                    job.Data["last_warp_tick"] = gameTick;
                    Game1.warpCharacter(npc, entrance, new Vector2(freeTile.X, freeTile.Y));
                    npc.controller = null;
                    npc.Halt();
                    npc.doEmote(16);
                    npc.showTextAboveHead(Utilities.I18n.Get("cave.child.stay_entrance", new { playerName = owner.displayName, npcName = npc.displayName }), duration: 4500);
                }
                return;
            }

            int heartsLvl = owner.getFriendshipHeartLevelForNPC(npc.Name);
            bool isExtremeDangerOwner = (owner.currentLocation is MineShaft msExtO && msExtO.mineLevel >= 121) || (owner.currentLocation is VolcanoDungeon vdExtO && vdExtO.level.Value >= 1);
            if (isExtremeDangerOwner && heartsLvl < 5 && (caveProf.Archetype == CaveArchetype.SupportHealer || caveProf.Archetype == CaveArchetype.CautiousFortune))
            {
                var entrance = GetCaveEntranceLocation(owner.currentLocation);
                if (entrance != null && npc.currentLocation != entrance)
                {
                    if (cmp != null) cmp.IsWaitingAtCaveEntrance = true;
                    Point freeTile = FindSafeWarpSpawnTile(entrance, new Point(18, 13), 2);
                    job.Data["last_warp_tick"] = gameTick;
                    Game1.warpCharacter(npc, entrance, new Vector2(freeTile.X, freeTile.Y));
                    npc.controller = null;
                    npc.Halt();
                    npc.doEmote(28); // sweating / nervous
                    npc.showTextAboveHead(Utilities.I18n.Get("cave.danger.need_more_hearts", new { playerName = owner.displayName, npcName = npc.displayName }), duration: 4500);
                }
                return;
            }

            bool wasInUnderground = IsUndergroundDangerLocation(npc.currentLocation);
            bool isNowInUnderground = IsUndergroundDangerLocation(owner.currentLocation);

            if (wasInUnderground && !isNowInUnderground)
            {
                RemoveCompanionCaveLight(npc.Name, npc.currentLocation);
                ResetCaveFloorState(npc.currentLocation?.NameOrUniqueName);
            }

            if (isNowInUnderground)
            {
                RemoveCompanionCaveLight(npc.Name, npc.currentLocation);
                ResetCaveFloorState(npc.currentLocation?.NameOrUniqueName);
                job.Data.Remove("lastCaveMine");

                Point caveEntry = FindSafeCaveEntryTile(owner.currentLocation, owner.TilePoint, owner.FacingDirection, npc);
                StopNpcCleanly(npc);
                job.Data["last_warp_tick"] = gameTick;
                Game1.warpCharacter(npc, owner.currentLocation, new Vector2(caveEntry.X, caveEntry.Y));
                _teleportGuardTrackers[npc.Name] = (npc.Position, owner.currentLocation.NameOrUniqueName, gameTick);
                npc.farmerPassesThrough = true;
                job.Data["cave_entry_tile"] = owner.TilePoint;
                job.Data["cave_pass_through_until"] = gameTick + 45;
                UpdateCompanionCaveLight(npc, owner.currentLocation);
                CheckCaveBiomeReaction(npc, owner.currentLocation, owner, gameTick);

                job.Data["follow_pause_until"] = gameTick + 18;
                job.Data["follow_is_resting"] = false;
                job.Data["follow_last_pos"] = npc.Position;
                job.Data["follow_last_moved_tick"] = gameTick;
                return;
            }

            Point entry = FindSafeWarpSpawnTile(owner.currentLocation, owner.TilePoint, owner.FacingDirection);
            StopNpcCleanly(npc);
            npc.addedSpeed = 0;
            job.Data["last_warp_tick"] = gameTick;
            Game1.warpCharacter(npc, owner.currentLocation, new Vector2(entry.X, entry.Y));
            _teleportGuardTrackers[npc.Name] = (npc.Position, owner.currentLocation.NameOrUniqueName, gameTick);
            CheckBeachOutfit(npc, owner.currentLocation);
            if (IsDirectlyInFrontOfFarmer(owner, npc))
            {
                npc.farmerPassesThrough = true;
            }
            job.Data["follow_pause_until"] = gameTick + 18;
            job.Data["follow_is_resting"] = false;
            job.Data["follow_last_pos"] = npc.Position;
            job.Data["follow_last_moved_tick"] = gameTick;
            return;
        }

        var loc = npc.currentLocation;
        if (loc == null) return;
        if (gameTick % 60 == 0)
        {
            CheckBeachOutfit(npc, loc);
        }

        // Subterranean companion autonomous actions (mining, combat, ladder spotting, stamina, light)
        // Cede el 100% del control de movimiento a la IA de cuevas para evitar que TickFollow pelee contra el pathfinder
        if (IsUndergroundDangerLocation(loc))
        {
            TickCaveAutonomy(job, npc, owner, gameTick);
            return;
        }

        if (job.Data.TryGetValue("cave_pass_through_until", out var cpu) && cpu is ulong passUntil && gameTick < passUntil)
        {
            npc.farmerPassesThrough = true;
        }
        float dist = Vector2.Distance(npc.Tile, owner.Tile);
        bool isResting = job.Data.TryGetValue("follow_is_resting", out var r) && r is bool b && b;

        // Monitor de atasco y recálculo de ruta suave
        Vector2 currentPos = npc.Position;
        Vector2 lastPos = job.Data.TryGetValue("follow_last_pos", out var lp) && lp is Vector2 v ? v : currentPos;
        ulong lastMovedTick = job.Data.TryGetValue("follow_last_moved_tick", out var lmt) && lmt is ulong m ? m : gameTick;

        if (Vector2.Distance(currentPos, lastPos) > 4f)
        {
            job.Data["follow_last_pos"] = currentPos;
            job.Data["follow_last_moved_tick"] = gameTick;
        }
        else if (!isResting)
        {
            ulong stuckTicks = gameTick >= lastMovedTick ? (gameTick - lastMovedTick) : 0;

            // Recálculo de ruta suave si se frena caminando cerca del jugador (75 ticks = 1.25s)
            if (dist > 2.8f && dist <= 12f && stuckTicks >= 75)
            {
                npc.controller = null;
                npc.Halt();
                job.Data.Remove("lastPathTick");
                job.Data.Remove("lastDest");
                job.Data["follow_last_pos"] = currentPos;
                job.Data["follow_last_moved_tick"] = gameTick;
            }
            // Salvavidas extremo de emergencia (fuera de pantalla / invisible ONLY):
            // Inmóvil >= 480 ticks (8.0s) Y distancia al dueño > 16 tiles
            else if (stuckTicks >= 480 && dist > 16f)
            {
                Point safeFollowTile = GetAdjacentWalkableTile(loc, owner.TilePoint, owner.TilePoint, npc);
                if (safeFollowTile != Point.Zero && safeFollowTile != npc.TilePoint)
                {
                    LogWarpAudit("FollowOffscreenEmergencyRescue", npc, loc, safeFollowTile);
                    npc.setTileLocation(new Vector2(safeFollowTile.X, safeFollowTile.Y));
                    npc.controller = null;
                    npc.Halt();
                    job.Data.Remove("lastPathTick");
                    job.Data.Remove("lastDest");
                    job.Data["follow_pause_until"] = gameTick + 10;
                    job.Data["follow_is_resting"] = false;
                    job.Data["follow_last_pos"] = npc.Position;
                    job.Data["follow_last_moved_tick"] = gameTick;
                    _teleportGuardTrackers[npc.Name] = (npc.Position, loc.NameOrUniqueName, gameTick);
                }
                else
                {
                    job.Data["follow_last_moved_tick"] = gameTick;
                }
            }
        }

        if (job.Data.TryGetValue("follow_pause_until", out var pu) && pu is ulong pauseUntil && gameTick < pauseUntil)
            return;

        if (!isResting && dist <= 2.7f)
        {
            StopNpcCleanly(npc);
            npc.addedSpeed = 0;
            job.Data["follow_is_resting"] = true;
            job.Data["follow_next_glance"] = gameTick + (ulong)Game1.random.Next(240, 480);
            npc.faceTowardFarmerForPeriod(1800, 3, false, owner);
            return;
        }

        if (isResting)
        {
            npc.addedSpeed = 0;
            if (dist <= 3.3f)
            {
                ulong nextGlance = job.Data.TryGetValue("follow_next_glance", out var ng) && ng is ulong u ? u : 0UL;
                if (gameTick >= nextGlance)
                {
                    if (Game1.random.NextDouble() < 0.6)
                        npc.faceTowardFarmerForPeriod(1500, 3, false, owner);
                    else
                        npc.faceDirection(Game1.random.Next(4));
                    job.Data["follow_next_glance"] = gameTick + (ulong)Game1.random.Next(300, 540);
                }
                return;
            }
            job.Data["follow_is_resting"] = false;
        }

        Point targetPoint = GetFlankingFollowerTargetTile(loc, owner, npc.TilePoint, npc);
        bool inFront = IsDirectlyInFrontOfFarmer(owner, npc);
        bool isNarrowCorridor = (npc.currentLocation is Farm && (npc.Tile.Y <= 9 || owner.Tile.Y <= 9));

        // Evitar atasco contra el hitbox del jugador: paso suave sin colisión mutua
        if (isNarrowCorridor || (inFront && dist <= 2.5f) || dist <= 1.5f)
        {
            npc.farmerPassesThrough = true;
        }
        else if (dist > 3.0f)
        {
            npc.farmerPassesThrough = false;
        }

        if (inFront && dist <= 2.2f)
        {
            // Companion is blocking the farmer's face/path! Step to free flank or single-file behind immediately.
            job.Data["follow_is_resting"] = false;
            MoveNpcTowards(job, npc, loc, targetPoint, gameTick);
            ApplyFollowerTrackingSpeed(npc, dist);
            return;
        }

        if (npc.TilePoint == targetPoint && dist <= 3.5f)
        {
            StopNpcCleanly(npc);
            npc.addedSpeed = 0;
            job.Data["follow_is_resting"] = true;
            npc.faceTowardFarmerForPeriod(1500, 3, false, owner);
            return;
        }

        if (gameTick % 300 == 0 && _multiplayerSync != null && _config != null)
            _multiplayerSync.SyncAuraToOwner(npc, owner, _config.EnableCompanionBuffs);

        MoveNpcTowards(job, npc, loc, targetPoint, gameTick);
        ApplyFollowerTrackingSpeed(npc, dist);
    }

    private static void ApplyFollowerTrackingSpeed(NPC npc, float dist)
    {
        if (npc == null) return;
        if (dist <= 2.5f)
        {
            npc.addedSpeed = 0;
            return;
        }

        int desiredSpeed = dist > 6.0f ? 5 : 4;
        if (npc.controller?.pathToEndPoint != null && npc.controller.pathToEndPoint.Count <= 2)
        {
            desiredSpeed = Math.Min(desiredSpeed, 3);
        }

        npc.addedSpeed = Math.Clamp(desiredSpeed - npc.speed, 0, 3);
    }
    internal (bool IsManagedCompanion, string ActiveJobKind, bool IsSpouseInHomeRoutine) GetWarpTraceState(string? npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return (false, "none", false);

        bool isComp = _companions.ContainsKey(npcName);
        string activeKind = _active.TryGetValue(npcName, out var job) && !job.Done ? job.Kind.ToString() : "none";
        bool isSpouseHome = _spouseHomeStates.ContainsKey(npcName);
        return (isComp, activeKind, isSpouseHome);
    }

    internal void LogWarpAudit(string reason, NPC npc, GameLocation? targetLoc, Point targetTile)
    {
        if (npc == null || targetLoc == null) return;
        string oldLoc = npc.currentLocation?.NameOrUniqueName ?? "null";
        Point oldTile = npc.TilePoint;
        if (Bridge.WarpTracePatches.IsEnabled)
        {
            Bridge.WarpTracePatches.RecordExplicitAuditNote($"AUDIT reason={reason} npc={npc.Name} {oldLoc}@({oldTile.X},{oldTile.Y})->{targetLoc.NameOrUniqueName}@({targetTile.X},{targetTile.Y})");
        }
        if (_debug)
        {
            _monitor?.Log($"[WarpAudit] Warped {npc.Name} from {oldLoc}@{oldTile} to {targetLoc.NameOrUniqueName}@{targetTile} reason={reason}", LogLevel.Debug);
        }
    }

    public static Point GetFarmSafePorchTile(Farm farm)
    {
        Point houseDoor = farm.GetMainFarmHouseEntry();
        if (houseDoor == Point.Zero)
            houseDoor = new Point(64, 15);

        // Ground in front of porch stairs starts at Y + 3 and Y + 4
        Point[] candidates = new Point[]
        {
            new(houseDoor.X, houseDoor.Y + 4),
            new(houseDoor.X - 1, houseDoor.Y + 4),
            new(houseDoor.X + 1, houseDoor.Y + 4),
            new(houseDoor.X, houseDoor.Y + 3),
            new(houseDoor.X - 1, houseDoor.Y + 3),
            new(houseDoor.X + 1, houseDoor.Y + 3),
            new(houseDoor.X + 2, houseDoor.Y + 4),
            new(houseDoor.X - 2, houseDoor.Y + 4),
            new(houseDoor.X + 3, houseDoor.Y + 4),
        };

        foreach (var p in candidates)
        {
            Vector2 pVec = new Vector2(p.X, p.Y);
            if (farm.Objects != null && farm.Objects.TryGetValue(pVec, out var obj) && obj != null && !obj.isPassable())
                continue;
            if (farm.terrainFeatures != null && farm.terrainFeatures.TryGetValue(pVec, out var tf) && tf != null && !tf.isPassable(Game1.player))
                continue;
            if (IsTileWalkable(farm, p))
                return p;
        }

        // Si los candidatos frontales inmediatos tienen escombros, buscar casilla despejada contigua
        foreach (var p in candidates)
        {
            Point[] adj = new Point[] { new(p.X, p.Y + 1), new(p.X - 1, p.Y), new(p.X + 1, p.Y), new(p.X, p.Y - 1) };
            foreach (var a in adj)
            {
                Vector2 aVec = new Vector2(a.X, a.Y);
                if (farm.Objects != null && farm.Objects.TryGetValue(aVec, out var obj) && obj != null && !obj.isPassable())
                    continue;
                if (farm.terrainFeatures != null && farm.terrainFeatures.TryGetValue(aVec, out var tf) && tf != null && !tf.isPassable(Game1.player))
                    continue;
                if (IsTileWalkable(farm, a))
                    return a;
            }
        }

        return new Point(houseDoor.X, houseDoor.Y + 4);
    }

    private void TickVisitFarm(NpcJob job, NPC npc, ulong gameTick)
    {
        var player = GetJobFarmer(job);
        GameLocation? targetLoc = null;
        if (job.Data.TryGetValue("locationName", out var loc) && loc != null)
        {
            targetLoc = Game1.getLocationFromName(loc.ToString());
        }
        if (targetLoc == null && player?.currentLocation != null)
        {
            targetLoc = player.currentLocation;
        }
        if (targetLoc == null)
        {
            targetLoc = Game1.getFarm();
        }
        if (targetLoc == null) { job.Done = true; return; }

        TickHangOutInternal(job, npc, targetLoc, gameTick, targetLoc.NameOrUniqueName);
    }

    private void TickHangOut(NpcJob job, NPC npc, ulong gameTick)
    {
        string locName = job.Data.TryGetValue("locationName", out var l) ? l.ToString()! : "Farm";
        GameLocation targetLoc = Game1.getLocationFromName(locName) ?? Game1.getFarm() ?? Game1.currentLocation;
        TickHangOutInternal(job, npc, targetLoc, gameTick, locName);
    }

    private void TickHangOutInternal(NpcJob job, NPC npc, GameLocation targetLoc, ulong gameTick, string locLabel)
    {
        if (targetLoc == null) { job.Done = true; return; }

        // 1. Initial warp to destination if not already there
        if (npc.currentLocation?.NameOrUniqueName != targetLoc.NameOrUniqueName)
        {
            Point spawn;
            if (targetLoc is Farm farmLoc)
            {
                var p = GetJobFarmer(job);
                if (p?.currentLocation == farmLoc)
                {
                    spawn = GetAdjacentWalkableTile(farmLoc, p.TilePoint, p.TilePoint, npc);
                }
                else
                {
                    Point door = GetOwnerHomeExteriorDoorTile(farmLoc, p);
                    spawn = IsTileWalkable(farmLoc, new Point(door.X, door.Y + 1), npc)
                        ? new Point(door.X, door.Y + 1)
                        : GetFarmSafePorchTile(farmLoc);
                }
            }
            else if (targetLoc is StardewValley.Locations.FarmHouse fh)
            {
                spawn = fh.getEntryLocation();
            }
            else
            {
                var p = GetJobFarmer(job);
                Point entry = (p?.currentLocation == targetLoc)
                    ? p.TilePoint
                    : (targetLoc.warps.Count > 0 ? new Point(targetLoc.warps[0].TargetX, targetLoc.warps[0].TargetY) : targetLoc.getRandomTile().ToPoint());
                if (entry == Point.Zero) entry = new Point(10, 10);
                spawn = GetAdjacentWalkableTile(targetLoc, entry, entry, npc);
            }

            LogWarpAudit("TickHangOutInternal", npc, targetLoc, spawn);
            Game1.warpCharacter(npc, targetLoc, new Vector2(spawn.X, spawn.Y));
            npc.controller = null;
            npc.Halt();
            npc.followSchedule = false;
            CheckBeachOutfit(npc, targetLoc);
            npc.doEmote(32);
            _debug_log($"[Actions] {npc.Name} arrived safely at {targetLoc.NameOrUniqueName} to visit!");
            return;
        }

        // 2. Late night cutoff: at 2200 (10 PM), conclude visit and go home to sleep
        if (Game1.timeOfDay >= 2200)
        {
            npc.doEmote(24);
            job.Done = true;
            ReleaseNpc(npc.Name);
            _debug_log($"[Actions] {npc.Name} completed visit at nightfall.");
            return;
        }

        // Dynamic beach outfit check during visit
        if (gameTick % 60 == 0)
        {
            CheckBeachOutfit(npc, targetLoc);
        }

        // 3. Living human behavior tick (every 60 ticks / ~1 second)
        if (gameTick % 60 == 0)
        {
            var player = GetJobFarmer(job);
            bool playerHere = (player?.currentLocation?.NameOrUniqueName == targetLoc.NameOrUniqueName);

            // If the player moved between Cabin, Farmhouse, or Farm, follow them inside/outside!
            if (!playerHere && player?.currentLocation != null &&
                (player.currentLocation is Farm || player.currentLocation is StardewValley.Locations.FarmHouse || player.currentLocation.NameOrUniqueName.Contains("Cabin")))
            {
                targetLoc = player.currentLocation;
                job.Data["locationName"] = targetLoc.NameOrUniqueName;
                Point spawn = (targetLoc is StardewValley.Locations.FarmHouse fh)
                    ? fh.getEntryLocation()
                    : GetAdjacentWalkableTile(targetLoc, player.TilePoint, player.TilePoint);
                Game1.warpCharacter(npc, targetLoc, new Vector2(spawn.X, spawn.Y));
                npc.controller = null;
                npc.Halt();
                playerHere = true;
            }

            float distToPlayer = playerHere ? Vector2.Distance(npc.Tile, player!.Tile) : 999f;

            if (playerHere && job.Data.TryGetValue("visit_greeting_pending", out var vgObj) && vgObj is bool isVg && isVg)
            {
                if (distToPlayer <= 2.5f)
                {
                    job.Data.Remove("visit_greeting_pending");
                    npc.controller = null;
                    npc.Halt();
                    npc.faceTowardFarmerForPeriod(3000, 3, false, player);
                    string vKey = Game1.random.NextDouble() < 0.5 ? "visit.arrived_here.v1" : "visit.arrived_here.v2";
                    SpeakNonRepeating(npc, vKey, new { playerName = player!.Name, npcName = npc.displayName, farmerName = player!.Name, ownerName = player!.Name });
                    npc.doEmote(20);
                }
                else
                {
                    Point nearPlayer = GetAdjacentWalkableTile(targetLoc, player!.TilePoint, npc.TilePoint);
                    MoveNpcTowards(job, npc, targetLoc, nearPlayer, gameTick);
                    return;
                }
            }

            if (playerHere)
            {
                if (distToPlayer <= 2.0f)
                {
                    // Right next to player — face them and occasionally react with warmth
                    npc.faceTowardFarmerForPeriod(2000, 3, false, player);
                    if (Game1.random.NextDouble() < 0.05) // Gentle, calm reaction rate (~1 in 20 seconds when close)
                    {
                        string? custom = GetCharacterFarmDialogue(npc, player);
                        if (custom != null && Game1.random.NextDouble() < 0.40)
                        {
                            npc.doEmote(20);
                            npc.showTextAboveHead(custom, duration: 3800);
                        }
                        else
                        {
                            int emoteRoll = Game1.random.Next(14);
                            if      (emoteRoll == 0) npc.doEmote(20);      // ♥ heart
                            else if (emoteRoll == 1) npc.doEmote(16);      // ♪ music note
                            else if (emoteRoll == 2) npc.doEmote(32);      // 😊 wave/smile
                            else if (emoteRoll == 3) npc.doEmote(8);       // 💡 exclamation
                            else if (emoteRoll == 4) npc.doEmote(40);      // 😍 extra heart
                            else if (emoteRoll == 5) npc.showTextAboveHead(Utilities.I18n.Get("bubble.visiting_nice_1"));
                            else if (emoteRoll == 6) npc.showTextAboveHead(Utilities.I18n.Get("bubble.visiting_nice_2"));
                            else if (emoteRoll == 7) npc.showTextAboveHead(Utilities.I18n.Get("bubble.visiting_nice_3"));
                            else if (emoteRoll == 8) npc.showTextAboveHead(Utilities.I18n.Get("bubble.visiting_nice_4"));
                            else if (emoteRoll == 9) npc.showTextAboveHead(npc.Gender == Gender.Male ? Utilities.I18n.Get("bubble.visiting_nice_male") : Utilities.I18n.Get("bubble.visiting_nice_female"));
                        }
                    }
                }
                else if (distToPlayer <= 8f)
                {
                    // Nearby — walk towards player to socialize
                    Point nearPlayer = GetAdjacentWalkableTile(targetLoc, player!.TilePoint, npc.TilePoint);
                    MoveNpcTowards(job, npc, targetLoc, nearPlayer, gameTick);
                }
                else
                {
                    // Far from player — explore the farm naturally: crops, animals, chests, trees
                    DoFarmExploration(job, npc, targetLoc, gameTick);
                }
            }
            else
            {
                // Player is away — NPC explores near porch/yard
                if (targetLoc is Farm farmLoc)
                {
                    DoFarmExploration(job, npc, farmLoc, gameTick);
                }
            }
        }

        // Occasional spontaneous comments while exploring (every ~20 seconds at 10% chance - peaceful, not spammy)
        if (gameTick % 1200 == 0 && Game1.random.NextDouble() < 0.10)
        {
            DoSpontaneousComment(npc, targetLoc);
        }
    }

    private void DoSpontaneousComment(NPC npc, GameLocation loc)
    {
        var farmer = GetCompanionOwner(npc.Name) ?? Game1.player;
        string? characterLine = GetCharacterFarmDialogue(npc, farmer);

        string msg;
        if (characterLine != null && Game1.random.NextDouble() < 0.40)
        {
            msg = characterLine;
        }
        else if (npc.Gender == Gender.Male && Game1.random.NextDouble() < 0.50)
        {
            int v = Game1.random.Next(1, 6);
            msg = Utilities.I18n.Get($"farm.comment.male.v{v}", new { playerName = farmer?.displayName ?? "amigo" });
        }
        else if (npc.Gender == Gender.Female && Game1.random.NextDouble() < 0.50)
        {
            int v = Game1.random.Next(1, 6);
            msg = Utilities.I18n.Get($"farm.comment.female.v{v}", new { playerName = farmer?.displayName ?? "amigo" });
        }
        else
        {
            int v = Game1.random.Next(1, 15);
            msg = Utilities.I18n.Get($"farm.comment.general.v{v}", new { playerName = farmer?.displayName ?? "amigo" });
        }

        npc.showTextAboveHead(msg, duration: 3200);
    }

    private static void DoFarmExploration(NpcJob job, NPC npc, GameLocation loc, ulong gameTick)
    {
        // Priority 1: If there's a ready-to-harvest crop nearby, admire or harvest it
        var nearCrop = FindNearestReadyCrop(loc, npc.Tile, 10);
        if (nearCrop != null && Game1.random.NextDouble() < 0.35)
        {
            Point walkTo = GetAdjacentWalkableTile(loc, new Point((int)nearCrop.Value.tile.X, (int)nearCrop.Value.tile.Y), npc.TilePoint);
            MoveNpcTowards(job, npc, loc, walkTo, gameTick);
            return;
        }

        // Priority 2: Look at an animal nearby
        if (loc is Farm farm && farm.animals != null)
        {
            foreach (var anim in farm.animals.Values)
            {
                float dist = Vector2.Distance(npc.Tile, anim.Tile);
                if (dist < 15f && Game1.random.NextDouble() < 0.25)
                {
                    Point walkTo = GetAdjacentWalkableTile(loc, anim.TilePoint, npc.TilePoint);
                    MoveNpcTowards(job, npc, loc, walkTo, gameTick);
                    return;
                }
            }
        }

        // Priority 3: Wander randomly but stay near porch
        if (Game1.random.NextDouble() < 0.55)
        {
            Point porch = loc is Farm f2 ? GetFarmSafePorchTile(f2) : npc.TilePoint;
            int range = 8;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int rx = Game1.random.Next(-range, range + 1);
                int ry = Game1.random.Next(-range, range + 1);
                Point wander = new Point(porch.X + rx, porch.Y + ry);
                if (IsTileWalkable(loc, wander, npc))
                {
                    MoveNpcTowards(job, npc, loc, wander, gameTick);
                    return;
                }
            }
        }
    }

    public static bool IsCaveOrMineLocation(GameLocation? loc)
    {
        if (loc == null) return false;
        string name = loc.NameOrUniqueName ?? string.Empty;
        return loc is StardewValley.Locations.MineShaft
            || loc is StardewValley.Locations.VolcanoDungeon
            || name.Contains("Mine", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Cave", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Volcano", StringComparison.OrdinalIgnoreCase)
            || name.Contains("UndergroundMine", StringComparison.OrdinalIgnoreCase);
    }

    public void CheckBeachOutfit(NPC npc, GameLocation loc)
    {
        if (npc == null || loc == null) return;
        string locName = loc.NameOrUniqueName ?? string.Empty;
        bool isPool = locName.Equals("BathHouse_Pool", StringComparison.OrdinalIgnoreCase);
        bool isBeach = loc is StardewValley.Locations.Beach
            || locName.Contains("Beach", StringComparison.OrdinalIgnoreCase)
            || locName.Contains("IslandSouth", StringComparison.OrdinalIgnoreCase)
            || locName.Contains("IslandEast", StringComparison.OrdinalIgnoreCase)
            || locName.Contains("IslandWest", StringComparison.OrdinalIgnoreCase);

        bool raining = loc.IsRainingHere();
        _companions.TryGetValue(npc.Name, out var comp);

        try
        {
            if (isPool || (isBeach && !raining))
            {
                if (comp != null && !comp.IsWearingBeachOutfit && HasValidBeachOutfit(npc))
                {
                    comp.IsWearingBeachOutfit = true;
                    npc.shouldWearIslandAttire.Value = true;
                    IsApplyingBeachOutfit = true;
                    try
                    {
                        npc.wearIslandAttire();
                    }
                    finally
                    {
                        IsApplyingBeachOutfit = false;
                    }
                }
                else if (comp != null && comp.IsWearingBeachOutfit)
                {
                    if (!npc.shouldWearIslandAttire.Value)
                        npc.shouldWearIslandAttire.Value = true;
                }
            }
            else
            {
                if (npc.swimming.Value)
                {
                    npc.swimming.Value = false;
                    npc.drawOffset = Vector2.Zero;
                }

                if (comp != null && comp.IsWearingBeachOutfit)
                {
                    comp.IsWearingBeachOutfit = false;
                    npc.shouldWearIslandAttire.Value = false;
                    IsRestoringNormalClothes = true;
                    try
                    {
                        npc.wearNormalClothes();
                        npc.ChooseAppearance();
                    }
                    finally
                    {
                        IsRestoringNormalClothes = false;
                    }
                }
            }
        }
        catch { }
    }

    private void CheckCaveArrival(NPC npc, GameLocation loc, Farmer farmer)
    {
        if (npc == null || loc == null || farmer == null) return;
        if (!IsCaveOrMineLocation(loc)) return;

        string key = $"{npc.Name}_{loc.NameOrUniqueName}";
        if (_caveGreetedNPCs.Contains(key)) return;
        _caveGreetedNPCs.Add(key);

        npc.controller = null;
        npc.Halt();
        npc.faceTowardFarmerForPeriod(3000, 3, false, farmer);

        string greeting = GetCaveGreetingDialogue(npc, farmer);
        npc.showTextAboveHead(greeting, duration: 4200);

        int emote = (npc.Name is "Abigail" or "Lance" or "Marlon" or "Alex") ? 8 : (npc.Name is "Sofia" or "Penny" or "Haley" ? 16 : 32);
        npc.doEmote(emote);

        try
        {
            farmer.changeFriendship(10, npc);
        }
        catch { }

        RecordMemory(npc.Name, $"We entered {loc.DisplayName} together: \"{greeting}\"");
    }

    private static string GetCaveGreetingDialogue(NPC npc, Farmer farmer)
    {
        string pName = farmer?.displayName ?? "amigo";
        string npcKey = npc.Name.ToLowerInvariant();
        string key = $"cave.greet.{npcKey}";
        if (Utilities.I18n.Has(key))
        {
            return Utilities.I18n.Get(key, new { playerName = pName });
        }
        string genericKey = npc.Gender == Gender.Male ? "cave.greet.generic_male" : "cave.greet.generic_female";
        return Utilities.I18n.Get(genericKey, new { playerName = pName });
    }

    // Note: Cave actions and logic are centralized in Execution/NpcActionExecutor.Caves.cs

    private static string? GetCharacterFarmDialogue(NPC npc, Farmer? farmer)
    {
        string pName = farmer?.displayName ?? "amigo";
        string npcKey = npc.Name.ToLowerInvariant();
        int idx = Game1.random.Next(1, 4);
        string key = $"farm.comment.{npcKey}.v{idx}";
        if (Utilities.I18n.Has(key))
        {
            return Utilities.I18n.Get(key, new { playerName = pName });
        }
        return null;
    }

    /// <summary>
    /// Gestor centralizado y anti-spam de puertas de valla (simples y dobles).
    /// Detecta acompañantes o cónyuges acercándose a una puerta (<= 1.65 tiles) y abre todas las hojas del grupo
    /// sin toggleGate ni spam de audio (reproduciendo doorClose 1 sola vez con cooldown de 30 ticks).
    /// Mantiene la puerta abierta mientras alguien esté a <= 2.2 tiles, y la cierra suavemente 1 sola vez
    /// cuando todos están a > 2.4 tiles y han pasado al menos 35 ticks.
    /// </summary>
    public void TickCompanionGateHandling(ulong gameTick)
    {
        if (gameTick % 4 != 0) return;
        if (!Context.IsWorldReady) return;

        // 1. Recolectar NPCs relevantes gestionados por el mod
        List<NPC> managedNpcs = new();
        foreach (var kvp in _active)
        {
            var npc = FindNpc(kvp.Key);
            if (npc != null && npc.currentLocation != null)
                managedNpcs.Add(npc);
        }

        foreach (var kvp in _spouseHomeStates)
        {
            var spouse = Game1.getCharacterFromName(kvp.Key);
            if (spouse != null && spouse.currentLocation != null && !managedNpcs.Contains(spouse))
                managedNpcs.Add(spouse);
        }

        // 2. Apertura anticipada y limpia para NPCs que se aproximan a cualquier valla-puerta
        foreach (var npc in managedNpcs)
        {
            var loc = npc.currentLocation;
            if (loc?.Objects == null) continue;

            Point npcTile = npc.TilePoint;
            for (int dx = -2; dx <= 2; dx++)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    Vector2 checkTile = new Vector2(npcTile.X + dx, npcTile.Y + dy);
                    if (Vector2.Distance(npc.Tile, checkTile) <= 1.65f)
                    {
                        if (loc.Objects.TryGetValue(checkTile, out var obj) && obj is Fence fence && fence.isGate.Value)
                        {
                            // Encontrar el grupo de la puerta (hoja actual + vecinos cardinales que también sean puerta)
                            List<Vector2> gateGroup = new() { checkTile };
                            Vector2[] cardinals = new[]
                            {
                                new Vector2(checkTile.X, checkTile.Y - 1),
                                new Vector2(checkTile.X, checkTile.Y + 1),
                                new Vector2(checkTile.X - 1, checkTile.Y),
                                new Vector2(checkTile.X + 1, checkTile.Y)
                            };

                            foreach (var c in cardinals)
                            {
                                if (loc.Objects.TryGetValue(c, out var cObj) && cObj is Fence cFence && cFence.isGate.Value)
                                {
                                    if (!gateGroup.Contains(c)) gateGroup.Add(c);
                                }
                            }

                            // Si al menos una hoja del grupo está cerrada, abrir TODO el grupo limpiamente
                            bool anyClosed = false;
                            foreach (var gTile in gateGroup)
                            {
                                if (loc.Objects.TryGetValue(gTile, out var gObj) && gObj is Fence gFence && gFence.gatePosition.Value == 0)
                                {
                                    anyClosed = true;
                                    break;
                                }
                            }

                            if (anyClosed)
                            {
                                foreach (var gTile in gateGroup)
                                {
                                    if (loc.Objects.TryGetValue(gTile, out var gObj) && gObj is Fence gFence)
                                    {
                                        gFence.gatePosition.Value = Fence.gateOpenedPosition;
                                        _modOpenedGates[(loc.NameOrUniqueName, gTile)] = gameTick;
                                    }
                                }

                                string locKey = loc.NameOrUniqueName;
                                if (!_lastGateSoundTickByLoc.TryGetValue(locKey, out ulong lastSoundTick) || gameTick >= lastSoundTick + 45)
                                {
                                    loc.playSound("doorClose");
                                    _lastGateSoundTickByLoc[locKey] = gameTick;
                                }
                            }
                        }
                    }
                }
            }
        }

        // 3. Evaluar puertas previamente abiertas por el mod: mantener abiertas o cerrar limpiamente en grupo
        if (_modOpenedGates.Count == 0) return;

        List<Farmer> allFarmers = Game1.getAllFarmers()?.ToList() ?? new List<Farmer>();
        HashSet<(string LocationName, Vector2 Tile)> processed = new();

        foreach (var kvp in _modOpenedGates.ToList())
        {
            var key = kvp.Key;
            if (processed.Contains(key)) continue;

            GameLocation? loc = Game1.getLocationFromName(key.LocationName);
            if (loc == null || loc.Objects == null || !loc.Objects.TryGetValue(key.Tile, out var obj) || obj is not Fence gateFence || !gateFence.isGate.Value)
            {
                _modOpenedGates.Remove(key);
                processed.Add(key);
                continue;
            }

            // Agrupar todas las hojas contiguas de esta puerta que también estén en _modOpenedGates
            List<(string LocationName, Vector2 Tile)> groupKeys = new() { key };
            Vector2[] adjCardinals = new[]
            {
                new Vector2(key.Tile.X, key.Tile.Y - 1),
                new Vector2(key.Tile.X, key.Tile.Y + 1),
                new Vector2(key.Tile.X - 1, key.Tile.Y),
                new Vector2(key.Tile.X + 1, key.Tile.Y)
            };

            foreach (var adj in adjCardinals)
            {
                var adjKey = (key.LocationName, adj);
                if (_modOpenedGates.ContainsKey(adjKey) && !groupKeys.Contains(adjKey))
                {
                    groupKeys.Add(adjKey);
                }
            }

            foreach (var gk in groupKeys) processed.Add(gk);

            // Verificar si algún NPC gestionado o algún Farmer está cerca (<= 2.6f) de CUALQUIER hoja del grupo
            bool groupHasSomeoneNear = false;
            ulong minOpenedTick = ulong.MaxValue;

            foreach (var gk in groupKeys)
            {
                if (_modOpenedGates.TryGetValue(gk, out ulong tOpened) && tOpened < minOpenedTick)
                    minOpenedTick = tOpened;

                foreach (var npc in managedNpcs)
                {
                    if (npc.currentLocation == loc && Vector2.Distance(npc.Tile, gk.Tile) <= 2.6f)
                    {
                        groupHasSomeoneNear = true;
                        break;
                    }
                }
                if (groupHasSomeoneNear) break;

                foreach (var farmer in allFarmers)
                {
                    if (farmer.currentLocation == loc && Vector2.Distance(farmer.Tile, gk.Tile) <= 2.6f)
                    {
                        groupHasSomeoneNear = true;
                        break;
                    }
                }
                if (groupHasSomeoneNear) break;
            }

            if (groupHasSomeoneNear)
            {
                // Mantener todas las hojas del grupo abiertas y refrescar tick
                foreach (var gk in groupKeys)
                {
                    if (loc.Objects.TryGetValue(gk.Tile, out var gObj) && gObj is Fence gFence)
                    {
                        gFence.gatePosition.Value = Fence.gateOpenedPosition;
                        _modOpenedGates[gk] = gameTick;
                    }
                }
            }
            else
            {
                // Si todo el grupo está libre (> 2.6f) y han pasado al menos 50 ticks desde su apertura, cerrar el grupo junto
                if (gameTick >= minOpenedTick + 50)
                {
                    foreach (var gk in groupKeys)
                    {
                        if (loc.Objects.TryGetValue(gk.Tile, out var gObj) && gObj is Fence gFence)
                        {
                            gFence.gatePosition.Value = 0;
                        }
                        _modOpenedGates.Remove(gk);
                    }

                    string locKey = loc.NameOrUniqueName;
                    if (!_lastGateSoundTickByLoc.TryGetValue(locKey, out ulong lastSoundTick) || gameTick >= lastSoundTick + 45)
                    {
                        loc.playSound("doorClose");
                        _lastGateSoundTickByLoc[locKey] = gameTick;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Determina si una puerta de valla está protegida contra cierre automático intempestivo
    /// (abierta recientemente por el mod o con un acompañante/cónyuge cruzando a <= 2.5 casillas).
    /// </summary>
    public bool IsGateProtectedFromAutoClose(Fence fence)
    {
        if (fence == null) return false;
        var loc = fence.Location;
        if (loc == null) return false;
        Vector2 tile = fence.TileLocation;
        string locName = loc.NameOrUniqueName;
        ulong curTick = ModEntry.CurrentTick;

        // Comprobar si esta hoja o una contigua fue abierta/gestionada en los últimos 45 ticks
        Vector2[] tilesToCheck = new[]
        {
            tile,
            new Vector2(tile.X, tile.Y - 1),
            new Vector2(tile.X, tile.Y + 1),
            new Vector2(tile.X - 1, tile.Y),
            new Vector2(tile.X + 1, tile.Y)
        };

        foreach (var t in tilesToCheck)
        {
            if (_modOpenedGates.TryGetValue((locName, t), out ulong openedTick))
            {
                if (curTick < openedTick + 45)
                    return true;
            }
        }

        // Comprobar si algún acompañante o cónyuge gestionado está a <= 2.5f casillas
        foreach (var kvp in _active)
        {
            var npc = FindNpc(kvp.Key);
            if (npc != null && npc.currentLocation == loc && Vector2.Distance(npc.Tile, tile) <= 2.5f)
                return true;
        }

        foreach (var kvp in _spouseHomeStates)
        {
            var spouse = Game1.getCharacterFromName(kvp.Key);
            if (spouse != null && spouse.currentLocation == loc && Vector2.Distance(spouse.Tile, tile) <= 2.5f)
                return true;
        }

        return false;
    }

    public static Point FindNearestWalkableTileNear(GameLocation loc, Vector2 center, Point fromTile, int maxRadius = 4)
    {
        Point cPt = new Point((int)center.X, (int)center.Y);
        List<Point> candidates = new();
        for (int dx = -maxRadius; dx <= maxRadius; dx++)
        {
            for (int dy = -maxRadius; dy <= maxRadius; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                Point p = new Point(cPt.X + dx, cPt.Y + dy);
                if (IsTileWalkable(loc, p))
                {
                    candidates.Add(p);
                }
            }
        }

        if (candidates.Count == 0)
        {
            return GetAdjacentWalkableTile(loc, cPt, fromTile);
        }

        return candidates
            .OrderBy(p => (p.X - cPt.X) * (p.X - cPt.X) + (p.Y - cPt.Y) * (p.Y - cPt.Y))
            .ThenBy(p => (p.X - fromTile.X) * (p.X - fromTile.X) + (p.Y - fromTile.Y) * (p.Y - fromTile.Y))
            .First();
    }

    /// <summary>
    /// Safely finds any GameLocation by name or player's current location, including multiplayer Cabins and Farm buildings.
    /// </summary>
    public static GameLocation? FindLocationByNameOrPlayer(string? locName, Farmer? farmer)
    {
        if (farmer?.currentLocation != null && (string.IsNullOrEmpty(locName) || farmer.currentLocation.NameOrUniqueName == locName))
            return farmer.currentLocation;

        if (!string.IsNullOrEmpty(locName))
        {
            var loc = Game1.getLocationFromName(locName);
            if (loc != null) return loc;

            var farm = Game1.getFarm();
            if (farm != null)
            {
                foreach (var b in farm.buildings)
                {
                    if (b.indoors.Value != null && (b.indoors.Value.NameOrUniqueName == locName || b.indoors.Value.Name == locName))
                        return b.indoors.Value;
                }
            }

            foreach (var p in Game1.getAllFarmers())
            {
                if (p.currentLocation != null && (p.currentLocation.NameOrUniqueName == locName || p.currentLocation.Name == locName))
                    return p.currentLocation;
            }
        }

        if (farmer?.currentLocation != null)
            return farmer.currentLocation;

        return null;
    }

    /// <summary>
    /// Finds a safe walkable indoor spawn tile inside a FarmHouse, Cabin, or building interior.
    /// Strictly avoids front axis and delegates to FindSafeWarpSpawnTile with unified lateral geometry.
    /// </summary>
    public static Point FindSafeIndoorSpawnTile(GameLocation house, Point entry, Farmer? farmer)
    {
        int facing = farmer?.FacingDirection ?? 0;
        return FindSafeWarpSpawnTile(house, entry, facing);
    }

    /// <summary>
    /// Finds a safe spawn tile when warping between maps, avoiding map edge warps and pushing inland.
    /// </summary>
    public static Point GetCrossMapSpawnTile(GameLocation loc, Point playerTile, int playerFacingDir) =>
        FindSafeWarpSpawnTile(loc, playerTile, playerFacingDir);

    public static Point FindSafeWarpSpawnTile(GameLocation loc, Point arrivalTile, int playerFacingDir)
    {
        if (loc == null) return arrivalTile;

        var backLayer = loc.map?.GetLayer("Back") ?? (loc.map?.Layers != null && loc.map.Layers.Count > 0 ? loc.map.Layers[0] : null);
        int width = backLayer?.LayerWidth ?? 100;
        int height = backLayer?.LayerHeight ?? 100;

        bool isEdgeWarp = arrivalTile.X <= 2 || arrivalTile.X >= width - 3 || arrivalTile.Y <= 2 || arrivalTile.Y >= height - 3;

        Point facingVec;
        if (isEdgeWarp)
        {
            if (arrivalTile.X <= 2) facingVec = new Point(1, 0);
            else if (arrivalTile.X >= width - 3) facingVec = new Point(-1, 0);
            else if (arrivalTile.Y <= 2) facingVec = new Point(0, 1);
            else facingVec = new Point(0, -1);
        }
        else
        {
            facingVec = playerFacingDir switch
            {
                0 => new Point(0, -1),
                1 => new Point(1, 0),
                2 => new Point(0, 1),
                3 => new Point(-1, 0),
                _ => new Point(0, 1)
            };
        }

        Point perp = facingVec.X != 0 ? new Point(0, 1) : new Point(1, 0);

        List<Point> candidates = new List<Point>();

        if (isEdgeWarp)
        {
            // Borde de mapa (Town <-> BusStop, Town <-> Beach, Farm <-> Backwoods, etc.)
            // Prioridad 1: Lados con avance suave hacia el interior (arrivalTile + facingVec ± perp, arrivalTile ± perp)
            candidates.Add(new Point(arrivalTile.X + facingVec.X + perp.X, arrivalTile.Y + facingVec.Y + perp.Y));
            candidates.Add(new Point(arrivalTile.X + facingVec.X - perp.X, arrivalTile.Y + facingVec.Y - perp.Y));
            candidates.Add(new Point(arrivalTile.X + perp.X, arrivalTile.Y + perp.Y));
            candidates.Add(new Point(arrivalTile.X - perp.X, arrivalTile.Y - perp.Y));

            // Prioridad 2: Desplazamientos laterales con mayor margen hacia el interior
            candidates.Add(new Point(arrivalTile.X + facingVec.X * 2 + perp.X, arrivalTile.Y + facingVec.Y * 2 + perp.Y));
            candidates.Add(new Point(arrivalTile.X + facingVec.X * 2 - perp.X, arrivalTile.Y + facingVec.Y * 2 - perp.Y));
            candidates.Add(new Point(arrivalTile.X + facingVec.X + perp.X * 2, arrivalTile.Y + facingVec.Y + perp.Y * 2));
            candidates.Add(new Point(arrivalTile.X + facingVec.X - perp.X * 2, arrivalTile.Y + facingVec.Y - perp.Y * 2));
            candidates.Add(new Point(arrivalTile.X + facingVec.X * 2 + perp.X * 2, arrivalTile.Y + facingVec.Y * 2 + perp.Y * 2));
            candidates.Add(new Point(arrivalTile.X + facingVec.X * 2 - perp.X * 2, arrivalTile.Y + facingVec.Y * 2 - perp.Y * 2));
            candidates.Add(new Point(arrivalTile.X + facingVec.X * 3 + perp.X, arrivalTile.Y + facingVec.Y * 3 + perp.Y));
            candidates.Add(new Point(arrivalTile.X + facingVec.X * 3 - perp.X, arrivalTile.Y + facingVec.Y * 3 - perp.Y));

            // Prioridad 3: Detrás del avance si es seguro contra warps
            candidates.Add(new Point(arrivalTile.X - facingVec.X, arrivalTile.Y - facingVec.Y));
            candidates.Add(new Point(arrivalTile.X - facingVec.X * 2, arrivalTile.Y - facingVec.Y * 2));
        }
        else
        {
            // Interiores de edificios / salidas de puertas:
            // Prioridad 1: Lados exactos (arrivalTile ± perp)
            candidates.Add(new Point(arrivalTile.X + perp.X, arrivalTile.Y + perp.Y));
            candidates.Add(new Point(arrivalTile.X - perp.X, arrivalTile.Y - perp.Y));

            // Prioridad 2: Diagonales laterales y desplazamientos (arrivalTile ± perp - facingVec, ± perp * 2, ± perp + facingVec)
            candidates.Add(new Point(arrivalTile.X + perp.X - facingVec.X, arrivalTile.Y + perp.Y - facingVec.Y));
            candidates.Add(new Point(arrivalTile.X - perp.X - facingVec.X, arrivalTile.Y - perp.Y - facingVec.Y));
            candidates.Add(new Point(arrivalTile.X + perp.X * 2, arrivalTile.Y + perp.Y * 2));
            candidates.Add(new Point(arrivalTile.X - perp.X * 2, arrivalTile.Y - perp.Y * 2));
            candidates.Add(new Point(arrivalTile.X + perp.X + facingVec.X, arrivalTile.Y + perp.Y + facingVec.Y));
            candidates.Add(new Point(arrivalTile.X - perp.X + facingVec.X, arrivalTile.Y - perp.Y + facingVec.Y));

            // Prioridad 3: Detrás del jugador (arrivalTile - facingVec, arrivalTile - facingVec * 2)
            candidates.Add(new Point(arrivalTile.X - facingVec.X, arrivalTile.Y - facingVec.Y));
            candidates.Add(new Point(arrivalTile.X - facingVec.X * 2, arrivalTile.Y - facingVec.Y * 2));
        }

        foreach (var cand in candidates)
        {
            if (IsValidWarpCandidate(loc, cand, arrivalTile, isEdgeWarp, width, height, facingVec, allowFront: false))
            {
                return cand;
            }
        }

        // Búsqueda en espiral ligera lateral/trasera si ninguno de los candidatos preferidos fue válido
        Point searchCenter = isEdgeWarp
            ? new Point(arrivalTile.X + facingVec.X * 2, arrivalTile.Y + facingVec.Y * 2)
            : arrivalTile;

        for (int r = 1; r <= 5; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    Point cand = new Point(searchCenter.X + dx, searchCenter.Y + dy);
                    if (IsValidWarpCandidate(loc, cand, arrivalTile, isEdgeWarp, width, height, facingVec, allowFront: false))
                    {
                        return cand;
                    }
                }
            }
        }

        // Fallback extremo (ej. puertas de 1 sola casilla de ancho sin espacio lateral):
        for (int r = 1; r <= 3; r++)
        {
            Point frontCand = new Point(arrivalTile.X + facingVec.X * r, arrivalTile.Y + facingVec.Y * r);
            if (IsValidWarpCandidate(loc, frontCand, arrivalTile, isEdgeWarp, width, height, facingVec, allowFront: true))
            {
                return frontCand;
            }
        }

        // Fallback final
        return GetAdjacentWalkableTile(loc, arrivalTile, arrivalTile);
    }

    private static bool IsValidWarpCandidate(GameLocation loc, Point cand, Point arrivalTile, bool isEdgeWarp, int width, int height, Point facingVec, bool allowFront = false)
    {
        if (cand == arrivalTile) return false;

        // Prohibir spawn enfrente del jugador en cualquier mapa salvo fallback extremo
        if (!allowFront)
        {
            if (cand == new Point(arrivalTile.X + facingVec.X, arrivalTile.Y + facingVec.Y) ||
                cand == new Point(arrivalTile.X + facingVec.X * 2, arrivalTile.Y + facingVec.Y * 2))
            {
                return false;
            }
        }

        if (isEdgeWarp)
        {
            if (arrivalTile.Y <= 2 && cand.Y < arrivalTile.Y + 2)
                return false;

            // Exigir al menos 2 casillas hacia el interior respecto a bordes
            if (cand.X < 2 || cand.X > width - 3 || cand.Y < 2 || cand.Y > height - 3)
                return false;

            // Mantener al menos 2 casillas de distancia de cualquier warp en bordes
            if (!IsFarFromWarps(loc, cand, minDistance: 2))
                return false;

            // Al menos 2 casillas hacia el interior de la franja de warps de entrada
            if (!IsInwardFromEntranceWarps(loc, cand, facingVec, minInwardTiles: 2))
                return false;
        }
        else
        {
            if (cand.X < 1 || cand.X > width - 2 || cand.Y < 1 || cand.Y > height - 2)
                return false;

            if (!IsFarFromWarps(loc, cand, minDistance: 1))
                return false;
        }

        // Verificar transitabilidad y descartar casillas con objetos sólidos o TerrainFeatures bloqueantes
        Vector2 candVec = new Vector2(cand.X, cand.Y);
        if (loc.Objects != null && loc.Objects.TryGetValue(candVec, out var candObj) && candObj != null && !candObj.isPassable())
            return false;
        if (loc.terrainFeatures != null && loc.terrainFeatures.TryGetValue(candVec, out var candTf) && candTf != null && !candTf.isPassable(Game1.player))
            return false;

        if (!IsTileWalkable(loc, cand))
            return false;

        // Al menos 2 vecinos cardinales transitables (anti-bolsillos)
        if (CountWalkableCardinalNeighbors(loc, cand) < 2)
            return false;

        // BFS ligero (radio <= 6) para confirmar camino libre hasta arrivalTile
        if (!HasShortFreePathToArrival(loc, cand, arrivalTile, maxRadius: 6))
            return false;

        return true;
    }

    private static bool IsFarFromWarps(GameLocation loc, Point tile, int minDistance)
    {
        if (loc?.warps == null) return true;
        foreach (var warp in loc.warps)
        {
            if (Math.Abs(tile.X - warp.X) < minDistance && Math.Abs(tile.Y - warp.Y) < minDistance)
                return false;
        }
        return true;
    }

    private static bool IsInwardFromEntranceWarps(GameLocation loc, Point cand, Point inward, int minInwardTiles)
    {
        if (loc?.warps == null) return true;
        int width = loc.map?.GetLayer("Back")?.LayerWidth ?? 100;
        int height = loc.map?.GetLayer("Back")?.LayerHeight ?? 100;

        foreach (var warp in loc.warps)
        {
            if (inward.X > 0 && warp.X <= 3)
            {
                if (cand.X < warp.X + minInwardTiles) return false;
            }
            else if (inward.X < 0 && warp.X >= width - 4)
            {
                if (cand.X > warp.X - minInwardTiles) return false;
            }
            else if (inward.Y > 0 && warp.Y <= 3)
            {
                if (cand.Y < warp.Y + minInwardTiles) return false;
            }
            else if (inward.Y < 0 && warp.Y >= height - 4)
            {
                if (cand.Y > warp.Y - minInwardTiles) return false;
            }
        }
        return true;
    }

    private static int CountWalkableCardinalNeighbors(GameLocation loc, Point tile)
    {
        int count = 0;
        if (IsTileWalkable(loc, new Point(tile.X + 1, tile.Y))) count++;
        if (IsTileWalkable(loc, new Point(tile.X - 1, tile.Y))) count++;
        if (IsTileWalkable(loc, new Point(tile.X, tile.Y + 1))) count++;
        if (IsTileWalkable(loc, new Point(tile.X, tile.Y - 1))) count++;
        return count;
    }

    private static bool HasShortFreePathToArrival(GameLocation loc, Point cand, Point arrivalTile, int maxRadius)
    {
        if (cand == arrivalTile) return true;
        if (Math.Abs(cand.X - arrivalTile.X) <= 1 && Math.Abs(cand.Y - arrivalTile.Y) <= 1)
            return true;

        var queue = new Queue<Point>();
        var visited = new HashSet<Point>();

        queue.Enqueue(cand);
        visited.Add(cand);

        int maxSteps = 80;
        int steps = 0;

        Point[] dirs = new Point[]
        {
            new(1, 0),
            new(-1, 0),
            new(0, 1),
            new(0, -1)
        };

        while (queue.Count > 0 && steps++ < maxSteps)
        {
            var current = queue.Dequeue();
            if (current == arrivalTile || (Math.Abs(current.X - arrivalTile.X) <= 1 && Math.Abs(current.Y - arrivalTile.Y) <= 1))
                return true;

            foreach (var dir in dirs)
            {
                Point next = new Point(current.X + dir.X, current.Y + dir.Y);
                if (visited.Contains(next)) continue;

                if (Math.Abs(next.X - arrivalTile.X) > maxRadius || Math.Abs(next.Y - arrivalTile.Y) > maxRadius)
                    continue;

                visited.Add(next);

                if (next == arrivalTile || IsTileWalkable(loc, next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return false;
    }

    public static NPC? FindNpc(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // 1. Stardew 1.6 character lookup (mustBeVillager: false includes custom mod NPCs)
        var npc = Game1.getCharacterFromName(name, mustBeVillager: false);
        if (npc != null) return npc;

        // 2. Direct search in current location (fastest, covers active MineShaft or temporary dungeon floors)
        if (Game1.currentLocation != null)
        {
            var found = Game1.currentLocation.getCharacterFromName(name);
            if (found != null) return found;
        }

        // 3. Fallback: direct location search across all active maps (handles unregistered/custom mod characters)
        if (Game1.locations != null)
        {
            foreach (var loc in Game1.locations)
            {
                var found = loc.getCharacterFromName(name);
                if (found != null) return found;
            }
        }

        // 4. Fallback: search indoor farm buildings
        var farm = Game1.getFarm();
        if (farm?.buildings != null)
        {
            foreach (var b in farm.buildings)
            {
                if (b?.indoors.Value != null)
                {
                    var found = b.indoors.Value.getCharacterFromName(name);
                    if (found != null) return found;
                }
            }
        }

        return null;
    }

    internal static int DirectionToward(Vector2 from, Vector2 to)
    {
        float dx = to.X - from.X, dy = to.Y - from.Y;
        if (Math.Abs(dx) >= Math.Abs(dy))
            return dx >= 0 ? 1 : 3;
        return dy >= 0 ? 2 : 0;
    }

    internal static StardewValley.Item? TryCreateByName(string name, int qty)
    {
        try
        {
            foreach (var kv in Game1.objectData)
                if (kv.Value.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return ItemRegistry.Create($"(O){kv.Key}", qty);
        }
        catch { }
        return null;
    }

    private bool IsBlocked(string npcName)
    {
        _dailyCounts.TryGetValue(npcName, out int cnt);
        return cnt >= _maxPerDay;
    }

    private void CleanDone()
    {
        foreach (var key in _active.Keys.ToList())
        {
            var finishedJob = _active[key];
            if (finishedJob.Done)
            {
                _companions.TryGetValue(key, out var comp);
                long ownerId = comp != null && comp.FarmerId != 0 ? comp.FarmerId : finishedJob.OwnerPlayerId;
                string ownerName = comp != null && !string.IsNullOrWhiteSpace(comp.FarmerName)
                    ? comp.FarmerName
                    : (Game1.GetPlayer(ownerId, true)?.Name ?? finishedJob.FarmerName);

                if (comp != null && comp.FarmerId == 0 && ownerId != 0)
                {
                    comp.FarmerId = ownerId;
                    comp.FarmerName = ownerName;
                }

                if (GetEveningResolution(key) == EveningResolutionState.Camping)
                {
                    var resumedJob = new NpcJob { NpcName = key, Kind = JobKind.Campfire, FarmerId = ownerId, FarmerName = ownerName, CreatedTick = Game1.ticks };
                    if (_overnightGuests.TryGetValue(key, out var campInfo))
                    {
                        resumedJob.Data["campTileX"] = campInfo.CampfireTile.X;
                        resumedJob.Data["campTileY"] = campInfo.CampfireTile.Y;
                        resumedJob.Data["tentTileX"] = campInfo.TentTile.X;
                        resumedJob.Data["tentTileY"] = campInfo.TentTile.Y;
                    }
                    if (comp != null)
                    {
                        comp.NightState = NightLifecycleState.CampOutdoorIdle;
                        comp.IsFollowing = false;
                        comp.IsRestingInPlace = true;
                    }
                    _active[key] = resumedJob;
                    if (Game1.IsMasterGame && Game1.IsMultiplayer)
                    {
                        ValleyBuddies.ModEntry.BroadcastJob(key, resumedJob.Kind, ownerId, ownerName, "silent_resume");
                    }
                    continue;
                }
                if (finishedJob.Kind != JobKind.LeaveFarm && comp != null)
                {
                    // Companion is still active: transition smoothly back to Follow or VisitFarm!
                    var npc = FindNpc(key);
                    if (npc != null)
                    {
                        npc.controller = null;
                        npc.temporaryController = null;
                        npc.Halt();
                        npc.followSchedule = false;
                    }

                    NpcJob resumedJob;
                    if (comp.IsFollowing)
                    {
                        resumedJob = new NpcJob { NpcName = key, Kind = JobKind.Follow, FarmerId = ownerId, FarmerName = ownerName, CreatedTick = Game1.ticks };
                    }
                    else if (comp.IsRestingInPlace)
                    {
                        resumedJob = new NpcJob { NpcName = key, Kind = JobKind.SitOrRest, FarmerId = ownerId, FarmerName = ownerName, CreatedTick = Game1.ticks };
                    }
                    else
                    {
                        resumedJob = new NpcJob { NpcName = key, Kind = JobKind.VisitFarm, FarmerId = ownerId, FarmerName = ownerName, CreatedTick = Game1.ticks };
                    }

                    resumedJob.Data["follow_pause_until"] = ModEntry.CurrentTick + 30;
                    resumedJob.Data["last_warp_tick"] = ModEntry.CurrentTick;
                    _active[key] = resumedJob;

                    if (Game1.IsMasterGame && Game1.IsMultiplayer)
                    {
                        ValleyBuddies.ModEntry.BroadcastJob(key, resumedJob.Kind, ownerId, ownerName, "silent_resume");
                    }
                }
                else
                {
                    ReleaseNpc(key);
                    _active.Remove(key);
                }
            }
        }
    }

    public void ReleaseNpc(string name)
    {
        RemoveCompanionCaveLight(name);
        var npc = FindNpc(name);
        if (npc != null)
        {
            npc.controller = null;
            npc.Halt();
            npc.speed = 2; // Restore default vanilla NPC walking speed
            npc.addedSpeed = 0;
            npc.followSchedule = true;
            npc.swimming.Value = false;
            npc.drawOffset = Vector2.Zero;
            try
            {
                if (npc.shouldWearIslandAttire.Value)
                {
                    npc.shouldWearIslandAttire.Value = false;
                    IsRestoringNormalClothes = true;
                    try
                    {
                        npc.wearNormalClothes();
                        npc.ChooseAppearance();
                    }
                    finally
                    {
                        IsRestoringNormalClothes = false;
                    }
                }
                // Re-evaluate vanilla schedule for current time of day
                npc.checkSchedule(Game1.timeOfDay);
                npc.doEmote(32); // Friendly wave goodbye
            }
            catch { }
            _debug_log($"[Actions] Released {name} back to schedule.");
        }
    }

    public void CancelAll(string? npcName)
    {
        if (npcName != null)
        {
            if (_active.TryGetValue(npcName, out var job))
            {
                job.Done = true;
                ReleaseNpc(npcName);
                _active.Remove(npcName);
            }
        }
        else
        {
            foreach (var kv in _active)
            {
                kv.Value.Done = true;
                ReleaseNpc(kv.Key);
            }
            _active.Clear();
        }
    }

    public void LogStatus(IMonitor monitor)
    {
        monitor.Log($"Active jobs: {_active.Count}", LogLevel.Info);
        foreach (var kv in _active)
            monitor.Log($"  {kv.Key}: {kv.Value.Kind}", LogLevel.Info);
    }

    private void _debug_log(string msg)
    {
        if (_debug) _monitor?.Log(msg, LogLevel.Debug);
    }
}
