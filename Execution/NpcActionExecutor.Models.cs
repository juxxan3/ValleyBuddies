using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.Systems;

namespace ValleyBuddies.Execution;

// ─────────────────────────── Job base ───────────────────────────────

public enum NightLifecycleState
{
    DaytimeActive,
    EveningTransition,
    NightSettled,
    DismissedForNight,
    IndoorRoaming,
    IndoorResting,
    GoingToBed,
    SleepingInBed,
    CampOutdoorIdle
}

public enum EveningResolutionState
{
    Undecided = 0,
    StayingInHouse = 1,
    StayInHouse = 1,
    Camping = 2,
    Leaving = 3,
    RoamHouse = 4,
    RestHere = 5,
    GoToBed = 6,
    OutdoorCamp = 7,
    LeaveForNight = 8
}

public enum JobKind
{
    ComeHere,
    Follow,
    GoToLocation,
    VisitFarm,
    HangOut,
    SitOrRest,
    GuardHere,
    EscortHome,
    WaterCrops,
    HarvestCrops,
    HarvestForage,
    ClearDebris,
    PetAnimals,
    MineNearbyRock,
    ChopWood,
    ChopTrees,
    TakeFromChest,
    FetchFromChest,
    TakeBreak,
    TakePhoto,
    Picnic,
    AdmireFarm,
    ShowEmote,
    FacePlayer,
    DoAnimation,
    HugOrCelebrate,
    GiveItem,
    MeetAtTime,
    LeaveFarm,
    StopFollowing,
    FeedAnimals,
    CollectAnimalProducts,
    WaterPetBowl,
    CollectMachines,
    PlantSeeds,
    RepairFences,
    CombatAssist,
    FishTogether,
    ScoutMines,
    CookMeal,
    PlayMusic,
    DanceTogether,
    Stargaze,
    SendLetterTomorrow,
    Campfire,
    CampSleepTogether,
    Sleepover,
    ExternalTask,
    Done,
}

public sealed class StoredItemRecord
{
    public string NpcName { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public string ItemDisplayName { get; init; } = string.Empty;
    public int Quantity { get; set; }
    public Point ChestTile { get; init; }
}

public sealed class SpontaneousVisitPlan
{
    public string NpcName { get; init; } = string.Empty;
    public int ScheduledTime { get; init; } = 830;
    public string GiftItemId { get; init; } = string.Empty;
    public int Hearts { get; init; } = 4;
}

public sealed class CompanionState
{
    public string NpcName { get; set; } = string.Empty;
    public long   FarmerId { get; set; }
    public string FarmerName { get; set; } = string.Empty;
    public bool   IsVisitingFarm { get; set; }
    public bool   IsFollowing { get; set; }
    public bool   IsRestingInPlace { get; set; }
    public NightLifecycleState NightState { get; set; } = NightLifecycleState.DaytimeActive;
    public EveningResolutionState EveningResolution { get; set; } = EveningResolutionState.Undecided;
    public Vector2? LockedBedPixelPosition { get; set; }
    public Point? AssignedBedSpot { get; set; }
    public bool   IsWearingBeachOutfit { get; set; } = false;
    public ulong  LastBiomeReactionTick { get; set; } = 0;
    public string? LastBiomeName { get; set; } = null;
    public bool   IsWaitingAtCaveEntrance { get; set; } = false;
    public int    ConsecutiveStuckTicks { get; set; } = 0;

    // ── Cuevas v4.3.0 / v4.3.1 (Tácticas, Autonomía, Aguante, Minado y Combate) ──
    public bool IsCaveAutonomyActive { get; set; } = false;
    public Point FloorEntryLadderTile { get; set; } = Point.Zero;
    public Point FloorSafeWaitTile { get; set; } = Point.Zero;
    public Point? CaveWanderTarget { get; set; } = null;
    public ulong LastCaveProgressCheckTick { get; set; } = 0;
    public Vector2 LastCaveProgressPos { get; set; } = Vector2.Zero;
    public ulong CaveStuckSinceTick { get; set; } = 0;
    public CaveTacticStance CaveStance { get; set; } = CaveTacticStance.Balanced;
    public int CurrentMorale { get; set; } = 0;
    public int MaxMorale { get; set; } = 0;
    public bool IsRecoveringBreath { get; set; } = false;
    public ulong RecoveringBreathUntilTick { get; set; } = 0;
    public ulong LastMoraleHitTick { get; set; } = 0;
    public ulong LastBreathEmoteTick { get; set; } = 0;
    public Vector2? CurrentCaveRockTarget { get; set; } = null;
    public Vector2? CurrentCaveCombatTarget { get; set; } = null;
    public ulong CurrentCaveRockTargetStartTick { get; set; } = 0;
    public ulong NextCaveRockSearchTick { get; set; } = 0;
    public ulong LastCaveSwingTick { get; set; } = 0;
    public ulong LastCaveAttackTick { get; set; } = 0;
    public ulong LastCaveAttackSoundTick { get; set; } = 0;
    public ulong NextCaveHpHealTick { get; set; } = 0;
    public ulong NextCaveStaminaHealTick { get; set; } = 0;
    public ulong LastHealthHealTick { get; set; } = 0;
    public ulong LastBombWarningTick { get; set; } = 0;
    public bool IsFleeingBomb { get; set; } = false;
    public Point BombFleeTargetTile { get; set; } = Point.Zero;
    public ulong LastAvoidanceEmoteTick { get; set; } = 0;
    public ulong NextCaveActionTick { get; set; } = 0;
    public ulong NextCaveMoveTick { get; set; } = 0;
    public ulong LastCaveHpBubbleTick { get; set; } = 0;

