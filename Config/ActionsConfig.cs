using StardewModdingAPI.Utilities;

namespace ValleyBuddies.Config;

public sealed class ActionsConfig
{
    // ── Master toggle ──────────────────────────────────────────────
    public bool EnableMod { get; set; } = true;

    // ── Offline / Standalone Mode v4.0 ────────────────────────────
    public bool ForceOfflineMode { get; set; } = false;
    public bool EnablePostDialogueInvitePrompt { get; set; } = true;
    public KeybindList OfflineCompanionMenuKey { get; set; } = KeybindList.Parse("LeftShift + E");
    public bool AllowRecruitingChildren { get; set; } = false;
    public string ExcludedNpcNames { get; set; } = "";

    // ── UI ────────────────────────────────────────────────────────
    public bool ShowHudMessages { get; set; } = true;

    // ── Memory integration ────────────────────────────────────────
    public bool EnableMemoryFeed   { get; set; } = true;
    public int  MemoryDailyLimit   { get; set; } = 6;

    // ── Movement & Multi-map ──────────────────────────────────────
    /// <summary>Tiles the NPC tries to stay away from the player while following.</summary>
    public int  FollowDistanceTiles    { get; set; } = 2;
    /// <summary>How many in-game minutes to wait before the NPC gives up coming to you.</summary>
    public int  ComeHereTimeoutMinutes { get; set; } = 30;
    /// <summary>Allow NPCs to follow the player across location changes.</summary>
    public bool AllowCrossMapFollow    { get; set; } = true;
    /// <summary>Use warp pathfinding to walk across maps instead of direct warps.</summary>
    public bool WalkAcrossMaps         { get; set; } = true;

    // ── Spontaneous visits ────────────────────────────────────────
    /// <summary>Allow spontaneous visits from friends with 4+ hearts to visit the farm.</summary>
    public bool EnableSpontaneousVisits { get; set; } = true;

    // ── Farm work v2.0 ────────────────────────────────────────────
    public bool AllowFarmWork               { get; set; } = true;
    /// <summary>Max tiles away from the NPC's current tile to look for work (default 35 for farm coverage).</summary>
    public int  FarmWorkRadius              { get; set; } = 35;
    /// <summary>Scan entire farm map if no work targets exist within FarmWorkRadius.</summary>
    public bool AllowFullMapFarmScan        { get; set; } = true;
    /// <summary>Maximum crops an NPC will water in a single job before resting.</summary>
    public int  MaxCropsPerWaterJob         { get; set; } = 200;
    /// <summary>Protect flowers within 5 tiles of bee houses so honey flavor is preserved.</summary>
    public bool ProtectFlowersNearBeeHouses { get; set; } = true;
    /// <summary>Store harvested crops directly into the nearest farm chest.</summary>
    public bool StoreHarvestInChests        { get; set; } = true;

    // ── Economy & Gifts ───────────────────────────────────────────
    public int  MaxGiftQuantity   { get; set; } = 3;
    public int  MaxGiftItemValue  { get; set; } = 500;
    public bool AllowRareNpcGifts { get; set; } = false;

    // ── Controls & Interactivity ──────────────────────────────────
    /// <summary>Key to dismiss active followers and send them back to their normal schedule.</summary>
    public string DismissKey                    { get; set; } = "End";
    /// <summary>Bypass the daily AI messages limit when an NPC is following you or visiting.</summary>
    public bool   BypassDailyLimitWhenFollowing { get; set; } = true;
    /// <summary>Show quick action menu when interacting with an NPC that is currently following you.</summary>
    public bool   EnableFollowerActionMenu      { get; set; } = true;
    /// <summary>Capture real screenshot to disk during take_photo.</summary>
    public bool   SaveRealPhotoScreenshots      { get; set; } = false;

    // ── Systems & Sinergias v2.0 ──────────────────────────────────
    public bool EnableCompanionBuffs  { get; set; } = true;
    public bool EnableCompanionCombat { get; set; } = true;
    public bool EnableFatigueSystem   { get; set; } = true;
    public bool EnableEasterEggs      { get; set; } = true;

    // ── Vida Hogareña y Correo v3.0 ──────────────────────────────
    public bool EnableCozyIndoorEvening       { get; set; } = true;
    public bool EnableSleepoverCoffeeLetter   { get; set; } = true;
    public bool AllowHostOverrideCompanion    { get; set; } = false;

