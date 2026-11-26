using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleyBuddies.Execution;
using ValleyBuddies.Systems;

namespace ValleyBuddies.Multiplayer;

public sealed class ExecuteNpcActionDto
{
    public long OwnerPlayerId { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string LocationName { get; set; } = string.Empty;
    public int PlayerTileX { get; set; }
    public int PlayerTileY { get; set; }
}

public sealed class SyncCompanionBuffDto
{
    public string NpcName { get; set; } = string.Empty;
    public bool Apply { get; set; }
}

public sealed class SyncTextAboveHeadDto
{
    public string NpcName { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int DurationMs { get; set; } = 3500;
}

public sealed class SyncDynamicMailDto
{
    public Dictionary<string, string> Entries { get; set; } = new();
}

public sealed class QueueMailForTomorrowDto
{
    public string MailId { get; set; } = string.Empty;
}

public sealed class OfflineInviteRequestMessage
{
    public string NpcName { get; set; } = string.Empty;
    public long RequesterFarmerId { get; set; }
}

public sealed class OfflineInviteResultMessage
{
    public string NpcName { get; set; } = string.Empty;
    public long RequesterFarmerId { get; set; }
    public bool Accepted { get; set; }
    public bool Success { get => Accepted; set => Accepted = value; }
    public string? RejectionReasonKey { get; set; }
    public string? ReasonKey { get => RejectionReasonKey; set => RejectionReasonKey = value; }
    public long OwnerFarmerId { get; set; }
    public string? CurrentOwnerName { get; set; }
    public string? OwnerFarmerName { get => CurrentOwnerName; set => CurrentOwnerName = value; }
}

public sealed class NightActionRequestMessage
{
    public string NpcName { get; set; } = string.Empty;
    public long RequesterFarmerId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? LocationName { get; set; }
}

public sealed class NightStateSyncMessage
{
    public string NpcName { get; set; } = string.Empty;
    public long OwnerFarmerId { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public int NightState { get; set; }
    public int EveningResolution { get; set; }
    public bool IsRestingInPlace { get; set; }
    public bool IsSettledInsideHouseForNight { get; set; }
    public bool IsSleepingInBed { get; set; }
    public Vector2? TargetBedPosition { get; set; }
    public int BedTileX { get; set; }
    public int BedTileY { get; set; }
    public Vector2? FrozenHousePosition { get; set; }
    public bool HasActiveTempCamp { get; set; }
}

public sealed class CompanionSupportHealMessage
{
    public long TargetFarmerId { get; set; }
    public int HpHeal { get; set; }
    public float StaminaHeal { get; set; }
    public string NpcName { get; set; } = string.Empty;
}

public sealed class CaveStanceSyncMessage
{
    public string NpcName { get; set; } = string.Empty;
    public long RequesterFarmerId { get; set; }
    public int Stance { get; set; }
}

public sealed class PrivateNoticeDto
{
    public string Text { get; set; } = string.Empty;
    public bool IsError { get; set; } = true;
    public int HudType { get; set; } = HUDMessage.error_type;
}

public sealed class SpouseGiftDeliveryMessage
{
    public long TargetFarmerId { get; set; }
    public string ItemQualifiedId { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public string NpcName { get; set; } = string.Empty;
}

public sealed class ToolVisualEffectMessage
{
    public string LocationName { get; set; } = string.Empty;
    public string NpcName { get; set; } = string.Empty;
    public string EffectKind { get; set; } = string.Empty;
    public int TargetTileX { get; set; }
    public int TargetTileY { get; set; }
    public bool ExtraFlag { get; set; }
    public long TargetFarmerId { get; set; }
}

public sealed class LivingEventPromptMessage
{
    public long TargetFarmerId { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string IntroKey { get; set; } = string.Empty;
    public string EmotionTag { get; set; } = string.Empty;
    public string PromptKey { get; set; } = string.Empty;
    public string OptionAId { get; set; } = string.Empty;
    public string OptionAKey { get; set; } = string.Empty;
    public string OptionBId { get; set; } = string.Empty;
    public string OptionBKey { get; set; } = string.Empty;
    public string ItemDisplayName { get; set; } = string.Empty;
}

public sealed class LivingEventChoiceMessage
{
    public long RequesterFarmerId { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public string EventId { get; set; } = string.Empty;
    public string ChoiceId { get; set; } = string.Empty;
}

public sealed class PortraitDialogueMessage
{
    public long TargetFarmerId { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public string TextKey { get; set; } = string.Empty;
    public string EmotionTag { get; set; } = string.Empty;
    public string ItemDisplayName { get; set; } = string.Empty;
}

public sealed class StartCinematicOutingMessage
{
    public long TargetFarmerId { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public string SceneType { get; set; } = string.Empty;
    public bool FromOutingInvite { get; set; }
}

public sealed class CinematicCompletedMessage
{
    public long FarmerId { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public string SceneType { get; set; } = string.Empty;
    public bool FromOutingInvite { get; set; }
}

public sealed class PicnicSceneSyncMessage
{
    public bool Active { get; set; }
    public string LocationName { get; set; } = string.Empty;
    public int OriginTileX { get; set; }
    public int OriginTileY { get; set; }
    public string NpcName { get; set; } = string.Empty;
    public long OwnerFarmerId { get; set; }
    public string[] PropItemIds { get; set; } = Array.Empty<string>();
}

public sealed class LivingEventBuffMessage
{
    public long TargetFarmerId { get; set; }
    public string BuffKind { get; set; } = string.Empty;
}

public sealed class BanterChatMessage
{
    public string SpeakerInternalName { get; set; } = string.Empty;
    public string SpeakerDisplayName { get; set; } = string.Empty;
    public string LocationName { get; set; } = string.Empty;
    public int TileX { get; set; }
    public int TileY { get; set; }
    public string FallbackText { get; set; } = string.Empty;
    public string EnText { get; set; } = string.Empty;
    public string EsText { get; set; } = string.Empty;
    public bool IsCompanionChat { get; set; }
    public bool IsPersonalChatter { get; set; }
    public float MaxDistanceTiles { get; set; } = 15.0f;
}

public sealed class MultiplayerSyncService
{
    public const string MsgExecuteAction        = "ExecuteNpcAction";
    public const string MsgSyncBuff             = "SyncCompanionBuff";
    public const string MsgSyncText             = "SyncTextAboveHead";
    public const string MsgSyncMail             = "SyncDynamicMail";
    public const string MsgQueueMail            = "QueueMailForTomorrow";
    public const string MsgOfflineInviteRequest = "OfflineInviteRequest";
    public const string MsgOfflineInviteResult  = "OfflineInviteResult";
    public const string MsgNightActionRequest   = "NightActionRequest";
    public const string MsgNightStateSync       = "NightStateSync";
    public const string MsgSupportHeal          = "CompanionSupportHeal";
    public const string MsgCaveStanceSync       = "CaveStanceSync";
    public const string MsgPrivateNotice        = "PrivateNotice";
    public const string MsgSpouseGiftDelivery   = "SpouseGiftDelivery";
    public const string MsgToolVisualEffect     = "ToolVisualEffect";
    public const string MsgLivingEventPrompt    = "LivingEventPrompt";
    public const string MsgLivingEventChoice    = "LivingEventChoice";
    public const string MsgPortraitDialogue     = "PortraitDialogue";
    public const string MsgStartCinematicOuting = "StartCinematicOuting";
    public const string MsgCinematicCompleted   = "CinematicCompleted";
    public const string MsgPicnicSceneSync      = "PicnicSceneSync";
    public const string MsgLivingEventBuff      = "LivingEventBuff";
    public const string MsgSyncBanterChat       = "SyncBanterChat";

