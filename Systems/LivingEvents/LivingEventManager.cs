namespace ValleyBuddies.Systems.LivingEvents;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buffs;
using ValleyBuddies.Execution;
using ValleyBuddies.Multiplayer;
using ValleyBuddies.Systems.LivingEvents.Events;
using ValleyBuddies.Utilities;

/// <summary>
/// Gestor central de micro-eventos interactivos orgánicos para Valley Buddies (12 eventos).
/// Maneja controles anti-spam diarios, detección de caricias animales, caminatas de aproximación,
/// retratos sincronizados Co-op, buffs temporales y despacho de decisiones.
/// </summary>
public sealed class LivingEventManager
{
    public static LivingEventManager Instance { get; } = new();

    private static readonly string[] AllEventIds =
    {
        "burned_breakfast",
        "kitchen_taste_test",
        "house_bug_scare",
        "shiny_farm_find",
        "rainy_chill_break",
        "mischievous_animal",
        "town_craving",
        "trail_stumble",
        "spontaneous_view",
        "dropped_keepsake",
        "home_snack_craving",
        "farm_work_cheering",
        "firefly_moment",
        "wildflower_crown",
        "street_music_hum",
        "snowball_play",
        "stargazing_wish",
        "kind_umbrella_share"
    };

    public static IReadOnlyList<string> SupportedEventIds => AllEventIds;

    private sealed class PendingWalkApproach
    {
        public string NpcName { get; set; } = string.Empty;
        public long OwnerFarmerId { get; set; }
        public string LocationName { get; set; } = string.Empty;
        public ulong WalkAfterTick { get; set; }
        public ulong TimeoutTick { get; set; }
        public bool WalkEmoteShown { get; set; }
        public Action<NPC, Farmer>? OnArrived { get; set; }
        public NpcJob ScratchJob { get; set; } = new();
    }

    private readonly Dictionary<string, ILivingEvent> _events = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, bool> _knownAnimalPetState = new();
    private readonly Dictionary<string, PendingWalkApproach> _pendingWalkApproaches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(LivingEventPromptMessage Msg, ulong QueuedAtTick)> _deferredClientPrompts = new();

    public LivingEventManager()
    {
        RegisterEvent(BurnedBreakfastEvent.Instance);
        RegisterEvent(KitchenTasteTestEvent.Instance);
        RegisterEvent(HouseBugScareEvent.Instance);
        RegisterEvent(ShinyFarmFindEvent.Instance);
        RegisterEvent(RainyChillBreakEvent.Instance);
        RegisterEvent(MischievousAnimalEvent.Instance);
        RegisterEvent(TownCravingEvent.Instance);
        RegisterEvent(TrailStumbleEvent.Instance);
        RegisterEvent(SpontaneousViewEvent.Instance);
        RegisterEvent(DroppedKeepsakeEvent.Instance);
        RegisterEvent(HomeSnackCravingEvent.Instance);
        RegisterEvent(FarmWorkCheeringEvent.Instance);
        RegisterEvent(FireflyMomentEvent.Instance);
        RegisterEvent(WildflowerCrownEvent.Instance);
        RegisterEvent(StreetMusicHumEvent.Instance);
        RegisterEvent(SnowballPlayEvent.Instance);
        RegisterEvent(StargazingWishEvent.Instance);
        RegisterEvent(KindUmbrellaShareEvent.Instance);
    }

    public void RegisterEvent(ILivingEvent ev)
    {
        if (ev != null)
        {
            _events[ev.Id] = ev;
        }
    }

    public ILivingEvent? GetEvent(string id)
    {
        _events.TryGetValue(id, out var ev);
        return ev;
    }

    public bool IsNpcWalkingInApproach(string npcName)
    {
        return !string.IsNullOrWhiteSpace(npcName) && _pendingWalkApproaches.ContainsKey(npcName);
    }

