namespace ValleyBuddies.Handlers.Social;

using System;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;
using ValleyBuddies.Systems;

public sealed class PicnicHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.Picnic;

    public void Tick(JobExecutionContext ctx)
    {
        var player = ctx.Owner ?? Game1.player;
        if (player == null)
        {
            PicnicSceneService.Instance.EndPicnic(ctx.Npc.Name);
            ctx.Job.Done = true;
            return;
        }

        var loc = ctx.Npc.currentLocation;
        if (loc == null)
        {
            PicnicSceneService.Instance.EndPicnic(ctx.Npc.Name);
            ctx.Job.Done = true;
            return;
        }

        var state = ctx.Job.GetOrCreateState<PicnicState>();
        ulong gameTick = ctx.GameTick > 0 ? ctx.GameTick : ModEntry.CurrentTick;

        // 1. Inicialización: buscar área 3x2 e iniciar sesión de picnic
        if (!state.Initialized)
        {
            if (!PicnicSceneService.Instance.TryStartPicnic(loc, ctx.Npc, player, out Point spot))
            {
                string blockedMsg = Utilities.I18n.Get("picnic.blocked_indoor_or_rain");
                ModEntry.ConversationBridge.ShowCompanionBubble(ctx.Npc, blockedMsg, 3500);
                ctx.Job.Done = true;
                return;
            }

            state.OriginTile = spot;
            state.SeatTile = new Point(spot.X + 1, spot.Y);
            state.WalkingToBlanket = true;
            state.Initialized = true;
            state.NextChatterTick = gameTick + 1200; // ~20s inicial
            state.LastStaminaTick = gameTick;
        }

        // 2. Caminar físicamente hasta la casilla de asiento sobre la manta
        if (state.WalkingToBlanket)
        {
            if (!ctx.Job.Data.TryGetValue("picnic_walk_start", out var pwsObj) || pwsObj is not ulong walkStart)
            {
                walkStart = gameTick;
                ctx.Job.Data["picnic_walk_start"] = walkStart;
            }
            if (gameTick >= walkStart + 420)
            {
                ctx.Navigator.StopCleanly(ctx.Npc);
                PicnicSceneService.Instance.EndPicnic(ctx.Npc.Name);
                ModEntry.ConversationBridge.ShowCompanionBubble(ctx.Npc, Utilities.I18n.Get("chore.path_blocked", new { playerName = player.displayName ?? player.Name }), 3200);
                ctx.Npc.doEmote(28);
                ctx.Job.Done = true;
                return;
            }

            ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, loc, state.SeatTile, gameTick);
            float distToSeat = Vector2.Distance(ctx.Npc.Tile, state.SeatTile.ToVector2());

            if (distToSeat <= 1.25f)
            {
                ctx.Npc.Halt();
                ctx.Npc.controller = null;
                ctx.Npc.faceDirection(2); // Mirar al sur hacia el centro de la manta
                state.WalkingToBlanket = false;
                state.ArrivedOnBlanket = true;

                try { ctx.Npc.currentLocation?.playSound("gulp"); } catch { }
                ctx.Npc.doEmote(20);

                if (!state.SnackDelivered)
                {
                    state.SnackDelivered = true;
                    if (PicnicSceneService.Instance.TryConsumeDailySnackForPair(player.UniqueMultiplayerID, ctx.Npc.Name))
                    {
                        var snack = ItemRegistry.Create("(O)223", 1) ?? ItemRegistry.Create("(O)216", 1);
                        if (snack != null)
                        {
                            if (player.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
                            {
                                ctx.World.GiveItemToFarmerDirectly(player, snack);
                                try { ctx.Npc.currentLocation?.playSound("coin"); } catch { }
                            }
                            else
                            {
                                ModEntry.MultiplayerSync?.SendSpouseGift(player.UniqueMultiplayerID, snack.QualifiedItemId, 1, ctx.Npc.Name);
                            }
                        }
                    }

                    string initMsg = Utilities.I18n.Get($"bubble.activity.picnic_{Game1.random.Next(1, 4)}");
                    ModEntry.ConversationBridge.ShowCompanionBubble(ctx.Npc, initMsg, 3800);
                }
            }
            return;
        }

        // 3. Estancia relajada sobre la manta hasta que el jugador decida marcharse o recoger
        if (state.ArrivedOnBlanket)
        {
            ctx.Npc.controller = null;
            float distToPlayer = Vector2.Distance(ctx.Npc.Tile, player.Tile);
            if (distToPlayer <= 3.5f)
            {
                ctx.Npc.faceGeneralDirection(player.Position);
            }
            else
            {
                ctx.Npc.faceDirection(2);
            }

            float distToBlanket = Vector2.Distance(new Vector2(state.OriginTile.X + 1, state.OriginTile.Y + 0.5f), player.Tile);

            // Condiciones de fin del picnic
            bool playerLeftArea = distToBlanket > 8.5f || player.currentLocation != loc;
            bool badWeatherOrLate = loc.IsRainingHere() || Game1.timeOfDay >= 2200;
            bool manuallyPacked = !PicnicSceneService.Instance.HasActivePicnicForNpc(ctx.Npc.Name);

            if (playerLeftArea || badWeatherOrLate || manuallyPacked)
            {
                PicnicSceneService.Instance.EndPicnic(ctx.Npc.Name);

                string finishMsg = Utilities.I18n.Get("picnic.finished", new { playerName = player.displayName ?? player.Name, npcName = ctx.Npc.displayName });
                ModEntry.ConversationBridge.ShowCompanionBubble(ctx.Npc, finishMsg, 3500);
                ctx.Npc.doEmote(20);

                NpcActionExecutor.TryAwardDailyActivityFriendship(player, ctx.Npc, "picnic", 15, 15);
                ctx.RecordMemory(ctx.Npc.Name, "We shared a lovely picnic together sitting on the checkered blanket outside.");
                ctx.Job.Done = true;
                return;
            }

            // Recuperación suave de energía (+5 cada 15s hasta 25 energía total/día)
            if (distToBlanket <= 3.5f && state.StaminaGranted < 25 && gameTick >= state.LastStaminaTick + 900)
            {
                state.LastStaminaTick = gameTick;
                int grant = PicnicSceneService.Instance.TryConsumeDailyStamina(player.UniqueMultiplayerID, ctx.Npc.Name, 5, 25);
                if (grant > 0)
                {
                    state.StaminaGranted += grant;

                    if (player.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
                    {
                        player.Stamina = Math.Min(player.MaxStamina, player.Stamina + grant);
                    }
                    else
                    {
                        ModEntry.MultiplayerSync?.SendSupportHeal(player.UniqueMultiplayerID, 0, grant, ctx.Npc.Name);
                    }

                    try { loc.playSound("healSound"); } catch { }
                }
            }

            // Frases periódicas de compañía cada 18-25 segundos
            if (gameTick >= state.NextChatterTick)
            {
                state.NextChatterTick = gameTick + (ulong)Game1.random.Next(1080, 1500);
                int chatIdx = Game1.random.Next(1, 5);
                string chatMsg = Utilities.I18n.Get($"picnic.chatter.v{chatIdx}", new { playerName = player.displayName ?? player.Name, npcName = ctx.Npc.displayName });
                ModEntry.ConversationBridge.ShowCompanionBubble(ctx.Npc, chatMsg, 3500);

                int emote = Game1.random.NextDouble() > 0.5 ? 20 : (Game1.random.NextDouble() > 0.5 ? 32 : 56);
                ctx.Npc.doEmote(emote);
            }
        }
    }

    public void OnAbort(JobExecutionContext ctx)
    {
        PicnicSceneService.Instance.EndPicnic(ctx.Npc.Name);
    }
}
