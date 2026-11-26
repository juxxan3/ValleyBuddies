using System;
using System.Collections.Generic;
using StardewValley;
using StardewValley.GameData.Characters;

namespace ValleyBuddies.Systems;

public enum CaveArchetype
{
    Vanguard,         // Exploradores/Guerreros: minado normal, abren paso, sin miedo en pisos infestados
    MinerArtisan,     // Mineros/Artesanos: minado muy rápido, bonus de menas/carbón/geodas
    SupportHealer,    // Sanadores/Apoyo: minado lento y cuidadoso (ej. Penny), restaura energía/ánimo y halla artefactos/geodas
    TechnicalArcane,  // Técnicos/Arcanos: luz dinámica potente en oscuridad, priorizan nodos de mineral
    CautiousFortune,  // Cautelosos/Fortuna: minado lento-medio, gran detección de escaleras, bonus de gemas/raíces
    ChildProtected    // Niños: por seguridad no bajan a pisos peligrosos
}

public enum CaveCombatStyle
{
    ProactiveMelee,   // Vanguard: busca y pelea cuerpo a cuerpo con buen daño y knockback
    RangedArcane,     // TechnicalArcane: ataca a distancia (3-6 tiles) con proyectiles/magia
    ShortSelfDefense, // MinerArtisan: solo se defiende si un monstruo se acerca <= 2.5 tiles
    PassiveHealOnly,  // SupportHealer (Penny, Harvey, Gus...): evita pelear, empujón mínimo (2-4 dmg) si acorralado; CURA vida y energía al jugador
    AvoidanceOnly,    // CautiousFortune (Haley, Emily, Leah...): se aparta del peligro, empujón mínimo (2-4 dmg) si acorralado
    None              // ChildProtected
}

public enum CaveTacticStance
{
    Balanced,      // Equilibrado: sigue su personalidad natural
    LadderHunter,  // Buscador de Escaleras: prioriza picar rocas cercanas para hallar escaleras/agujeros
    GuardCombat,   // Guardia / Combate: permanece cerca del jugador (<= 3.5 tiles) protegiéndolo de amenazas
    StayBack       // Quédate Atrás (Modo Seguro): no pica ni ataca, sigue al jugador pegado fuera de peligro
}

public sealed record CavePersonalityProfile(
    CaveArchetype Archetype,
    CaveCombatStyle CombatStyle,
    float MiningSpeedMultiplier,     // >1.0 más rápido, <1.0 más lento (ej. 1.55f Minero, 0.65f Apoyo)
    float BonusDropChance,           // Probabilidad de 1 ítem extra temático al romper rocas/nodos (0.0f..0.22f)
    int LadderSpotRadius,            // Radio en tiles para detectar escaleras/agujeros (ej. 8..15)
    float DarkFloorLightRadius,      // Radio de luz en pisos oscuros/Volcán (0f sin luz, 1.5f linterna, 2.8f luz potente)
    bool PrioritizesOreNodes,        // Prefiere nodos de mineral/gemas antes que piedra común
    bool GivesPeriodicStaminaSupport,// Restaura energía al jugador dueño periódicamente en cuevas
    bool IsChildProtected,           // Impide bajar a pisos peligrosos de MineShaft / VolcanoDungeon
    int BaseMinDamage,               // Daño mínimo base en combate
    int BaseMaxDamage,               // Daño máximo base en combate
    float KnockbackPower,            // Fuerza de empuje contra monstruos
    int AttackCooldownTicks,         // Cooldown entre ataques
    int MiningSwingIntervalTicks,    // Ritmo realista entre golpes de pico
    int PickaxePower,                // Progreso que quita por golpe a cada roca
    bool CanHealPlayerHp,            // Capacidad de curar puntos de vida (HP) al jugador
    int BaseMaxMorale                // Aguante base antes de necesitar recuperar el aliento
);

public static class CavePersonalityRegistry
{
    // ── v4.3.3: Dureza base 12 para piedra común, 18 para mena básica, 24 para mena dura ──
    // MinerArtisan  Power=12 → 1 golpe en piedra común (12/12=1), 2 en mena básica, 2 en mena dura
    // Vanguard      Power= 6 → 2 golpes en piedra común (12/6=2), 3 en mena básica, 4 en mena dura
    // TechnicalArcane Power=6 → 2 golpes en piedra común, 3 en mena básica, 4 en mena dura
    // CautiousFortune Power=4 → 3 golpes en piedra común (12/4=3), 5 en mena básica, 6 en mena dura
    // SupportHealer  Power=3 → 4 golpes en piedra común (12/3=4), 6 en mena básica, 8 en mena dura

