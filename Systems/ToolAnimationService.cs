using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.ItemTypeDefinitions;

namespace ValleyBuddies.Systems;

/// <summary>
/// Universal tool animations and affection visual bursts for any NPC (Vanilla, SVE, RSV, Custom)
/// using ephemeral TemporaryAnimatedSprite overlays without XML serialization risk.
/// </summary>
public static class ToolAnimationService
{
    private static bool OverlaysEnabled => ValleyBuddies.ModEntry.Config?.EnableToolVisualOverlays ?? true;

    /// <summary>
    /// Plays an animated pickaxe swing arc with debris sparks at the target tile.
    /// </summary>
    public static void PlayPickaxeSwing(GameLocation? loc, NPC? npc, Point targetTile, bool isMasterMiner = false, bool broadcast = true)
    {
        if (loc == null || npc == null || !OverlaysEnabled) return;

        string toolId = isMasterMiner ? "(T)GoldPickaxe" : "(T)Pickaxe";
        ParsedItemData? data = ItemRegistry.GetData(toolId) ?? ItemRegistry.GetData("(T)Pickaxe");

        Vector2 tileCenter = new Vector2(targetTile.X * 64f + 32f, targetTile.Y * 64f + 32f);
        Vector2 npcPos = npc.Position;

        // Position overlay near NPC hand based on facing direction
        Vector2 toolPos = npc.FacingDirection switch
        {
            0 => new Vector2(npcPos.X + 8f, npcPos.Y - 24f),  // Up
            1 => new Vector2(npcPos.X + 40f, npcPos.Y + 8f),  // Right
            2 => new Vector2(npcPos.X + 16f, npcPos.Y + 36f), // Down
            _ => new Vector2(npcPos.X - 16f, npcPos.Y + 8f)   // Left
        };

        float startRot = npc.FacingDirection switch
        {
            0 => -0.5f,
            1 => -0.8f,
            2 => 0.2f,
            _ => 0.8f
        };
        float rotChange = (npc.FacingDirection == 3) ? -0.15f : 0.15f;
        bool flipped = (npc.FacingDirection == 3);

        if (data != null)
        {
            try
            {
                var sprite = new TemporaryAnimatedSprite(
                    data.TextureName,
                    data.GetSourceRect(),
                    animationInterval: 18f,
                    animationLength: 1,
                    numberOfLoops: 1,
                    position: toolPos,
                    flicker: false,
                    flipped: flipped,
                    layerDepth: (npc.StandingPixel.Y + 64) / 10000f,
                    alphaFade: 0.05f,
                    color: Color.White,
                    scale: 3.5f,
                    scaleChange: 0f,
                    rotation: startRot,
                    rotationChange: rotChange
                )
                {
                    totalNumberOfLoops = 1,
                    holdLastFrame = false
                };
                loc.temporarySprites.Add(sprite);
            }
            catch { }
        }

        // Sparks & rock dust on target tile
        try
        {
            loc.temporarySprites.Add(new TemporaryAnimatedSprite(46, tileCenter, Color.White, 8, false, 60f) { scale = 2.5f });
            loc.temporarySprites.Add(new TemporaryAnimatedSprite(12, tileCenter + new Vector2(Game1.random.Next(-8, 9), Game1.random.Next(-8, 9)), Color.LightGoldenrodYellow, 8, false, 70f));
            if (broadcast) loc.playSound("daggerswipe");
        }
        catch { }

        if (broadcast && Game1.IsMultiplayer)
        {
            ValleyBuddies.ModEntry.MultiplayerSync?.BroadcastToolVisualEffect(loc.NameOrUniqueName, npc.Name, "pickaxe", targetTile.X, targetTile.Y, isMasterMiner);
        }
    }