    public static Farmer ResolveOwner(long ownerFarmerId)
    {
        if (ownerFarmerId != 0)
        {
            var online = Game1.GetPlayer(ownerFarmerId, onlyOnline: true);
            if (online != null) return online;
            var any = Game1.GetPlayer(ownerFarmerId, onlyOnline: false);
            if (any != null) return any;
        }
        return Game1.player ?? Game1.MasterPlayer;
    }

    private readonly IModHelper _helper;
    private readonly IManifest _manifest;
    private readonly CompanionSynergyManager _synergy;
    private readonly DynamicMailService _mailService;
    private readonly Action<ExecuteNpcActionDto> _onHostReceiveAction;

    public MultiplayerSyncService(
        IModHelper helper,
        IManifest manifest,
        CompanionSynergyManager synergy,
        DynamicMailService mailService,
        Action<ExecuteNpcActionDto> onHostReceiveAction)
    {
        _helper = helper;
        _manifest = manifest;
        _synergy = synergy;
        _mailService = mailService;
        _onHostReceiveAction = onHostReceiveAction;
    }

    public void SendActionRequestToHost(ExecuteNpcActionDto dto)
    {
        if (Context.IsMainPlayer)
        {
            _onHostReceiveAction(dto);
            return;
        }

        _helper.Multiplayer.SendMessage(
            dto,
            MsgExecuteAction,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID }
        );
    }