    public static readonly CavePersonalityProfile ProfileVanguard = new(
        Archetype: CaveArchetype.Vanguard,
        CombatStyle: CaveCombatStyle.ProactiveMelee,
        MiningSpeedMultiplier: 1.05f,
        BonusDropChance: 0.08f,
        LadderSpotRadius: 10,
        DarkFloorLightRadius: 1.5f,
        PrioritizesOreNodes: false,
        GivesPeriodicStaminaSupport: false,
        IsChildProtected: false,
        BaseMinDamage: 10,
        BaseMaxDamage: 18,
        KnockbackPower: 1.8f,
        AttackCooldownTicks: 105,
        MiningSwingIntervalTicks: 64,  // v4.3.3: 64 ticks (~1.07 s) por golpe
        PickaxePower: 6,               // 2 golpes exactos en piedra común (12/6=2)
        CanHealPlayerHp: false,
        BaseMaxMorale: 120
    );

    public static readonly CavePersonalityProfile ProfileMinerArtisan = new(
        Archetype: CaveArchetype.MinerArtisan,
        CombatStyle: CaveCombatStyle.ShortSelfDefense,
        MiningSpeedMultiplier: 1.55f,
        BonusDropChance: 0.18f,
        LadderSpotRadius: 10,
        DarkFloorLightRadius: 1.6f,
        PrioritizesOreNodes: true,
        GivesPeriodicStaminaSupport: false,
        IsChildProtected: false,
        BaseMinDamage: 5,
        BaseMaxDamage: 10,
        KnockbackPower: 1.5f,
        AttackCooldownTicks: 120,
        MiningSwingIntervalTicks: 52,  // v4.3.3: 52 ticks (~0.87 s) por golpe
        PickaxePower: 12,              // 1 golpe exacto en piedra común (12/12=1)
        CanHealPlayerHp: false,
        BaseMaxMorale: 100
    );

    public static readonly CavePersonalityProfile ProfileSupportHealer = new(
        Archetype: CaveArchetype.SupportHealer,
        CombatStyle: CaveCombatStyle.PassiveHealOnly,
        MiningSpeedMultiplier: 0.65f,
        BonusDropChance: 0.14f,
        LadderSpotRadius: 11,
        DarkFloorLightRadius: 0.8f,
        PrioritizesOreNodes: false,
        GivesPeriodicStaminaSupport: true,
        IsChildProtected: false,
        BaseMinDamage: 2,
        BaseMaxDamage: 4,
        KnockbackPower: 0.9f,
        AttackCooldownTicks: 170,
        MiningSwingIntervalTicks: 88,  // v4.3.3: 88 ticks (~1.47 s) por golpe
        PickaxePower: 3,               // 4 golpes exactos en piedra común (12/3=4)
        CanHealPlayerHp: true,
        BaseMaxMorale: 75
    );

    public static readonly CavePersonalityProfile ProfileTechnicalArcane = new(
        Archetype: CaveArchetype.TechnicalArcane,
        CombatStyle: CaveCombatStyle.RangedArcane,
        MiningSpeedMultiplier: 1.00f,
        BonusDropChance: 0.12f,
        LadderSpotRadius: 12,
        DarkFloorLightRadius: 2.8f,
        PrioritizesOreNodes: true,
        GivesPeriodicStaminaSupport: false,
        IsChildProtected: false,
        BaseMinDamage: 8,
        BaseMaxDamage: 15,
        KnockbackPower: 1.0f,
        AttackCooldownTicks: 120,
        MiningSwingIntervalTicks: 66,  // v4.3.3: 66 ticks (~1.10 s) por golpe
        PickaxePower: 6,               // 2 golpes exactos en piedra común (12/6=2)
        CanHealPlayerHp: false,
        BaseMaxMorale: 85
    );

    public static readonly CavePersonalityProfile ProfileCautiousFortune = new(
        Archetype: CaveArchetype.CautiousFortune,
        CombatStyle: CaveCombatStyle.AvoidanceOnly,
        MiningSpeedMultiplier: 0.82f,
        BonusDropChance: 0.18f,
        LadderSpotRadius: 15,
        DarkFloorLightRadius: 0.9f,
        PrioritizesOreNodes: true,
        GivesPeriodicStaminaSupport: false,
        IsChildProtected: false,
        BaseMinDamage: 2,
        BaseMaxDamage: 4,
        KnockbackPower: 0.8f,
        AttackCooldownTicks: 170,
        MiningSwingIntervalTicks: 76,  // v4.3.3: 76 ticks (~1.27 s) por golpe
        PickaxePower: 4,               // 3 golpes exactos en piedra común (12/4=3)
        CanHealPlayerHp: false,
        BaseMaxMorale: 70
    );