    /// <summary>
    /// Plays an animated axe swing arc with woodchip debris and tree shake if applicable.
    /// </summary>
    public static void PlayAxeSwing(GameLocation? loc, NPC? npc, Point targetTile, bool isMasterArtisan = false, bool broadcast = true, bool playSound = true)
    {
        if (loc == null || npc == null || !OverlaysEnabled) return;

        string toolId = isMasterArtisan ? "(T)SteelAxe" : "(T)Axe";
        ParsedItemData? data = ItemRegistry.GetData(toolId) ?? ItemRegistry.GetData("(T)Axe");

        Vector2 tileCenter = new Vector2(targetTile.X * 64f + 32f, targetTile.Y * 64f + 32f);
        Vector2 npcPos = npc.Position;

        Vector2 toolPos = npc.FacingDirection switch
        {
            0 => new Vector2(npcPos.X + 8f, npcPos.Y - 24f),
            1 => new Vector2(npcPos.X + 40f, npcPos.Y + 8f),
            2 => new Vector2(npcPos.X + 16f, npcPos.Y + 36f),
            _ => new Vector2(npcPos.X - 16f, npcPos.Y + 8f)
        };

        float startRot = npc.FacingDirection switch
        {
            0 => -0.5f,
            1 => -0.8f,
            2 => 0.2f,
            _ => 0.8f
        };
        float rotChange = (npc.FacingDirection == 3) ? -0.15f : 0.15f;
        bool flipped = (npc.FacingDirection == 3);

        if (data != null)
        {
            try
            {
                var sprite = new TemporaryAnimatedSprite(
                    data.TextureName,
                    data.GetSourceRect(),
                    animationInterval: 18f,
                    animationLength: 1,
                    numberOfLoops: 1,
                    position: toolPos,
                    flicker: false,
                    flipped: flipped,
                    layerDepth: (npc.StandingPixel.Y + 64) / 10000f,
                    alphaFade: 0.05f,
                    color: Color.White,
                    scale: 3.5f,
                    scaleChange: 0f,
                    rotation: startRot,
                    rotationChange: rotChange
                )
                {
                    totalNumberOfLoops = 1,
                    holdLastFrame = false
                };
                loc.temporarySprites.Add(sprite);
            }
            catch { }
        }

        // Woodchips and tree shake
        try
        {
            loc.temporarySprites.Add(new TemporaryAnimatedSprite(12, tileCenter + new Vector2(Game1.random.Next(-8, 9), Game1.random.Next(-8, 9)), Color.SandyBrown, 8, false, 70f));
            if (broadcast && playSound) loc.playSound("axchop");
        }
        catch { }

        if (broadcast && Game1.IsMultiplayer)
        {
            ValleyBuddies.ModEntry.MultiplayerSync?.BroadcastToolVisualEffect(loc.NameOrUniqueName, npc.Name, "axe", targetTile.X, targetTile.Y, isMasterArtisan);
        }
    }