    public void SendOfflineInviteRequest(string npcName, long requesterId)
    {
        if (Context.IsMainPlayer)
        {
            NpcActionExecutor.Instance.ProcessHostOfflineInvite(npcName, requesterId);
            return;
        }

        _helper.Multiplayer.SendMessage(
            new OfflineInviteRequestMessage
            {
                NpcName = npcName,
                RequesterFarmerId = requesterId
            },
            MsgOfflineInviteRequest,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID }
        );
    }

    public void SendOfflineInviteResult(long targetPlayerId, OfflineInviteResultMessage result)
    {
        if (!Context.IsMainPlayer) return;

        if (targetPlayerId == Game1.player.UniqueMultiplayerID)
        {
            NpcActionExecutor.Instance.HandleOfflineInviteResultFromHost(result);
            return;
        }

        _helper.Multiplayer.SendMessage(
            result,
            MsgOfflineInviteResult,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { targetPlayerId }
        );
    }

    public void SendNightActionRequest(string npcName, long requesterId, string action, string? locName)
    {
        if (Context.IsMainPlayer)
        {
            NpcActionExecutor.Instance.ProcessHostNightActionRequest(npcName, requesterId, action, locName);
            return;
        }

        _helper.Multiplayer.SendMessage(
            new NightActionRequestMessage
            {
                NpcName = npcName,
                RequesterFarmerId = requesterId,
                Action = action,
                LocationName = locName
            },
            MsgNightActionRequest,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID }
        );
    }

    public void BroadcastNightState(NightStateSyncMessage msg, long? specificPeerId = null)
    {
        if (!Context.IsMainPlayer || !Context.IsMultiplayer) return;

        _helper.Multiplayer.SendMessage(
            msg,
            MsgNightStateSync,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: specificPeerId.HasValue ? new[] { specificPeerId.Value } : null
        );
    }

    public void SendSupportHeal(long targetFarmerId, int hpHeal, float staminaHeal, string npcName)
    {
        if (!Context.IsMainPlayer || !Context.IsMultiplayer) return;

        _helper.Multiplayer.SendMessage(
            new CompanionSupportHealMessage
            {
                TargetFarmerId = targetFarmerId,
                HpHeal = hpHeal,
                StaminaHeal = staminaHeal,
                NpcName = npcName
            },
            MsgSupportHeal,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { targetFarmerId }
        );
    }

    public void SendCaveStanceSync(string npcName, long requesterId, int stance)
    {
        if (Context.IsMainPlayer)
        {
            NpcActionExecutor.Instance.ProcessHostCaveStanceSync(npcName, requesterId, stance);
            return;
        }

        _helper.Multiplayer.SendMessage(
            new CaveStanceSyncMessage
            {
                NpcName = npcName,
                RequesterFarmerId = requesterId,
                Stance = stance
            },
            MsgCaveStanceSync,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID }
        );
    }

