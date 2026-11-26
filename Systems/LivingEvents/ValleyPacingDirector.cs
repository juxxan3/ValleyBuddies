namespace ValleyBuddies.Systems.LivingEvents;

using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using ValleyBuddies.Systems;

public sealed class PlayerPacingRecord
{
    public int LastDoorEventDay { get; set; } = -999;
    public int LastSpouseDateDay { get; set; } = -999;
    public int LastBurnedBreakfastDay { get; set; } = -999;
    public int LastFavoriteSpotCinematicDay { get; set; } = -999;
    public Dictionary<string, int> LastInviteDayByNpc { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ValleyPacingSaveData
{
    public Dictionary<long, PlayerPacingRecord> ByFarmer { get; set; } = new();
}

public readonly record struct PacingPresetParams(
    string PresetName,
    int DoorCooldownDays,
    int SpouseDateCooldownDays,
    int OutdoorCooldownMin,
    int DomesticCooldownMin,
    int GlobalBreathMin,
    int MaxOutdoorPerDay,
    int MaxDomesticPerDay,
    int FavoriteSpotCooldownDays,
    double ProbabilityFactor
);

/// <summary>
/// Director de Ritmo Unificado (v5.3.1):
/// Centraliza probabilidades, pity timers, prioridades de Plan Grande (Cita Cónyuge > Invitación Inversa > Visita Clásica),
/// límites diarios por categoría y filtros estrictos de corazones/edad, aislado por OwnerFarmerId en Co-op.
/// </summary>
public sealed class ValleyPacingDirector
{
    public static ValleyPacingDirector Instance { get; } = new();

    private const string SaveDataKey = "ValleyBuddies.PacingState";

    private sealed class DailyFarmerState
    {
        public bool HasMajorPlanToday { get; set; }
        public int OutdoorEventsToday { get; set; }
        public int DomesticEventsToday { get; set; }
        public int LastAnyMicroEventGameMinutes { get; set; } = -999;
        public int LastOutdoorEventGameMinutes { get; set; } = -999;
        public int LastDomesticEventGameMinutes { get; set; } = -999;
        public bool FavoriteSpotOfferedToday { get; set; }
        public HashSet<string> TriggeredEventIdsToday { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private IModHelper? _helper;
    private ValleyPacingSaveData _saveData = new();
    private readonly Dictionary<long, DailyFarmerState> _dailyByFarmer = new();

    private static readonly HashSet<string> DomesticEventIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "burned_breakfast",
        "kitchen_taste_test",
        "house_bug_scare",
        "home_snack_craving"
    };

    private static readonly HashSet<string> ChildSafeEventIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "shiny_farm_find",
        "mischievous_animal",
        "rainy_chill_break",
        "wildflower_crown",
        "snowball_play",
        "street_music_hum"
    };

    private static readonly HashSet<string> LightCasualAdultEventIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "shiny_farm_find",
        "rainy_chill_break",
        "mischievous_animal",
        "farm_work_cheering",
        "town_craving",
        "firefly_moment",
        "wildflower_crown",
        "street_music_hum",
        "snowball_play"
    };

    public double ProbabilityFactor => GetPresetParams().ProbabilityFactor;
    public double MicroEventProbabilityFactor => GetPresetParams().ProbabilityFactor * (Math.Clamp(ModEntry.Config?.MicroEventFrequencyPercent ?? 100, 25, 200) / 100.0);

    public void Initialize(IModHelper helper)
    {
        _helper = helper;
    }

    public void OnSaveLoaded()
    {
        _dailyByFarmer.Clear();
        if (_helper != null && Context.IsMainPlayer)
        {
            _saveData = _helper.Data.ReadSaveData<ValleyPacingSaveData>(SaveDataKey) ?? new ValleyPacingSaveData();
            _saveData.ByFarmer ??= new Dictionary<long, PlayerPacingRecord>();
            foreach (var rec in _saveData.ByFarmer.Values)
            {
                if (rec == null) continue;
                rec.LastInviteDayByNpc = new Dictionary<string, int>(
                    rec.LastInviteDayByNpc ?? new Dictionary<string, int>(),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        else
        {
            _saveData = new ValleyPacingSaveData();
        }
    }

    public void Save()
    {
        if (_helper != null && Context.IsMainPlayer && Context.IsWorldReady)
        {
            try
            {
                _helper.Data.WriteSaveData(SaveDataKey, _saveData);
            }
            catch { }
        }
    }

    public void ResetDaily()
    {
        _dailyByFarmer.Clear();
    }

    public void ResetAll()
    {
        _dailyByFarmer.Clear();
        _saveData = new ValleyPacingSaveData();
    }

    public void ImportLegacyOutingHistory(long hostFarmerId, int lastGlobalDay, Dictionary<string, int>? byNpc)
    {
        var rec = GetOrCreateRecord(hostFarmerId);
        bool changed = false;

        if (lastGlobalDay > rec.LastDoorEventDay && lastGlobalDay >= 0)
        {
            rec.LastDoorEventDay = lastGlobalDay;
            changed = true;
        }

        if (byNpc != null)
        {
            foreach (var (name, day) in byNpc)
            {
                if (!rec.LastInviteDayByNpc.TryGetValue(name, out int existing) || day > existing)
                {
                    rec.LastInviteDayByNpc[name] = day;
                    changed = true;
                }
            }
        }

        if (changed) Save();
    }

    public PacingPresetParams GetPresetParams()
    {
        string raw = ModEntry.Config?.LivingPacingPreset?.Trim() ?? "Balanced";
        int baseDoorCooldown = Math.Clamp(ModEntry.Config?.NpcOutingCooldownDays ?? 4, 1, 14);

        if (raw.Equals("Relaxed", StringComparison.OrdinalIgnoreCase))
        {
            return new PacingPresetParams(
                PresetName: "Relaxed",
                DoorCooldownDays: Math.Max(6, baseDoorCooldown + 2),
                SpouseDateCooldownDays: 12,
                OutdoorCooldownMin: 210,
                DomesticCooldownMin: 150,
                GlobalBreathMin: 150,
                MaxOutdoorPerDay: 1,
                MaxDomesticPerDay: 1,
                FavoriteSpotCooldownDays: 5,
                ProbabilityFactor: 0.70
            );
        }

        if (raw.Equals("Lively", StringComparison.OrdinalIgnoreCase))
        {
            return new PacingPresetParams(
                PresetName: "Lively",
                DoorCooldownDays: Math.Max(2, baseDoorCooldown / 2),
                SpouseDateCooldownDays: 4,
                OutdoorCooldownMin: 90,
                DomesticCooldownMin: 70,
                GlobalBreathMin: 70,
                MaxOutdoorPerDay: 3,
                MaxDomesticPerDay: 2,
                FavoriteSpotCooldownDays: 1,
                ProbabilityFactor: 1.45
            );
        }

        return new PacingPresetParams(
            PresetName: "Balanced",
            DoorCooldownDays: baseDoorCooldown,
            SpouseDateCooldownDays: 7,
            OutdoorCooldownMin: 150,
            DomesticCooldownMin: 120,
            GlobalBreathMin: 120,
            MaxOutdoorPerDay: 2,
            MaxDomesticPerDay: 1,
            FavoriteSpotCooldownDays: 3,
            ProbabilityFactor: 1.00
        );
    }

    public bool HasMajorPlanToday(long farmerId)
    {
        return GetOrCreateDaily(farmerId).HasMajorPlanToday;
    }

    public int GetLastInviteDayForNpc(long farmerId, string npcName)
    {
        var rec = GetOrCreateRecord(farmerId);
        return rec.LastInviteDayByNpc.TryGetValue(npcName, out int day) ? day : -999;
    }

    // ─────────────────── Pity Timers & Planificación Diaria ───────────────────

    public double ComputeDoorInviteChance(long farmerId, int totalDays, int baseChancePercent)
    {
        if (baseChancePercent <= 0) return 0.0;

        var rec = GetOrCreateRecord(farmerId);
        var p = GetPresetParams();

        int elapsed = rec.LastDoorEventDay < 0 ? p.DoorCooldownDays : (totalDays - rec.LastDoorEventDay);
        if (elapsed < p.DoorCooldownDays)
            return 0.0;

        int overdueDays = elapsed - p.DoorCooldownDays;
        double baseProb = Math.Clamp(baseChancePercent, 0, 100) / 100.0;

        double raw = overdueDays switch
        {
            0 => baseProb,
            1 => Math.Max(baseProb, 0.30),
            2 => Math.Max(baseProb, 0.50),
            3 => Math.Max(baseProb, 0.75),
            _ => 0.95
        };

        return Math.Clamp(raw * p.ProbabilityFactor, 0.0, 1.0);
    }

    public double ComputeSpouseDateChance(long farmerId, int totalDays)
    {
        var rec = GetOrCreateRecord(farmerId);
        var p = GetPresetParams();

        int elapsed = rec.LastSpouseDateDay < 0 ? p.SpouseDateCooldownDays : (totalDays - rec.LastSpouseDateDay);
        if (elapsed < p.SpouseDateCooldownDays)
            return 0.0;

        int overdueDays = elapsed - p.SpouseDateCooldownDays;
        bool isWeekend = (Game1.dayOfMonth % 7) is 6 or 0;
        double startProb = isWeekend ? 0.40 : 0.25;
        double raw = Math.Min(0.95, startProb + overdueDays * 0.20);

        return Math.Clamp(raw * p.ProbabilityFactor, 0.0, 1.0);
    }

    public double ComputeBurnedBreakfastChance(long farmerId, int totalDays)
    {
        var rec = GetOrCreateRecord(farmerId);
        var p = GetPresetParams();

        int elapsed = rec.LastBurnedBreakfastDay < 0 ? 14 : (totalDays - rec.LastBurnedBreakfastDay);
        double raw = elapsed >= 14 ? 0.15 : 0.07;

        return Math.Clamp(raw * p.ProbabilityFactor, 0.0, 1.0);
    }

    public void RecordSpouseDateScheduled(long farmerId, int totalDays)
    {
        var daily = GetOrCreateDaily(farmerId);
        daily.HasMajorPlanToday = true;

        var rec = GetOrCreateRecord(farmerId);
        rec.LastSpouseDateDay = totalDays;
        Save();
    }

    public void RecordDoorEventScheduled(long farmerId, string npcName, int totalDays)
    {
        var daily = GetOrCreateDaily(farmerId);
        daily.HasMajorPlanToday = true;

        var rec = GetOrCreateRecord(farmerId);
        rec.LastDoorEventDay = totalDays;
        if (!string.IsNullOrWhiteSpace(npcName))
        {
            rec.LastInviteDayByNpc[npcName] = totalDays;
        }
        Save();
    }

    public bool CanScheduleClassicVisit(long farmerId, int totalDays)
    {
        if (HasMajorPlanToday(farmerId))
            return false;

        var rec = GetOrCreateRecord(farmerId);
        var p = GetPresetParams();
        int elapsed = rec.LastDoorEventDay < 0 ? p.DoorCooldownDays : (totalDays - rec.LastDoorEventDay);
        return elapsed >= p.DoorCooldownDays;
    }

    public void RecordClassicVisitScheduled(long farmerId, int totalDays)
    {
        var daily = GetOrCreateDaily(farmerId);
        daily.HasMajorPlanToday = true;

        var rec = GetOrCreateRecord(farmerId);
        rec.LastDoorEventDay = totalDays;
        Save();
    }

    // ─────────────────── Micro-Eventos & Filtros de Corazones/Edad ───────────────────

    public static bool IsDomesticEvent(string eventId) => DomesticEventIds.Contains(eventId);

    public bool PassesHeartAndAgeGate(string eventId, Farmer? owner, NPC? npc)
    {
        if (owner == null || npc == null) return false;

        bool isChild = CavePersonalityRegistry.ResolveProfile(npc).IsChildProtected || npc.Age == NPC.child;
        int hearts = owner.getFriendshipHeartLevelForNPC(npc.Name);

        if (isChild)
        {
            return hearts >= 2 && ChildSafeEventIds.Contains(eventId);
        }

        if (IsDomesticEvent(eventId))
        {
            return !string.IsNullOrWhiteSpace(owner.spouse) &&
                   owner.spouse.Equals(npc.Name, StringComparison.OrdinalIgnoreCase);
        }

        if (hearts <= 1)
            return false;

        if (hearts < 4)
            return LightCasualAdultEventIds.Contains(eventId);

        return true;
    }

    public bool CanTriggerMicroEvent(string eventId, Farmer? owner, NPC? npc)
    {
        if (owner == null || npc == null) return false;
        if (!PassesHeartAndAgeGate(eventId, owner, npc)) return false;

        var daily = GetOrCreateDaily(owner.UniqueMultiplayerID);
        if (daily.TriggeredEventIdsToday.Contains(eventId)) return false;

        var p = GetPresetParams();
        int freqPct = Math.Clamp(ModEntry.Config?.MicroEventFrequencyPercent ?? 100, 25, 200);
        double cdScale = 100.0 / freqPct;
        int effGlobalBreathMin = Math.Max(15, (int)Math.Round(p.GlobalBreathMin * cdScale));
        int effDomesticCooldownMin = Math.Max(15, (int)Math.Round(p.DomesticCooldownMin * cdScale));
        int effOutdoorCooldownMin = Math.Max(15, (int)Math.Round(p.OutdoorCooldownMin * cdScale));
        int nowMin = ToTotalGameMinutes(Game1.timeOfDay);

        if (daily.LastAnyMicroEventGameMinutes >= 0 && (nowMin - daily.LastAnyMicroEventGameMinutes < effGlobalBreathMin))
            return false;

        if (IsDomesticEvent(eventId))
        {
            if (daily.DomesticEventsToday >= p.MaxDomesticPerDay) return false;
            if (daily.LastDomesticEventGameMinutes >= 0 && (nowMin - daily.LastDomesticEventGameMinutes < effDomesticCooldownMin))
                return false;
        }
        else
        {
            if (daily.OutdoorEventsToday >= p.MaxOutdoorPerDay) return false;
            if (daily.LastOutdoorEventGameMinutes >= 0 && (nowMin - daily.LastOutdoorEventGameMinutes < effOutdoorCooldownMin))
                return false;
        }

        return true;
    }

    public void RecordMicroEventTriggered(long farmerId, string eventId)
    {
        var daily = GetOrCreateDaily(farmerId);
        int nowMin = ToTotalGameMinutes(Game1.timeOfDay);

        daily.TriggeredEventIdsToday.Add(eventId);
        daily.LastAnyMicroEventGameMinutes = nowMin;

        if (IsDomesticEvent(eventId))
        {
            daily.DomesticEventsToday++;
            daily.LastDomesticEventGameMinutes = nowMin;
        }
        else
        {
            daily.OutdoorEventsToday++;
            daily.LastOutdoorEventGameMinutes = nowMin;
        }

        if (eventId.Equals("burned_breakfast", StringComparison.OrdinalIgnoreCase))
        {
            var rec = GetOrCreateRecord(farmerId);
            rec.LastBurnedBreakfastDay = Game1.Date.TotalDays;
            Save();
        }
    }

    // ─────────────────── Sitios Favoritos Orgánicos ───────────────────

    public bool CanOfferFavoriteSpotCinematic(long farmerId, int totalDays)
    {
        var daily = GetOrCreateDaily(farmerId);
        if (daily.FavoriteSpotOfferedToday) return false;

        var rec = GetOrCreateRecord(farmerId);
        var p = GetPresetParams();
        int elapsed = rec.LastFavoriteSpotCinematicDay < 0 ? p.FavoriteSpotCooldownDays : (totalDays - rec.LastFavoriteSpotCinematicDay);
        return elapsed >= p.FavoriteSpotCooldownDays;
    }

    public void MarkFavoriteSpotOfferedToday(long farmerId)
    {
        GetOrCreateDaily(farmerId).FavoriteSpotOfferedToday = true;
    }

    public void RecordFavoriteSpotCinematicTaken(long farmerId, int totalDays)
    {
        GetOrCreateDaily(farmerId).FavoriteSpotOfferedToday = true;
        GetOrCreateRecord(farmerId).LastFavoriteSpotCinematicDay = totalDays;
        Save();
    }

    // ─────────────────── Consola / Diagnóstico ───────────────────

    public string FormatStatusReport(long farmerId)
    {
        int totalDays = Game1.Date?.TotalDays ?? 0;
        var p = GetPresetParams();
        var rec = GetOrCreateRecord(farmerId);
        var daily = GetOrCreateDaily(farmerId);

        int baseInvitePct = ModEntry.Config?.NpcOutingInviteChancePercent ?? 20;
        double doorChance = ComputeDoorInviteChance(farmerId, totalDays, baseInvitePct);
        double spouseChance = ComputeSpouseDateChance(farmerId, totalDays);
        double burnChance = ComputeBurnedBreakfastChance(farmerId, totalDays);

        string doorElapsed = rec.LastDoorEventDay < 0 ? "never" : $"{totalDays - rec.LastDoorEventDay}d ago (day {rec.LastDoorEventDay})";
        string spouseElapsed = rec.LastSpouseDateDay < 0 ? "never" : $"{totalDays - rec.LastSpouseDateDay}d ago (day {rec.LastSpouseDateDay})";
        string burnElapsed = rec.LastBurnedBreakfastDay < 0 ? "never" : $"{totalDays - rec.LastBurnedBreakfastDay}d ago (day {rec.LastBurnedBreakfastDay})";
        string favElapsed = rec.LastFavoriteSpotCinematicDay < 0 ? "never" : $"{totalDays - rec.LastFavoriteSpotCinematicDay}d ago (day {rec.LastFavoriteSpotCinematicDay})";

        return
            $"=== Valley Buddies Pacing Director ===\n" +
            $"Preset: {p.PresetName} (probFactor={p.ProbabilityFactor:F2} | doorCD={p.DoorCooldownDays}d | spouseCD={p.SpouseDateCooldownDays}d | outdoorCD={p.OutdoorCooldownMin}m | domesticCD={p.DomesticCooldownMin}m | breath={p.GlobalBreathMin}m | favCD={p.FavoriteSpotCooldownDays}d)\n" +
            $"Door Invite / Visit: last={doorElapsed} | todayPityChance={doorChance * 100:F1}% | MajorPlanToday={daily.HasMajorPlanToday}\n" +
            $"Spouse Date:         last={spouseElapsed} | todayPityChance={spouseChance * 100:F1}%\n" +
            $"Burned Breakfast:    last={burnElapsed} | todayChance={burnChance * 100:F1}%\n" +
            $"Favorite Spot Cine:  last={favElapsed} | offeredToday={daily.FavoriteSpotOfferedToday}\n" +
            $"Today Micro-Events:  Outdoor={daily.OutdoorEventsToday}/{p.MaxOutdoorPerDay} | Domestic={daily.DomesticEventsToday}/{p.MaxDomesticPerDay} | LastTimeMin={daily.LastAnyMicroEventGameMinutes}";
    }

    private PlayerPacingRecord GetOrCreateRecord(long farmerId)
    {
        if (!_saveData.ByFarmer.TryGetValue(farmerId, out var rec) || rec == null)
        {
            rec = new PlayerPacingRecord();
            _saveData.ByFarmer[farmerId] = rec;
        }
        rec.LastInviteDayByNpc ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return rec;
    }

    private DailyFarmerState GetOrCreateDaily(long farmerId)
    {
        if (!_dailyByFarmer.TryGetValue(farmerId, out var st) || st == null)
        {
            st = new DailyFarmerState();
            _dailyByFarmer[farmerId] = st;
        }
        return st;
    }

    public static int ToTotalGameMinutes(int timeOfDay)
    {
        int hours = timeOfDay / 100;
        int mins = timeOfDay % 100;
        return hours * 60 + mins;
    }
}