    public static readonly CavePersonalityProfile ProfileChildProtected = new(
        Archetype: CaveArchetype.ChildProtected,
        CombatStyle: CaveCombatStyle.None,
        MiningSpeedMultiplier: 0.0f,
        BonusDropChance: 0.0f,
        LadderSpotRadius: 0,
        DarkFloorLightRadius: 0.0f,
        PrioritizesOreNodes: false,
        GivesPeriodicStaminaSupport: false,
        IsChildProtected: true,
        BaseMinDamage: 0,
        BaseMaxDamage: 0,
        KnockbackPower: 0.0f,
        AttackCooldownTicks: 9999,
        MiningSwingIntervalTicks: 9999,
        PickaxePower: 0,
        CanHealPlayerHp: false,
        BaseMaxMorale: 50
    );

    private static readonly Dictionary<string, CavePersonalityProfile> _registry = new(StringComparer.OrdinalIgnoreCase);

    static CavePersonalityRegistry()
    {
        // ── 1. STARDEW VALLEY VANILLA (36 NPCs) ──
        // Vanguard (5)
        Register("Abigail", ProfileVanguard);
        Register("Alex", ProfileVanguard);
        Register("Kent", ProfileVanguard);
        Register("Marlon", ProfileVanguard);
        Register("Gil", ProfileVanguard);

        // MinerArtisan (6)
        Register("Clint", ProfileMinerArtisan);
        Register("Dwarf", ProfileMinerArtisan);
        Register("Robin", ProfileMinerArtisan);
        Register("Willy", ProfileMinerArtisan);
        Register("Pam", ProfileMinerArtisan);
        Register("Shane", ProfileMinerArtisan);

        // SupportHealer (8)
        Register("Penny", ProfileSupportHealer);
        Register("Harvey", ProfileSupportHealer);
        Register("Gus", ProfileSupportHealer);
        Register("Evelyn", ProfileSupportHealer);
        Register("Jodi", ProfileSupportHealer);
        Register("Caroline", ProfileSupportHealer);
        Register("Pierre", ProfileSupportHealer);
        Register("Lewis", ProfileSupportHealer);

        // TechnicalArcane (6)
        Register("Maru", ProfileTechnicalArcane);
        Register("Sebastian", ProfileTechnicalArcane);
        Register("Wizard", ProfileTechnicalArcane);
        Register("Krobus", ProfileTechnicalArcane);
        Register("Demetrius", ProfileTechnicalArcane);
        Register("Sam", ProfileTechnicalArcane);

        // CautiousFortune (8)
        Register("Emily", ProfileCautiousFortune);
        Register("Haley", ProfileCautiousFortune);
        Register("Elliott", ProfileCautiousFortune);
        Register("Leah", ProfileCautiousFortune);
        Register("Linus", ProfileCautiousFortune);
        Register("Sandy", ProfileCautiousFortune);
        Register("George", ProfileCautiousFortune);
        Register("Marnie", ProfileCautiousFortune);

        // ChildProtected (3)
        Register("Vincent", ProfileChildProtected);
        Register("Jas", ProfileChildProtected);
        Register("Leo", ProfileChildProtected);

        // ── 2. STARDEW VALLEY EXPANDED (SVE) (17 NPCs) ──
        // Vanguard (5)
        Register("Lance", ProfileVanguard);
        Register("MarlonFay", ProfileVanguard);
        Register("Alesia", ProfileVanguard);
        Register("Isaac", ProfileVanguard);
        Register("Camilla", ProfileVanguard);

        // MinerArtisan (2)
        Register("Andy", ProfileMinerArtisan);
        Register("Susan", ProfileMinerArtisan);

        // SupportHealer (3)
        Register("Sophia", ProfileSupportHealer);
        Register("Claire", ProfileSupportHealer);
        Register("Scarlett", ProfileSupportHealer);

        // TechnicalArcane (3)
        Register("Morgan", ProfileTechnicalArcane);
        Register("Victor", ProfileTechnicalArcane);
        Register("Apples", ProfileTechnicalArcane); // Junimo místico con luz mágica

        // CautiousFortune (4)
        Register("Olivia", ProfileCautiousFortune);
        Register("GuntherSilvian", ProfileCautiousFortune);
        Register("Martin", ProfileCautiousFortune);
        Register("MorrisTod", ProfileCautiousFortune);

        // ── 3. RIDGESIDE VILLAGE (RSV) (43 NPCs) ──
        // Vanguard (11)
        Register("Jio", ProfileVanguard);
        Register("Kiwi", ProfileVanguard);
        Register("Daia", ProfileVanguard);
        Register("Shiro", ProfileVanguard);
        Register("Ezekiel", ProfileVanguard);
        Register("Freddie", ProfileVanguard);
        Register("Jeric", ProfileVanguard);
        Register("Corine", ProfileVanguard);
        Register("Anton", ProfileVanguard);
        Register("Bryle", ProfileVanguard);
        Register("Belinda", ProfileVanguard);

        // MinerArtisan (4)
        Register("Ian", ProfileMinerArtisan);
        Register("Lenny", ProfileMinerArtisan);
        Register("Carmen", ProfileMinerArtisan);
        Register("Blair", ProfileMinerArtisan);

        // SupportHealer (11)
        Register("Philip", ProfileSupportHealer);
        Register("Faye", ProfileSupportHealer);
        Register("Paula", ProfileSupportHealer);
        Register("Flor", ProfileSupportHealer);
        Register("Pika", ProfileSupportHealer);
        Register("Lola", ProfileSupportHealer);
        Register("Naomi", ProfileSupportHealer);
        Register("Richard", ProfileSupportHealer);
        Register("Bert", ProfileSupportHealer);
        Register("Olga", ProfileSupportHealer);
        Register("Alissa", ProfileSupportHealer);

        // TechnicalArcane (6)
        Register("Kenneth", ProfileTechnicalArcane);
        Register("Maddie", ProfileTechnicalArcane);
        Register("Malaya", ProfileTechnicalArcane);
        Register("Raeriyala", ProfileTechnicalArcane);
        Register("Bliss", ProfileTechnicalArcane);
        Register("Sari", ProfileTechnicalArcane);

        // CautiousFortune (8)
        Register("Ysabelle", ProfileCautiousFortune);
        Register("Kiarra", ProfileCautiousFortune);
        Register("Sean", ProfileCautiousFortune);
        Register("Maive", ProfileCautiousFortune);
        Register("Sonny", ProfileCautiousFortune);
        Register("Lorenzo", ProfileCautiousFortune);
        Register("Shanice", ProfileCautiousFortune);
        Register("Torts", ProfileCautiousFortune);

        // ChildProtected (4)
        Register("Keahi", ProfileChildProtected);
        Register("Trinnie", ProfileChildProtected);
        Register("Yuuma", ProfileChildProtected);
        Register("Louie", ProfileChildProtected);
    }

