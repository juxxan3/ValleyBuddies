namespace ValleyBuddies.Systems.LivingEvents;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Locations;
using ValleyBuddies.Execution;
using ValleyBuddies.Multiplayer;
using ValleyBuddies.Utilities;

public enum OutingInvitationPhase
{
    None,
    WaitingAtDoor,
    TravelingToDestination,
    AtDestination
}

public sealed class OutingHistoryData
{
    public int LastGlobalInviteTotalDay { get; set; } = -999;
    public Dictionary<string, int> LastInviteTotalDayByNpc { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class NpcOutingSession
{
    public string NpcName { get; set; } = string.Empty;
    public long TargetFarmerId { get; set; }
    public string TargetDestination { get; set; } = "Beach";
    public int ScheduledTime { get; set; } = 730;
    public OutingInvitationPhase Phase { get; set; } = OutingInvitationPhase.WaitingAtDoor;
    public bool PlacedAtDoor { get; set; }
    public bool QuestionShown { get; set; }
    public bool CinematicTriggered { get; set; }
    public bool WaitingForCinematicCompletion { get; set; }
    public ulong LastCinematicAttemptTick { get; set; }
    public int CinematicRetryCount { get; set; }
    public bool Stage1BubbleShown { get; set; }
    public bool Stage2BubbleShown { get; set; }
    public bool CompletedThanksShown { get; set; }
    public ulong ArrivedAtDestinationTick { get; set; }
    public ulong FadeClearedTick { get; set; }
    public bool IsSpouseDate { get; set; }
    public bool IsRomanticInvite { get; set; }
}

public sealed class PendingSpouseDate
{
    public string NpcName { get; set; } = string.Empty;
    public long OwnerFarmerId { get; set; }
    public string TargetDestination { get; set; } = "Beach";
    public bool IsRomantic { get; set; }
}

/// <summary>
/// Servicio de invitaciones inversas y Citas Sorpresa de Cónyuge coordinado con ValleyPacingDirector:
/// Soporta Co-op independiente por jugador con exclusividad de NPC, horarios reales de edificios,
/// rotación de destinos, limpieza automática a la 1:00 AM / desconexión y cinemáticas al llegar.
/// </summary>
public sealed class NpcOutingInvitationService
{
    public static NpcOutingInvitationService Instance { get; } = new();

    private const string LegacySaveDataKey = "ValleyBuddies.OutingHistory";
    private readonly Dictionary<long, NpcOutingSession> _invitationsByFarmer = new();
    private readonly Dictionary<long, PendingSpouseDate> _pendingSpouseDates = new();
    private readonly Dictionary<long, PendingSpouseDate> _activeSpouseDateOffersByFarmer = new();
    private readonly Dictionary<string, string> _lastDestinationByNpc = new(StringComparer.OrdinalIgnoreCase);
    private IModHelper? _helper;

    public void Initialize(IModHelper helper)
    {
        _helper = helper;
    }

    public void OnSaveLoaded()
    {
        _invitationsByFarmer.Clear();
        _pendingSpouseDates.Clear();
        _activeSpouseDateOffersByFarmer.Clear();
        _lastDestinationByNpc.Clear();

        if (_helper != null && Context.IsMainPlayer)
        {
            var legacy = _helper.Data.ReadSaveData<OutingHistoryData>(LegacySaveDataKey);
            if (legacy != null)
            {
                long hostId = Game1.MasterPlayer?.UniqueMultiplayerID ?? Game1.player?.UniqueMultiplayerID ?? 0;
                ValleyPacingDirector.Instance.ImportLegacyOutingHistory(hostId, legacy.LastGlobalInviteTotalDay, legacy.LastInviteTotalDayByNpc);
            }
        }
    }

    public bool HasActiveOutingForNpc(string? npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName) || _invitationsByFarmer.Count == 0) return false;
        return _invitationsByFarmer.Values.Any(s => string.Equals(s.NpcName, npcName, StringComparison.OrdinalIgnoreCase));
    }

    public bool HasPendingSpouseDateFor(long farmerId, string npcName)
    {
        return _pendingSpouseDates.TryGetValue(farmerId, out var pending)
            && string.Equals(pending.NpcName, npcName, StringComparison.OrdinalIgnoreCase);
    }

    public void ExpirePendingSpouseDateIfTimedOut(long farmerId)
    {
        _pendingSpouseDates.Remove(farmerId);
        _activeSpouseDateOffersByFarmer.Remove(farmerId);
    }

    public void ClearPendingSpouseDates()
    {
        _pendingSpouseDates.Clear();
        _activeSpouseDateOffersByFarmer.Clear();
    }

    public void ClearForPlayer(long farmerId)
    {
        if (_invitationsByFarmer.Remove(farmerId, out var session))
        {
            if (Context.IsWorldReady && !string.IsNullOrWhiteSpace(session.NpcName))
            {
                NpcActionExecutor.Instance.EndOutingCompanionSilently(session.NpcName);
            }
        }

        _pendingSpouseDates.Remove(farmerId);
        _activeSpouseDateOffersByFarmer.Remove(farmerId);
    }

    public void ClearAll(bool includeLastDestinationHistory = false)
    {
        if (Context.IsWorldReady)
        {
            foreach (var session in _invitationsByFarmer.Values.ToList())
            {
                if (!string.IsNullOrWhiteSpace(session.NpcName))
                {
                    NpcActionExecutor.Instance.EndOutingCompanionSilently(session.NpcName);
                }
            }
        }

        _invitationsByFarmer.Clear();
        _pendingSpouseDates.Clear();
        _activeSpouseDateOffersByFarmer.Clear();

        if (includeLastDestinationHistory)
        {
            _lastDestinationByNpc.Clear();
        }
    }

