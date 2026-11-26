using System;
using ValleyBuddies.Execution;
using StardewValley;

namespace ValleyBuddies.Actions;

// ══════════════════════════════════════════════════════════════
//  ADVENTURE & HOMESTEAD ACTIONS (v2.0)
// ══════════════════════════════════════════════════════════════

public sealed class CombatAssistAction : IPhysicalAction
{
    public string Name              => "combat_assist";
    public ActionCategory Category  => ActionCategory.Adventure;
    public string PromptDescription => "Assist the farmer in combat inside the mines or dangerous areas by attacking nearby monsters.";
    public string PromptSchema       => "{\"action\":\"combat_assist\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFollow;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.EnableCompanionCombat;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts for combat assistance");
        return ActionResult.Ok($"{req.NpcName} te asistirá en combate");
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.CombatAssist, CreatedTick = Game1.ticks };
}

public sealed class FishTogetherAction : IPhysicalAction
{
    public string Name              => "fish_together";
    public ActionCategory Category  => ActionCategory.Adventure;
    public string PromptDescription => "Accompany the farmer to go fishing near water and enjoy the moment together.";
    public string PromptSchema       => "{\"action\":\"fish_together\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFollow;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to fish together");
        return ActionResult.Ok($"{req.NpcName} pescará contigo");
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.FishTogether, CreatedTick = Game1.ticks };
}

public sealed class ScoutMinesAction : IPhysicalAction
{
    public string Name              => "scout_mines";
    public ActionCategory Category  => ActionCategory.Adventure;
    public string PromptDescription => "Explore the mine floor looking for ladders, mineral deposits, or safe exits.";
    public string PromptSchema       => "{\"action\":\"scout_mines\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFollow;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to scout mines");
        return ActionResult.Ok($"{req.NpcName} explorará el nivel de la mina");
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.ScoutMines, CreatedTick = Game1.ticks };
}

public sealed class CookMealAction : IPhysicalAction
{
    public string Name              => "cook_meal";
    public ActionCategory Category  => ActionCategory.Adventure;
    public string PromptDescription => "Cook a homemade dish or snack from available ingredients to share with the farmer.";
    public string PromptSchema       => "{\"action\":\"cook_meal\", \"dish\":\"<optional dish name>\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFollow;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to cook a meal");
        return ActionResult.Ok($"{req.NpcName} preparará comida para compartir");
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        var job = new NpcJob { NpcName = req.NpcName, Kind = JobKind.CookMeal, CreatedTick = Game1.ticks };
        string? dish = req.GetStringAny("dish", "meal", "platillo", "comida");
        if (!string.IsNullOrWhiteSpace(dish))
            job.Data["dishName"] = dish;
        return job;
    }
}

public sealed class SetupCampAction : IPhysicalAction
{
    public string Name              => "setup_camp";
    public ActionCategory Category  => ActionCategory.Adventure;
    public string PromptDescription => "Set up a campsite with a campfire and tent to spend the night outdoors.";
    public string PromptSchema       => "{\"action\":\"setup_camp\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFollow;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to set up camp");

        var player = req.OwnerPlayerId != 0
            ? (Game1.GetPlayer(req.OwnerPlayerId, onlyOnline: true) ?? Game1.player)
            : Game1.player;
        var loc = player?.currentLocation;
        if (loc == null || !loc.IsOutdoors || loc is StardewValley.Locations.FarmHouse || loc is StardewValley.Locations.Cabin)
        {
            return ActionResult.Fail("Cannot set up camp indoors");
        }
        return ActionResult.Ok($"{req.NpcName} acampará al aire libre");
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.Campfire, CreatedTick = Game1.ticks };
}

