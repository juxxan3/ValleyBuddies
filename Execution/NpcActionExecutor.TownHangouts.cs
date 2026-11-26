using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Tools;
using ValleyBuddies.Systems;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    internal bool IsNpcBusyForAmbientBanter(string npcName, out bool isManagedCompanion)
    {
        isManagedCompanion = false;
        if (string.IsNullOrWhiteSpace(npcName)) return true;

        if (_companions.TryGetValue(npcName, out var comp))
        {
            isManagedCompanion = true;
            if (comp.IsFishingTogether
                || comp.IsWalkingToFishingSpot
                || comp.IsCaveAutonomyActive
                || comp.IsSettledInsideHouseForNight
                || comp.NightState == NightLifecycleState.SleepingInBed
                || comp.NightState == NightLifecycleState.GoingToBed)
            {
                return true;
            }
        }

        if (_active.TryGetValue(npcName, out var activeJob) && !activeJob.Done)
        {
            isManagedCompanion = true;
            if (activeJob.Kind == JobKind.Sleepover
                || activeJob.Kind == JobKind.Campfire
                || activeJob.Kind == JobKind.CampSleepTogether)
            {
                return true;
            }
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 1. Real Fishing Together 2.0 (3s wait, separated stand tile, 0 teleports)
    // ─────────────────────────────────────────────────────────────────────────

    public void TickCompanionFishing(NpcJob job, NPC npc, CompanionState comp, Farmer owner, ulong gameTick)
    {
        if (_config?.EnableCompanionFishing == false) return;
        var loc = npc.currentLocation;
        if (loc == null || loc != owner.currentLocation) return;

        bool ownerIsFishing = owner.CurrentTool is FishingRod rod && (rod.isFishing || rod.isCasting || rod.isNibbling || rod.isReeling);
        bool manualFishJob = job.Kind == JobKind.FishTogether;

        // Espera de 3 segundos (180 ticks) antes de unirse en modo automático
        if (ownerIsFishing)
        {
            comp.OwnerNotFishingGraceTicks = 0;
            comp.OwnerFishingTicks++;

            if (!manualFishJob && !comp.IsFishingTogether && !comp.IsWalkingToFishingSpot && comp.OwnerFishingTicks < 180)
            {
                return;
            }
        }
        else if (!manualFishJob)
        {
            comp.OwnerNotFishingGraceTicks++;
            float distToOwner = Vector2.Distance(npc.Tile, owner.Tile);
            if (comp.OwnerNotFishingGraceTicks > 90 || distToOwner > 10f)
            {
                if (comp.IsFishingTogether || comp.IsWalkingToFishingSpot)
                {
                    comp.IsFishingTogether = false;
                    comp.IsWalkingToFishingSpot = false;
                    comp.FishingStandTile = Point.Zero;
                    comp.FishingWaterTile = Point.Zero;
                    comp.OwnerFishingTicks = 0;
                    comp.OwnerNotFishingGraceTicks = 0;
                    comp.ShownFishingStartBubble = false;
                    npc.Halt();
                    npc.controller = null;
                }
                return;
            }
        }

        // Si ya está en postura activa de pesca
        if (comp.IsFishingTogether)
        {
            // Detect owner catching fish -> Cheer!
            if (owner.CurrentTool is FishingRod rodCheck && (rodCheck.fishCaught || rodCheck.pullingOutOfWater))
            {
                if (gameTick - comp.LastCheerTick > 300)
                {
                    comp.LastCheerTick = gameTick;
                    npc.jumpWithoutSound(3.2f);
                    npc.doEmote(32); // Happy wave
                    int v = Game1.random.Next(1, 4);
                    string cheer = I18n.Get($"hangout.fish.cheer.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
                    ModEntry.ConversationBridge.ShowCompanionBubble(npc, cheer, 3000);
                }
            }

            // Periodic water ripple pulse + bobber sobre comp.FishingWaterTile guardado
            if (gameTick % 150 == 0 && comp.FishingWaterTile != Point.Zero)
            {
                ToolAnimationService.PlayFishingBobberPulse(loc, comp.FishingWaterTile);
            }

            // Occasional companion catch / gift fish (max 2 per day)
            if (comp.FishingGiftsGivenToday < 2)
            {
                bool isExpert = npc.Name is "Willy" or "Elliott" or "Pam" or "Linus" or "Leo";
                ulong giftCooldown = isExpert ? 2400UL : 3600UL;
                if (gameTick - comp.LastFishingGiftTick > giftCooldown && gameTick - comp.FishingCastTick > 600)
                {
                    if (gameTick % 120 == 0 && Game1.random.NextDouble() < (isExpert ? 0.35 : 0.20))
                    {
                        comp.FishingGiftsGivenToday++;
                        comp.LastFishingGiftTick = gameTick;

                        npc.jumpWithoutSound(3.5f);
                        loc.playSound("pullItemFromWater");
                        loc.playSound("fishSlap");
                        npc.doEmote(56); // Star

                        string curLocName = loc.NameOrUniqueName ?? string.Empty;
                        string fishId = "(O)142";
                        if (loc is StardewValley.Locations.Beach || curLocName.Contains("Beach", StringComparison.OrdinalIgnoreCase) || curLocName.Contains("IslandSouth", StringComparison.OrdinalIgnoreCase))
                        {
                            if (isExpert)
                                fishId = Game1.random.NextDouble() < 0.5 ? "(O)148" : "(O)154"; // Eel / Sea Cucumber
                            else
                            {
                                double r = Game1.random.NextDouble();
                                fishId = r < 0.34 ? "(O)131" : (r < 0.67 ? "(O)147" : "(O)150"); // Sardine / Herring / Red Snapper
                            }
                        }
                        else if (loc is StardewValley.Locations.Mountain || curLocName.Contains("Mountain", StringComparison.OrdinalIgnoreCase))
                        {
                            double r = Game1.random.NextDouble();
                            fishId = r < 0.34 ? "(O)142" : (r < 0.67 ? "(O)141" : "(O)136"); // Carp / Perch / Largemouth Bass
                        }
                        else
                        {
                            double r = Game1.random.NextDouble();
                            fishId = r < 0.34 ? "(O)145" : (r < 0.67 ? "(O)132" : "(O)138"); // Sunfish / Bream / Rainbow Trout
                        }

                        var giftFish = ItemRegistry.Create<StardewValley.Object>(fishId) ?? ItemRegistry.Create<StardewValley.Object>("(O)142");
                        if (giftFish != null)
                        {
                            if (Game1.IsMultiplayer && owner.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID)
                            {
                                ValleyBuddies.ModEntry.MultiplayerSync?.SendSpouseGift(owner.UniqueMultiplayerID, giftFish.QualifiedItemId, 1, npc.Name);
                            }
                            else
                            {
                                GiveItemToFarmerDirectly(owner, giftFish);
                            }
                            int v = Game1.random.Next(1, 4);
                            string giftMsg = I18n.Get($"hangout.fish.gift.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName, fishName = giftFish.DisplayName });
                            ModEntry.ConversationBridge.ShowCompanionBubble(npc, giftMsg, 3500);
                            owner.changeFriendship(10, npc);
                        }
                    }
                }
            }
            return;
        }

        // Si está caminando físicamente hacia el puesto de pesca
        if (comp.IsWalkingToFishingSpot)
        {
            npc.speed = 2;
            npc.addedSpeed = 0;
            var navRes = HumanNavigateToTarget(job, npc, loc, comp.FishingStandTile, gameTick, arrivalChebyshev: 0, maxStuckTicks: 240, tickStep: 4);
            if (navRes == HumanNavResult.Unreachable)
            {
                comp.IsWalkingToFishingSpot = false;
                comp.FishingStandTile = Point.Zero;
                comp.FishingWaterTile = Point.Zero;
                comp.OwnerFishingTicks = 0;
                npc.Halt();
                npc.controller = null;
                return;
            }
            if (navRes == HumanNavResult.Arrived)
            {
                npc.Halt();
                npc.controller = null;
                npc.faceGeneralDirection(new Vector2(comp.FishingWaterTile.X * 64f + 32f, comp.FishingWaterTile.Y * 64f + 32f));
                comp.IsWalkingToFishingSpot = false;
                comp.IsFishingTogether = true;
                comp.FishingCastTick = gameTick;

                bool isExpert = npc.Name is "Willy" or "Elliott" or "Pam" or "Linus" or "Leo";
                ToolAnimationService.PlayFishingCastAndBobber(loc, npc, comp.FishingWaterTile, isExpert, broadcast: true);

                if (!comp.ShownFishingStartBubble)
                {
                    comp.ShownFishingStartBubble = true;
                    string startLine = NpcPersonalityDialogueService.GetFishingStartLine(npc, owner);
                    ModEntry.ConversationBridge.ShowCompanionBubble(npc, startLine, 3500);
                }
            }
            return;
        }

        // Buscar puesto propio separado en la orilla
        var spot = FindSeparatedFishingSpot(loc, owner, npc);
        if (spot.standTile != Point.Zero && spot.waterTile != Point.Zero)
        {
            comp.FishingStandTile = spot.standTile;
            comp.FishingWaterTile = spot.waterTile;
            comp.IsWalkingToFishingSpot = true;
            npc.speed = 2;
            npc.addedSpeed = 0;
            HumanNavigateToTarget(job, npc, loc, comp.FishingStandTile, gameTick, arrivalChebyshev: 0, maxStuckTicks: 240, tickStep: 4);
        }
    }

    private (Point standTile, Point waterTile) FindSeparatedFishingSpot(GameLocation loc, Farmer owner, NPC npc)
    {
        Point ownerTile = owner.TilePoint;
        var candidates = new List<(Point stand, Point water, float score)>();

        for (int r = 2; r <= 9; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r) continue;
                    Point stand = new Point(ownerTile.X + dx, ownerTile.Y + dy);

                    if (stand == ownerTile) continue;
                    if (loc.isWaterTile(stand.X, stand.Y)) continue;
                    if (!IsTileWalkable(loc, stand, npc)) continue;

                    // Evitar solaparse con otros compañeros pescando
                    if (_companions.Values.Any(c => c.NpcName != npc.Name && (c.IsFishingTogether || c.IsWalkingToFishingSpot) && Vector2.Distance(stand.ToVector2(), c.FishingStandTile.ToVector2()) < 1.5f))
                        continue;

                    float distToOwner = Vector2.Distance(stand.ToVector2(), owner.Tile);
                    if (distToOwner < 1.6f) continue;

                    // Buscar agua en vecinos cardinales
                    Point[] cardinalDirs = new[]
                    {
                        new Point(0, 1),
                        new Point(0, -1),
                        new Point(1, 0),
                        new Point(-1, 0)
                    };

                    Point chosenWater = Point.Zero;
                    foreach (var dir in cardinalDirs)
                    {
                        int wx = stand.X + dir.X;
                        int wy = stand.Y + dir.Y;
                        if (loc.isWaterTile(wx, wy))
                        {
                            int w2x = stand.X + dir.X * 2;
                            int w2y = stand.Y + dir.Y * 2;
                            if (loc.isWaterTile(w2x, w2y))
                            {
                                chosenWater = new Point(w2x, w2y);
                            }
                            else
                            {
                                chosenWater = new Point(wx, wy);
                            }
                            break;
                        }
                    }

                    if (chosenWater == Point.Zero) continue;

                    // Puntuación: priorizar distancia entre 2.5 y 7.5 tiles del dueño (~3.5-5 óptimo)
                    float distScore = 100f;
                    if (distToOwner >= 2.5f && distToOwner <= 7.5f)
                    {
                        distScore -= Math.Abs(distToOwner - 4.0f) * 5f;
                    }
                    else
                    {
                        distScore -= 50f;
                    }

                    float npcDist = Vector2.Distance(stand.ToVector2(), npc.Tile);
                    distScore -= npcDist * 2f;

                    candidates.Add((stand, chosenWater, distScore));
                }
            }
        }

        if (candidates.Count > 0)
        {
            candidates.Sort((a, b) => b.score.CompareTo(a.score));
            return (candidates[0].stand, candidates[0].water);
        }

        return (Point.Zero, Point.Zero);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 2. Submerged BathHouse Swimming (BathHouse_Pool)
    // ─────────────────────────────────────────────────────────────────────────

    private static (Point dryEdgeTile, Point waterEntryTile) FindBathHouseWaterEntry(BathHousePool loc)
    {
        int mapW = loc.map?.Layers[0]?.LayerWidth ?? 30;
        int mapH = loc.map?.Layers[0]?.LayerHeight ?? 30;
        int midX = mapW / 2;

        (Point dry, Point water)? best = null;
        float bestScore = float.MaxValue;

        for (int y = 5; y < mapH - 2; y++)
        {
            for (int x = 5; x < mapW - 5; x++)
            {
                if (!Bridge.PathfindingSafetyPatches.IsTileInBathHousePoolWater(loc, new Point(x, y)) && loc.isTilePassable(new xTile.Dimensions.Location(x, y), Game1.viewport))
                {
                    if (Bridge.PathfindingSafetyPatches.IsTileInBathHousePoolWater(loc, new Point(x, y + 1)))
                    {
                        float score = Math.Abs(x - midX) + Math.Abs(y - 20) * 0.5f;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            best = (new Point(x, y), new Point(x, y + 1));
                        }
                    }
                }
            }
        }

        if (best.HasValue) return best.Value;
        return (new Point(14, 20), new Point(14, 21));
    }

    public bool TickSpaPoolSwimming(NpcJob job, NPC npc, CompanionState comp, Farmer owner, ulong gameTick)
    {
        var loc = npc.currentLocation as BathHousePool;
        if (loc == null)
        {
            if (npc.swimming.Value)
            {
                npc.swimming.Value = false;
                npc.drawOffset = Vector2.Zero;
            }
            return false;
        }

        if (loc != owner.currentLocation)
        {
            if (npc.swimming.Value && !Bridge.PathfindingSafetyPatches.IsTileInBathHousePoolWater(loc, npc.TilePoint))
            {
                npc.swimming.Value = false;
                npc.drawOffset = Vector2.Zero;
            }
            return false;
        }

        if (!comp.IsWearingBeachOutfit)
        {
            return false;
        }

        (Point dryEdgeTile, Point waterEntryTile) = FindBathHouseWaterEntry(loc);

        // CASO A: El dueño está dentro del agua
        if (owner.swimming.Value)
        {
            if (!npc.swimming.Value)
            {
                float distToEdge = Vector2.Distance(npc.Tile, dryEdgeTile.ToVector2());
                if (distToEdge > 1.4f)
                {
                    HumanNavigateToTarget(job, npc, loc, dryEdgeTile, gameTick, arrivalChebyshev: 0, maxStuckTicks: 240, tickStep: 4);
                    try { npc.animateInFacingDirection(Game1.currentGameTime); } catch { }
                }
                else
                {
                    Vector2 targetPos = new Vector2(waterEntryTile.X * 64f + 32f, waterEntryTile.Y * 64f + 32f);
                    Vector2 dir = targetPos - npc.Position;
                    if (dir.LengthSquared() > 4f)
                    {
                        dir.Normalize();
                        npc.Position += dir * 1.8f;
                        npc.faceGeneralDirection(targetPos);
                        try { npc.animateInFacingDirection(Game1.currentGameTime); } catch { }
                    }

                    if (Bridge.PathfindingSafetyPatches.IsTileInBathHousePoolWater(loc, npc.TilePoint))
                    {
                        npc.swimming.Value = true;
                        loc.playSound("dropItemInWater");
                        loc.playSound("waterSlosh");
                    }
                }
                return true;
            }
            else
            {
                // Ya en el agua: nadar suavemente hacia una casilla de agua a 2.0 - 2.8 tiles del dueño
                Vector2 ownerPos = owner.Position;
                float distToOwner = Vector2.Distance(npc.Position, ownerPos);

                if (distToOwner > 2.8f * 64f)
                {
                    Vector2 dir = ownerPos - npc.Position;
                    dir.Normalize();
                    Vector2 nextPos = npc.Position + dir * 1.6f;
                    Point nextTile = new Point((int)(nextPos.X + 32f) / 64, (int)(nextPos.Y + 48f) / 64);
                    if (Bridge.PathfindingSafetyPatches.IsTileInBathHousePoolWater(loc, nextTile))
                    {
                        npc.Position = nextPos;
                        npc.faceGeneralDirection(ownerPos);
                        try { npc.animateInFacingDirection(Game1.currentGameTime); } catch { }
                    }
                }
                else if (distToOwner < 1.8f * 64f)
                {
                    Vector2 dir = npc.Position - ownerPos;
                    if (dir.LengthSquared() < 0.1f) dir = new Vector2(1f, 0f);
                    dir.Normalize();
                    Vector2 nextPos = npc.Position + dir * 1.6f;
                    Point nextTile = new Point((int)(nextPos.X + 32f) / 64, (int)(nextPos.Y + 48f) / 64);
                    if (Bridge.PathfindingSafetyPatches.IsTileInBathHousePoolWater(loc, nextTile))
                    {
                        npc.Position = nextPos;
                        try { npc.animateInFacingDirection(Game1.currentGameTime); } catch { }
                    }
                }
                else
                {
                    npc.faceGeneralDirection(ownerPos);
                }

                // Ondas de agua periódicas alrededor del cuerpo
                if (gameTick % 40 == 0)
                {
                    BroadcastTemporarySprite(loc, new TemporaryAnimatedSprite(
                        29,
                        new Vector2(npc.Position.X + 32f, npc.Position.Y + 48f),
                        Color.White * 0.7f,
                        4,
                        false,
                        80f)
                    {
                        scale = 1.0f
                    });
                }

                return true;
            }
        }
        else
        {
            // CASO B: El dueño está fuera del agua
            if (npc.swimming.Value)
            {
                Vector2 dryPos = new Vector2(dryEdgeTile.X * 64f + 32f, dryEdgeTile.Y * 64f + 32f);
                Vector2 dir = dryPos - npc.Position;
                bool inPoolWater = Bridge.PathfindingSafetyPatches.IsTileInBathHousePoolWater(loc, npc.TilePoint);

                if (inPoolWater && dir.LengthSquared() > 4f)
                {
                    dir.Normalize();
                    Vector2 nextPos = npc.Position + dir * 1.6f;
                    Rectangle nextBox = new Rectangle((int)nextPos.X + 8, (int)nextPos.Y + 32, 48, 32);
                    if (!loc.isCollidingPosition(nextBox, Game1.viewport, false, 0, false, npc))
                    {
                        npc.Position = nextPos;
                        npc.faceGeneralDirection(dryPos);
                        try { npc.animateInFacingDirection(Game1.currentGameTime); } catch { }
                    }
                    else
                    {
                        npc.swimming.Value = false;
                        npc.drawOffset = Vector2.Zero;
                        loc.playSound("waterSlosh");
                        MoveNpcTowards(job, npc, loc, dryEdgeTile, gameTick);
                    }
                }

                if (!Bridge.PathfindingSafetyPatches.IsTileInBathHousePoolWater(loc, npc.TilePoint) || Vector2.Distance(npc.Position, dryPos) < 24f)
                {
                    npc.swimming.Value = false;
                    npc.drawOffset = Vector2.Zero;
                    loc.playSound("waterSlosh");
                    if (Vector2.Distance(npc.Position, dryPos) >= 24f)
                    {
                        MoveNpcTowards(job, npc, loc, dryEdgeTile, gameTick);
                    }
                }
                return true;
            }
        }

        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 3. Automatic Hangout Moments by Location
    // ─────────────────────────────────────────────────────────────────────────

    public void TickTownHangouts(NpcJob job, NPC npc, CompanionState comp, Farmer owner, ulong gameTick)
    {
        if (_config?.EnableTownHangouts == false) return;
        if (gameTick % 60 != 0) return;

        var loc = npc.currentLocation;
        if (loc == null || loc != owner.currentLocation) return;
        string locName = loc.NameOrUniqueName ?? string.Empty;

        if (comp.LastHangoutLocationName != locName)
        {
            comp.LastHangoutLocationName = locName;
            comp.HangoutTimerTicks = 0;
        }

        // A. Saloon (Drinks / Toast, +10 friendship)
        if (locName.Equals("Saloon", StringComparison.OrdinalIgnoreCase))
        {
            if (!comp.TriggeredHangoutLocationsToday.Contains("Saloon"))
            {
                comp.HangoutTimerTicks += 60;
                if (comp.HangoutTimerTicks >= 180)
                {
                    comp.TriggeredHangoutLocationsToday.Add("Saloon");
                    npc.faceGeneralDirection(owner.Position);
                    npc.doEmote(32);
                    owner.changeFriendship(10, npc);
                    string line = NpcPersonalityDialogueService.GetSaloonLine(npc, owner);
                    ModEntry.ConversationBridge.ShowCompanionBubble(npc, line, 3500);
                }
            }
            return;
        }

        // B. Spa / BathHouse_Pool (+15 stamina recover, +10 friendship)
        if (locName.Equals("BathHouse_Pool", StringComparison.OrdinalIgnoreCase))
        {
            if (!comp.TriggeredHangoutLocationsToday.Contains("BathHouse_Pool"))
            {
                comp.HangoutTimerTicks += 60;
                if (comp.HangoutTimerTicks >= 180)
                {
                    comp.TriggeredHangoutLocationsToday.Add("BathHouse_Pool");
                    npc.faceGeneralDirection(owner.Position);
                    npc.doEmote(20);
                    if (Game1.IsMultiplayer && owner.UniqueMultiplayerID != Game1.player.UniqueMultiplayerID)
                    {
                        ValleyBuddies.ModEntry.MultiplayerSync?.SendSupportHeal(owner.UniqueMultiplayerID, 0, 15f, npc.Name);
                    }
                    else
                    {
                        owner.Stamina = Math.Min(owner.MaxStamina, owner.Stamina + 15f);
                    }
                    owner.changeFriendship(10, npc);
                    string line = NpcPersonalityDialogueService.GetSpaLine(npc, owner);
                    ModEntry.ConversationBridge.ShowCompanionBubble(npc, line, 3500);
                }
            }
            return;
        }

        // C. Museum / ArchaeologyHouse (+10 friendship)
        if (locName.Equals("ArchaeologyHouse", StringComparison.OrdinalIgnoreCase))
        {
            if (!comp.TriggeredHangoutLocationsToday.Contains("ArchaeologyHouse"))
            {
                comp.HangoutTimerTicks += 60;
                if (comp.HangoutTimerTicks >= 180)
                {
                    comp.TriggeredHangoutLocationsToday.Add("ArchaeologyHouse");
                    npc.faceGeneralDirection(owner.Position);
                    npc.doEmote(32);
                    owner.changeFriendship(10, npc);
                    string line = NpcPersonalityDialogueService.GetMuseumLine(npc, owner);
                    ModEntry.ConversationBridge.ShowCompanionBubble(npc, line, 3500);
                }
            }
            return;
        }

        // D. Beach / IslandSouth (+10 friendship)
        bool isBeach = loc is StardewValley.Locations.Beach || locName.Contains("Beach", StringComparison.OrdinalIgnoreCase) || locName.Contains("IslandSouth", StringComparison.OrdinalIgnoreCase);
        if (isBeach && !loc.IsRainingHere() && Game1.timeOfDay < 1800)
        {
            if (!comp.TriggeredHangoutLocationsToday.Contains("Beach"))
            {
                comp.HangoutTimerTicks += 60;
                if (comp.HangoutTimerTicks >= 240)
                {
                    comp.TriggeredHangoutLocationsToday.Add("Beach");
                    npc.faceGeneralDirection(owner.Position);
                    npc.doEmote(20);
                    owner.changeFriendship(10, npc);
                    string line = NpcPersonalityDialogueService.GetBeachLine(npc, owner);
                    ModEntry.ConversationBridge.ShowCompanionBubble(npc, line, 3500);
                }
            }
            return;
        }

        // E. Scenic Nature Rest (standing still >= 6s, daytime, outdoor)
        if (loc.IsOutdoors && !loc.IsRainingHere() && Game1.timeOfDay < 1800)
        {
            if (!comp.TriggeredHangoutLocationsToday.Contains("ScenicNature"))
            {
                Vector2 pPos = owner.Position;
                if (Vector2.Distance(pPos, comp.LastScenicStillPos) < 16f)
                {
                    comp.ScenicRestTicks += 60;
                    if (comp.ScenicRestTicks >= 360) // 6 seconds
                    {
                        comp.TriggeredHangoutLocationsToday.Add("ScenicNature");
                        npc.faceGeneralDirection(owner.Position);
                        npc.doEmote(20);
                        owner.changeFriendship(10, npc);
                        int v = Game1.random.Next(1, 5);
                        string line = I18n.Get($"hangout.nature.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
                        ModEntry.ConversationBridge.ShowCompanionBubble(npc, line, 3500);
                    }
                }
                else
                {
                    comp.LastScenicStillPos = pPos;
                    comp.ScenicRestTicks = 0;
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 4. Outdoor Season & Weather Flavor Reflection (1x/day)
    // ─────────────────────────────────────────────────────────────────────────

    public void TickSeasonWeatherFlavor(NpcJob job, NPC npc, CompanionState comp, Farmer owner, ulong gameTick)
    {
        if (gameTick % 120 != 0) return;
        var loc = npc.currentLocation;
        if (loc == null || loc != owner.currentLocation || !loc.IsOutdoors) return;
        if (comp.TriggeredHangoutLocationsToday.Contains("SeasonWeatherFlavor")) return;

        comp.OutdoorWeatherFlavorTicks += 120;
        if (comp.OutdoorWeatherFlavorTicks >= 2700) // ~45 segundos juntos al aire libre
        {
            comp.TriggeredHangoutLocationsToday.Add("SeasonWeatherFlavor");
            npc.faceGeneralDirection(owner.Position);
            npc.doEmote(20);
            string line = NpcPersonalityDialogueService.GetSeasonWeatherFlavorLine(npc, owner, loc);
            ModEntry.ConversationBridge.ShowCompanionBubble(npc, line, 3800);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 5. Daily Friendship Bonding Bonus
    // ─────────────────────────────────────────────────────────────────────────

    public void TickCompanionBonding(NpcJob job, NPC npc, CompanionState comp, Farmer owner, ulong gameTick)
    {
        if (_config?.EnableDailyBondingBonus == false) return;
        if (gameTick % 60 != 0) return;

        var loc = npc.currentLocation;
        if (loc == null || loc != owner.currentLocation || !comp.IsFollowing) return;

        comp.BondingTicksToday += 60;
        if (comp.BondingTiersPaidToday < 3)
        {
            int nextTierTicks = (comp.BondingTiersPaidToday + 1) * 5400; // 5400 (~90s), 10800 (~180s), 16200 (~270s)
            if (comp.BondingTicksToday >= nextTierTicks)
            {
                comp.BondingTiersPaidToday++;
                owner.changeFriendship(15, npc);
                try { loc.playSound("dwop"); } catch { }
                npc.doEmote(20); // Heart
                string tierKey = $"hangout.bonding.tier{comp.BondingTiersPaidToday}";
                string msg = I18n.Get(tierKey, new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
                ModEntry.ConversationBridge.ShowCompanionBubble(npc, msg, 3200);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 6. Rare Find Reactions
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> _rareTreasures = new(StringComparer.OrdinalIgnoreCase)
    {
        "(O)72",  // Diamond
        "(O)386", // Iridium Ore
        "(O)107", // Dinosaur Egg
        "(O)114"  // Ancient Seed
    };

    private static readonly HashSet<string> _legendaryFish = new(StringComparer.OrdinalIgnoreCase)
    {
        "(O)159", "(O)160", "(O)163", "(O)682", "(O)775",
        "(O)898", "(O)899", "(O)900", "(O)901", "(O)902"
    };

    public void TickRareFindReactions(NpcJob job, NPC npc, CompanionState comp, Farmer owner, ulong gameTick)
    {
        if (_config?.EnableRareFindReactions == false) return;
        if (gameTick % 60 != 0) return;

        var loc = npc.currentLocation;
        if (loc == null || loc != owner.currentLocation) return;

        int prismaticCount = 0;
        int treasureCount = 0;
        int legendaryCount = 0;

        foreach (var item in owner.Items)
        {
            if (item == null) continue;
            string qid = item.QualifiedItemId;
            if (qid == "(O)74")
                prismaticCount += item.Stack;
            else if (_rareTreasures.Contains(qid))
                treasureCount += item.Stack;
            else if (_legendaryFish.Contains(qid))
                legendaryCount += item.Stack;
        }

        CheckRareCategory(npc, comp, owner, loc, "PrismaticShard", prismaticCount, "hangout.rare.prismatic");
        CheckRareCategory(npc, comp, owner, loc, "Treasures", treasureCount, "hangout.rare.treasure");
        CheckRareCategory(npc, comp, owner, loc, "LegendaryFish", legendaryCount, "hangout.rare.legendary");
    }

    private static void CheckRareCategory(NPC npc, CompanionState comp, Farmer owner, GameLocation loc, string category, int currentCount, string baseI18nKey)
    {
        if (!comp.KnownRareItemCounts.TryGetValue(category, out int prevCount))
        {
            comp.KnownRareItemCounts[category] = currentCount;
            return;
        }

        if (currentCount > prevCount && !comp.ReactedRareCategoriesToday.Contains(category))
        {
            comp.ReactedRareCategoriesToday.Add(category);
            comp.KnownRareItemCounts[category] = currentCount;

            npc.jumpWithoutSound(3.8f);
            npc.doEmote(16); // Exclamation
            try { loc.playSound("getNewSpecialItem"); } catch { }

            int v = Game1.random.Next(1, 3);
            string line = I18n.Get($"{baseI18nKey}.v{v}", new { playerName = owner.displayName ?? owner.Name, npcName = npc.displayName });
            ModEntry.ConversationBridge.ShowCompanionBubble(npc, line, 3800);
        }
        else
        {
            comp.KnownRareItemCounts[category] = currentCount;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // 7. Organic Living Micro-Events (45s pacing, 1 candidate/cycle) + Favorite Spot Prompt
    // ─────────────────────────────────────────────────────────────────────────

    public void TickCompanionLivingEvents(NpcJob job, NPC npc, CompanionState comp, Farmer owner, ulong gameTick)
    {
        if (_config?.EnableLivingMicroEvents == false) return;
        var loc = npc.currentLocation;
        if (loc == null || loc != owner.currentLocation) return;
        if (!StardewModdingAPI.Context.IsMainPlayer) return;

        // Seguimiento cada ~1s de caricias animales en Farm/AnimalHouse + tiempo en sitio favorito
        if (gameTick % 60 == 0)
        {
            if (loc is Farm || loc is AnimalHouse)
            {
                if (ValleyBuddies.Systems.LivingEvents.LivingEventManager.Instance.TryDetectRecentAnimalPetting(npc, owner, loc))
                    return;
            }

            if (TickFavoriteSpotPrompt(job, npc, comp, owner))
                return;
        }

        // Cadencia equilibrada de paseos: evaluación orgánica cada 45 segundos reales (2700 ticks)
        if (gameTick % 2700 != 0) return;

        float dist = Vector2.Distance(npc.Tile, owner.Tile);
        string locName = loc.NameOrUniqueName ?? loc.Name ?? string.Empty;
        string baseName = loc.Name ?? string.Empty;
        double pf = ValleyBuddies.Systems.LivingEvents.ValleyPacingDirector.Instance.MicroEventProbabilityFactor;
        var mgr = ValleyBuddies.Systems.LivingEvents.LivingEventManager.Instance;

        var candidates = new List<(string EventId, double Chance, Action Trigger)>();

        // 1. RainyChillBreakEvent (bajo lluvia al aire libre)
        if (loc.IsOutdoors && loc.IsRainingHere() && dist <= 5.0f &&
            mgr.CanTriggerNow("rainy_chill_break", owner, npc))
        {
            candidates.Add(("rainy_chill_break", 0.08 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.RainyChillBreakEvent.Instance.TriggerRainBreak(npc, owner)));
        }

        // 2. MischievousAnimalEvent (cerca de animales en Farm o AnimalHouse)
        if ((loc is Farm || loc is AnimalHouse) && dist <= 6.0f &&
            mgr.CanTriggerNow("mischievous_animal", owner, npc))
        {
            var animals = loc is Farm farmLoc ? farmLoc.animals.Values : ((AnimalHouse)loc).animals.Values;
            var nearAnimal = animals.FirstOrDefault(a => a != null && Vector2.Distance(a.Tile, npc.Tile) <= 5.0f);
            if (nearAnimal != null)
            {
                candidates.Add(("mischievous_animal", 0.06 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.MischievousAnimalEvent.Instance.TriggerAnimalMischief(npc, owner, nearAnimal)));
            }
        }

        // 3. SpontaneousViewEvent (atardecer soleado 17:00-19:30 al aire libre)
        if (loc.IsOutdoors && !loc.IsRainingHere() &&
            Game1.timeOfDay >= 1700 && Game1.timeOfDay <= 1930 &&
            dist <= 5.5f && mgr.CanTriggerNow("spontaneous_view", owner, npc))
        {
            bool isViewSpot = loc is StardewValley.Locations.Beach ||
                              loc is StardewValley.Locations.Mountain ||
                              loc is StardewValley.Locations.Forest ||
                              loc is Farm ||
                              loc is StardewValley.Locations.Town ||
                              locName.Contains("Ridge", StringComparison.OrdinalIgnoreCase) ||
                              locName.Contains("Cliff", StringComparison.OrdinalIgnoreCase);
            if (isViewSpot)
            {
                candidates.Add(("spontaneous_view", 0.07 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.SpontaneousViewEvent.Instance.TriggerView(npc, owner)));
            }
        }

        // 4. TownCravingEvent (Town, Saloon o Beach entre 09:00 y 22:00)
        if (Game1.timeOfDay >= 900 && Game1.timeOfDay <= 2200 && dist <= 5.5f &&
            mgr.CanTriggerNow("town_craving", owner, npc))
        {
            bool isTownOrFoodSpot = loc is StardewValley.Locations.Town ||
                                    loc is StardewValley.Locations.Beach ||
                                    baseName.Equals("Saloon", StringComparison.OrdinalIgnoreCase) ||
                                    locName.Contains("Saloon", StringComparison.OrdinalIgnoreCase) ||
                                    locName.Contains("Town", StringComparison.OrdinalIgnoreCase);
            if (isTownOrFoodSpot)
            {
                candidates.Add(("town_craving", 0.07 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.TownCravingEvent.Instance.TriggerCraving(npc, owner)));
            }
        }

        // 5. TrailStumbleEvent (Forest, Woods, Mountain, MineShaft o VolcanoDungeon)
        if (dist <= 5.5f && mgr.CanTriggerNow("trail_stumble", owner, npc))
        {
            bool isTrailSpot = loc is StardewValley.Locations.Forest ||
                               loc is StardewValley.Locations.Woods ||
                               loc is StardewValley.Locations.Mountain ||
                               loc is StardewValley.Locations.MineShaft ||
                               loc is StardewValley.Locations.VolcanoDungeon ||
                               locName.Contains("Forest", StringComparison.OrdinalIgnoreCase) ||
                               locName.Contains("Woods", StringComparison.OrdinalIgnoreCase) ||
                               locName.Contains("Mountain", StringComparison.OrdinalIgnoreCase);
            if (isTrailSpot)
            {
                candidates.Add(("trail_stumble", 0.06 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.TrailStumbleEvent.Instance.TriggerStumble(npc, owner)));
            }
        }

        // 6. FarmWorkCheeringEvent (Farm o Greenhouse cuando < 80% stamina o durante faena agrícola)
        if ((loc is Farm || loc.IsGreenhouse || locName.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase)) &&
            dist <= 5.5f && mgr.CanTriggerNow("farm_work_cheering", owner, npc))
        {
            bool lowEnergy = owner.MaxStamina > 0 && (owner.Stamina < owner.MaxStamina * 0.80f);
            bool doingFarmWork = job != null && job.Kind is JobKind.WaterCrops or JobKind.HarvestCrops or JobKind.HarvestForage
                                                        or JobKind.ClearDebris or JobKind.PetAnimals or JobKind.ChopWood
                                                        or JobKind.ChopTrees or JobKind.FeedAnimals or JobKind.CollectAnimalProducts
                                                        or JobKind.CollectMachines or JobKind.PlantSeeds or JobKind.RepairFences
                                                        or JobKind.WaterPetBowl;
            if (lowEnergy || doingFarmWork)
            {
                candidates.Add(("farm_work_cheering", 0.07 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.FarmWorkCheeringEvent.Instance.TriggerCheering(npc, owner)));
            }
        }

        // 7. DroppedKeepsakeEvent (Town, Beach, Forest, Mountain o Farm)
        if (loc.IsOutdoors && dist <= 5.5f && mgr.CanTriggerNow("dropped_keepsake", owner, npc))
        {
            bool isKeepsakeSpot = loc is StardewValley.Locations.Town ||
                                  loc is StardewValley.Locations.Beach ||
                                  loc is StardewValley.Locations.Forest ||
                                  loc is StardewValley.Locations.Mountain ||
                                  loc is Farm;
            if (isKeepsakeSpot)
            {
                candidates.Add(("dropped_keepsake", 0.06 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.DroppedKeepsakeEvent.Instance.TriggerKeepsake(npc, owner)));
            }
        }

        // 8. ShinyFarmFindEvent (al explorar o faenar al aire libre)
        if (loc.IsOutdoors && dist <= 5.5f && mgr.CanTriggerNow("shiny_farm_find", owner, npc))
        {
            bool isEligibleMap = loc is Farm ||
                                 locName.Contains("Farm", StringComparison.OrdinalIgnoreCase) ||
                                 locName.Contains("Beach", StringComparison.OrdinalIgnoreCase) ||
                                 locName.Contains("Mountain", StringComparison.OrdinalIgnoreCase) ||
                                 locName.Contains("Forest", StringComparison.OrdinalIgnoreCase) ||
                                 locName.Contains("Woods", StringComparison.OrdinalIgnoreCase);
            if (isEligibleMap)
            {
                candidates.Add(("shiny_farm_find", 0.07 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.ShinyFarmFindEvent.Instance.TriggerFind(npc, owner)));
            }
        }

        // 9. FireflyMomentEvent (exteriores despejados Primavera/Verano/Otoño >= 19:30)
        if (loc.IsOutdoors && !loc.IsRainingHere() && !Game1.IsWinter && Game1.timeOfDay >= 1930 &&
            dist <= 5.5f && mgr.CanTriggerNow("firefly_moment", owner, npc))
        {
            candidates.Add(("firefly_moment", 0.09 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.FireflyMomentEvent.Instance.TriggerFireflies(npc, owner)));
        }

        // 10. WildflowerCrownEvent (exteriores soleados Primavera/Verano en Forest, Mountain o Farm)
        if (loc.IsOutdoors && !loc.IsRainingHere() && (Game1.IsSpring || Game1.IsSummer) &&
            (loc is StardewValley.Locations.Forest || loc is StardewValley.Locations.Mountain || loc is Farm ||
             locName.Contains("Forest", StringComparison.OrdinalIgnoreCase) || locName.Contains("Mountain", StringComparison.OrdinalIgnoreCase)) &&
            dist <= 5.5f && mgr.CanTriggerNow("wildflower_crown", owner, npc))
        {
            candidates.Add(("wildflower_crown", 0.07 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.WildflowerCrownEvent.Instance.TriggerCrown(npc, owner)));
        }

        // 11. StreetMusicHumEvent (Town, Beach o Saloon)
        if ((loc is StardewValley.Locations.Town || loc is StardewValley.Locations.Beach ||
             baseName.Equals("Saloon", StringComparison.OrdinalIgnoreCase) || locName.Contains("Town", StringComparison.OrdinalIgnoreCase) ||
             locName.Contains("Saloon", StringComparison.OrdinalIgnoreCase)) &&
            dist <= 5.5f && mgr.CanTriggerNow("street_music_hum", owner, npc))
        {
            candidates.Add(("street_music_hum", 0.07 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.StreetMusicHumEvent.Instance.TriggerMusic(npc, owner)));
        }

        // 12. SnowballPlayEvent (exteriores en Invierno)
        if (loc.IsOutdoors && Game1.IsWinter && dist <= 5.5f && mgr.CanTriggerNow("snowball_play", owner, npc))
        {
            candidates.Add(("snowball_play", 0.09 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.SnowballPlayEvent.Instance.TriggerSnowball(npc, owner)));
        }

        // 13. StargazingWishEvent (exteriores despejados >= 20:00)
        if (loc.IsOutdoors && !loc.IsRainingHere() && Game1.timeOfDay >= 2000 &&
            dist <= 5.5f && mgr.CanTriggerNow("stargazing_wish", owner, npc))
        {
            candidates.Add(("stargazing_wish", 0.08 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.StargazingWishEvent.Instance.TriggerStar(npc, owner)));
        }

        // 14. KindUmbrellaShareEvent (exteriores bajo la lluvia)
        if (loc.IsOutdoors && loc.IsRainingHere() && dist <= 5.5f && mgr.CanTriggerNow("kind_umbrella_share", owner, npc))
        {
            candidates.Add(("kind_umbrella_share", 0.08 * pf, () => ValleyBuddies.Systems.LivingEvents.Events.KindUmbrellaShareEvent.Instance.TriggerUmbrella(npc, owner)));
        }

        if (candidates.Count == 0) return;

        // Elegir un solo candidato aleatorio por ciclo (evita tirar 7 dados apilados)
        var picked = candidates[Game1.random.Next(candidates.Count)];
        double cappedChance = Math.Clamp(picked.Chance, 0.0, 0.30);
        if (Game1.random.NextDouble() < cappedChance)
        {
            picked.Trigger();
        }
    }

    private bool TickFavoriteSpotPrompt(NpcJob? job, NPC npc, CompanionState comp, Farmer owner)
    {
        if (ModEntry.Config?.EnableFavoriteSpotPrompts == false) return false;
        if (job == null || job.Kind != JobKind.Follow) return false;
        if (Game1.eventUp || Utility.isFestivalDay() || Game1.weddingToday) return false;

        var loc = npc.currentLocation;
        if (loc == null || loc != owner.currentLocation) return false;

        string locKey = loc.NameOrUniqueName ?? loc.Name ?? string.Empty;
        if (!string.Equals(comp.FavoriteSpotLocationName, locKey, StringComparison.OrdinalIgnoreCase))
        {
            comp.FavoriteSpotLocationName = locKey;
            comp.FavoriteSpotTicks = 0;
            return false;
        }

        if (owner.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID &&
            (!StardewModdingAPI.Context.IsPlayerFree || Game1.activeClickableMenu != null || Game1.dialogueUp || Game1.fadeToBlack || Game1.isWarping))
        {
            return false;
        }

        if (owner.getFriendshipHeartLevelForNPC(npc.Name) < 6) return false;
        if (ValleyBuddies.Systems.LivingEvents.NpcOutingInvitationService.Instance.HasActiveOutingForNpc(npc.Name)) return false;
        if (!ValleyBuddies.Systems.LivingEvents.ValleyPacingDirector.Instance.CanOfferFavoriteSpotCinematic(owner.UniqueMultiplayerID, Game1.Date.TotalDays)) return false;

        string scene = ValleyBuddies.Systems.LivingEvents.CinematicDirectorService.ResolveSceneType(loc);
        if (ValleyBuddies.Systems.LivingEvents.CinematicDirectorService.Instance.HasCompletedSceneToday(owner.UniqueMultiplayerID, npc.Name, scene)) return false;
        if (!ValleyBuddies.Systems.LivingEvents.CharacterVoicePackService.Instance.IsPreferredDestination(npc, loc)) return false;

        comp.FavoriteSpotTicks += 60;
        if (comp.FavoriteSpotTicks < 600) return false;
        if (Vector2.Distance(npc.Tile, owner.Tile) > 5.5f) return false;

        ValleyBuddies.Systems.LivingEvents.ValleyPacingDirector.Instance.MarkFavoriteSpotOfferedToday(owner.UniqueMultiplayerID);
        comp.FavoriteSpotTicks = 0;

        npc.faceGeneralDirection(owner.Position);
        npc.doEmote(20);

        string destDisplay = I18n.Get($"outing.loc.{scene}");
        ValleyBuddies.Systems.LivingEvents.LivingEventManager.Instance.TriggerDirectPrompt(
            "favorite_spot_cinematic",
            npc,
            owner,
            "cinematic.fav_spot.prompt",
            "watch", "cinematic.fav_spot.opt_watch",
            "skip", "cinematic.fav_spot.opt_skip",
            itemDisplayName: destDisplay,
            introKey: "cinematic.fav_spot.intro",
            emotionTag: "$h"
        );
        return true;
    }
}
