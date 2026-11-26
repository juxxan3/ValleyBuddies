using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using ValleyBuddies.Systems;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Execution;

public enum WalkResult
{
    Walking,
    Arrived,
    Unreachable
}

public enum IndoorBlockStage
{
    WalkingToSpot,
    PerformingBlock
}

public enum SpouseHomePhase
{
    Idle,
    MorningKiss,
    CookingBreakfast,
    WalkingToDoorOut,
    FarmChores,
    ReturningHome,
    IndoorLiving,
    GoingToBed
}

public sealed class SpouseHomeState
{
    public string SpouseName { get; init; } = string.Empty;
    public long OwnerFarmerId { get; init; }
    public SpouseHomePhase Phase { get; set; } = SpouseHomePhase.Idle;
    public HashSet<string> VisitedAnimalHousesToday { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool DidMorningKissToday { get; set; } = false;
    public bool HasDoneMorningKissToday
    {
        get => DidMorningKissToday;
        set => DidMorningKissToday = value;
    }
    public int KissPhase { get; set; } = 0; // 0: Kissing, 1: Walking to kitchen, 2: Waiting in kitchen until 07:00

    public bool DidMorningBreakfastToday { get; set; } = false;
    public bool HasPreparedBreakfastToday
    {
        get => DidMorningBreakfastToday;
        set => DidMorningBreakfastToday = value;
    }
    public bool DidFarewellKissToday { get; set; } = false;
    public bool DidMorningChoresToday { get; set; } = false;
    public bool DidEveningWelcomeToday { get; set; } = false;
    public Item? PreparedBreakfastItem { get; set; } = null;
    public bool BreakfastWasNewlyLearned { get; set; } = false;
    public bool IsBreakfastBurned { get; set; } = false;
    public bool BurnedBreakfastPromptShown { get; set; } = false;
    public ulong BurnedBreakfastWalkStartTick { get; set; } = 0;
    public int BreakfastPhase { get; set; } = 0; // 0: WalkToKitchen/Cook, 1: DeliveringToPlayer, 2: FarewellKiss, 3: WalkToTv/Relax, 4: Done/WaitingUntil0800
    public int ChoresPhase { get; set; } = 0; // 0: WalkingToDoorOut, 1: WateringFarm, 2: PettingOutdoor, 3: PettingIndoor, 4: GreenhouseChores, 5: HarvestingFarm, 6: DepositingOrDelivering, 7: ReturnHome, 8: Done

    // Door exit tracking (Corridor approach -> threshold -> proximity or 5s stuck)
    public int DoorExitSubStep { get; set; } = 0;
    public Vector2 LastTrackedPosition { get; set; } = Vector2.Zero;
    public ulong LastPositionMovedTick { get; set; } = 0;
    public int StuckCounter { get; set; } = 0;

    // Animal buildings & Greenhouse tracking
    public bool InsideAnimalBuilding { get; set; } = false;
    public string? CurrentBuildingId { get; set; } = null;
    public string? ActiveSubBuildingDoorMap { get; set; } = null;
    public Point ActiveSubBuildingFarmDoorTile { get; set; } = Point.Zero;
    public Point ActiveSubBuildingInsideExitTile { get; set; } = Point.Zero;
    public bool ShouldReturnHomeAfterExitingBuilding { get; set; } = false;
    public bool GreenhouseVisitedToday { get; set; } = false;
    public bool InsideGreenhouse { get; set; } = false;
    public int GreenhouseWateredCount { get; set; } = 0;
    public int GreenhouseHarvestedCount { get; set; } = 0;

    // Waypoint & physical movement tracking (v4.5.9)
    public Point? CurrentWaypoint { get; set; } = null;
    public Point? CurrentNavTarget { get; set; } = null;
    public Vector2 LastPixelPos { get; set; } = Vector2.Zero;
    public ulong LastPixelCheckTick { get; set; } = 0;
    public int RealStuckTicks { get; set; } = 0;
    public int StuckRetryCount { get; set; } = 0;

    // Indoor living 30-min block tracking (v4.5.9)
    public int IndoorBlockIndex { get; set; } = 0; // 0: Dishes, 1: Tidy, 2: SpouseRoom, 3: Relax/TV
    public IndoorBlockStage IndoorStage { get; set; } = IndoorBlockStage.WalkingToSpot;
    public int IndoorBlockEndTime { get; set; } = 0; // Game timeOfDay when current 30-min block finishes
    public ulong IndoorBlockMinEndTick { get; set; } = 0;
    public bool IndoorBlockBubbleShown { get; set; } = false;
    public ulong NextAmbientEffectTick { get; set; } = 0;

    public HashSet<Point> SkippedChoreTilesToday { get; } = new();
    public HashSet<long> SkippedAnimalIdsToday { get; } = new();
    public HashSet<string> PettedPetNamesToday { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool FilledPetBowlToday { get; set; } = false;
    public bool LivestockCompletedToday { get; set; } = false;
    public bool HasStartedAgriculturalChoresToday { get; set; } = false;
    public bool AnnouncedBedtimeToday { get; set; } = false;

    public List<Item> HarvestBasket { get; } = new();
    public List<Item> PendingHarvestItems => HarvestBasket;
    public bool HasPendingHarvestForOwner { get; set; } = false;
    public bool IsDeliveringPendingHarvest { get; set; } = false;
    public int WateredCount { get; set; } = 0;
    public int PettedCount { get; set; } = 0;
    public int HarvestedCount { get; set; } = 0;
    public ulong NextActionTick { get; set; } = 0;
    public ulong StepStartedTick { get; set; } = 0;
    public Point CurrentTargetTile { get; set; } = Point.Zero;
    public string? LastBreakfastItemId { get; set; } = null;

    public Point? BedSlideOutTargetTile { get; set; } = null;
    public Vector2? BedSlideOutTargetPixel { get; set; } = null;
    public int BedSlideOutTicksRemaining { get; set; } = 0;
    public bool WokeUpByPlayerTonight { get; set; } = false;
    public bool HasGoneOutToday { get; set; } = false;
    public bool HasCompletedChoresToday { get; set; } = false;
    public bool CheckedTownOutingToday { get; set; } = false;
    public bool HasTownOutingToday { get; set; } = false;
    public int TownOutingStartTime { get; set; } = 0;
    public bool AnnouncedWorkDayToday { get; set; } = false;
    public bool YieldedToTownScheduleToday { get; set; } = false;
    public bool WateringDispatchLogged { get; set; } = false;
    public ulong PhaseStartedTick { get; set; } = 0;
    public bool HasWelcomedHomeToday
    {
        get => DidEveningWelcomeToday;
        set => DidEveningWelcomeToday = value;
    }
}

public sealed partial class NpcActionExecutor
{
    private readonly Dictionary<string, SpouseHomeState> _spouseHomeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NpcJob> _spouseNavJobs = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<Point, string> _claimedWaterTiles = new();
    private static readonly Dictionary<Point, string> _claimedHarvestTiles = new();
    private static readonly Dictionary<long, string> _claimedAnimalIds = new();
    private static readonly Dictionary<string, string> _claimedAnimalHouses = new(StringComparer.OrdinalIgnoreCase);

    public static void ReleaseSpouseClaims(string spouseName)
    {
        if (string.IsNullOrWhiteSpace(spouseName)) return;
        var wKeys = _claimedWaterTiles.Where(k => string.Equals(k.Value, spouseName, StringComparison.OrdinalIgnoreCase)).Select(k => k.Key).ToList();
        foreach (var k in wKeys) _claimedWaterTiles.Remove(k);
        var hKeys = _claimedHarvestTiles.Where(k => string.Equals(k.Value, spouseName, StringComparison.OrdinalIgnoreCase)).Select(k => k.Key).ToList();
        foreach (var k in hKeys) _claimedHarvestTiles.Remove(k);
        var aKeys = _claimedAnimalIds.Where(k => string.Equals(k.Value, spouseName, StringComparison.OrdinalIgnoreCase)).Select(k => k.Key).ToList();
        foreach (var k in aKeys) _claimedAnimalIds.Remove(k);
        var bKeys = _claimedAnimalHouses.Where(k => string.Equals(k.Value, spouseName, StringComparison.OrdinalIgnoreCase)).Select(k => k.Key).ToList();
        foreach (var k in bKeys) _claimedAnimalHouses.Remove(k);
    }

    public void ResetSpouseHomeStatesDaily()
    {
        _claimedWaterTiles.Clear();
        _claimedHarvestTiles.Clear();
        _claimedAnimalIds.Clear();
        _claimedAnimalHouses.Clear();
        foreach (var kvp in _spouseHomeStates)
        {
            var s = kvp.Value;
            s.Phase = SpouseHomePhase.Idle;
            s.VisitedAnimalHousesToday.Clear();
            s.SkippedChoreTilesToday.Clear();
            s.SkippedAnimalIdsToday.Clear();
            s.PettedPetNamesToday.Clear();
            s.FilledPetBowlToday = false;
            s.LivestockCompletedToday = false;
            s.HasStartedAgriculturalChoresToday = false;
            s.AnnouncedBedtimeToday = false;
            s.DidMorningKissToday = false;
            s.KissPhase = 0;
            s.DidMorningBreakfastToday = false;
            s.DidFarewellKissToday = false;
            s.DidMorningChoresToday = false;
            s.WateringDispatchLogged = false;
            s.DidEveningWelcomeToday = false;
            s.HasGoneOutToday = false;
            s.HasCompletedChoresToday = false;
            s.CheckedTownOutingToday = false;
            s.HasTownOutingToday = false;
            s.TownOutingStartTime = 0;
            s.AnnouncedWorkDayToday = false;
            s.YieldedToTownScheduleToday = false;
            s.PhaseStartedTick = 0;
            s.PreparedBreakfastItem = null;
            s.BreakfastWasNewlyLearned = false;
            s.IsBreakfastBurned = false;
            s.BurnedBreakfastPromptShown = false;
            s.BurnedBreakfastWalkStartTick = 0;
            s.BreakfastPhase = 0;
            s.ChoresPhase = 0;
            s.DoorExitSubStep = 0;
            s.LastTrackedPosition = Vector2.Zero;
            s.LastPositionMovedTick = 0;
            s.StuckCounter = 0;
            s.InsideAnimalBuilding = false;
            s.CurrentBuildingId = null;
            s.ActiveSubBuildingDoorMap = null;
            s.ActiveSubBuildingFarmDoorTile = Point.Zero;
            s.ActiveSubBuildingInsideExitTile = Point.Zero;
            s.ShouldReturnHomeAfterExitingBuilding = false;
            s.GreenhouseVisitedToday = false;
            s.InsideGreenhouse = false;
            s.GreenhouseWateredCount = 0;
            s.GreenhouseHarvestedCount = 0;
            s.HarvestBasket.Clear();
            s.HasPendingHarvestForOwner = false;
            s.IsDeliveringPendingHarvest = false;
            s.WateredCount = 0;
            s.PettedCount = 0;
            s.HarvestedCount = 0;
            s.CurrentTargetTile = Point.Zero;
            s.CurrentWaypoint = null;
            s.CurrentNavTarget = null;
            s.LastPixelPos = Vector2.Zero;
            s.LastPixelCheckTick = 0;
            s.RealStuckTicks = 0;
            s.StuckRetryCount = 0;
            s.IndoorBlockIndex = 0;
            s.IndoorStage = IndoorBlockStage.WalkingToSpot;
            s.IndoorBlockEndTime = 0;
            s.IndoorBlockMinEndTick = 0;
            s.IndoorBlockBubbleShown = false;
            s.NextAmbientEffectTick = 0;
            s.BedSlideOutTargetTile = null;
            s.BedSlideOutTargetPixel = null;
            s.BedSlideOutTicksRemaining = 0;
            s.WokeUpByPlayerTonight = false;
        }
        _spouseNavJobs.Clear();
    }

    public bool TryGetSpouseHomeState(string npcName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SpouseHomeState? state)
    {
        return _spouseHomeStates.TryGetValue(npcName, out state);
    }

    private NpcJob GetSpouseNavJob(NPC spouse)
    {
        if (!_spouseNavJobs.TryGetValue(spouse.Name, out var job))
        {
            job = new NpcJob { NpcName = spouse.Name, Kind = JobKind.VisitFarm };
            _spouseNavJobs[spouse.Name] = job;
        }
        return job;
    }

    private SpouseHomeState GetOrCreateSpouseHomeState(NPC spouse, Farmer owner)
    {
        if (!_spouseHomeStates.TryGetValue(spouse.Name, out var state))
        {
            state = new SpouseHomeState
            {
                SpouseName = spouse.Name,
                OwnerFarmerId = owner.UniqueMultiplayerID
            };
            _spouseHomeStates[spouse.Name] = state;
        }
        return state;
    }

    /// <summary>
    /// Deposita cualquier cultivo o ítem retenido en PendingHarvestItems en la nevera de FarmHouse antes de guardar el save.
    /// </summary>
    public void DepositPendingHarvestsBeforeSave()
    {
        var mainFarmHouse = Game1.getLocationFromName("FarmHouse") as FarmHouse;
        var mainFridge = mainFarmHouse?.GetFridge();

        foreach (var kvp in _spouseHomeStates)
        {
            var state = kvp.Value;
            if (state.HarvestBasket.Count > 0)
            {
                var owner = Game1.GetPlayer(state.OwnerFarmerId, true) ?? Game1.MasterPlayer;
                var house = GetOwnerHouse(owner) ?? mainFarmHouse;
                var fridge = house?.GetFridge() ?? mainFridge;

                if (fridge != null)
                {
                    foreach (var item in state.HarvestBasket.ToList())
                    {
                        if (fridge.addItem(item) == null)
                        {
                            state.HarvestBasket.Remove(item);
                        }
                    }
                    ModEntry.ModMonitor?.Log($"[SpouseHome] Deposited leftover harvest for {kvp.Key} into fridge before save.", LogLevel.Debug);
                }
            }
            state.HasPendingHarvestForOwner = false;
            state.IsDeliveringPendingHarvest = false;
        }
    }

    private void DeliverHarvestBasketToPlayer(Farmer owner, NPC spouse, SpouseHomeState state)
    {
        bool hadOverflow = false;
        var house = GetOwnerHouse(owner) ?? Game1.getLocationFromName("FarmHouse") as FarmHouse;
        var fridge = house?.GetFridge() ?? (Game1.getLocationFromName("FarmHouse") as FarmHouse)?.GetFridge();

        foreach (var item in state.HarvestBasket.ToList())
        {
            if (owner.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID)
            {
                if (item.GetType() == typeof(StardewValley.Object) && item.Quality == 0 && _multiplayerSync != null)
                {
                    _multiplayerSync.SendSpouseGift(owner.UniqueMultiplayerID, item.QualifiedItemId, item.Stack, spouse.Name);
                    state.HarvestBasket.Remove(item);
                }
                else
                {
                    Item? remRemote = fridge != null ? fridge.addItem(item) : item;
                    if (remRemote == null)
                    {
                        state.HarvestBasket.Remove(item);
                        hadOverflow = true;
                    }
                    else
                    {
                        GiveItemToFarmerDirectly(owner, remRemote);
                        state.HarvestBasket.Remove(item);
                    }
                }
                continue;
            }

            if (owner.addItemToInventoryBool(item, false))
            {
                state.HarvestBasket.Remove(item);
            }
            else
            {
                Item? leftover = fridge != null ? fridge.addItem(item) : item;
                if (leftover == null)
                {
                    state.HarvestBasket.Remove(item);
                    hadOverflow = true;
                }
                else
                {
                    GiveItemToFarmerDirectly(owner, leftover);
                    state.HarvestBasket.Remove(item);
                }
            }
        }

        Game1.playSound("coin");

        if (hadOverflow)
        {
            string ofMsg = I18n.Get("spouse.chores.harvest_overflow_fridge", new { playerName = owner.displayName ?? owner.Name, npcName = spouse.displayName });
            ModEntry.ConversationBridge.ShowCompanionBubble(spouse, ofMsg, 3500);
        }

        if (state.HarvestBasket.Count == 0)
        {
            state.HasPendingHarvestForOwner = false;
            state.IsDeliveringPendingHarvest = false;
        }
    }

    public static void AddHarvestItem(List<Item> basket, Item newItem)
    {
        if (newItem == null) return;
        foreach (var existing in basket)
        {
            if (existing != null && existing.canStackWith(newItem))
            {
                int rem = existing.addToStack(newItem);
                if (rem <= 0)
                    return;
                newItem.Stack = rem;
            }
        }
        basket.Add(newItem);
    }
}
