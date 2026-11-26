using System;
using ValleyBuddies.Execution;
using ValleyBuddies.Utilities;
using StardewValley;

namespace ValleyBuddies.Actions;

// ══════════════════════════════════════════════════════════════
//  MOVEMENT & COMPANY ACTIONS
// ══════════════════════════════════════════════════════════════

public sealed class FollowPlayerAction : IPhysicalAction
{
    public string Name              => "follow_player";
    public ActionCategory Category  => ActionCategory.Movement;
    public string PromptDescription => "Start following the farmer at a short distance. You keep up with them as they move, even if they go to another map (if the friendship is high enough). Say something warm when you decide to follow.";
    public string PromptSchema       => "{\"action\":\"follow_player\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFollow;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        int minH = GetMinHearts(ctx);
        if (hearts < minH) return ActionResult.Fail($"needs {minH} hearts to follow");
        return ActionResult.Ok("will follow player")
            .WithHud("hud.following", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new NpcJob { NpcName = req.NpcName, Kind = JobKind.Follow, CreatedTick = Game1.ticks };
}

public sealed class StopFollowingAction : IPhysicalAction
{
    public string Name              => "stop_following";
    public ActionCategory Category  => ActionCategory.Movement;
    public string PromptDescription => "Stop following the farmer and return to your normal routine.";
    public string PromptSchema       => "{\"action\":\"stop_following\"}";
    public int    MinHearts          => 0;
    public bool   IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts) =>
        ActionResult.Ok("stopped following")
            .WithHud("hud.stop-following", new { name = req.NpcName });

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        return new NpcJob { NpcName = req.NpcName, Kind = JobKind.StopFollowing, CreatedTick = Game1.ticks };
    }
}

public sealed class ComeHereAction : IPhysicalAction
{
    public string Name              => "come_here";
    public ActionCategory Category  => ActionCategory.Movement;
    public string PromptDescription => "Walk directly to the farmer's current position and stand in front of them.";
    public string PromptSchema       => "{\"action\":\"come_here\"}";
    public int    MinHearts          => 0;
    public bool   IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts) =>
        ActionResult.Ok("walking to player")
            .WithHud("hud.come-here", new { name = req.NpcName });

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new()
        {
            NpcName = req.NpcName, Kind = JobKind.ComeHere, CreatedTick = Game1.ticks,
        };
}

public sealed class GoToLocationAction : IPhysicalAction
{
    public string Name              => "go_to_location";
    public ActionCategory Category  => ActionCategory.Movement;
    public string PromptDescription => "Travel to a specific location in the valley. Supported locations include: ArchaeologyHouse (Museum/Library), Saloon, Beach, Town, Farm, Mountain, Forest, Hospital, Mine, ManorHouse, CommunityCenter, BathHouse.";
    public string PromptSchema       => "{\"action\":\"go_to_location\", \"location\":\"<location name>\"}";
    public int    MinHearts          => 0;
    public bool   IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        string? loc = req.GetString("location");
        if (string.IsNullOrWhiteSpace(loc)) return ActionResult.Fail("location not specified");

        var resolvedLoc = LocationResolver.Resolve(loc);
        if (resolvedLoc == null) return ActionResult.Fail($"unknown location '{loc}'");

        return ActionResult.Ok($"going to {resolvedLoc.NameOrUniqueName}").WithHud("hud.go-location", new { name = req.NpcName, location = resolvedLoc.NameOrUniqueName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        string loc = req.GetString("location") ?? "";
        var resolvedLoc = LocationResolver.Resolve(loc);

        var job = new NpcJob { NpcName = req.NpcName, Kind = JobKind.GoToLocation, CreatedTick = Game1.ticks };
        job.Data["locationName"] = resolvedLoc?.NameOrUniqueName ?? loc;
        return job;
    }
}

public sealed class VisitFarmAction : IPhysicalAction
{
    public string Name              => "visit_player_farm";
    public ActionCategory Category  => ActionCategory.Movement;
    public string PromptDescription => "Walk or travel to the player's farm and spend time hanging out, exploring, and visiting with the farmer.";
    public string PromptSchema       => "{\"action\":\"visit_player_farm\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsVisitFarm;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to visit the farm");
        var npc = Game1.getCharacterFromName(req.NpcName);
        if (Execution.NpcActionExecutor.IsMarriedToAnyFarmer(npc))
            return ActionResult.Fail("already lives on the farm as spouse/roommate");
        return ActionResult.Ok("visiting farm").WithHud("hud.visit-farm", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        var job = new NpcJob { NpcName = req.NpcName, Kind = JobKind.VisitFarm, CreatedTick = Game1.ticks };
        var actingFarmer = req.OwnerPlayerId != 0 ? (Game1.GetPlayer(req.OwnerPlayerId, onlyOnline: true) ?? Game1.player) : Game1.player;
        string locName = "Farm";
        if (actingFarmer?.currentLocation != null)
        {
            var pLoc = actingFarmer.currentLocation;
            if (pLoc is Farm || pLoc is StardewValley.Locations.FarmHouse || pLoc.NameOrUniqueName.Contains("Cabin", StringComparison.OrdinalIgnoreCase))
            {
                locName = pLoc.NameOrUniqueName;
            }
        }
        job.Data["locationName"] = locName;
        return job;
    }
}

public sealed class SitOrRestAction : IPhysicalAction
{
    public string Name              => "sit_or_rest";
    public ActionCategory Category  => ActionCategory.Movement;
    public string PromptDescription => "Find a nearby seat or resting spot and sit down, taking a break from moving around.";
    public string PromptSchema       => "{\"action\":\"sit_or_rest\"}";
    public int    MinHearts          => 0;
    public bool   IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;
    public ActionResult Validate(ActionRequest r, ActionContext c, int h) => ActionResult.Ok("resting");
    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.SitOrRest, CreatedTick = Game1.ticks };
}

public sealed class GuardOrWaitAction : IPhysicalAction
{
    public string Name              => "guard_or_wait_here";
    public ActionCategory Category  => ActionCategory.Movement;
    public string PromptDescription => "Stay exactly in your current position and wait until further notice. Use when you agree to guard a spot or wait for the farmer.";
    public string PromptSchema       => "{\"action\":\"guard_or_wait_here\"}";
    public int    MinHearts          => 0;
    public bool   IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;
    public ActionResult Validate(ActionRequest r, ActionContext c, int h) => ActionResult.Ok("waiting here");
    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.GuardHere, CreatedTick = Game1.ticks };
}

public sealed class EscortHomeAction : IPhysicalAction
{
    public string Name              => "escort_home";
    public ActionCategory Category  => ActionCategory.Movement;
    public string PromptDescription => "Walk with the farmer to their home and say goodbye at the door.";
    public string PromptSchema       => "{\"action\":\"escort_home\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsEscort;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to escort home");
        return ActionResult.Ok("escorting home").WithHud("hud.escort-home", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.EscortHome, CreatedTick = Game1.ticks };
}
