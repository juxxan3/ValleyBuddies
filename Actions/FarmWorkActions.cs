using System;
using ValleyBuddies.Execution;
using StardewValley;

namespace ValleyBuddies.Actions;

// ══════════════════════════════════════════════════════════════
//  FARM WORK & HELP ACTIONS
// ══════════════════════════════════════════════════════════════

public sealed class WaterCropsAction : IPhysicalAction
{
    public string Name              => "water_crops";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Find dry crops nearby on the farm and water them. The game will walk you to the farm to water them if you agree.";
    public string PromptSchema       => "{\"action\":\"water_crops\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to help with farm work");
        return ActionResult.Ok($"{req.NpcName} watered some crops")
            .WithHud("hud.water-crops", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.WaterCrops, CreatedTick = Game1.ticks };
}

public sealed class HarvestCropsAction : IPhysicalAction
{
    public string Name              => "harvest_crops";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Find ready-to-harvest, fully-grown crops on the farm, harvest them, and store the produce in the nearest farm chest (or give it to the farmer).";
    public string PromptSchema       => "{\"action\":\"harvest_crops\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to help harvest crops");
        return ActionResult.Ok($"{req.NpcName} is harvesting crops")
            .WithHud("hud.harvest-crops", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.HarvestCrops, CreatedTick = Game1.ticks };
}

public sealed class HarvestForageAction : IPhysicalAction
{
    public string Name              => "harvest_forage";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Gather nearby forage items (berries, flowers, mushrooms, shells) and hand them to the farmer.";
    public string PromptSchema       => "{\"action\":\"harvest_forage\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to forage for the farmer");
        return ActionResult.Ok($"{req.NpcName} gathered forage")
            .WithHud("hud.harvest-forage", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.HarvestForage, CreatedTick = Game1.ticks };
}

public sealed class ClearDebrisAction : IPhysicalAction
{
    public string Name              => "clear_debris";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Clear nearby weeds, twigs, or small stones. Good for helping tidy up the farm or the area around you.";
    public string PromptSchema       => "{\"action\":\"clear_debris\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to help clear debris");
        return ActionResult.Ok($"{req.NpcName} cleared some debris")
            .WithHud("hud.clear-debris", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.ClearDebris, CreatedTick = Game1.ticks };
}

public sealed class PetAnimalsAction : IPhysicalAction
{
    public string Name              => "pet_animals";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Find nearby farm animals or pets and give them some affection. Only suitable if you are near a barn, coop, or the player's pet.";
    public string PromptSchema       => "{\"action\":\"pet_animals\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to pet the farmer's animals");
        return ActionResult.Ok($"{req.NpcName} petted the animals")
            .WithHud("hud.pet-animals", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.PetAnimals, CreatedTick = Game1.ticks };
}

public sealed class MineNearbyRockAction : IPhysicalAction
{
    public string Name              => "mine_nearby_rock";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "If in the mines, quarry, or near rocks, walk toward a nearby stone or ore deposit and break it, handing the materials to the farmer.";
    public string PromptSchema       => "{\"action\":\"mine_nearby_rock\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to mine with the farmer");
        return ActionResult.Ok($"{req.NpcName} mined a nearby rock")
            .WithHud("hud.mine-rock", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.MineNearbyRock, CreatedTick = Game1.ticks };
}

public sealed class ChopWoodAction : IPhysicalAction
{
    public string Name              => "chop_wood";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Find nearby fallen branches, twigs, or trees on the farm or outdoors and chop them to collect wood for yourself or the farmer.";
    public string PromptSchema       => "{\"action\":\"chop_wood\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to chop wood");
        return ActionResult.Ok($"{req.NpcName} is chopping wood")
            .WithHud("hud.chop-wood", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.ChopWood, CreatedTick = Game1.ticks };
}

public sealed class TakeFromChestAction : IPhysicalAction
{
    public string Name              => "take_from_chest";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Walk to the nearest farm chest and politely take requested materials (wood, stone, etc.) that the farmer gave you permission to have.";
    public string PromptSchema       => "{\"action\":\"take_from_chest\", \"item\":\"wood\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsChestAccess;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to access chests");
        string item = req.GetString("item") ?? "materials";
        return ActionResult.Ok($"{req.NpcName} took {item} from a chest")
            .WithHud("hud.take-from-chest", new { name = req.NpcName, item });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        var job = new NpcJob { NpcName = req.NpcName, Kind = JobKind.TakeFromChest, CreatedTick = Game1.ticks };
        string? item = req.GetString("item");
        if (!string.IsNullOrWhiteSpace(item))
            job.Data["item"] = item;
        return job;
    }
}