    // ── Pijamada e Interiores v4.3.2 (Permanencia en casa al salir el granjero) ──
    public bool IsSettledInsideHouseForNight { get; set; } = false;
    public string? SettledHouseName { get; set; } = null;

    // ── Anclaje de posición en casa v4.3.3 (FrozenHousePosition — evita pared norte) ──
    public Vector2? FrozenHousePosition { get; set; } = null;
    public int FrozenHouseFacing { get; set; } = 2;
    public bool IsPausedWhileOwnerOutsideHouse { get; set; } = false;

    // ── Emote anti-spam indoor (v4.0 FASE 3) ──
    public bool HasEmotedAtCurrentPoi { get; set; } = false;
    public ulong NextAllowedIndoorEmoteTick { get; set; } = 0;
    public ulong IndoorInitPauseUntilTick { get; set; } = 0;
    public Point? LastIndoorPoi { get; set; } = null;

    // ── Momentos Tiernos v4.5 (Cariño, Sorpresas, Atardecer, etc.) ──
    public int Hearts { get; set; } = 4;
    public bool DidAffectionToday { get; set; } = false;
    public bool DidSurpriseGiftToday { get; set; } = false;
    public bool DidTiredCareToday { get; set; } = false;
    public bool DidSunsetMomentToday { get; set; } = false;
    public bool   DidWeatherCozyToday { get; set; } = false;
    public bool   DidNightWarmDrinkTonight { get; set; } = false;
    public Vector2 LastSunsetStillPos { get; set; } = Vector2.Zero;
    public Vector2 LastScenicStillPos { get; set; } = Vector2.Zero;
    public int PlayerStillTicks { get; set; } = 0;

    // ── v4.5.1 Kiss and Bed Fix ──
    public ulong AffectionAnimationUntilTick { get; set; } = 0;
    public bool WokeUpByPlayerTonight { get; set; } = false;

    // ── v4.5.3 BedSlideOut ──
    public Point? BedSlideOutTargetTile { get; set; } = null;
    public Vector2? BedSlideOutTargetPixel { get; set; } = null;
    public int BedSlideOutTicksRemaining { get; set; } = 0;

    // ── Salidas y Momentos en el Pueblo v4.6.0 ──
    public bool IsFishingTogether { get; set; } = false;
    public int OwnerFishingTicks { get; set; } = 0;
    public bool IsWalkingToFishingSpot { get; set; } = false;
    public Point FishingStandTile { get; set; } = Point.Zero;
    public Point FishingWaterTile { get; set; } = Point.Zero;
    public int OwnerNotFishingGraceTicks { get; set; } = 0;
    public bool ShownFishingStartBubble { get; set; } = false;
    public ulong FishingCastTick { get; set; } = 0;
    public bool WasOwnerFishCaughtLastTick { get; set; } = false;
    public ulong LastCheerTick { get; set; } = 0;
    public int FishingGiftsGivenToday { get; set; } = 0;
    public ulong LastFishingGiftTick { get; set; } = 0;
    public HashSet<string> TriggeredHangoutLocationsToday { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string LastHangoutLocationName { get; set; } = string.Empty;
    public int HangoutTimerTicks { get; set; } = 0;
    public string FavoriteSpotLocationName { get; set; } = string.Empty;
    public int FavoriteSpotTicks { get; set; } = 0;
    public int OutdoorWeatherFlavorTicks { get; set; } = 0;
    public int ScenicRestTicks { get; set; } = 0;
    public int BondingTicksToday { get; set; } = 0;
    public int BondingTiersPaidToday { get; set; } = 0;
    public Dictionary<string, int> KnownRareItemCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ReactedRareCategoriesToday { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OvernightGuestInfo
{
    public string NpcName { get; init; } = string.Empty;
    public long FarmerId { get; init; }
    public bool DidCampfire { get; init; }
    public bool DidSleepover { get; init; }
    public Point CampfireTile { get; init; }
    public Point TentTile { get; init; }
}

public sealed class NpcJob
{
    public string    NpcName       { get; init; } = string.Empty;
    public JobKind   Kind          { get; set; }
    public long      OwnerPlayerId { get; set; } = Game1.player?.UniqueMultiplayerID ?? 0;
    public long      FarmerId      { get => OwnerPlayerId; set => OwnerPlayerId = value; }
    public string    FarmerName    { get; set; } = Game1.player?.Name ?? string.Empty;
    public Dictionary<string, object> Data { get; } = new();
    public bool      Done          { get; set; }
    public int       CreatedTick   { get; init; }

    private object? _typedState;

    public TState GetOrCreateState<TState>() where TState : class, new()
    {
        if (_typedState is TState state)
            return state;
        var created = new TState();
        _typedState = created;
        return created;
    }

    public bool TryGetState<TState>([NotNullWhen(true)] out TState? state) where TState : class
    {
        if (_typedState is TState typed)
        {
            state = typed;
            return true;
        }
        state = null;
        return false;
    }

    public void ClearTypedState()
    {
        _typedState = null;
    }
}

