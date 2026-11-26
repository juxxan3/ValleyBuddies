using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ValleyBuddies.Execution;

namespace ValleyBuddies.Actions;

/// <summary>Maintains the registry of all physical actions and builds prompt text.</summary>
public sealed class ActionRegistry
{
    private readonly Dictionary<string, IPhysicalAction> _actions =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(IPhysicalAction action)
    {
        if (action != null && !string.IsNullOrWhiteSpace(action.Name))
            _actions[action.Name] = action;
    }

    public IEnumerable<IPhysicalAction> EnabledActions(ActionContext context, string npcName, int hearts) =>
        _actions.Values.Where(a => a.IsEnabled(context) && hearts >= a.GetMinHearts(context));

    public bool TryGet(string name, out IPhysicalAction? action)
    {
        if (_actions.TryGetValue(name, out action))
            return true;

        string normalized = name.ToLowerInvariant().Trim().Replace("-", "_").Replace(" ", "_");
        if (_actions.TryGetValue(normalized, out action))
            return true;

        string? alias = normalized switch
        {
            // Movement (8)
            "follow" or "follow_player" or "sigueme" or "seguir" or "ven_conmigo" or "come_with_me" => "follow_player",
            "stop" or "halt" or "parar" or "detener" or "dejar_de_seguir" or "adios" => "stop_following",
            "come" or "come_here" or "ven" or "ven_aqui" or "acercate" => "come_here",
            "goto" or "go_to" or "go_to_location" or "ir_a" or "viajar" or "caminar_a" => "go_to_location",
            "visit" or "visit_farm" or "visit_player_farm" or "visitar" or "visitar_granja" or "granja" => "visit_player_farm",
            "sit" or "rest" or "sit_or_rest" or "sentar" or "sentarse" or "descansar" => "sit_or_rest",
            "wait" or "guard" or "guard_here" or "guard_or_wait_here" or "esperar" or "espera_aqui" => "guard_or_wait_here",
            "escort" or "escort_home" or "acompaniar_casa" or "acompanar_casa" or "llevar_casa" => "escort_home",

            // Farm Work Existing (10)
            "water" or "water_crops" or "regar" or "regar_plantas" or "regar_cultivos" => "water_crops",
            "harvest" or "harvest_crops" or "crops" or "cosechar" or "recoger_cultivos" => "harvest_crops",
            "forage" or "gather_forage" or "harvest_forage" or "recolectar" or "forrajear" => "harvest_forage",
            "debris" or "clear" or "clean" or "weed" or "clear_debris" or "maleza" or "limpiar" => "clear_debris",
            "pet" or "pet_animal" or "pet_animals" or "acariciar" or "mimar_animales" => "pet_animals",
            "mine" or "mine_rock" or "break_rock" or "mine_nearby_rock" or "picar" or "piedra" => "mine_nearby_rock",
            "wood" or "chop" or "cut_wood" or "gather_wood" or "chop_wood" or "leña" or "lena" => "chop_wood",
            "trees" or "chop_trees" or "talar" or "talar_arboles" or "cortar_arboles" => "chop_trees",
            "chest" or "take_chest" or "loot_chest" or "take_from_chest" or "cofre" or "tomar_cofre" => "take_from_chest",
            "fetch" or "fetch_chest" or "fetch_from_chest" or "traer_cofre" or "buscar_cofre" => "fetch_from_chest",

            // Farm Work New v2.0 (6)
            "feed" or "feed_animals" or "alimentar" or "alimentar_animales" or "dar_heno" => "feed_animals",
            "collect_animals" or "collect_animal_products" or "recoger_huevos" or "productos_animales" => "collect_animal_products",
            "pet_bowl" or "water_bowl" or "water_pet_bowl" or "agua_mascota" or "cuenco_mascota" => "water_pet_bowl",
            "machines" or "collect_machines" or "recoger_maquinas" or "barriles" or "artesanias" => "collect_machines",
            "plant" or "seed" or "plant_seeds" or "sembrar" or "plantar" => "plant_seeds",
            "repair" or "fix_fences" or "repair_fences" or "reparar_vallas" or "arreglar_cercas" => "repair_fences",

            // Social Existing (10)
            "give" or "gift" or "give_item" or "regalar" or "dar_objeto" => "give_item",
            "emote" or "show_emote" or "emocion" or "gesto" => "show_emote",
            "face" or "look_at_player" or "face_player" or "mirar" => "face_player",
            "anim" or "animation" or "do_animation" or "animacion" => "do_animation",
            "hug" or "celebrate" or "hug_or_celebrate" or "abrazar" or "abrazo" or "celebrar" => "hug_or_celebrate",
            "meet" or "date" or "meet_at_time" or "cita" or "quedar" or "reunirse" => "meet_at_time",
            "break" or "coffee_break" or "take_break" or "descanso" or "tomar_cafe" => "take_break",
            "photo" or "picture" or "take_photo" or "foto" or "tomar_foto" or "selfie" => "take_photo",
            "picnic" or "merienda" or "comer_juntos" => "picnic",
            "admire" or "admire_farm" or "admirar" or "admirar_granja" or "ver_flores" => "admire_farm",

            // Adventure & Home & Social New v2.0 (8)
            "combat" or "fight" or "defend" or "combat_assist" or "pelear" or "ayudar_combate" => "combat_assist",
            "fish" or "fishing" or "fish_together" or "pescar" or "pescar_juntos" => "fish_together",
            "scout" or "find_ladder" or "scout_mines" or "buscar_escalera" or "explorar_minas" => "scout_mines",
            "camp" or "setup_camp" or "camping" or "acampar" or "campamento" => "setup_camp",
            "cook" or "cook_meal" or "cocinar" or "preparar_comida" => "cook_meal",
            "music" or "play_music" or "tocar_musica" or "instrumento" => "play_music",
            "dance" or "dance_together" or "bailar" or "bailar_juntos" => "dance_together",
            "stargaze" or "stars" or "ver_estrellas" or "mirar_estrellas" => "stargaze",
            "mail" or "letter" or "send_letter" or "send_letter_tomorrow" or "enviar_carta" or "escribir_carta" => "send_letter_tomorrow",
            _ => null
        };

        return alias != null && _actions.TryGetValue(alias, out action);
    }

    /// <summary>Build the full action catalog section of the prompt (called per conversation).</summary>
    public string BuildCatalog(ActionContext context, string npcName, int hearts)
    {
        var enabled = EnabledActions(context, npcName, hearts)
            .OrderBy(a => a.Category)
            .ThenBy(a => a.Name)
            .ToList();
        if (enabled.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("Available physical actions (use inside the npc_action JSON array):");
        foreach (var a in enabled)
            sb.AppendLine($"  - {a.Name} | Schema: {a.PromptSchema} | When: {a.PromptDescription}");
        return sb.ToString();
    }
}