    private static void Register(string name, CavePersonalityProfile profile)
    {
        _registry[name] = profile;
    }

    /// <summary>
    /// Total of explicitly mapped characters in the registry (Vanilla + SVE + RSV).
    /// </summary>
    public static int ExplicitlyMappedCount => _registry.Count;

    /// <summary>
    /// Resolves the cave personality profile for any NPC.
    /// Priority 1: Child age check -> ChildProtected.
    /// Priority 2: Explicit mapping (Vanilla, SVE, RSV).
    /// Priority 3: Automatic fallback classifier using SDV 1.6 Data/Characters (Manner, SocialAnxiety).
    /// </summary>
    public static CavePersonalityProfile ResolveProfile(NPC? npc)
    {
        if (npc == null)
            return ProfileMinerArtisan;

        // Paso 1: Si es niño en motor físico, siempre ChildProtected (prioridad absoluta)
        if (npc.Age == NPC.child)
            return ProfileChildProtected;

        // Paso 2: Búsqueda en el registro explícito
        if (_registry.TryGetValue(npc.Name, out var registered))
            return registered;

        // Paso 3: Clasificador automático genérico para East Scarp, Sunberry o custom NPCs
        try
        {
            var data = npc.GetData();
            if (data != null)
            {
                if (data.Age == NpcAge.Child)
                    return ProfileChildProtected;

                if (data.Manner == NpcManner.Rude && data.SocialAnxiety == NpcSocialAnxiety.Outgoing)
                    return ProfileVanguard;

                if (data.Manner == NpcManner.Polite && data.SocialAnxiety == NpcSocialAnxiety.Shy)
                    return ProfileSupportHealer;

                if (data.SocialAnxiety == NpcSocialAnxiety.Shy)
                    return ProfileTechnicalArcane;

                if (data.Manner == NpcManner.Polite && data.SocialAnxiety == NpcSocialAnxiety.Outgoing)
                    return ProfileCautiousFortune;

                return ProfileMinerArtisan;
            }
        }
        catch { }

        // Fallback seguro estándar
        return ProfileMinerArtisan;
    }
}