    /// <summary>
    /// Plays an animated watering can pouring animation with water droplets over the target tile.
    /// </summary>
    public static void PlayWateringCanPour(GameLocation? loc, NPC? npc, Point targetTile, bool broadcast = true, bool playSound = true)
    {
        if (loc == null || npc == null || !OverlaysEnabled) return;

        ParsedItemData? data = ItemRegistry.GetData("(T)WateringCan");
        Vector2 tileCenter = new Vector2(targetTile.X * 64f + 32f, targetTile.Y * 64f + 32f);
        Vector2 npcPos = npc.Position;

        Vector2 toolPos = npc.FacingDirection switch
        {
            0 => new Vector2(npcPos.X + 12f, npcPos.Y - 12f),
            1 => new Vector2(npcPos.X + 36f, npcPos.Y + 16f),
            2 => new Vector2(npcPos.X + 18f, npcPos.Y + 36f),
            _ => new Vector2(npcPos.X - 12f, npcPos.Y + 16f)
        };

        float rot = (npc.FacingDirection == 3) ? -0.4f : 0.4f;
        bool flipped = (npc.FacingDirection == 3);

        if (data != null)
        {
            try
            {
                var sprite = new TemporaryAnimatedSprite(
                    data.TextureName,
                    data.GetSourceRect(),
                    animationInterval: 300f,
                    animationLength: 1,
                    numberOfLoops: 1,
                    position: toolPos,
                    flicker: false,
                    flipped: flipped,
                    layerDepth: (npc.StandingPixel.Y + 64) / 10000f,
                    alphaFade: 0.03f,
                    color: Color.White,
                    scale: 3.2f,
                    scaleChange: 0f,
                    rotation: rot,
                    rotationChange: 0f
                )
                {
                    totalNumberOfLoops = 1,
                    holdLastFrame = false
                };
                loc.temporarySprites.Add(sprite);
            }
            catch { }
        }

        // 4-5 blue water droplets falling on the tile
        try
        {
            loc.temporarySprites.Add(new TemporaryAnimatedSprite(13, tileCenter, Color.LightSkyBlue, 6, false, 70f) { scale = 2.2f });
            loc.temporarySprites.Add(new TemporaryAnimatedSprite(13, tileCenter + new Vector2(8f, -4f), Color.LightCyan, 6, false, 80f) { delayBeforeAnimationStart = 60, scale = 1.8f });
            if (broadcast && playSound) loc.playSound("wateringCan");
        }
        catch { }

        if (broadcast && Game1.IsMultiplayer)
        {
            ValleyBuddies.ModEntry.MultiplayerSync?.BroadcastToolVisualEffect(loc.NameOrUniqueName, npc.Name, "water", targetTile.X, targetTile.Y);
        }
    }

    /// <summary>
    /// Plays an animated weapon swing sweep towards the target position.
    /// </summary>
    public static void PlayWeaponSwing(GameLocation? loc, NPC? npc, Vector2 targetPixelPos, bool broadcast = true)
    {
        if (loc == null || npc == null || !OverlaysEnabled) return;

        ParsedItemData? data = ItemRegistry.GetData("(W)4") ?? ItemRegistry.GetData("(W)0");
        Vector2 npcPos = npc.Position;

        Vector2 diff = targetPixelPos - npcPos;
        float angle = (float)Math.Atan2(diff.Y, diff.X);

        if (data != null)
        {
            try
            {
                var sprite = new TemporaryAnimatedSprite(
                    data.TextureName,
                    data.GetSourceRect(),
                    animationInterval: 20f,
                    animationLength: 1,
                    numberOfLoops: 1,
                    position: npcPos + new Vector2(16f, 16f),
                    flicker: false,
                    flipped: false,
                    layerDepth: (npc.StandingPixel.Y + 64) / 10000f,
                    alphaFade: 0.06f,
                    color: Color.White,
                    scale: 3.5f,
                    scaleChange: 0f,
                    rotation: angle - 0.7f,
                    rotationChange: 0.15f
                )
                {
                    totalNumberOfLoops = 1,
                    holdLastFrame = false
                };
                loc.temporarySprites.Add(sprite);
            }
            catch { }
        }

        try
        {
            if (broadcast) loc.playSound("daggerswipe");
        }
        catch { }

        if (broadcast && Game1.IsMultiplayer)
        {
            Point p = new Point((int)(targetPixelPos.X / 64f), (int)(targetPixelPos.Y / 64f));
            ValleyBuddies.ModEntry.MultiplayerSync?.BroadcastToolVisualEffect(loc.NameOrUniqueName, npc.Name, "weapon", p.X, p.Y);
        }
    }