    public void SendPrivateNotice(long targetFarmerId, string text, bool isError = true, int hudType = HUDMessage.error_type)
    {
        if (!Context.IsMainPlayer) return;

        if (targetFarmerId == Game1.player.UniqueMultiplayerID)
        {
            if (isError)
                Game1.showRedMessage(text);
            else
                Game1.addHUDMessage(new HUDMessage(text, hudType));
            return;
        }

        _helper.Multiplayer.SendMessage(
            new PrivateNoticeDto
            {
                Text = text,
                IsError = isError,
                HudType = hudType
            },
            MsgPrivateNotice,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { targetFarmerId }
        );
    }

    public void SendSpouseGift(long targetFarmerId, string itemQualifiedId, int quantity, string npcName)
    {
        if (!Context.IsMultiplayer) return;

        if (targetFarmerId == Game1.player.UniqueMultiplayerID)
        {
            var item = ItemRegistry.Create(itemQualifiedId, quantity);
            if (item != null)
            {
                Game1.player.addItemByMenuIfNecessaryElseHoldUp(item);
                Game1.playSound("coin");
            }
            return;
        }

        _helper.Multiplayer.SendMessage(
            new SpouseGiftDeliveryMessage
            {
                TargetFarmerId = targetFarmerId,
                ItemQualifiedId = itemQualifiedId,
                Quantity = quantity,
                NpcName = npcName
            },
            MsgSpouseGiftDelivery,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { targetFarmerId }
        );
    }

    public void BroadcastToolVisualEffect(string locationName, string npcName, string effectKind, int tileX, int tileY, bool extraFlag = false, long targetFarmerId = 0)
    {
        if (!Context.IsMultiplayer) return;

        _helper.Multiplayer.SendMessage(
            new ToolVisualEffectMessage
            {
                LocationName = locationName,
                NpcName = npcName,
                EffectKind = effectKind,
                TargetTileX = tileX,
                TargetTileY = tileY,
                ExtraFlag = extraFlag,
                TargetFarmerId = targetFarmerId
            },
            MsgToolVisualEffect,
            modIDs: new[] { _manifest.UniqueID }
        );
    }

    public void SyncAuraToOwner(NPC npc, Farmer owner, bool enabled)
    {
        if (!Context.IsMainPlayer || npc == null || owner == null) return;
        if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
        {
            if (enabled) _synergy.RefreshCompanionAura(npc, owner, true);
            else _synergy.RemoveCompanionAura(npc.Name, owner);
            return;
        }

        _helper.Multiplayer.SendMessage(
            new SyncCompanionBuffDto { NpcName = npc.Name, Apply = enabled },
            MsgSyncBuff,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { owner.UniqueMultiplayerID }
        );
    }

    public void RelayNpcSpeechToPeers(NPC npc, string text, int durationMs = 3500)
    {
        if (npc == null || string.IsNullOrWhiteSpace(text) || !Context.IsMultiplayer || npc.currentLocation == null) return;

        _helper.Multiplayer.SendMessage(
            new SyncTextAboveHeadDto
            {
                NpcName = npc.Name,
                LocationName = npc.currentLocation.NameOrUniqueName,
                Text = text,
                DurationMs = durationMs
            },
            MsgSyncText,
            modIDs: new[] { _manifest.UniqueID }
        );
    }

    public void BroadcastNpcSpeech(NPC npc, string text, int durationMs = 3500)
    {
        if (npc == null || string.IsNullOrWhiteSpace(text)) return;
        npc.showTextAboveHead(text, duration: durationMs);
    }

