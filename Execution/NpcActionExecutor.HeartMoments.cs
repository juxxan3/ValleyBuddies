using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using ValleyBuddies.Systems;
using ValleyBuddies.Utilities;

namespace ValleyBuddies.Execution;

public sealed partial class NpcActionExecutor
{
    /// <summary>
    /// Evaluates organic tender companion moments during exploration and indoor time.
    /// Called per tick in TickFollow loop.
    /// </summary>
    public void TickCompanionTenderMoments(NpcJob job, NPC npc, CompanionState comp, Farmer player, ulong gameTick)
    {
        if (_config?.EnableCompanionTenderMoments == false) return;
        if (comp.IsRecoveringBreath || comp.IsFleeingBomb || comp.CurrentCaveCombatTarget != null) return;
        if (npc.isSleeping.Value) return;

        var loc = npc.currentLocation;
        if (loc == null || loc != player.currentLocation) return;

        // 1. "¡Vi esto y pensé en ti!" (Surprise gift in nature, 4+♥, 1/day)
        if (!comp.DidSurpriseGiftToday && Game1.timeOfDay >= 800 && comp.Hearts >= 4 && loc.IsOutdoors)
        {
            bool isWildMap = loc is Forest or Mountain or Beach || loc.NameOrUniqueName.Contains("Backwoods") || loc.NameOrUniqueName.Contains("Woods");
            if (isWildMap && gameTick % 300 == 0 && Game1.random.NextDouble() < 0.20)
            {
                TriggerSurpriseGift(npc, comp, player, loc);
                return;
            }
        }

        // 2. Cuidado cuando estás agotado (< 25% energía, 1/day)
        if (!comp.DidTiredCareToday && player.Stamina < player.MaxStamina * 0.25f && (loc.IsOutdoors || loc is Farm))
        {
            TriggerTiredCare(npc, comp, player, loc);
            return;
        }

        // 3. Contemplar el atardecer juntos (17:30 – 18:50 en Beach, Mountain, Forest, Woods)
        if (!comp.DidSunsetMomentToday && Game1.timeOfDay >= 1730 && Game1.timeOfDay <= 1850)
        {
            bool isSunsetMap = loc is Beach or Mountain or Forest || loc.NameOrUniqueName.Contains("Woods");
            if (isSunsetMap && Vector2.Distance(npc.Tile, player.Tile) <= 5.5f)
            {
                // Track if player stood still for >= 240 ticks (4.0s)
                if (Vector2.Distance(player.Position, comp.LastSunsetStillPos) < 4f)
                {
                    comp.PlayerStillTicks += 10;
                    if (comp.PlayerStillTicks >= 240)
                    {
                        TriggerSunsetMoment(npc, comp, player, loc);
                        return;
                    }
                }
                else
                {
                    comp.LastSunsetStillPos = player.Position;
                    comp.PlayerStillTicks = 0;
                }
            }
        }

        // 4. Momento acogedor en lluvia o nieve (1/day)
        if (!comp.DidWeatherCozyToday && (loc.IsRainingHere() || loc.IsSnowingHere()) && loc.IsOutdoors)
        {
            if (gameTick % 600 == 0 && Game1.random.NextDouble() < 0.25)
            {
                int v = Game1.random.Next(1, 3);
                string weatherLine = I18n.Get($"companion.weather.cozy.v{v}", new { playerName = player.displayName ?? player.Name, npcName = npc.displayName });
                ModEntry.ConversationBridge.ShowCompanionBubble(npc, weatherLine, 3500);
                npc.doEmote(20);
                comp.DidWeatherCozyToday = true;
                return;
            }
        }

        // 5. Bebida calentita nocturna en casa (>= 20:00 en FarmHouse/Cabin, 1/noche)
        if (!comp.DidNightWarmDrinkTonight && Game1.timeOfDay >= 2000 && (loc is FarmHouse || loc.NameOrUniqueName.Contains("Cabin")))
        {
            Point kitchenSpot = loc is FarmHouse fh ? fh.getKitchenStandingSpot() : new Point(7, 24);
            float distToKitchen = Vector2.Distance(npc.TilePoint.ToVector2(), kitchenSpot.ToVector2());

            if (distToKitchen <= 4.0f && gameTick % 300 == 0 && Game1.random.NextDouble() < 0.35)
            {
                TriggerNightWarmDrink(npc, comp, player, loc);
            }
        }
    }