    public bool IsNpcWaitingAtDoor(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName) || _invitationsByFarmer.Count == 0) return false;
        return _invitationsByFarmer.Values.Any(s =>
            s.Phase == OutingInvitationPhase.WaitingAtDoor &&
            string.Equals(s.NpcName, npcName, StringComparison.OrdinalIgnoreCase));
    }

    public void MarkOutingCompletedFromCinematic(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName) || _invitationsByFarmer.Count == 0) return;

        var matchingIds = _invitationsByFarmer
            .Where(kv => string.Equals(kv.Value.NpcName, npcName, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();

        foreach (long farmerId in matchingIds)
        {
            _invitationsByFarmer.Remove(farmerId);
        }
    }

    public static string NormalizeDestinationName(string? destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return string.Empty;

        string trimmed = destination.Trim();
        if (trimmed.Equals("Library", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Museum", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("ArchaeologyHouse", StringComparison.OrdinalIgnoreCase))
        {
            return "ArchaeologyHouse";
        }
        if (trimmed.Equals("SecretWoods", StringComparison.OrdinalIgnoreCase))
        {
            return "Woods";
        }
        return trimmed;
    }

    private static bool IsLocationMatch(GameLocation? loc, string normalizedTarget)
    {
        if (loc == null || string.IsNullOrWhiteSpace(normalizedTarget)) return false;
        string targetNorm = NormalizeDestinationName(normalizedTarget);
        string curNorm = NormalizeDestinationName(loc.NameOrUniqueName);
        if (curNorm.Equals(targetNorm, StringComparison.OrdinalIgnoreCase))
            return true;

        string baseNorm = NormalizeDestinationName(loc.Name);
        if (!string.IsNullOrWhiteSpace(baseNorm) && baseNorm.Equals(targetNorm, StringComparison.OrdinalIgnoreCase))
            return true;

        if (targetNorm.Equals("ArchaeologyHouse", StringComparison.OrdinalIgnoreCase) && loc is LibraryMuseum)
            return true;

        return false;
    }

    public static bool IsDestinationAccessibleNow(string locationName, int currentTime, bool requireArrivalBuffer)
    {
        return IsDestinationAccessibleNow(locationName, currentTime, requireArrivalBuffer, out _);
    }

    public static bool IsDestinationAccessibleNow(string locationName, int currentTime, bool requireArrivalBuffer, out string blockReason)
    {
        blockReason = string.Empty;
        locationName = NormalizeDestinationName(locationName);
        if (string.IsNullOrWhiteSpace(locationName))
        {
            blockReason = "closed";
            return false;
        }

        if (Utility.isFestivalDay() || Game1.weddingToday || (Game1.CurrentEvent != null && Game1.CurrentEvent.isFestival))
        {
            blockReason = "closed";
            return false;
        }

        if (currentTime >= 2500)
        {
            blockReason = "late";
            return false;
        }

        GameLocation? loc = Game1.getLocationFromName(locationName);
        if (loc == null)
        {
            blockReason = "closed";
            return false;
        }

        var farm = Game1.getFarm();
        if (farm?.buildings != null)
        {
            foreach (var b in farm.buildings)
            {
                if (b != null && b.isUnderConstruction() && b.indoors?.Value != null &&
                    (b.indoors.Value == loc || string.Equals(b.indoors.Value.NameOrUniqueName, locationName, StringComparison.OrdinalIgnoreCase)))
                {
                    blockReason = "closed";
                    return false;
                }
            }
        }

        string norm = locationName.Trim();

        if (norm.Equals("ArchaeologyHouse", StringComparison.OrdinalIgnoreCase))
        {
            int closeCutoff = requireArrivalBuffer ? 1630 : 1750;
            if (currentTime < 800 || currentTime > closeCutoff)
            {
                blockReason = "closed";
                return false;
            }
            return true;
        }

        if (norm.Equals("Saloon", StringComparison.OrdinalIgnoreCase))
        {
            int closeCutoff = requireArrivalBuffer ? 2230 : 2350;
            if (currentTime < 1200 || currentTime > closeCutoff)
            {
                blockReason = "closed";
                return false;
            }
            return true;
        }

        if (norm.Equals("SeedShop", StringComparison.OrdinalIgnoreCase))
        {
            bool isWednesday = Game1.dayOfMonth % 7 == 3;
            bool ccComplete = Game1.MasterPlayer?.mailReceived?.Contains("ccIsComplete") == true
                           || Game1.MasterPlayer?.mailReceived?.Contains("JojaMember") == true;
            if (isWednesday && !ccComplete)
            {
                blockReason = "closed";
                return false;
            }

            int closeCutoff = requireArrivalBuffer ? 1530 : 1650;
            if (currentTime < 900 || currentTime > closeCutoff)
            {
                blockReason = "closed";
                return false;
            }
            return true;
        }

        if (norm.Contains("BathHouse", StringComparison.OrdinalIgnoreCase) ||
            norm.Equals("Railroad", StringComparison.OrdinalIgnoreCase))
        {
            bool earthquakeBlocked = Game1.year == 1 && (Game1.IsSpring || (Game1.IsSummer && Game1.dayOfMonth < 3));
            if (earthquakeBlocked)
            {
                blockReason = "closed";
                return false;
            }

            if (norm.Contains("BathHouse", StringComparison.OrdinalIgnoreCase))
            {
                int bathClose = requireArrivalBuffer ? 2100 : 2200;
                if (currentTime > bathClose)
                {
                    blockReason = "closed";
                    return false;
                }
            }
        }

        if (norm.Equals("Woods", StringComparison.OrdinalIgnoreCase))
        {
            bool anyCanAccessWoods = Game1.year > 1 || Game1.getAllFarmers().Any(f =>
                f != null && (f.locationsVisited?.Contains("Woods") == true
                || f.Items.OfType<StardewValley.Tools.Axe>().Any(axe => axe != null && axe.UpgradeLevel >= 2)));
            if (!anyCanAccessWoods)
            {
                blockReason = "closed";
                return false;
            }
        }

        if (norm.Equals("Mine", StringComparison.OrdinalIgnoreCase) || norm.Equals("Mines", StringComparison.OrdinalIgnoreCase))
        {
            if (Game1.year == 1 && Game1.IsSpring && Game1.dayOfMonth < 5)
            {
                blockReason = "closed";
                return false;
            }
        }

        if (norm.Equals("Sewer", StringComparison.OrdinalIgnoreCase))
        {
            if (Game1.MasterPlayer?.hasRustyKey != true)
            {
                blockReason = "closed";
                return false;
            }
        }

        if (norm.Equals("Desert", StringComparison.OrdinalIgnoreCase))
        {
            if (Game1.MasterPlayer?.mailReceived?.Contains("ccVault") != true)
            {
                blockReason = "closed";
                return false;
            }
        }

        if (norm.Contains("Island", StringComparison.OrdinalIgnoreCase) ||
            norm.Equals("Resort", StringComparison.OrdinalIgnoreCase))
        {
            if (Game1.MasterPlayer?.mailReceived?.Contains("willyBoatFixed") != true)
            {
                blockReason = "closed";
                return false;
            }
        }

        int exteriorCutoff = requireArrivalBuffer ? 2330 : 2450;
        if (currentTime > exteriorCutoff)
        {
            blockReason = "late";
            return false;
        }

        return true;
    }

    public void OnDayStarted()
    {
        ClearAll(includeLastDestinationHistory: false);

        if (!Context.IsMainPlayer) return;
        if (Utility.isFestivalDay() || Game1.weddingToday) return;

        int totalDays = Game1.Date.TotalDays;
        var claimedNpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // PRIORIDAD #1: Cita Sorpresa de Cónyuge (por jugador online)
        PlanSpouseDatesForDay(totalDays, claimedNpcs);

        // PRIORIDAD #2: Invitación Inversa de Amigo (excluida en lluvia o si el jugador ya tiene Plan Grande)
        if (ModEntry.Config?.EnableNpcOutingInvitations == false || Game1.isRaining) return;

        var availableFarmers = Game1.getOnlineFarmers()
            .Where(f => f != null && !ValleyPacingDirector.Instance.HasMajorPlanToday(f.UniqueMultiplayerID))
            .OrderBy(_ => Game1.random.Next())
            .ToList();
        if (availableFarmers.Count == 0) return;

        int baseChance = ModEntry.Config?.NpcOutingInviteChancePercent ?? 20;

        foreach (Farmer targetFarmer in availableFarmers)
        {
            long farmerId = targetFarmer.UniqueMultiplayerID;
            double inviteChance = ValleyPacingDirector.Instance.ComputeDoorInviteChance(farmerId, totalDays, baseChance);
            if (inviteChance <= 0.0 || Game1.random.NextDouble() >= inviteChance)
            {
                continue;
            }

            var candidates = GetEligibleNpcCandidates(targetFarmer)
                .Where(c => !claimedNpcs.Contains(c.Name))
                .ToList();
            if (candidates.Count == 0) continue;

            var freshCandidates = candidates
                .Where(c =>
                {
                    int prevDay = ValleyPacingDirector.Instance.GetLastInviteDayForNpc(farmerId, c.Name);
                    return prevDay < 0 || (totalDays - prevDay >= 14);
                })
                .ToList();

            NPC chosen = freshCandidates.Count > 0
                ? SelectWeightedCandidateByHearts(freshCandidates, targetFarmer)
                : candidates.OrderBy(c => ValleyPacingDirector.Instance.GetLastInviteDayForNpc(farmerId, c.Name)).First();

            claimedNpcs.Add(chosen.Name);
            ValleyPacingDirector.Instance.RecordDoorEventScheduled(farmerId, chosen.Name, totalDays);
            NpcActionExecutor.Instance.CancelPendingSpontaneousVisit();

            bool isDating = targetFarmer.friendshipData.TryGetValue(chosen.Name, out var fd) && fd != null && fd.IsDating();
            int scheduledTime = Game1.random.Next(73, 83) * 10; // 07:30 a 08:20 AM
            string dest = ResolveDestinationForNpc(chosen, preferIndoor: false, checkTime: Math.Max(scheduledTime, 800));
            if (dest.Equals("ArchaeologyHouse", StringComparison.OrdinalIgnoreCase) && scheduledTime < 800)
            {
                scheduledTime = 800;
            }
            if (dest.Equals("SeedShop", StringComparison.OrdinalIgnoreCase) && scheduledTime < 900)
            {
                scheduledTime = 900;
            }

            _invitationsByFarmer[farmerId] = new NpcOutingSession
            {
                NpcName = chosen.Name,
                TargetFarmerId = farmerId,
                TargetDestination = dest,
                ScheduledTime = scheduledTime,
                Phase = OutingInvitationPhase.WaitingAtDoor,
                IsSpouseDate = false,
                IsRomanticInvite = isDating
            };

            ModEntry.ModMonitor?.Log($"[NpcOuting] Planned outing with {chosen.Name} (dating={isDating}, chance={inviteChance * 100:F1}%) to {dest} for farmer {targetFarmer.Name} at {scheduledTime}.", LogLevel.Debug);
        }
    }

    private void PlanSpouseDatesForDay(int totalDays, HashSet<string> claimedNpcs)
    {
        if (ModEntry.Config?.EnableSpouseSurpriseDates == false) return;

        foreach (var farmer in Game1.getOnlineFarmers())
        {
            if (farmer == null || string.IsNullOrWhiteSpace(farmer.spouse)) continue;
            if (ValleyPacingDirector.Instance.HasMajorPlanToday(farmer.UniqueMultiplayerID)) continue;

            NPC? spouse = Game1.getCharacterFromName(farmer.spouse);
            if (spouse == null || spouse.IsInvisible || spouse.Age == NPC.child) continue;
            if (CavePersonalityRegistry.ResolveProfile(spouse).IsChildProtected) continue;
            if (claimedNpcs.Contains(spouse.Name)) continue;

            double chance = ValleyPacingDirector.Instance.ComputeSpouseDateChance(farmer.UniqueMultiplayerID, totalDays);
            if (chance <= 0.0 || Game1.random.NextDouble() >= chance) continue;

            bool isRoommate = farmer.friendshipData.TryGetValue(spouse.Name, out var fd) && fd != null && fd.IsRoommate();
            string dest = ResolveDestinationForNpc(spouse, preferIndoor: Game1.isRaining, checkTime: 800);

            claimedNpcs.Add(spouse.Name);
            _pendingSpouseDates[farmer.UniqueMultiplayerID] = new PendingSpouseDate
            {
                NpcName = spouse.Name,
                OwnerFarmerId = farmer.UniqueMultiplayerID,
                TargetDestination = dest,
                IsRomantic = !isRoommate
            };

            ValleyPacingDirector.Instance.RecordSpouseDateScheduled(farmer.UniqueMultiplayerID, totalDays);
            ModEntry.ModMonitor?.Log($"[SpouseDate] Planned morning surprise date with {spouse.Name} -> '{dest}' for {farmer.Name} (pityChance={chance * 100:F1}%, romantic={!isRoommate}).", LogLevel.Debug);
        }
    }

    private static NPC SelectWeightedCandidateByHearts(List<NPC> pool, Farmer owner)
    {
        int totalWeight = 0;
        var weights = new int[pool.Count];

        for (int i = 0; i < pool.Count; i++)
        {
            NPC npc = pool[i];
            int hearts = owner.getFriendshipHeartLevelForNPC(npc.Name);
            bool isDating = owner.friendshipData.TryGetValue(npc.Name, out var fd) && fd != null && fd.IsDating();

            int w = isDating ? 3 : (hearts >= 8 ? 2 : 1);
            weights[i] = w;
            totalWeight += w;
        }

        if (totalWeight <= 0) return pool[0];

        int roll = Game1.random.Next(totalWeight);
        int accum = 0;
        for (int i = 0; i < pool.Count; i++)
        {
            accum += weights[i];
            if (roll < accum) return pool[i];
        }

        return pool[0];
    }

    public void OnReturnedToTitle()
    {
        ClearAll(includeLastDestinationHistory: true);
    }

    public void Update(ulong gameTick)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer) return;
        if (_invitationsByFarmer.Count == 0 && _pendingSpouseDates.Count == 0 && _activeSpouseDateOffersByFarmer.Count == 0)
            return;

        // Limpieza automática a la 1:00 AM (>= 2500)
        if (Game1.timeOfDay >= 2500)
        {
            foreach (var (farmerId, session) in _invitationsByFarmer.ToList())
            {
                NPC? npc = Game1.getCharacterFromName(session.NpcName);
                Farmer? owner = Game1.GetPlayer(farmerId, onlyOnline: true);
                if (npc != null && owner != null && (session.PlacedAtDoor || session.Phase == OutingInvitationPhase.TravelingToDestination))
                {
                    string lateMsg = I18n.Get("invite.expired.late", new
                    {
                        playerName = owner.displayName ?? owner.Name,
                        npcName = npc.displayName,
                        itemName = GetDestDisplayName(session.TargetDestination)
                    });
                    ModEntry.ConversationBridge.ShowCompanionBubble(npc, lateMsg, 3500);
                    NotifyOwner(owner, lateMsg, 2);
                }

                if (!string.IsNullOrWhiteSpace(session.NpcName))
                {
                    NpcActionExecutor.Instance.EndOutingCompanionSilently(session.NpcName);
                }
            }

            _invitationsByFarmer.Clear();
            _pendingSpouseDates.Clear();
            _activeSpouseDateOffersByFarmer.Clear();
            return;
        }

        foreach (var (farmerId, session) in _invitationsByFarmer.ToList())
        {
            UpdateSession(farmerId, session, gameTick);
        }
    }

    private void UpdateSession(long farmerId, NpcOutingSession session, ulong gameTick)
    {
        NPC? npc = Game1.getCharacterFromName(session.NpcName);
        Farmer? owner = Game1.GetPlayer(farmerId, onlyOnline: true);
        if (npc == null || owner == null)
        {
            ClearForPlayer(farmerId);
            return;
        }

        // FASE 1: Esperando en la puerta exterior
        if (session.Phase == OutingInvitationPhase.WaitingAtDoor)
        {
            if (Game1.timeOfDay < session.ScheduledTime) return;

            var farm = Game1.getFarm();
            if (farm == null) return;

            if (!session.PlacedAtDoor)
            {
                if (!IsDestinationAccessibleNow(session.TargetDestination, Game1.timeOfDay, requireArrivalBuffer: true, out _))
                {
                    string closedMsg = I18n.Get("invite.expired.closed", new
                    {
                        playerName = owner.displayName ?? owner.Name,
                        npcName = npc.displayName,
                        itemName = GetDestDisplayName(session.TargetDestination)
                    });
                    NotifyOwner(owner, closedMsg, 2);
                    _invitationsByFarmer.Remove(farmerId);
                    return;
                }

                Point doorTile = GetExteriorDoorTile(farm, owner);
                Point standTile = new Point(doorTile.X, doorTile.Y + 2);
                if (!NpcActionExecutor.Instance.Navigator.IsTileWalkable(farm, standTile, npc))
                {
                    standTile = new Point(doorTile.X, doorTile.Y + 1);
                }

                Game1.warpCharacter(npc, farm, new Vector2(standTile.X, standTile.Y));
                npc.Halt();
                npc.controller = null;
                npc.followSchedule = false;
                npc.faceDirection(0);
                session.PlacedAtDoor = true;

                string hudMsg = I18n.Get("outing.hud.waiting_at_door", new { npcName = npc.displayName });
                NotifyOwner(owner, hudMsg, 2);
                return;
            }

            if (Game1.timeOfDay >= 1200)
            {
                string missed = I18n.Get("outing.missed_timeout", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
                ModEntry.ConversationBridge.ShowCompanionBubble(npc, missed, 3500);
                NpcActionExecutor.Instance.EndOutingCompanionSilently(npc.Name);
                _invitationsByFarmer.Remove(farmerId);
                return;
            }

            if (owner.currentLocation is Farm && Vector2.Distance(npc.Tile, owner.Tile) <= 4.5f && !session.QuestionShown)
            {
                if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID &&
                    (!Context.IsPlayerFree || Game1.activeClickableMenu != null || Game1.dialogueUp || Game1.eventUp || Game1.fadeToBlack || Game1.isWarping))
                {
                    return;
                }

                session.QuestionShown = true;
                npc.faceGeneralDirection(owner.Position);
                npc.doEmote(20);

                string destName = GetDestDisplayName(session.TargetDestination);

                LivingEventManager.Instance.TriggerDirectPrompt(
                    "outing_invite",
                    npc,
                    owner,
                    "outing.invite.prompt",
                    "accept", "outing.invite.opt_accept",
                    "decline", "outing.invite.opt_decline",
                    itemDisplayName: destName,
                    introKey: session.IsRomanticInvite ? "outing.invite.greeting_dating" : "outing.invite.greeting",
                    emotionTag: session.IsRomanticInvite ? "$l" : "$h"
                );
            }
            return;
        }

        // FASE 2: Viajando hacia el destino
        if (session.Phase == OutingInvitationPhase.TravelingToDestination)
        {
            string targetNorm = NormalizeDestinationName(session.TargetDestination);
            if (!string.Equals(session.TargetDestination, targetNorm, StringComparison.Ordinal))
                session.TargetDestination = targetNorm;

            if (IsLocationMatch(owner.currentLocation, targetNorm) &&
                IsLocationMatch(npc.currentLocation, targetNorm))
            {
                session.Phase = OutingInvitationPhase.AtDestination;
                session.ArrivedAtDestinationTick = gameTick;
                session.FadeClearedTick = 0;
                session.LastCinematicAttemptTick = 0;
                session.CinematicRetryCount = 0;
                ModEntry.ModMonitor?.Log($"[NpcOuting] Both {owner.Name} and {npc.Name} arrived at '{session.TargetDestination}'. Preparing destination scene...", LogLevel.Debug);
                return;
            }

            // Verificar cada segundo si el destino cerró, comenzó un festival o se hizo muy tarde
            if (gameTick % 60 == 0 && !IsDestinationAccessibleNow(session.TargetDestination, Game1.timeOfDay, requireArrivalBuffer: false, out string blockReason))
            {
                string key = blockReason == "late" ? "invite.expired.late" : "invite.expired.closed";
                string expiredMsg = I18n.Get(key, new
                {
                    playerName = owner.displayName ?? owner.Name,
                    npcName = npc.displayName,
                    itemName = GetDestDisplayName(session.TargetDestination)
                });
                ModEntry.ConversationBridge.ShowCompanionBubble(npc, expiredMsg, 3800);
                NotifyOwner(owner, expiredMsg, 2);
                NpcActionExecutor.Instance.EndOutingCompanionSilently(npc.Name);
                _invitationsByFarmer.Remove(farmerId);
            }
            return;
        }

        // FASE 3: En el destino -> Esperar fin de eventos ajenos + fundido + 180 ticks (3.0s) + Cinemática Nativa
        if (session.Phase == OutingInvitationPhase.AtDestination)
        {
            bool isLocalHost = owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID;
            bool otherEventRunning = isLocalHost && (Game1.eventUp || Game1.CurrentEvent != null || owner.currentLocation?.currentEvent != null);

            if (session.WaitingForCinematicCompletion)
            {
                if (!otherEventRunning && gameTick >= session.ArrivedAtDestinationTick + 7200)
                {
                    _invitationsByFarmer.Remove(farmerId);
                }
                return;
            }

            // Si hay un evento vanilla / de otro mod activo (ej. 6943962 en ArchaeologyHouse),
            // esperar sin incrementar reintentos ni activar burbujas de respaldo; al terminar reinicia la espera de 3.0s.
            if (otherEventRunning)
            {
                session.FadeClearedTick = 0;
                return;
            }

            bool fadeClear = !isLocalHost || (!Game1.fadeToBlack && !Game1.isWarping);
            if (fadeClear && session.FadeClearedTick == 0)
            {
                session.FadeClearedTick = gameTick;
                ModEntry.ModMonitor?.Log($"[NpcOuting] Fade cleared at '{session.TargetDestination}'. Waiting 180 ticks (3.0s) before starting cinematic...", LogLevel.Debug);
            }

            if (!session.CinematicTriggered && ModEntry.Config?.EnableCinematicOutings != false)
            {
                if (fadeClear && session.FadeClearedTick > 0 &&
                    gameTick >= session.FadeClearedTick + 180 &&
                    gameTick >= session.LastCinematicAttemptTick + 30)
                {
                    session.LastCinematicAttemptTick = gameTick;
                    var loc = owner.currentLocation;
                    string sceneType = CinematicDirectorService.ResolveSceneType(loc);

                    bool localClear = !isLocalHost || (!Game1.fadeToBlack && !Game1.isWarping && !Game1.eventUp && Game1.CurrentEvent == null && Game1.activeClickableMenu == null && !Game1.dialogueUp);

                    if (localClear && loc != null)
                    {
                        if (isLocalHost)
                        {
                            if (CinematicDirectorService.Instance.TryStartCinematic(loc, npc, owner, sceneType, fromOutingInvite: true, ignoreDailyCooldown: true))
                            {
                                session.CinematicTriggered = true;
                                session.WaitingForCinematicCompletion = true;
                                ModEntry.ModMonitor?.Log($"[NpcOuting] Launched '{sceneType}' cinematic for {npc.Name} at {loc.NameOrUniqueName} (attempt #{session.CinematicRetryCount + 1}).", LogLevel.Debug);
                                return;
                            }
                        }
                        else
                        {
                            ModEntry.MultiplayerSync?.SendStartCinematicOuting(new StartCinematicOutingMessage
                            {
                                TargetFarmerId = owner.UniqueMultiplayerID,
                                NpcName = npc.Name,
                                SceneType = sceneType,
                                FromOutingInvite = true
                            });
                            session.CinematicTriggered = true;
                            session.WaitingForCinematicCompletion = true;
                            ModEntry.ModMonitor?.Log($"[NpcOuting] Dispatched remote '{sceneType}' cinematic to Farmhand {owner.Name}.", LogLevel.Debug);
                            return;
                        }
                    }

                    session.CinematicRetryCount++;
                    if (session.CinematicRetryCount < 12)
                    {
                        ModEntry.ModMonitor?.Log($"[NpcOuting] Waiting to launch cinematic for {npc.Name} (attempt {session.CinematicRetryCount}/12: fade={Game1.fadeToBlack}, warp={Game1.isWarping}, menu={Game1.activeClickableMenu != null}, dlg={Game1.dialogueUp}).", LogLevel.Debug);
                        return;
                    }

                    // Se agotaron los 12 reintentos -> habilitar respaldo de burbujas
                    session.CinematicTriggered = true;
                    ModEntry.ModMonitor?.Log($"[NpcOuting] Exhausted 12 cinematic retries for {npc.Name} at {session.TargetDestination}; falling back to outing bubbles.", LogLevel.Debug);
                }
                else
                {
                    return;
                }
            }

            string destName = GetDestDisplayName(session.TargetDestination);

            if (!session.Stage1BubbleShown && gameTick >= session.ArrivedAtDestinationTick + 180)
            {
                session.Stage1BubbleShown = true;
                int v = Game1.random.Next(1, 4);
                string arrMsg = I18n.Get($"outing.arrived.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName, itemName = destName });
                ModEntry.ConversationBridge.ShowCompanionBubble(npc, arrMsg, 3500);
                npc.doEmote(20);
            }

            if (!session.Stage2BubbleShown && gameTick >= session.ArrivedAtDestinationTick + 1500)
            {
                session.Stage2BubbleShown = true;
                int v = Game1.random.Next(1, 4);
                string enjMsg = I18n.Get($"outing.enjoying.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName, itemName = destName });

                if ((session.TargetDestination == "Beach" || session.TargetDestination == "Forest" || session.TargetDestination == "Mountain") && !Game1.isRaining)
                {
                    enjMsg += " " + I18n.Get("outing.enjoying.picnic_hint");
                }

                ModEntry.ConversationBridge.ShowCompanionBubble(npc, enjMsg, 4000);
                npc.doEmote(Game1.random.NextDouble() > 0.5 ? 20 : 32);
            }

            bool elapsed = gameTick >= session.ArrivedAtDestinationTick + 3000;
            bool leftLoc = !IsLocationMatch(owner.currentLocation, NormalizeDestinationName(session.TargetDestination));

            if (!session.CompletedThanksShown && (elapsed || leftLoc))
            {
                session.CompletedThanksShown = true;
                string thxMsg = I18n.Get("outing.thanks_complete", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName, itemName = destName });
                ModEntry.ConversationBridge.ShowCompanionBubble(npc, thxMsg, 4000);
                npc.doEmote(20);

                owner.changeFriendship(40, npc);
                NpcActionExecutor.Instance.RecordMemory(npc.Name, $"We went on a wonderful outing together to {session.TargetDestination}.");

                _invitationsByFarmer.Remove(farmerId);
            }
        }
    }

    private static void NotifyOwner(Farmer owner, string message, int hudType)
    {
        if (owner == null || string.IsNullOrWhiteSpace(message)) return;
        if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
        {
            Game1.addHUDMessage(new HUDMessage(message, hudType));
        }
        else
        {
            ModEntry.MultiplayerSync?.SendPrivateNotice(owner.UniqueMultiplayerID, message, isError: hudType == HUDMessage.error_type, hudType: hudType);
        }
    }

    public void HandlePromptChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;
        long farmerId = owner.UniqueMultiplayerID;
        if (!_invitationsByFarmer.TryGetValue(farmerId, out var session)) return;

        if (choiceId.Equals("accept", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsDestinationAccessibleNow(session.TargetDestination, Game1.timeOfDay, requireArrivalBuffer: true, out _))
            {
                session.TargetDestination = ResolveDestinationForNpc(npc, preferIndoor: Game1.isRaining, checkTime: Game1.timeOfDay);
            }

            NpcAnimationService.TryPlayEmotionGesture(npc, "happy");
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "outing.invite.accepted", "$h");

            NpcActionExecutor.Instance.StartFollowDirectly(npc, owner);
            session.Phase = OutingInvitationPhase.TravelingToDestination;

            string destName = GetDestDisplayName(session.TargetDestination);
            string hudDest = I18n.Get("outing.hud.accepted_dest", new { npcName = npc.displayName, itemName = destName });
            NotifyOwner(owner, hudDest, 1);
        }
        else
        {
            PostDialogueInviteMonitor.SuppressCurrentInteraction(npc.Name);
            npc.doEmote(32);
            LivingEventManager.Instance.ShowReactionDialogue(npc, owner, "outing.invite.declined", "$s");

            _invitationsByFarmer.Remove(farmerId);
            npc.followSchedule = true;
            try { npc.checkSchedule(Game1.timeOfDay); } catch { }
        }
    }

    public bool TryTriggerPendingSpouseDate(NPC spouse, Farmer owner, GameLocation house)
    {
        if (spouse == null || owner == null || house == null) return false;
        long farmerId = owner.UniqueMultiplayerID;

        if (_invitationsByFarmer.ContainsKey(farmerId) || _activeSpouseDateOffersByFarmer.ContainsKey(farmerId))
            return false;
        if (HasActiveOutingForNpc(spouse.Name))
            return false;

        if (!_pendingSpouseDates.TryGetValue(farmerId, out var pending))
            return false;
        if (!string.Equals(pending.NpcName, spouse.Name, StringComparison.OrdinalIgnoreCase))
            return false;
        if (spouse.currentLocation != house || owner.currentLocation != house)
            return false;

        // Proteger la rutina matutina: jamás teletransportar o sacar al cónyuge fuera de FarmHouse/Cabin antes de las 8:00 AM
        if (ModEntry.Config?.EnableSpouseLivingHome != false && Game1.timeOfDay < 800 && !(house is FarmHouse || house is Cabin))
            return false;

        int evalTime = Math.Max(Game1.timeOfDay, 800);
        if (!IsDestinationAccessibleNow(pending.TargetDestination, evalTime, requireArrivalBuffer: true, out _))
        {
            pending.TargetDestination = ResolveDestinationForNpc(spouse, preferIndoor: Game1.isRaining, checkTime: evalTime);
        }

        if (Game1.timeOfDay < 800 && !IsDestinationAccessibleNow(pending.TargetDestination, Game1.timeOfDay, requireArrivalBuffer: true, out _))
            return false;

        if (Vector2.Distance(spouse.Tile, owner.Tile) > 7.5f)
            return false;

        if (farmerId == Game1.player.UniqueMultiplayerID &&
            (!Context.IsPlayerFree || Game1.activeClickableMenu != null || Game1.dialogueUp || Game1.eventUp || Game1.fadeToBlack || Game1.isWarping))
        {
            return false;
        }

        _pendingSpouseDates.Remove(farmerId);
        _activeSpouseDateOffersByFarmer[farmerId] = pending;
        TriggerSpouseDateOfferPrompt(spouse, owner, pending);
        return true;
    }

    private void TriggerSpouseDateOfferPrompt(NPC spouse, Farmer owner, PendingSpouseDate offer)
    {
        spouse.Halt();
        spouse.controller = null;
        NpcAnimationService.StopAnimationCleanly(spouse);
        spouse.faceGeneralDirection(owner.Position);
        spouse.doEmote(20);

        string destName = GetDestDisplayName(offer.TargetDestination);
        LivingEventManager.Instance.TriggerDirectPrompt(
            "spouse_date_invite",
            spouse,
            owner,
            "spouse_date.invite.prompt",
            "accept", "spouse_date.invite.opt_accept",
            "decline", "spouse_date.invite.opt_decline",
            itemDisplayName: destName,
            introKey: offer.IsRomantic ? "spouse_date.invite.intro" : "spouse_date.invite.intro_roommate",
            emotionTag: offer.IsRomantic ? "$l" : "$h"
        );
    }

    public void HandleSpouseDateChoice(NPC spouse, Farmer owner, string choiceId)
    {
        if (spouse == null || owner == null) return;
        long farmerId = owner.UniqueMultiplayerID;
        _activeSpouseDateOffersByFarmer.Remove(farmerId, out var offer);

        bool isRomantic = offer?.IsRomantic ?? !(owner.friendshipData.TryGetValue(spouse.Name, out var fd) && fd != null && fd.IsRoommate());
        string dest = offer?.TargetDestination ?? ResolveDestinationForNpc(spouse, preferIndoor: Game1.isRaining, checkTime: Game1.timeOfDay);

        if (choiceId.Equals("accept", StringComparison.OrdinalIgnoreCase))
        {
            if (!IsDestinationAccessibleNow(dest, Game1.timeOfDay, requireArrivalBuffer: true, out _))
            {
                dest = ResolveDestinationForNpc(spouse, preferIndoor: Game1.isRaining, checkTime: Game1.timeOfDay);
            }

            NpcActionExecutor.Instance.MarkSpouseDateAccepted(spouse, owner);
            NpcAnimationService.TryPlayEmotionGesture(spouse, "happy");
            LivingEventManager.Instance.ShowReactionDialogue(
                spouse,
                owner,
                isRomantic ? "spouse_date.invite.accepted" : "spouse_date.invite.accepted_roommate",
                isRomantic ? "$l" : "$h");

            _invitationsByFarmer[farmerId] = new NpcOutingSession
            {
                NpcName = spouse.Name,
                TargetFarmerId = farmerId,
                TargetDestination = dest,
                ScheduledTime = Game1.timeOfDay,
                Phase = OutingInvitationPhase.TravelingToDestination,
                IsSpouseDate = true,
                IsRomanticInvite = isRomantic
            };

            string destName = GetDestDisplayName(dest);
            string hudDest = I18n.Get("spouse_date.hud.accepted_dest", new { npcName = spouse.displayName, itemName = destName });
            NotifyOwner(owner, hudDest, 1);
        }
        else
        {
            PostDialogueInviteMonitor.SuppressCurrentInteraction(spouse.Name);
            spouse.doEmote(32);
            LivingEventManager.Instance.ShowReactionDialogue(spouse, owner, "spouse_date.invite.declined", "$h");
        }
    }

    public bool TestSpouseDate(string? npcName, out string statusMsg)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
        {
            statusMsg = "You must be in-game as host to test spouse dates.";
            return false;
        }

        Farmer owner = Game1.player;
        NPC? chosen = null;

        if (!string.IsNullOrWhiteSpace(npcName))
        {
            chosen = Game1.getCharacterFromName(npcName);
        }
        if (chosen == null && !string.IsNullOrWhiteSpace(owner.spouse))
        {
            chosen = Game1.getCharacterFromName(owner.spouse);
        }
        if (chosen == null)
        {
            chosen = Game1.currentLocation?.characters.FirstOrDefault(c => c.IsVillager) ?? Game1.getCharacterFromName("Penny");
        }
        if (chosen == null)
        {
            statusMsg = "Could not find a suitable NPC for spouse date test.";
            return false;
        }

        if (chosen.currentLocation != owner.currentLocation && owner.currentLocation != null)
        {
            Point nearTile = new Point(owner.TilePoint.X + 1, owner.TilePoint.Y);
            if (!NpcActionExecutor.Instance.Navigator.IsTileWalkable(owner.currentLocation, nearTile, chosen))
            {
                nearTile = owner.TilePoint;
            }
            Game1.warpCharacter(chosen, owner.currentLocation, nearTile.ToVector2());
        }

        bool isRoommate = owner.friendshipData.TryGetValue(chosen.Name, out var fd) && fd != null && fd.IsRoommate();
        string dest = ResolveDestinationForNpc(chosen, preferIndoor: Game1.isRaining, checkTime: Game1.timeOfDay);

        var offer = new PendingSpouseDate
        {
            NpcName = chosen.Name,
            OwnerFarmerId = owner.UniqueMultiplayerID,
            TargetDestination = dest,
            IsRomantic = !isRoommate
        };

        ClearForPlayer(owner.UniqueMultiplayerID);
        _activeSpouseDateOffersByFarmer[owner.UniqueMultiplayerID] = offer;
        TriggerSpouseDateOfferPrompt(chosen, owner, offer);

        statusMsg = $"Triggered surprise date invite with {chosen.Name} -> '{dest}'.";
        return true;
    }

    public bool TestInvite(string? npcName, out string statusMsg)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
        {
            statusMsg = "You must be in-game as host.";
            return false;
        }

        Farmer owner = Game1.player;
        NPC? chosen = null;

        if (!string.IsNullOrWhiteSpace(npcName))
        {
            chosen = Game1.getCharacterFromName(npcName);
        }

        if (chosen == null)
        {
            var candidates = GetEligibleNpcCandidates(owner);
            chosen = candidates.FirstOrDefault();
        }

        if (chosen == null)
        {
            chosen = Game1.getCharacterFromName("Penny") ?? Game1.currentLocation?.characters.FirstOrDefault(c => c.IsVillager);
        }

        if (chosen == null)
        {
            statusMsg = "Could not find any suitable NPC for test invite.";
            return false;
        }

        ClearForPlayer(owner.UniqueMultiplayerID);
        string dest = ResolveDestinationForNpc(chosen, preferIndoor: false, checkTime: Game1.timeOfDay);
        _invitationsByFarmer[owner.UniqueMultiplayerID] = new NpcOutingSession
        {
            NpcName = chosen.Name,
            TargetFarmerId = owner.UniqueMultiplayerID,
            TargetDestination = dest,
            ScheduledTime = Game1.timeOfDay,
            Phase = OutingInvitationPhase.WaitingAtDoor
        };

        statusMsg = $"Spawned outing invite from {chosen.Name} to {dest} at your doorstep.";
        return true;
    }

    private List<NPC> GetEligibleNpcCandidates(Farmer farmer)
    {
        var list = new List<NPC>();
        foreach (var name in farmer.friendshipData.Keys)
        {
            var npc = Game1.getCharacterFromName(name);
            if (npc == null || npc.IsInvisible || npc.Age == NPC.child) continue;
            if (farmer.getFriendshipHeartLevelForNPC(npc.Name) < 4) continue;
            if (NpcActionExecutor.IsMarriedToAnyFarmer(npc)) continue;
            if (!ShopDutyDetector.IsNpcEligibleForOfflineRecruitment(npc, ModEntry.Config)) continue;
            if (ShopDutyDetector.IsShopkeeperOnDuty(npc, npc.currentLocation)) continue;

            list.Add(npc);
        }
        return list;
    }

    private string ResolveDestinationForNpc(NPC npc, bool preferIndoor = false, int? checkTime = null)
    {
        int evalTime = checkTime ?? Game1.timeOfDay;
        var rawCandidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string rawDest in CharacterVoicePackService.Instance.GetAllPreferredDestinations(npc, preferIndoor))
        {
            string dest = NormalizeDestinationName(rawDest);
            if (!string.IsNullOrWhiteSpace(dest) && seen.Add(dest))
                rawCandidates.Add(dest);
        }

        string[] pool = preferIndoor
            ? new[] { "ArchaeologyHouse", "Saloon", "Town" }
            : new[] { "Beach", "Forest", "Mountain", "Town", "Saloon" };
        foreach (string p in pool)
        {
            string dest = NormalizeDestinationName(p);
            if (Game1.getLocationFromName(dest) != null && seen.Add(dest))
                rawCandidates.Add(dest);
        }

        var accessible = rawCandidates
            .Where(d => IsDestinationAccessibleNow(d, evalTime, requireArrivalBuffer: true, out _))
            .ToList();

        if (accessible.Count == 0)
        {
            string fallback = Game1.getLocationFromName("Town") != null ? "Town" : "Beach";
            _lastDestinationByNpc[npc.Name] = fallback;
            return fallback;
        }

        string chosen;
        if (accessible.Count >= 2 && _lastDestinationByNpc.TryGetValue(npc.Name, out string? lastDest))
        {
            var rotated = accessible.Where(d => !string.Equals(d, lastDest, StringComparison.OrdinalIgnoreCase)).ToList();
            chosen = rotated.Count > 0
                ? rotated[Game1.random.Next(rotated.Count)]
                : accessible[0];
        }
        else
        {
            chosen = accessible[0];
        }

        _lastDestinationByNpc[npc.Name] = chosen;
        return chosen;
    }

    private string GetDestDisplayName(string destLoc)
    {
        var loc = Game1.getLocationFromName(destLoc);
        if (loc != null)
        {
            string scene = CinematicDirectorService.ResolveSceneType(loc);
            if (loc is Woods) scene = "secret_woods";
            else if (destLoc.Contains("Island", StringComparison.OrdinalIgnoreCase) || destLoc.Contains("Resort", StringComparison.OrdinalIgnoreCase)) scene = "island";
            else if (loc is Beach) scene = "beach";
            else if (loc is LibraryMuseum) scene = "museum";
            else if (loc is Mountain) scene = "mountain";
            else if (loc is Forest) scene = "forest";
            else if (destLoc.Equals("Saloon", StringComparison.OrdinalIgnoreCase)) scene = "saloon";
            else if (loc is Desert) scene = "desert";
            else if (loc is Sewer) scene = "sewer";
            else if (destLoc.Contains("Railroad", StringComparison.OrdinalIgnoreCase)) scene = "railroad";
            else if (destLoc.Contains("BathHouse", StringComparison.OrdinalIgnoreCase)) scene = "spa";
            else if (loc is Town) scene = "town";
            return I18n.Get($"outing.loc.{scene}");
        }

        return destLoc switch
        {
            "Beach" => I18n.Get("outing.loc.beach"),
            "Forest" => I18n.Get("outing.loc.forest"),
            "Woods" => I18n.Get("outing.loc.secret_woods"),
            "Mountain" => I18n.Get("outing.loc.mountain"),
            "Saloon" => I18n.Get("outing.loc.saloon"),
            "ArchaeologyHouse" => I18n.Get("outing.loc.museum"),
            "Desert" => I18n.Get("outing.loc.desert"),
            "Railroad" => I18n.Get("outing.loc.railroad"),
            "Sewer" => I18n.Get("outing.loc.sewer"),
            _ => I18n.Get("outing.loc.town")
        };
    }

    private Point GetExteriorDoorTile(Farm farm, Farmer owner)
    {
        if (owner.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID)
        {
            var cabinBuilding = farm.buildings.FirstOrDefault(b => b.isCabin && (b.indoors.Value as Cabin)?.owner?.UniqueMultiplayerID == owner.UniqueMultiplayerID);
            if (cabinBuilding != null)
            {
                return new Point(cabinBuilding.tileX.Value + cabinBuilding.humanDoor.X, cabinBuilding.tileY.Value + cabinBuilding.humanDoor.Y);
            }
        }

        Point houseDoor = farm.GetMainFarmHouseEntry();
        if (houseDoor == Point.Zero)
        {
            houseDoor = new Point(64, 15);
        }
        return houseDoor;
    }
}