    /// <summary>
    /// Plays gentle columns of steam rising from the stove/kitchen.
    /// </summary>
    public static void PlayCookingSteamEffect(GameLocation? loc, Point stoveTile, bool broadcast = true)
    {
        if (loc == null || !OverlaysEnabled) return;

        Vector2 stovePixel = new Vector2(stoveTile.X * 64f + 24f, stoveTile.Y * 64f - 16f);
        try
        {
            loc.temporarySprites.Add(new TemporaryAnimatedSprite(10, stovePixel, Color.White * 0.7f, 8, false, 90f)
            {
                scale = 2.5f,
                motion = new Vector2(0f, -0.6f)
            });
            loc.temporarySprites.Add(new TemporaryAnimatedSprite(10, stovePixel + new Vector2(12f, -12f), Color.White * 0.5f, 8, false, 110f)
            {
                delayBeforeAnimationStart = 140,
                scale = 2.0f,
                motion = new Vector2(0.2f, -0.7f)
            });
            if (broadcast) loc.playSound("fireball");
        }
        catch { }

        if (broadcast && Game1.IsMultiplayer)
        {
            ValleyBuddies.ModEntry.MultiplayerSync?.BroadcastToolVisualEffect(loc.NameOrUniqueName, string.Empty, "steam", stoveTile.X, stoveTile.Y);
        }
    }

    /// <summary>
    /// Plays gentle dishwashing bubble particles and subtle water sloshing at the kitchen sink / counter.
    /// </summary>
    public static void PlayDishwashingBubbles(GameLocation? loc, Point counterTile, bool broadcast = true)
    {
        if (loc == null || !OverlaysEnabled) return;

        try
        {
            Vector2 tileCenter = new Vector2(counterTile.X * 64f + 32f, counterTile.Y * 64f + 16f);

            for (int i = 0; i < 4; i++)
            {
                loc.temporarySprites.Add(new TemporaryAnimatedSprite(
                    @"LooseSprites\Cursors",
                    new Rectangle(372, 1956, 10, 10),
                    tileCenter + new Vector2(Game1.random.Next(-16, 17), Game1.random.Next(-10, 11)),
                    false,
                    0.002f,
                    Color.LightCyan * 0.8f)
                {
                    scale = 2.0f + (float)Game1.random.NextDouble() * 1.0f,
                    motion = new Vector2((float)(Game1.random.NextDouble() * 0.8 - 0.4), -0.5f - (float)Game1.random.NextDouble() * 0.5f),
                    acceleration = new Vector2(0f, -0.01f),
                    interval = 80f,
                    totalNumberOfLoops = 1,
                    animationLength = 4,
                    delayBeforeAnimationStart = i * 60
                });
            }

            if (broadcast && Game1.random.NextDouble() < 0.35)
            {
                loc.playSound("waterSlosh");
            }
        }
        catch { }

        if (broadcast && Game1.IsMultiplayer)
        {
            ValleyBuddies.ModEntry.MultiplayerSync?.BroadcastToolVisualEffect(loc.NameOrUniqueName, string.Empty, "dishes", counterTile.X, counterTile.Y);
        }
    }

    /// <summary>
    /// Plays gentle tidying sparkles and dust puffs at furniture, counters, or bookcases.
    /// </summary>
    public static void PlayTidyingSparkles(GameLocation? loc, Point furnitureTile, bool broadcast = true)
    {
        if (loc == null || !OverlaysEnabled) return;

        try
        {
            Vector2 tileCenter = new Vector2(furnitureTile.X * 64f + 32f, furnitureTile.Y * 64f + 32f);

            loc.temporarySprites.Add(new TemporaryAnimatedSprite(
                @"LooseSprites\Cursors",
                new Rectangle(Game1.random.NextDouble() < 0.5 ? 356 : 372, 1956, 10, 10),
                tileCenter + new Vector2(Game1.random.Next(-18, 19), Game1.random.Next(-18, 19)),
                false,
                0.002f,
                Color.LightYellow * 0.85f)
            {
                scale = 2.0f,
                motion = new Vector2((float)(Game1.random.NextDouble() * 0.6 - 0.3), -0.4f),
                interval = 70f,
                totalNumberOfLoops = 1,
                animationLength = 4
            });

            if (broadcast && Game1.random.NextDouble() < 0.25)
            {
                loc.playSound("shiny4");
            }
        }
        catch { }

        if (broadcast && Game1.IsMultiplayer)
        {
            ValleyBuddies.ModEntry.MultiplayerSync?.BroadcastToolVisualEffect(loc.NameOrUniqueName, string.Empty, "tidy", furnitureTile.X, furnitureTile.Y);
        }
    }