    private void TriggerSurpriseGift(NPC npc, CompanionState comp, Farmer player, GameLocation loc)
    {
        string name = npc.Name;
        string giftId = "(O)223"; // Cookie default

        if (name is "Abigail")
            giftId = Game1.random.NextDouble() < 0.5 ? "(O)80" : "(O)66"; // Quartz or Amethyst
        else if (name is "Elliott" or "Haley")
            giftId = Game1.random.NextDouble() < 0.5 ? "(O)393" : "(O)392"; // Coral or Nautilus Shell
        else if (name is "Penny" or "Leah")
            giftId = Game1.random.NextDouble() < 0.5 ? "(O)402" : "(O)22"; // Sweet Pea or Dandelion
        else if (name is "Harvey" or "Sebastian")
            giftId = "(O)395"; // Coffee
        else if (name is "Emily")
            giftId = "(O)428"; // Cloth or gemstone
        else if (name is "Maru")
            giftId = "(O)378"; // Copper ore
        else
            giftId = Game1.random.NextDouble() < 0.5 ? "(O)410" : "(O)223"; // Blackberry or Cookie

        var giftItem = ItemRegistry.Create(giftId, 1);
        if (giftItem == null) return;

        npc.Halt();
        npc.faceGeneralDirection(player.Position);
        npc.doEmote(16); // Exclamation mark

        if (player.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
        {
            player.addItemByMenuIfNecessaryElseHoldUp(giftItem);
            Game1.playSound("coin");
        }
        else
        {
            _multiplayerSync?.SendSpouseGift(player.UniqueMultiplayerID, giftItem.QualifiedItemId, 1, npc.Name);
        }

        int v = Game1.random.Next(1, 4);
        string line = I18n.Get($"companion.surprise.gift.v{v}", new { playerName = player.displayName ?? player.Name, npcName = npc.displayName, itemName = giftItem.DisplayName });
        ModEntry.ConversationBridge.ShowCompanionBubble(npc, line, 4000);

        comp.DidSurpriseGiftToday = true;
    }

    private void TriggerTiredCare(NPC npc, CompanionState comp, Farmer player, GameLocation loc)
    {
        npc.Halt();
        npc.faceGeneralDirection(player.Position);
        npc.doEmote(20); // Heart

        float restoreAmount = 25f;
        if (player.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
        {
            player.Stamina = Math.Min(player.MaxStamina, player.Stamina + restoreAmount);
            loc.playSound("healSound");
        }
        else
        {
            _multiplayerSync?.SendSupportHeal(player.UniqueMultiplayerID, 0, restoreAmount, npc.Name);
        }

        int v = Game1.random.Next(1, 3);
        string line = I18n.Get($"companion.care.tired.v{v}", new { playerName = player.displayName ?? player.Name, npcName = npc.displayName });
        ModEntry.ConversationBridge.ShowCompanionBubble(npc, line, 4000);

        comp.DidTiredCareToday = true;
    }

    private static void TriggerSunsetMoment(NPC npc, CompanionState comp, Farmer player, GameLocation loc)
    {
        float dist = Vector2.Distance(npc.Tile, player.Tile);
        if (dist <= 4.5f)
        {
            npc.Halt();
            npc.controller = null;
            npc.faceDirection(player.FacingDirection);
        }
        else
        {
            npc.faceGeneralDirection(player.Position);
        }

        npc.doEmote(20); // Heart

        int v = Game1.random.Next(1, 4);
        string line = I18n.Get($"companion.sunset.moment.v{v}", new { playerName = player.displayName ?? player.Name, npcName = npc.displayName });
        ModEntry.ConversationBridge.ShowCompanionBubble(npc, line, 4500);

        comp.DidSunsetMomentToday = true;
        comp.PlayerStillTicks = 0;
    }

    private void TriggerNightWarmDrink(NPC npc, CompanionState comp, Farmer player, GameLocation loc)
    {
        string drinkId = Game1.random.NextDouble() < 0.5 ? "(O)614" : "(O)395"; // Green tea or Coffee
        var drink = ItemRegistry.Create(drinkId, 1);
        if (drink == null) return;

        npc.Halt();
        npc.faceGeneralDirection(player.Position);
        npc.doEmote(20); // Heart

        if (player.UniqueMultiplayerID == Game1.player.UniqueMultiplayerID)
        {
            player.addItemByMenuIfNecessaryElseHoldUp(drink);
            Game1.playSound("coin");
        }
        else
        {
            _multiplayerSync?.SendSpouseGift(player.UniqueMultiplayerID, drink.QualifiedItemId, 1, npc.Name);
        }

        int v = Game1.random.Next(1, 3);
        string line = I18n.Get($"companion.night.warm_drink.v{v}", new { playerName = player.displayName ?? player.Name, npcName = npc.displayName, itemName = drink.DisplayName });
        ModEntry.ConversationBridge.ShowCompanionBubble(npc, line, 4000);

        comp.DidNightWarmDrinkTonight = true;
    }
}