    public void SendBanterChatEcho(
        long targetFarmerId,
        NPC speaker,
        string fallbackText,
        string enText,
        string esText,
        bool isCompanionChat,
        bool isPersonalChatter = false,
        float maxDistanceTiles = 15.0f)
    {
        if (!Context.IsMultiplayer || speaker?.currentLocation == null) return;

        _helper.Multiplayer.SendMessage(
            new BanterChatMessage
            {
                SpeakerInternalName = speaker.Name ?? string.Empty,
                SpeakerDisplayName = speaker.displayName ?? speaker.Name ?? string.Empty,
                LocationName = speaker.currentLocation.NameOrUniqueName ?? string.Empty,
                TileX = speaker.TilePoint.X,
                TileY = speaker.TilePoint.Y,
                FallbackText = fallbackText ?? string.Empty,
                EnText = enText ?? string.Empty,
                EsText = esText ?? string.Empty,
                IsCompanionChat = isCompanionChat,
                IsPersonalChatter = isPersonalChatter,
                MaxDistanceTiles = maxDistanceTiles
            },
            MsgSyncBanterChat,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { targetFarmerId }
        );
    }

    public void BroadcastMailEntries(Dictionary<string, string> entries, long? specificPeerId = null)
    {
        if (!Context.IsMainPlayer || !Context.IsMultiplayer || entries.Count == 0) return;

        _helper.Multiplayer.SendMessage(
            new SyncDynamicMailDto { Entries = new Dictionary<string, string>(entries) },
            MsgSyncMail,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: specificPeerId.HasValue ? new[] { specificPeerId.Value } : null
        );
    }

    public void QueueMailForSpecificPlayer(long targetPlayerId, string mailId)
    {
        var targetFarmer = Game1.GetPlayer(targetPlayerId, onlyOnline: true);
        targetFarmer?.mailForTomorrow.Add(mailId);

        if (targetPlayerId == Game1.player.UniqueMultiplayerID)
        {
            Game1.player.mailForTomorrow.Add(mailId);
            return;
        }

        _helper.Multiplayer.SendMessage(
            new QueueMailForTomorrowDto { MailId = mailId },
            MsgQueueMail,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { targetPlayerId }
        );
    }

    public void SendLivingEventPrompt(LivingEventPromptMessage msg)
    {
        if (msg == null) return;
        _helper.Multiplayer.SendMessage(
            msg,
            MsgLivingEventPrompt,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { msg.TargetFarmerId }
        );
    }

    public void SendLivingEventChoice(LivingEventChoiceMessage msg)
    {
        if (msg == null) return;
        long hostId = Game1.MasterPlayer?.UniqueMultiplayerID ?? 0;
        _helper.Multiplayer.SendMessage(
            msg,
            MsgLivingEventChoice,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { hostId }
        );
    }

    public void SendPortraitDialogue(PortraitDialogueMessage msg)
    {
        if (msg == null) return;
        _helper.Multiplayer.SendMessage(
            msg,
            MsgPortraitDialogue,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { msg.TargetFarmerId }
        );
    }

    public void SendStartCinematicOuting(StartCinematicOutingMessage msg)
    {
        if (msg == null) return;
        _helper.Multiplayer.SendMessage(
            msg,
            MsgStartCinematicOuting,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { msg.TargetFarmerId }
        );
    }

    public void SendCinematicCompletedToHost(CinematicCompletedMessage msg)
    {
        if (msg == null) return;
        long hostId = Game1.MasterPlayer?.UniqueMultiplayerID ?? 0;
        _helper.Multiplayer.SendMessage(
            msg,
            MsgCinematicCompleted,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { hostId }
        );
    }

    public void BroadcastPicnicScene(PicnicSceneSyncMessage msg, long? specificPeerId = null)
    {
        if (msg == null) return;
        _helper.Multiplayer.SendMessage(
            msg,
            MsgPicnicSceneSync,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: specificPeerId.HasValue ? new[] { specificPeerId.Value } : null
        );
    }