    public void StartWalkApproach(NPC npc, Farmer owner, int delayTicks, int timeoutTicks, Action<NPC, Farmer> onArrived)
    {
        if (npc == null || owner == null || npc.currentLocation == null) return;

        ulong now = ModEntry.CurrentTick;
        ulong walkAfter = now + (ulong)Math.Max(0, delayTicks);

        _pendingWalkApproaches[npc.Name] = new PendingWalkApproach
        {
            NpcName = npc.Name,
            OwnerFarmerId = owner.UniqueMultiplayerID,
            LocationName = npc.currentLocation.NameOrUniqueName,
            WalkAfterTick = walkAfter,
            TimeoutTick = walkAfter + (ulong)Math.Max(60, timeoutTicks),
            OnArrived = onArrived,
            ScratchJob = new NpcJob
            {
                NpcName = npc.Name,
                Kind = JobKind.ComeHere,
                FarmerId = owner.UniqueMultiplayerID,
                FarmerName = owner.Name,
                CreatedTick = Game1.ticks
            }
        };
    }

    public void Update(ulong gameTick)
    {
        if (!Context.IsWorldReady) return;

        while (_deferredClientPrompts.Count > 0 && Game1.activeClickableMenu == null && !Game1.dialogueUp && !Game1.eventUp)
        {
            var (deferredMsg, queuedAt) = _deferredClientPrompts.Dequeue();
            if (gameTick <= queuedAt + 600)
            {
                DisplayPromptOnClient(deferredMsg);
                break;
            }
        }

        if (_pendingWalkApproaches.Count == 0 || !Context.IsMainPlayer) return;

        foreach (var kv in _pendingWalkApproaches.ToList())
        {
            string key = kv.Key;
            var approach = kv.Value;

            var npc = Game1.getCharacterFromName(approach.NpcName);
            var owner = Game1.GetPlayer(approach.OwnerFarmerId, onlyOnline: false);

            if (npc == null || owner == null || npc.currentLocation == null || owner.currentLocation == null ||
                npc.currentLocation != owner.currentLocation ||
                !string.Equals(npc.currentLocation.NameOrUniqueName, approach.LocationName, StringComparison.OrdinalIgnoreCase))
            {
                _pendingWalkApproaches.Remove(key);
                continue;
            }

            if (gameTick < approach.WalkAfterTick)
            {
                npc.controller = null;
                if (npc.Sprite?.CurrentAnimation == null)
                {
                    npc.Halt();
                }
                continue;
            }

            if (!approach.WalkEmoteShown)
            {
                approach.WalkEmoteShown = true;
                if (!npc.IsEmoting)
                {
                    npc.doEmote(28);
                }
            }

            if (Game1.eventUp ||
                (approach.OwnerFarmerId == Game1.player?.UniqueMultiplayerID && (Game1.activeClickableMenu != null || Game1.dialogueUp)))
            {
                continue;
            }

            float dist = Vector2.Distance(npc.Tile, owner.Tile);
            bool arrived = dist <= 2.2f || gameTick >= approach.TimeoutTick;

            if (arrived)
            {
                var callback = approach.OnArrived;
                _pendingWalkApproaches.Remove(key);

                npc.Halt();
                npc.controller = null;
                npc.faceGeneralDirection(owner.Position);

                callback?.Invoke(npc, owner);
                continue;
            }

            NpcActionExecutor.Instance.Navigator.MoveTowards(approach.ScratchJob, npc, npc.currentLocation, owner.TilePoint, gameTick);
        }
    }

    /// <summary>
    /// Valida mediante ValleyPacingDirector las compuertas de corazones, edad infantil, topes diarios y cooldowns.
    /// </summary>
    public bool CanTriggerNow(string eventId, Farmer? owner, NPC? npc)
    {
        if (!Context.IsMainPlayer || !Context.IsWorldReady) return false;
        if (ModEntry.Config?.EnableLivingMicroEvents == false) return false;
        if (Game1.eventUp || Utility.isFestivalDay() || Game1.weddingToday) return false;

        if (owner == null || npc == null || owner.currentLocation == null || owner.currentLocation != npc.currentLocation)
        {
            return false;
        }

        if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
        {
            if (Game1.activeClickableMenu != null || Game1.dialogueUp || !Context.IsPlayerFree)
            {
                return false;
            }
        }

        return ValleyPacingDirector.Instance.CanTriggerMicroEvent(eventId, owner, npc);
    }