    /// <summary>
    /// Plays an affectionate visual burst (kiss or hug) with floating hearts, real sprite animation, and gentle sound.
    /// </summary>
    public static void PlayAffectionBurst(GameLocation? loc, NPC? npc, Farmer? target, bool isKiss, bool broadcast = true, bool playPlayerLean = true)
    {
        if (loc == null || npc == null || target == null) return;

        try
        {
            if (isKiss)
            {
                var charData = npc.GetData();
                int kissFrame = charData?.KissSpriteIndex ?? 28;
                bool kissFacingRight = charData?.KissSpriteFacingRight ?? true;

                bool playerIsLeft = target.StandingPixel.X <= npc.StandingPixel.X;
                int npcFacing = playerIsLeft ? 3 : 1;
                int playerFacing = playerIsLeft ? 1 : 3;

                npc.faceDirection(npcFacing);

                bool flipNpc = (npcFacing == 1) ? !kissFacingRight : kissFacingRight;

                int totalFrames = (npc.Sprite?.Texture != null)
                    ? (npc.Sprite.Texture.Width / Math.Max(1, npc.Sprite.SpriteWidth)) * (npc.Sprite.Texture.Height / Math.Max(1, npc.Sprite.SpriteHeight))
                    : 0;
                bool validKissFrame = totalFrames > 0 && kissFrame < totalFrames;

                if (broadcast)
                {
                    npc.Halt();
                    npc.controller = null;
                    npc.movementPause = 650;
                }

                if (Execution.NpcActionExecutor.Instance != null && Execution.NpcActionExecutor.Instance.TryGetCompanionState(npc.Name, out var comp))
                {
                    comp.AffectionAnimationUntilTick = ModEntry.CurrentTick + 42UL; // ~650 ms a 60 ticks/s
                }

                if (validKissFrame && npc.Sprite != null)
                {
                    npc.Sprite.setCurrentAnimation(new List<FarmerSprite.AnimationFrame>
                    {
                        new FarmerSprite.AnimationFrame(kissFrame, 650, secondaryArm: false, flip: flipNpc, frameBehavior: null, behaviorAtEndOfFrame: true)
                    });
                }

                if (playPlayerLean && target == Game1.player && !target.isRidingHorse() && !target.UsingTool)
                {
                    target.completelyStopAnimatingOrDoingAction();
                    target.Halt();
                    target.faceDirection(playerFacing);
                    target.FarmerSprite.setCurrentSingleFrame(101, 650, secondaryArm: false, flip: playerFacing == 3);
                    target.freezePause = 650;
                }
            }
            else
            {
                if (broadcast)
                {
                    npc.Halt();
                    npc.controller = null;
                    npc.faceGeneralDirection(target.Position);
                    npc.movementPause = 500;
                }
                npc.jumpWithoutSound(2.2f);

                if (Execution.NpcActionExecutor.Instance != null && Execution.NpcActionExecutor.Instance.TryGetCompanionState(npc.Name, out var comp))
                {
                    comp.AffectionAnimationUntilTick = ModEntry.CurrentTick + 32UL;
                }
            }

            Vector2 midPos = new Vector2(
                (npc.Position.X + target.Position.X) / 2f + 16f,
                Math.Min(npc.Position.Y, target.Position.Y) - 16f
            );

            // Floating heart particles
            loc.temporarySprites.Add(new TemporaryAnimatedSprite(
                @"LooseSprites\Cursors",
                new Rectangle(211, 428, 7, 6),
                midPos,
                false,
                0.015f,
                Color.White
            )
            {
                scale = 4f,
                motion = new Vector2(0f, -1.2f),
                acceleration = new Vector2(0f, -0.02f)
            });

            loc.temporarySprites.Add(new TemporaryAnimatedSprite(
                @"LooseSprites\Cursors",
                new Rectangle(211, 428, 7, 6),
                midPos + new Vector2(16f, 8f),
                false,
                0.015f,
                Color.White
            )
            {
                scale = 3.5f,
                motion = new Vector2(0.2f, -1.4f),
                delayBeforeAnimationStart = 120
            });

            if (broadcast)
            {
                loc.playSound("dwop");
                if (!npc.IsEmoting) npc.doEmote(20); // Heart
                if (!target.IsEmoting) target.doEmote(20);
            }
        }
        catch { }

        if (broadcast && Game1.IsMultiplayer)
        {
            Point p = target.TilePoint;
            ValleyBuddies.ModEntry.MultiplayerSync?.BroadcastToolVisualEffect(
                loc.NameOrUniqueName,
                npc.Name,
                isKiss ? "kiss" : "hug",
                p.X,
                p.Y,
                targetFarmerId: target.UniqueMultiplayerID);
        }
    }