    public void SendLivingEventBuff(long targetFarmerId, string buffKind)
    {
        if (string.IsNullOrWhiteSpace(buffKind)) return;

        if (targetFarmerId == Game1.player?.UniqueMultiplayerID)
        {
            Systems.LivingEvents.LivingEventManager.ApplyLocalLivingEventBuff(buffKind);
            return;
        }

        if (!Context.IsMultiplayer) return;

        _helper.Multiplayer.SendMessage(
            new LivingEventBuffMessage
            {
                TargetFarmerId = targetFarmerId,
                BuffKind = buffKind
            },
            MsgLivingEventBuff,
            modIDs: new[] { _manifest.UniqueID },
            playerIDs: new[] { targetFarmerId }
        );
    }

    public void OnPeerConnected(object? sender, PeerConnectedEventArgs e)
    {
        if (!Context.IsMainPlayer) return;

        // 1. Re-sincronizar entradas de correo pendientes
        var allEntries = _mailService.GetAllActiveMailEntries();
        if (allEntries.Count > 0)
            BroadcastMailEntries(allEntries, e.Peer.PlayerID);

        // 2. Re-sincronizar snapshot de estado nocturno, acompañantes y picnics activos
        NpcActionExecutor.Instance.SendMultiplayerSnapshotToPeer(e.Peer.PlayerID);
        Systems.PicnicSceneService.Instance.SyncActivePicnicsToPeer(e.Peer.PlayerID);
    }

    public void OnPeerDisconnected(object? sender, PeerDisconnectedEventArgs e)
    {
        if (!Context.IsMainPlayer) return;

        // Limpieza y liberación ordenada de acompañantes del jugador desconectado
        NpcActionExecutor.Instance.HandlePeerDisconnected(e.Peer.PlayerID);
    }