    // ── Hogar, Herramientas y Cariño v4.5 ─────────────────────────
    public bool EnableToolVisualOverlays      { get; set; } = true;
    public bool EnableSpouseLivingHome        { get; set; } = true;
    public bool EnableSpouseMorningKiss       { get; set; } = true;
    public bool EnableSpouseMorningBreakfast  { get; set; } = true;
    public bool EnableSpouseFarmChores        { get; set; } = true;
    public bool EnableSpouseAutoHarvest       { get; set; } = true;
    public bool EnableCompanionTenderMoments  { get; set; } = true;

    // ── Salidas y Momentos en el Pueblo v4.6.0 ──────────────────────
    public bool EnableTownHangouts            { get; set; } = true;
    public bool EnableCompanionFishing        { get; set; } = true;
    public bool EnableDailyBondingBonus       { get; set; } = true;
    public bool EnableRareFindReactions       { get; set; } = true;
    public bool EnableOfflineGroupBanter      { get; set; } = true;

    // ── Inmersión y Vida Orgánica v5.1.0+ ─────────────────────────
    public bool   EnableLivingMicroEvents      { get; set; } = true;
    public int    MicroEventFrequencyPercent   { get; set; } = 100;
    public int    BubbleMaxWidthPx             { get; set; } = 200;
    public float  BubbleTextScale              { get; set; } = 0.85f;
    public float  BubbleDurationMultiplier     { get; set; } = 1.2f;
    public bool   EnableNpcOutingInvitations   { get; set; } = true;
    public int    NpcOutingInviteChancePercent { get; set; } = 20;
    public int    NpcOutingCooldownDays        { get; set; } = 4;
    public bool   EnableCinematicOutings       { get; set; } = true;
    public string LivingPacingPreset           { get; set; } = "Balanced";
    public bool   EnableSpouseSurpriseDates    { get; set; } = true;
    public bool   EnableFavoriteSpotPrompts    { get; set; } = true;

    // ── Pueblo Vivo & Eco al Chat v5.4.0+ ─────────────────────────
    public bool   EnableAmbientTownConversations   { get; set; } = true;
    public int    AmbientTownBanterCooldownSeconds { get; set; } = 25;
    public bool   EchoNpcConversationsToChat         { get; set; } = true;
    public bool   EnableCharacterAmbientChatter      { get; set; } = true;
    public bool   EnablePassByGreetings            { get; set; } = true;
    public bool   EnableLivingTownVignettes          { get; set; } = true;
    public int    LivingTownVignetteCooldownSeconds  { get; set; } = 45;

    // ── Ambient Cutscenes (v5.7.0) ────────────────────────────────────────────
    public bool EnableAmbientCutscenes         { get; set; } = true;
    public int  AmbientCutsceneChancePercent   { get; set; } = 25;
    public int  MaxAmbientCutscenesPerDay      { get; set; } = 2;

    // ── Hearts requirements (defaults to 0 so the AI's free will governs acceptance) ─
    public int MinHeartsFollow      { get; set; } = 0;
    public int MinHeartsVisitFarm   { get; set; } = 0;
    public int MinHeartsHug         { get; set; } = 0;
    public int MinHeartsFarmWork    { get; set; } = 0;
    public int MinHeartsChestAccess { get; set; } = 0;
    public int MinHeartsEscort      { get; set; } = 0;
    public int MinHeartsGiveItem    { get; set; } = 0;
    public int MinHeartsEmote       { get; set; } = 0;
    public int MinHeartsMeetAtTime  { get; set; } = 0;

    // ── Safety & Limits ───────────────────────────────────────────
    /// <summary>Never execute NPC actions during festivals or scripted events.</summary>
    public bool BlockDuringEvents      { get; set; } = true;
    /// <summary>How many physical actions one NPC can do per in-game day.</summary>
    public int  MaxActionsPerNpcPerDay { get; set; } = 50;

    // ── Debug ─────────────────────────────────────────────────────
    public bool   DebugLogging    { get; set; } = false;
    public string DebugOverlayKey { get; set; } = "F9";

    public void ResetToDefaults()
    {
        var defaults = new ActionsConfig();
        foreach (var prop in typeof(ActionsConfig).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.CanRead && prop.CanWrite)
                prop.SetValue(this, prop.GetValue(defaults));
        }
    }
}