    /// <summary>
    /// Handles incoming remote visual effect from a peer on the same location.
    /// </summary>
    public static void HandleRemoteVisualEffect(Multiplayer.ToolVisualEffectMessage msg)
    {
        if (msg == null || Game1.currentLocation?.NameOrUniqueName != msg.LocationName) return;

        var loc = Game1.currentLocation;
        var npc = !string.IsNullOrEmpty(msg.NpcName) ? loc.characters.FirstOrDefault(c => c.Name.Equals(msg.NpcName, StringComparison.OrdinalIgnoreCase)) : null;
        Point targetTile = new Point(msg.TargetTileX, msg.TargetTileY);

        switch (msg.EffectKind)
        {
            case "pickaxe":
                PlayPickaxeSwing(loc, npc, targetTile, msg.ExtraFlag, broadcast: false);
                break;
            case "axe":
                PlayAxeSwing(loc, npc, targetTile, msg.ExtraFlag, broadcast: false);
                break;
            case "water":
                PlayWateringCanPour(loc, npc, targetTile, broadcast: false);
                break;
            case "weapon":
                PlayWeaponSwing(loc, npc, new Vector2(targetTile.X * 64f + 32f, targetTile.Y * 64f + 32f), broadcast: false);
                break;
            case "steam":
                PlayCookingSteamEffect(loc, targetTile, broadcast: false);
                break;
            case "dishes":
                PlayDishwashingBubbles(loc, targetTile, broadcast: false);
                break;
            case "tidy":
                PlayTidyingSparkles(loc, targetTile, broadcast: false);
                break;
            case "kiss":
            {
                var realTarget = msg.TargetFarmerId != 0 ? Game1.GetPlayer(msg.TargetFarmerId, onlyOnline: true) : null;
                if (npc != null && realTarget != null)
                    PlayAffectionBurst(loc, npc, realTarget, isKiss: true, broadcast: false, playPlayerLean: false);
                break;
            }
            case "hug":
            {
                var realTarget = msg.TargetFarmerId != 0 ? Game1.GetPlayer(msg.TargetFarmerId, onlyOnline: true) : null;
                if (npc != null && realTarget != null)
                    PlayAffectionBurst(loc, npc, realTarget, isKiss: false, broadcast: false, playPlayerLean: false);
                break;
            }
            case "fish_cast":
                if (npc != null) PlayFishingCastAndBobber(loc, npc, targetTile, msg.ExtraFlag, broadcast: false);
                break;
        }
    }