    public void OnModMessageReceived(object? sender, ModMessageReceivedEventArgs e)
    {
        if (e.FromModID != _manifest.UniqueID) return;

        switch (e.Type)
        {
            case MsgExecuteAction when Context.IsMainPlayer:
                var req = e.ReadAs<ExecuteNpcActionDto>();
                req.OwnerPlayerId = e.FromPlayerID;
                _onHostReceiveAction(req);
                break;

            case MsgOfflineInviteRequest when Context.IsMainPlayer:
                var inviteReq = e.ReadAs<OfflineInviteRequestMessage>();
                if (inviteReq != null)
                {
                    NpcActionExecutor.Instance.ProcessHostOfflineInvite(inviteReq.NpcName, e.FromPlayerID);
                }
                break;

            case MsgOfflineInviteResult when !Context.IsMainPlayer:
                var inviteRes = e.ReadAs<OfflineInviteResultMessage>();
                if (inviteRes != null && inviteRes.RequesterFarmerId == Game1.player.UniqueMultiplayerID)
                {
                    NpcActionExecutor.Instance.HandleOfflineInviteResultFromHost(inviteRes);
                }
                break;

            case MsgNightActionRequest when Context.IsMainPlayer:
                var nightReq = e.ReadAs<NightActionRequestMessage>();
                if (nightReq != null)
                {
                    NpcActionExecutor.Instance.ProcessHostNightActionRequest(nightReq.NpcName, e.FromPlayerID, nightReq.Action, nightReq.LocationName);
                }
                break;

            case MsgNightStateSync:
                var nightSync = e.ReadAs<NightStateSyncMessage>();
                if (nightSync != null)
                {
                    NpcActionExecutor.Instance.ApplyNightStateSync(nightSync);
                }
                break;

            case MsgSupportHeal:
                var healMsg = e.ReadAs<CompanionSupportHealMessage>();
                if (healMsg != null && Game1.player.UniqueMultiplayerID == healMsg.TargetFarmerId)
                {
                    if (healMsg.HpHeal > 0)
                    {
                        Game1.player.health = Math.Min(Game1.player.maxHealth, Game1.player.health + healMsg.HpHeal);
                        Game1.currentLocation?.debris.Add(new Debris(healMsg.HpHeal, new Vector2(Game1.player.getStandingPosition().X + 8, Game1.player.getStandingPosition().Y), Color.LimeGreen, 1f, Game1.player));
                    }
                    if (healMsg.StaminaHeal > 0)
                    {
                        Game1.player.Stamina = Math.Min(Game1.player.MaxStamina, Game1.player.Stamina + healMsg.StaminaHeal);
                    }
                    Game1.currentLocation?.playSound("healSound");
                }
                break;

            case MsgCaveStanceSync when Context.IsMainPlayer:
                var stanceMsg = e.ReadAs<CaveStanceSyncMessage>();
                if (stanceMsg != null)
                {
                    NpcActionExecutor.Instance.ProcessHostCaveStanceSync(stanceMsg.NpcName, e.FromPlayerID, stanceMsg.Stance);
                }
                break;

            case MsgPrivateNotice:
                var noticeDto = e.ReadAs<PrivateNoticeDto>();
                if (noticeDto != null && !string.IsNullOrWhiteSpace(noticeDto.Text))
                {
                    if (noticeDto.IsError)
                        Game1.showRedMessage(noticeDto.Text);
                    else
                        Game1.addHUDMessage(new HUDMessage(noticeDto.Text, noticeDto.HudType));
                }
                break;

            case MsgSyncBuff:
                var buffDto = e.ReadAs<SyncCompanionBuffDto>();
                var npc = Game1.getCharacterFromName(buffDto.NpcName);
                if (buffDto.Apply && npc != null)
                    _synergy.RefreshCompanionAura(npc, Game1.player, true);
                else
                    _synergy.RemoveCompanionAura(buffDto.NpcName, Game1.player);
                break;

            case MsgSyncText:
                var txtDto = e.ReadAs<SyncTextAboveHeadDto>();
                if (txtDto != null)
                {
                    if (Game1.player?.currentLocation?.NameOrUniqueName == txtDto.LocationName)
                    {
                        var localNpc = Game1.getCharacterFromName(txtDto.NpcName);
                        if (localNpc != null)
                        {
                            Bridge.TextAboveHeadPatches.IsApplyingRemoteSpeech = true;
                            try
                            {
                                localNpc.showTextAboveHead(txtDto.Text, duration: txtDto.DurationMs);
                            }
                            finally
                            {
                                Bridge.TextAboveHeadPatches.IsApplyingRemoteSpeech = false;
                            }
                        }
                    }
                }
                break;

            case MsgSyncMail:
                var mailDto = e.ReadAs<SyncDynamicMailDto>();
                _mailService.MergeSyncedEntriesFromHost(mailDto.Entries);
                break;

            case MsgQueueMail:
                var qDto = e.ReadAs<QueueMailForTomorrowDto>();
                if (!string.IsNullOrWhiteSpace(qDto.MailId))
                    Game1.player.mailForTomorrow.Add(qDto.MailId);
                break;

            case MsgSpouseGiftDelivery:
                var giftMsg = e.ReadAs<SpouseGiftDeliveryMessage>();
                if (giftMsg != null && Game1.player.UniqueMultiplayerID == giftMsg.TargetFarmerId)
                {
                    var item = ItemRegistry.Create(giftMsg.ItemQualifiedId, giftMsg.Quantity);
                    if (item != null)
                    {
                        Game1.player.addItemByMenuIfNecessaryElseHoldUp(item);
                        Game1.playSound("coin");
                    }
                }
                break;

            case MsgToolVisualEffect:
                var effectMsg = e.ReadAs<ToolVisualEffectMessage>();
                if (effectMsg != null)
                {
                    Systems.ToolAnimationService.HandleRemoteVisualEffect(effectMsg);
                }
                break;

            case MsgLivingEventPrompt:
                var promptMsg = e.ReadAs<LivingEventPromptMessage>();
                if (promptMsg != null && Game1.player?.UniqueMultiplayerID == promptMsg.TargetFarmerId)
                {
                    Systems.LivingEvents.LivingEventManager.Instance.DisplayPromptOnClient(promptMsg);
                }
                break;

            case MsgLivingEventChoice:
                var choiceMsg = e.ReadAs<LivingEventChoiceMessage>();
                if (choiceMsg != null && Context.IsMainPlayer)
                {
                    choiceMsg.RequesterFarmerId = e.FromPlayerID;
                    Systems.LivingEvents.LivingEventManager.Instance.HandleChoiceFromRemote(choiceMsg);
                }
                break;

            case MsgPortraitDialogue:
                var portMsg = e.ReadAs<PortraitDialogueMessage>();
                if (portMsg != null && Game1.player?.UniqueMultiplayerID == portMsg.TargetFarmerId)
                {
                    Systems.LivingEvents.LivingEventManager.Instance.DisplayPortraitDialogueOnClient(portMsg);
                }
                break;

            case MsgStartCinematicOuting:
                var cineStart = e.ReadAs<StartCinematicOutingMessage>();
                if (cineStart != null && Game1.player?.UniqueMultiplayerID == cineStart.TargetFarmerId)
                {
                    var cineNpc = Game1.getCharacterFromName(cineStart.NpcName);
                    if (cineNpc != null && Game1.player != null)
                    {
                        var loc = Game1.player.currentLocation ?? Game1.currentLocation;
                        if (loc != null)
                        {
                            Systems.LivingEvents.CinematicDirectorService.Instance.TryStartCinematic(
                                loc, cineNpc, Game1.player, cineStart.SceneType, cineStart.FromOutingInvite, ignoreDailyCooldown: cineStart.FromOutingInvite);
                        }
                    }
                }
                break;

            case MsgCinematicCompleted:
                var cineDone = e.ReadAs<CinematicCompletedMessage>();
                if (cineDone != null && Context.IsMainPlayer)
                {
                    cineDone.FarmerId = e.FromPlayerID;
                    Systems.LivingEvents.CinematicDirectorService.Instance.HandleRemoteCinematicCompletedOnHost(cineDone);
                }
                break;

            case MsgPicnicSceneSync:
                var picnicMsg = e.ReadAs<PicnicSceneSyncMessage>();
                if (picnicMsg != null)
                {
                    Systems.PicnicSceneService.Instance.ApplyRemoteSync(picnicMsg);
                }
                break;

            case MsgLivingEventBuff:
                var evBuffMsg = e.ReadAs<LivingEventBuffMessage>();
                if (evBuffMsg != null && Game1.player?.UniqueMultiplayerID == evBuffMsg.TargetFarmerId)
                {
                    Systems.LivingEvents.LivingEventManager.ApplyLocalLivingEventBuff(evBuffMsg.BuffKind);
                }
                break;

            case MsgSyncBanterChat:
                var banterChat = e.ReadAs<BanterChatMessage>();
                if (banterChat != null && ModEntry.Config?.EchoNpcConversationsToChat != false && Game1.player?.currentLocation != null)
                {
                    float maxDist = banterChat.MaxDistanceTiles > 0f ? banterChat.MaxDistanceTiles : 15.0f;
                    if (string.Equals(Game1.player.currentLocation.NameOrUniqueName, banterChat.LocationName, StringComparison.OrdinalIgnoreCase)
                        && Vector2.Distance(Game1.player.Tile, new Vector2(banterChat.TileX, banterChat.TileY)) <= maxDist)
                    {
                        bool useSpanish = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.es;
                        string line = useSpanish
                            ? (!string.IsNullOrWhiteSpace(banterChat.EsText) ? banterChat.EsText : banterChat.FallbackText)
                            : (!string.IsNullOrWhiteSpace(banterChat.EnText) ? banterChat.EnText : banterChat.FallbackText);

                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            string speakerName = Game1.getCharacterFromName(banterChat.SpeakerInternalName)?.displayName ?? banterChat.SpeakerDisplayName;
                            Color chatColor = banterChat.IsPersonalChatter
                                ? new Color(220, 235, 215)
                                : (banterChat.IsCompanionChat ? new Color(195, 235, 255) : new Color(235, 225, 195));
                            Game1.chatBox?.addMessage($"{speakerName}: {line}", chatColor);
                        }
                    }
                }
                break;
        }
    }
}