public sealed class ChopTreesAction : IPhysicalAction
{
    public string Name              => "chop_trees";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Chop down fully grown wild trees on the farm to gather wood and hardwood into the chest. Optional: tree_type='oak'|'maple'|'pine'|'mahogany'|'all'.";
    public string PromptSchema       => "{\"action\":\"chop_trees\", \"tree_type\":\"all\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to chop trees");
        return ActionResult.Ok($"{req.NpcName} is chopping trees on the farm")
            .WithHud("hud.chop-trees", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        var job = new NpcJob { NpcName = req.NpcName, Kind = JobKind.ChopTrees, CreatedTick = Game1.ticks };
        string? type = req.GetString("tree_type");
        job.Data["treeFilter"] = !string.IsNullOrWhiteSpace(type) ? type.ToLowerInvariant() : "all";
        return job;
    }
}

public sealed class FetchFromChestAction : IPhysicalAction
{
    public string Name              => "fetch_from_chest";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Walk to the farm chest, retrieve items stored previously (wood, stone, crops, fiber) and bring them back to hand to the player.";
    public string PromptSchema       => "{\"action\":\"fetch_from_chest\", \"category\":\"wood\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsChestAccess;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to access chests");
        return ActionResult.Ok($"{req.NpcName} is fetching items from the chest")
            .WithHud("hud.fetch-chest", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        var job = new NpcJob { NpcName = req.NpcName, Kind = JobKind.FetchFromChest, CreatedTick = Game1.ticks };
        string? cat = req.GetString("category");
        job.Data["fetchCategory"] = !string.IsNullOrWhiteSpace(cat) ? cat.ToLowerInvariant() : "fetch_last";
        return job;
    }
}

// ══════════════════════════════════════════════════════════════
//  NEW v2.0 FARM WORK ACTIONS
// ══════════════════════════════════════════════════════════════

public sealed class FeedAnimalsAction : IPhysicalAction
{
    public string Name              => "feed_animals";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Distribute hay into the troughs inside the barn or coop.";
    public string PromptSchema       => "{\"action\":\"feed_animals\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to feed animals");
        return ActionResult.Ok($"{req.NpcName} fed the animals")
            .WithHud("hud.feed-animals", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.FeedAnimals, CreatedTick = Game1.ticks };
}

public sealed class CollectAnimalProductsAction : IPhysicalAction
{
    public string Name              => "collect_animal_products";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Collect eggs, milk, wool, or truffles produced by animals on the farm or in barns and coops.";
    public string PromptSchema       => "{\"action\":\"collect_animal_products\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to collect animal products");
        return ActionResult.Ok($"{req.NpcName} collected animal products")
            .WithHud("hud.collect-animal-products", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.CollectAnimalProducts, CreatedTick = Game1.ticks };
}

public sealed class WaterPetBowlAction : IPhysicalAction
{
    public string Name              => "water_pet_bowl";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Fill the pet's water bowl on the farm.";
    public string PromptSchema       => "{\"action\":\"water_pet_bowl\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to water the pet bowl");
        return ActionResult.Ok($"{req.NpcName} filled the pet water bowl")
            .WithHud("hud.water-pet-bowl", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.WaterPetBowl, CreatedTick = Game1.ticks };
}

public sealed class CollectMachinesAction : IPhysicalAction
{
    public string Name              => "collect_machines";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Collect finished artisan goods from farm machines (furnaces, cheese presses, mayonnaise machines, etc.).";
    public string PromptSchema       => "{\"action\":\"collect_machines\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to collect machine products");
        return ActionResult.Ok($"{req.NpcName} collected machine products")
            .WithHud("hud.collect-machines", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.CollectMachines, CreatedTick = Game1.ticks };
}

public sealed class PlantSeedsAction : IPhysicalAction
{
    public string Name              => "plant_seeds";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Plant seeds into empty tilled soil tiles on the farm.";
    public string PromptSchema       => "{\"action\":\"plant_seeds\", \"seed_type\":\"<optional seed>\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to plant seeds");
        return ActionResult.Ok($"{req.NpcName} planted seeds on the farm")
            .WithHud("hud.plant-seeds", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        var job = new NpcJob { NpcName = req.NpcName, Kind = JobKind.PlantSeeds, CreatedTick = Game1.ticks };
        string? seed = req.GetStringAny("seed_type", "seed", "semilla");
        if (!string.IsNullOrWhiteSpace(seed))
            job.Data["seedType"] = seed;
        return job;
    }
}

public sealed class RepairFencesAction : IPhysicalAction
{
    public string Name              => "repair_fences";
    public ActionCategory Category  => ActionCategory.FarmWork;
    public string PromptDescription => "Repair broken or decayed fences on the farm using wood or stone.";
    public string PromptSchema       => "{\"action\":\"repair_fences\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsFarmWork;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod && ctx.Config.AllowFarmWork;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to repair fences");
        return ActionResult.Ok($"{req.NpcName} repaired fences on the farm")
            .WithHud("hud.repair-fences", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.RepairFences, CreatedTick = Game1.ticks };
}