    public void RecordEventTriggered(string eventId, long farmerId = 0)
    {
        long id = farmerId != 0 ? farmerId : (Game1.player?.UniqueMultiplayerID ?? 0);
        ValleyPacingDirector.Instance.RecordMicroEventTriggered(id, eventId);
    }

    public void ResetDaily()
    {
        _knownAnimalPetState.Clear();
        _pendingWalkApproaches.Clear();
        _deferredClientPrompts.Clear();
    }

    public static void GrantStaminaCoopSafe(Farmer owner, NPC npc, float amount)
    {
        if (owner == null || npc == null || amount <= 0f) return;
        if (owner.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
        {
            Game1.player.Stamina = Math.Min(Game1.player.MaxStamina, Game1.player.Stamina + amount);
        }
        else
        {
            ModEntry.MultiplayerSync?.SendSupportHeal(owner.UniqueMultiplayerID, 0, amount, npc.Name);
        }
    }

    public static void GrantBuffCoopSafe(Farmer owner, string buffKind)
    {
        if (owner == null || string.IsNullOrWhiteSpace(buffKind)) return;
        if (ModEntry.MultiplayerSync != null)
        {
            ModEntry.MultiplayerSync.SendLivingEventBuff(owner.UniqueMultiplayerID, buffKind);
        }
        else if (owner.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
        {
            ApplyLocalLivingEventBuff(buffKind);
        }
    }

    public static void GiveItemCoopSafe(Farmer owner, NPC npc, string itemId)
    {
        if (owner == null || npc == null || string.IsNullOrWhiteSpace(itemId)) return;
        var item = ItemRegistry.Create(itemId, 1);
        if (item == null) return;

        if (owner.UniqueMultiplayerID == Game1.player?.UniqueMultiplayerID)
        {
            Game1.player.addItemByMenuIfNecessaryElseHoldUp(item);
            try { Game1.playSound("coin"); } catch { }
        }
        else
        {
            ModEntry.MultiplayerSync?.SendSpouseGift(owner.UniqueMultiplayerID, item.QualifiedItemId, 1, npc.Name);
        }
    }

    /// <summary>
    /// Aplica un Buff real temporal al jugador local tras un micro-evento (suerte de trébol, luciérnagas o impulso de ánimo).
    /// </summary>
    public static void ApplyLocalLivingEventBuff(string buffKind)
    {
        if (Game1.player == null || string.IsNullOrWhiteSpace(buffKind)) return;

        try
        {
            if (buffKind.Equals("luck", StringComparison.OrdinalIgnoreCase))
            {
                var buff = new Buff(
                    id: "ValleyBuddies_CloverWish",
                    displayName: I18n.Get("living.buff.clover_title"),
                    description: I18n.Get("living.buff.clover_desc"),
                    duration: 120_000,
                    iconTexture: Game1.buffsIcons,
                    iconSheetIndex: Game1.buffsIcons != null ? 4 : -1,
                    effects: new BuffEffects { LuckLevel = { 1 } }
                );
                Game1.player.applyBuff(buff);
            }
            else if (buffKind.Equals("firefly_luck", StringComparison.OrdinalIgnoreCase))
            {
                var buff = new Buff(
                    id: "ValleyBuddies_FireflyWish",
                    displayName: I18n.Get("living.buff.firefly_title"),
                    description: I18n.Get("living.buff.firefly_desc"),
                    duration: 120_000,
                    iconTexture: Game1.buffsIcons,
                    iconSheetIndex: Game1.buffsIcons != null ? 4 : -1,
                    effects: new BuffEffects { LuckLevel = { 1 } }
                );
                Game1.player.applyBuff(buff);
            }
            else if (buffKind.Equals("speed", StringComparison.OrdinalIgnoreCase))
            {
                var buff = new Buff(
                    id: "ValleyBuddies_CheerBoost",
                    displayName: I18n.Get("living.buff.cheer_title"),
                    description: I18n.Get("living.buff.cheer_desc"),
                    duration: 120_000,
                    iconTexture: Game1.buffsIcons,
                    iconSheetIndex: Game1.buffsIcons != null ? 9 : -1,
                    effects: new BuffEffects { Speed = { 1 } }
                );
                Game1.player.applyBuff(buff);
            }
            else if (buffKind.Equals("elliott_muse", StringComparison.OrdinalIgnoreCase))
            {
                var buff = new Buff(
                    id: "ValleyBuddies_ElliottMuse",
                    displayName: Utilities.I18n.Get("easter_egg.elliott_buff_title"),
                    description: Utilities.I18n.Get("easter_egg.elliott_buff_desc"),
                    duration: 60_000,
                    effects: new BuffEffects { LuckLevel = { 1 } }
                );
                Game1.player.applyBuff(buff);
            }
            else if (buffKind.Equals("snow_speed", StringComparison.OrdinalIgnoreCase))
            {
                var buff = new Buff(
                    id: "ValleyBuddies_SnowSpirit",
                    displayName: I18n.Get("living.buff.snow_title"),
                    description: I18n.Get("living.buff.snow_desc"),
                    duration: 120_000,
                    iconTexture: Game1.buffsIcons,
                    iconSheetIndex: Game1.buffsIcons != null ? 9 : -1,
                    effects: new BuffEffects { Speed = { 1 } }
                );
                Game1.player.applyBuff(buff);
            }
        }
        catch { }
    }

    /// <summary>
    /// Detecta en Farm o AnimalHouse si el dueño acaba de acariciar un animal cerca del acompañante (5%).
    /// </summary>
    public bool TryDetectRecentAnimalPetting(NPC npc, Farmer owner, GameLocation loc)
    {
        if (!Context.IsMainPlayer || ModEntry.Config?.EnableLivingMicroEvents == false || npc == null || owner == null) return false;
        if (loc is not Farm && loc is not AnimalHouse) return false;

        var animals = loc is Farm f ? f.animals.Values : ((AnimalHouse)loc).animals.Values;
        FarmAnimal? justPettedNearOwner = null;

        foreach (var animal in animals)
        {
            if (animal == null) continue;
            long id = animal.myID.Value;
            bool nowPet = animal.wasPet.Value;

            if (!_knownAnimalPetState.TryGetValue(id, out bool prevPet))
            {
                _knownAnimalPetState[id] = nowPet;
                continue;
            }

            if (!prevPet && nowPet)
            {
                _knownAnimalPetState[id] = true;
                if (justPettedNearOwner == null &&
                    Vector2.Distance(animal.Tile, owner.Tile) <= 4.5f &&
                    Vector2.Distance(npc.Tile, owner.Tile) <= 6.0f)
                {
                    justPettedNearOwner = animal;
                }
            }
        }

        if (justPettedNearOwner != null &&
            CanTriggerNow("mischievous_animal", owner, npc) &&
            Game1.random.NextDouble() < 0.05)
        {
            MischievousAnimalEvent.Instance.TriggerAnimalMischief(npc, owner, justPettedNearOwner);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Presenta retrato introductorio (opcional) seguido de diálogo interactivo al jugador dueño (localmente o vía Co-op).
    /// </summary>
    public void TriggerPrompt(
        string eventId,
        NPC npc,
        Farmer owner,
        string promptKey,
        string optionAId,
        string optionAKey,
        string optionBId,
        string optionBKey,
        string? itemDisplayName = null,
        string? introKey = null,
        string? emotionTag = null)
    {
        RecordEventTriggered(eventId, owner?.UniqueMultiplayerID ?? 0);
        DispatchPromptInternal(eventId, npc, owner!, promptKey, optionAId, optionAKey, optionBId, optionBKey, itemDisplayName, introKey, emotionTag);
    }

    /// <summary>
    /// Presenta retrato introductorio + pregunta al jugador dueño sin consumir límite diario de micro-eventos (invitaciones inversas).
    /// </summary>
    public void TriggerDirectPrompt(
        string eventId,
        NPC npc,
        Farmer owner,
        string promptKey,
        string optionAId,
        string optionAKey,
        string optionBId,
        string optionBKey,
        string? itemDisplayName = null,
        string? introKey = null,
        string? emotionTag = null)
    {
        DispatchPromptInternal(eventId, npc, owner, promptKey, optionAId, optionAKey, optionBId, optionBKey, itemDisplayName, introKey, emotionTag);
    }

    private void DispatchPromptInternal(
        string eventId,
        NPC npc,
        Farmer owner,
        string promptKey,
        string optionAId,
        string optionAKey,
        string optionBId,
        string optionBKey,
        string? itemDisplayName,
        string? introKey,
        string? emotionTag)
    {
        if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
        {
            var tokens = new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName, itemName = itemDisplayName ?? string.Empty };
            string promptText = I18n.Get(promptKey, tokens);
            string optAText = I18n.Get(optionAKey, tokens);
            string optBText = I18n.Get(optionBKey, tokens);

            var responses = new Response[]
            {
                new(optionAId, optAText),
                new(optionBId, optBText)
            };

            if (!string.IsNullOrWhiteSpace(introKey))
            {
                string introText = I18n.Get(introKey, tokens);
                PortraitDialogueHelper.ShowPortraitThenQuestion(
                    npc,
                    introText,
                    emotionTag ?? string.Empty,
                    promptText,
                    responses,
                    (who, answer) => HandleChoice(eventId, npc, who, answer)
                );
            }
            else
            {
                Game1.currentLocation?.createQuestionDialogue(promptText, responses, (who, answer) =>
                {
                    Game1.objectDialoguePortraitPerson = null;
                    HandleChoice(eventId, npc, who, answer);
                }, npc);
            }
        }
        else
        {
            var msg = new LivingEventPromptMessage
            {
                TargetFarmerId = owner.UniqueMultiplayerID,
                NpcName = npc.Name,
                EventId = eventId,
                IntroKey = introKey ?? string.Empty,
                EmotionTag = emotionTag ?? string.Empty,
                PromptKey = promptKey,
                OptionAId = optionAId,
                OptionAKey = optionAKey,
                OptionBId = optionBId,
                OptionBKey = optionBKey,
                ItemDisplayName = itemDisplayName ?? string.Empty
            };
            ModEntry.MultiplayerSync?.SendLivingEventPrompt(msg);
        }
    }

    /// <summary>
    /// Muestra un diálogo de reacción con retrato expresivo en la pantalla del dueño (local o Farmhand remoto).
    /// </summary>
    public void ShowReactionDialogue(NPC npc, Farmer owner, string textKey, string emotionTag = "", string? itemDisplayName = null)
    {
        if (npc == null || owner == null || string.IsNullOrWhiteSpace(textKey)) return;

        if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
        {
            string text = I18n.Get(textKey, new
            {
                playerName = owner.displayName ?? owner.Name,
                npcName = npc.displayName,
                itemName = itemDisplayName ?? string.Empty
            });
            PortraitDialogueHelper.ShowPortraitDialogue(npc, text, emotionTag);
        }
        else
        {
            ModEntry.MultiplayerSync?.SendPortraitDialogue(new PortraitDialogueMessage
            {
                TargetFarmerId = owner.UniqueMultiplayerID,
                NpcName = npc.Name,
                TextKey = textKey,
                EmotionTag = emotionTag,
                ItemDisplayName = itemDisplayName ?? string.Empty
            });
        }
    }

    /// <summary>
    /// Ejecutado en el Farmhand receptor para mostrar primero retrato expresivo y luego la pregunta Sí/No.
    /// </summary>
    public void DisplayPromptOnClient(LivingEventPromptMessage msg)
    {
        if (msg == null) return;
        if (Game1.activeClickableMenu != null || Game1.dialogueUp || Game1.eventUp)
        {
            if (_deferredClientPrompts.Count < 8)
                _deferredClientPrompts.Enqueue((msg, ModEntry.CurrentTick));
            return;
        }

        NPC? npc = Game1.getCharacterFromName(msg.NpcName);
        string npcDisplayName = npc?.displayName ?? msg.NpcName;
        var tokens = new { playerName = Game1.player.displayName ?? Game1.player.Name, npcName = npcDisplayName, itemName = msg.ItemDisplayName };

        string promptText = I18n.Get(msg.PromptKey, tokens);
        string optAText = I18n.Get(msg.OptionAKey, tokens);
        string optBText = I18n.Get(msg.OptionBKey, tokens);

        var responses = new Response[]
        {
            new(msg.OptionAId, optAText),
            new(msg.OptionBId, optBText)
        };

        void SendChoiceBack(Farmer who, string answer)
        {
            ModEntry.MultiplayerSync?.SendLivingEventChoice(new LivingEventChoiceMessage
            {
                RequesterFarmerId = Game1.player.UniqueMultiplayerID,
                NpcName = msg.NpcName,
                EventId = msg.EventId,
                ChoiceId = answer
            });
        }

        if (npc != null && !string.IsNullOrWhiteSpace(msg.IntroKey))
        {
            string introText = I18n.Get(msg.IntroKey, tokens);
            PortraitDialogueHelper.ShowPortraitThenQuestion(npc, introText, msg.EmotionTag, promptText, responses, SendChoiceBack);
        }
        else
        {
            Game1.currentLocation?.createQuestionDialogue(promptText, responses, (who, answer) =>
            {
                Game1.objectDialoguePortraitPerson = null;
                SendChoiceBack(who, answer);
            }, npc);
        }
    }

    /// <summary>
    /// Ejecutado en el Farmhand receptor para mostrar un retrato expresivo de reacción.
    /// </summary>
    public void DisplayPortraitDialogueOnClient(PortraitDialogueMessage msg)
    {
        if (msg == null || Game1.eventUp) return;

        NPC? npc = Game1.getCharacterFromName(msg.NpcName);
        if (npc == null) return;

        string text = I18n.Get(msg.TextKey, new
        {
            playerName = Game1.player.displayName ?? Game1.player.Name,
            npcName = npc.displayName,
            itemName = msg.ItemDisplayName
        });

        PortraitDialogueHelper.ShowPortraitDialogue(npc, text, msg.EmotionTag);
    }

    /// <summary>
    /// Despacha la decisión tomada por el jugador al evento correspondiente.
    /// </summary>
    public void HandleChoice(string eventId, NPC npc, Farmer owner, string choiceId)
    {
        if (string.Equals(eventId, "outing_invite", StringComparison.OrdinalIgnoreCase))
        {
            NpcOutingInvitationService.Instance.HandlePromptChoice(npc, owner, choiceId);
            return;
        }

        if (string.Equals(eventId, "spouse_date_invite", StringComparison.OrdinalIgnoreCase))
        {
            NpcOutingInvitationService.Instance.HandleSpouseDateChoice(npc, owner, choiceId);
            return;
        }

        if (string.Equals(eventId, "favorite_spot_cinematic", StringComparison.OrdinalIgnoreCase))
        {
            HandleFavoriteSpotChoice(npc, owner, choiceId);
            return;
        }

        if (_events.TryGetValue(eventId, out var ev))
        {
            ev.OnPlayerChoice(npc, owner, choiceId);
        }
    }

    private void HandleFavoriteSpotChoice(NPC npc, Farmer owner, string choiceId)
    {
        if (npc == null || owner == null) return;

        if (choiceId.Equals("watch", StringComparison.OrdinalIgnoreCase))
        {
            ValleyPacingDirector.Instance.RecordFavoriteSpotCinematicTaken(owner.UniqueMultiplayerID, Game1.Date.TotalDays);
            var loc = owner.currentLocation;
            if (loc == null) return;

            string scene = CinematicDirectorService.ResolveSceneType(loc);
            if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
            {
                CinematicDirectorService.Instance.TryStartCinematic(loc, npc, owner, scene, fromOutingInvite: false, ignoreDailyCooldown: false);
            }
            else
            {
                ModEntry.MultiplayerSync?.SendStartCinematicOuting(new StartCinematicOutingMessage
                {
                    TargetFarmerId = owner.UniqueMultiplayerID,
                    NpcName = npc.Name,
                    SceneType = scene,
                    FromOutingInvite = false
                });
            }
        }
        else
        {
            ValleyPacingDirector.Instance.MarkFavoriteSpotOfferedToday(owner.UniqueMultiplayerID);
            npc.doEmote(32);
            ShowReactionDialogue(npc, owner, "cinematic.fav_spot.skipped", "$h");
        }
    }

    /// <summary>
    /// Procesa la decisión devuelta por un Farmhand remoto en el Host.
    /// </summary>
    public void HandleChoiceFromRemote(LivingEventChoiceMessage msg)
    {
        if (msg == null) return;
        var owner = Game1.GetPlayer(msg.RequesterFarmerId, false) ?? Game1.player;
        var npc = Game1.getCharacterFromName(msg.NpcName);

        if (npc != null && owner != null)
        {
            HandleChoice(msg.EventId, npc, owner, msg.ChoiceId);
        }
    }

    /// <summary>
    /// Dispara inmediatamente un evento para pruebas directas desde la consola (vb_test_event).
    /// </summary>
    public bool TestEvent(string eventId, string? npcName, out string statusMessage)
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer)
        {
            statusMessage = "World must be ready and you must be the host to test living events.";
            return false;
        }

        NPC? targetNpc = null;
        if (!string.IsNullOrWhiteSpace(npcName))
        {
            targetNpc = Game1.getCharacterFromName(npcName);
        }

        if (targetNpc == null)
        {
            string? spouseName = Game1.player.spouse;
            if (!string.IsNullOrWhiteSpace(spouseName))
            {
                targetNpc = Game1.getCharacterFromName(spouseName);
            }
        }

        if (targetNpc == null)
        {
            targetNpc = Game1.currentLocation?.characters.FirstOrDefault(c => c.IsVillager);
        }

        if (targetNpc == null)
        {
            statusMessage = $"Could not find any suitable NPC to test event '{eventId}'.";
            return false;
        }

        Farmer owner = Game1.player;

        switch (eventId.ToLowerInvariant())
        {
            case "burned_breakfast":
                var loc = owner.currentLocation;
                if (loc != null)
                {
                    float dist = Vector2.Distance(targetNpc.Tile, owner.Tile);
                    if (targetNpc.currentLocation != loc || dist < 4.0f || dist > 12.0f)
                    {
                        Point? spawnTile = FindNearbyWalkableTileForTest(loc, owner.TilePoint, targetNpc);
                        if (spawnTile.HasValue)
                        {
                            if (targetNpc.currentLocation != loc)
                            {
                                Game1.warpCharacter(targetNpc, loc, spawnTile.Value.ToVector2());
                            }
                            else
                            {
                                targetNpc.setTileLocation(spawnTile.Value.ToVector2());
                            }
                            targetNpc.Halt();
                            targetNpc.controller = null;
                            targetNpc.faceDirection(0);
                        }
                    }
                }

                Point stoveSpot = new Point(targetNpc.TilePoint.X, Math.Max(0, targetNpc.TilePoint.Y - 1));
                BurnedBreakfastEvent.Instance.TriggerBurnWithWalk(targetNpc, owner, stoveSpot);
                statusMessage = $"Triggered 'burned_breakfast' walk sequence with {targetNpc.Name}.";
                return true;

            case "kitchen_taste_test":
                var dish = ItemRegistry.Create("(O)240", 1); // Farmer's Lunch
                KitchenTasteTestEvent.Instance.TriggerTasteTest(targetNpc, owner, dish);
                statusMessage = $"Triggered 'kitchen_taste_test' on {targetNpc.Name}.";
                return true;

            case "house_bug_scare":
                HouseBugScareEvent.Instance.TriggerBugScare(targetNpc, owner);
                statusMessage = $"Triggered 'house_bug_scare' on {targetNpc.Name}.";
                return true;

            case "shiny_farm_find":
                ShinyFarmFindEvent.Instance.TriggerFind(targetNpc, owner);
                statusMessage = $"Triggered 'shiny_farm_find' on {targetNpc.Name}.";
                return true;

            case "rainy_chill_break":
                RainyChillBreakEvent.Instance.TriggerRainBreak(targetNpc, owner);
                statusMessage = $"Triggered 'rainy_chill_break' on {targetNpc.Name}.";
                return true;

            case "mischievous_animal":
                MischievousAnimalEvent.Instance.TriggerAnimalMischief(targetNpc, owner);
                statusMessage = $"Triggered 'mischievous_animal' on {targetNpc.Name}.";
                return true;

            case "town_craving":
                TownCravingEvent.Instance.TriggerCraving(targetNpc, owner);
                statusMessage = $"Triggered 'town_craving' on {targetNpc.Name}.";
                return true;

            case "trail_stumble":
                TrailStumbleEvent.Instance.TriggerStumble(targetNpc, owner);
                statusMessage = $"Triggered 'trail_stumble' on {targetNpc.Name}.";
                return true;

            case "spontaneous_view":
                SpontaneousViewEvent.Instance.TriggerView(targetNpc, owner);
                statusMessage = $"Triggered 'spontaneous_view' on {targetNpc.Name}.";
                return true;

            case "dropped_keepsake":
                DroppedKeepsakeEvent.Instance.TriggerKeepsake(targetNpc, owner);
                statusMessage = $"Triggered 'dropped_keepsake' on {targetNpc.Name}.";
                return true;

            case "home_snack_craving":
                HomeSnackCravingEvent.Instance.TriggerSnackCraving(targetNpc, owner);
                statusMessage = $"Triggered 'home_snack_craving' on {targetNpc.Name}.";
                return true;

            case "farm_work_cheering":
                FarmWorkCheeringEvent.Instance.TriggerCheering(targetNpc, owner);
                statusMessage = $"Triggered 'farm_work_cheering' on {targetNpc.Name}.";
                return true;

            case "firefly_moment":
                FireflyMomentEvent.Instance.TriggerFireflies(targetNpc, owner);
                statusMessage = $"Triggered 'firefly_moment' on {targetNpc.Name}.";
                return true;

            case "wildflower_crown":
                WildflowerCrownEvent.Instance.TriggerCrown(targetNpc, owner);
                statusMessage = $"Triggered 'wildflower_crown' on {targetNpc.Name}.";
                return true;

            case "street_music_hum":
                StreetMusicHumEvent.Instance.TriggerMusic(targetNpc, owner);
                statusMessage = $"Triggered 'street_music_hum' on {targetNpc.Name}.";
                return true;

            case "snowball_play":
                SnowballPlayEvent.Instance.TriggerSnowball(targetNpc, owner);
                statusMessage = $"Triggered 'snowball_play' on {targetNpc.Name}.";
                return true;

            case "stargazing_wish":
                StargazingWishEvent.Instance.TriggerStar(targetNpc, owner);
                statusMessage = $"Triggered 'stargazing_wish' on {targetNpc.Name}.";
                return true;

            case "kind_umbrella_share":
                KindUmbrellaShareEvent.Instance.TriggerUmbrella(targetNpc, owner);
                statusMessage = $"Triggered 'kind_umbrella_share' on {targetNpc.Name}.";
                return true;

            default:
                statusMessage = $"Unknown living event '{eventId}'. Valid ({AllEventIds.Length}): {string.Join(", ", AllEventIds)}.";
                return false;
        }
    }

    private static Point? FindNearbyWalkableTileForTest(GameLocation loc, Point center, NPC npc)
    {
        for (int radius = 5; radius <= 7; radius++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    if (Math.Abs(dx) + Math.Abs(dy) < 4) continue;
                    Point cand = new Point(center.X + dx, center.Y + dy);
                    if (NpcActionExecutor.Instance.Navigator.IsTileWalkable(loc, cand, npc))
                    {
                        return cand;
                    }
                }
            }
        }
        return null;
    }
}