    /// <summary>
    /// Plays an animated fishing cast overlay with splash at the target water tile.
    /// </summary>
    public static void PlayFishingCastAndBobber(GameLocation? loc, NPC? npc, Point waterTile, bool isExpert = false, bool broadcast = true)
    {
        if (loc == null || npc == null || !OverlaysEnabled) return;

        string toolId = isExpert ? "(T)IridiumRod" : "(T)BambooPole";
        ParsedItemData? data = ItemRegistry.GetData(toolId) ?? ItemRegistry.GetData("(T)BambooPole");

        Vector2 splashPos = new Vector2(waterTile.X * 64f + 32f, waterTile.Y * 64f + 32f);
        Vector2 npcPos = npc.Position;

        Vector2 toolPos = npc.FacingDirection switch
        {
            0 => new Vector2(npcPos.X + 8f, npcPos.Y - 24f),
            1 => new Vector2(npcPos.X + 36f, npcPos.Y + 8f),
            2 => new Vector2(npcPos.X + 16f, npcPos.Y + 36f),
            _ => new Vector2(npcPos.X - 16f, npcPos.Y + 8f)
        };

        float startRot = npc.FacingDirection switch
        {
            0 => -0.6f,
            1 => -0.8f,
            2 => 0.3f,
            _ => 0.8f
        };
        float rotChange = (npc.FacingDirection == 3) ? -0.1f : 0.1f;
        bool flipped = (npc.FacingDirection == 3);

        if (data != null)
        {
            try
            {
                var sprite = new TemporaryAnimatedSprite(
                    data.TextureName,
                    data.GetSourceRect(),
                    animationInterval: 30f,
                    animationLength: 1,
                    numberOfLoops: 1,
                    position: toolPos,
                    flicker: false,
                    flipped: flipped,
                    layerDepth: (npc.StandingPixel.Y + 64) / 10000f,
                    alphaFade: 0.04f,
                    color: Color.White,
                    scale: 3.2f,
                    scaleChange: 0f,
                    rotation: startRot,
                    rotationChange: rotChange
                )
                {
                    totalNumberOfLoops = 1,
                    holdLastFrame = false
                };
                loc.temporarySprites.Add(sprite);
            }
            catch { }
        }

        try
        {
            if (broadcast) loc.playSound("cast");
            // Water splash at target tile
            loc.temporarySprites.Add(new TemporaryAnimatedSprite(28, splashPos, Color.White, 4, false, 80f) { scale = 1.5f });
            if (broadcast) loc.playSound("dropItemInWater");
            PlayFishingBobberPulse(loc, waterTile);
        }
        catch { }

        if (broadcast && Game1.IsMultiplayer)
        {
            ValleyBuddies.ModEntry.MultiplayerSync?.BroadcastToolVisualEffect(loc.NameOrUniqueName, npc.Name, "fish_cast", waterTile.X, waterTile.Y, isExpert);
        }
    }

    /// <summary>
    /// Plays an ephemeral water ripple and gentle bobber sprite at the water tile while companion fishes.
    /// </summary>
    public static void PlayFishingBobberPulse(GameLocation? loc, Point waterTile)
    {
        if (loc == null || !OverlaysEnabled) return;
        Vector2 splashPos = new Vector2(waterTile.X * 64f + 32f, waterTile.Y * 64f + 32f);
        try
        {
            // Subtle water ripple
            loc.temporarySprites.Add(new TemporaryAnimatedSprite(29, splashPos, Color.LightSkyBlue, 4, false, 100f) { scale = 1.2f });

            // Ephemeral bobbing float
            float bobOffset = (float)Math.Sin(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 240.0) * 2.5f;
            loc.temporarySprites.Add(new TemporaryAnimatedSprite(
                "LooseSprites\\Cursors",
                new Rectangle(653, 2180, 4, 7),
                splashPos + new Vector2(-6f, -12f + bobOffset),
                false,
                0f,
                Color.White)
            {
                scale = 3.5f,
                totalNumberOfLoops = 1,
                interval = 2500f,
                holdLastFrame = false,
                layerDepth = (waterTile.Y * 64f + 32f) / 10000f + 0.001f
            });
        }
        catch { }
    }
}
