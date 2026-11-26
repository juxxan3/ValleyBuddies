using System;
using ValleyBuddies.Execution;
using ValleyBuddies.Utilities;
using StardewValley;

namespace ValleyBuddies.Actions;

// ══════════════════════════════════════════════════════════════
//  SOCIAL & EMOTIONAL ACTIONS
// ══════════════════════════════════════════════════════════════

public sealed class GiveItemToPlayerAction : IPhysicalAction
{
    public string Name              => "give_item";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Give the farmer a small item that fits your personality or the moment (a flower, food, crafted good). Item must be a real Stardew Valley item name.";
    public string PromptSchema       => "{\"action\":\"give_item\", \"item\":\"<Stardew item name>\", \"quantity\":1}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsGiveItem;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts to give a gift");

        string? item = req.GetString("item");
        if (string.IsNullOrWhiteSpace(item)) return ActionResult.Fail("item name missing");

        string? qualifiedId = SafeItemResolver.ResolveObjectQualifiedId(item);
        if (qualifiedId == null)
            return ActionResult.Fail($"item '{item}' could not be resolved");
        if (!SafeItemResolver.IsAllowedAsNpcGift(qualifiedId, ctx.Config, hearts, out string failReason))
            return ActionResult.Fail($"item '{item}' is not allowed or safe as a gift ({failReason})");

        int qty = Math.Clamp(req.GetInt("quantity", 1), 1, ctx.Config.MaxGiftQuantity);

        return ActionResult.Ok($"{req.NpcName} gave {item}")
            .WithHud("hud.gave-item", new { name = req.NpcName, item });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        string item = req.GetString("item") ?? "Daffodil";
        string qualifiedId = SafeItemResolver.ResolveObjectQualifiedId(item) ?? "(O)18";
        int qty = Math.Clamp(req.GetInt("quantity", 1), 1, ctx.Config.MaxGiftQuantity);

        var job = new NpcJob { NpcName = req.NpcName, Kind = JobKind.GiveItem, CreatedTick = Game1.ticks };
        job.Data["itemName"]    = item;
        job.Data["qualifiedId"] = qualifiedId;
        job.Data["qty"]         = qty;
        return job;
    }
}

public sealed class ShowEmoteAction : IPhysicalAction
{
    public string Name              => "show_emote";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Show a visual emotion above your head. Available: heart, angry, surprised, music, question, sleep, laugh, blush, sad, pause, annoyed, gaming, empty_can.";
    public string PromptSchema       => "{\"action\":\"show_emote\", \"emote\":\"<emote name>\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsEmote;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts for emotes");
        return ActionResult.Ok("showed emote");
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        string emoteName = (req.GetStringAny("emote", "emotion", "mood") ?? "heart").ToLowerInvariant();
        int emoteId = emoteName switch
        {
            "heart" or "love" or "corazon" or "corazón" => 20,
            "angry" or "mad" or "enojado" or "molesto" => 12,
            "surprised" or "exclamation" or "sorprendido" or "exclamacion" or "alerta" => 16,
            "music" or "sing" or "musica" or "música" or "nota" => 56,
            "question" or "confused" or "pregunta" or "duda" => 8,
            "sleep" or "tired" or "sueno" or "sueño" or "dormir" or "cansado" => 24,
            "laugh" or "happy" or "smile" or "feliz" or "risa" or "alegre" => 32,
            "blush" or "shy" or "sonrojo" or "timido" or "tímido" => 60,
            "sad" or "cry" or "triste" => 28,
            "pause" or "thinking" or "silence" or "pensando" or "silencio" => 40,
            "annoyed" or "no" or "x" or "rechazo" => 36,
            "gaming" or "videogame" or "jugando" => 52,
            "empty_can" or "thirsty" => 4,
            _ => 20
        };

        var job = new NpcJob { NpcName = req.NpcName, Kind = JobKind.ShowEmote, CreatedTick = Game1.ticks };
        job.Data["emoteId"] = emoteId;
        return job;
    }
}

public sealed class FacePlayerAction : IPhysicalAction
{
    public string Name              => "face_player";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Turn to face the farmer directly, making eye contact during conversation.";
    public string PromptSchema       => "{\"action\":\"face_player\"}";
    public int    MinHearts          => 0;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;
    public ActionResult Validate(ActionRequest r, ActionContext c, int h) => ActionResult.Ok("facing player");
    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.FacePlayer, CreatedTick = Game1.ticks };
}

public sealed class DoAnimationAction : IPhysicalAction
{
    public string Name              => "do_animation";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Play a special animation. Available: laugh, dance, surprised, music, question, sleep, drink.";
    public string PromptSchema       => "{\"action\":\"do_animation\", \"animation\":\"<name>\"}";
    public int    MinHearts          => 0;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest r, ActionContext c, int h) =>
        ActionResult.Ok($"playing animation: {r.GetString("animation") ?? "laugh"}");

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        var job = new NpcJob { NpcName = req.NpcName, Kind = JobKind.DoAnimation, CreatedTick = Game1.ticks };
        job.Data["anim"] = req.GetString("animation") ?? "laugh";
        return job;
    }
}

public sealed class HugOrCelebrateAction : IPhysicalAction
{
    public string Name              => "hug_or_celebrate";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Move close to the farmer, show a heart or celebrate animation, and share a warm moment. Only appropriate at high friendship.";
    public string PromptSchema       => "{\"action\":\"hug_or_celebrate\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsHug;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts for a hug/celebration");
        return ActionResult.Ok($"{req.NpcName} gave a hug")
            .WithHud("hud.hug", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.HugOrCelebrate, CreatedTick = Game1.ticks };
}

public sealed class MeetAtTimeAction : IPhysicalAction
{
    public string Name              => "meet_at_time";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Agree to meet the farmer at a specific in-game time and location. Time uses 24h format (e.g. 1200 = noon, 2000 = 8pm). Location uses standard location names.";
    public string PromptSchema       => "{\"action\":\"meet_at_time\", \"time\":1800, \"location\":\"Saloon\"}";
    public int    MinHearts          => 0;

    public int GetMinHearts(ActionContext context) => context.Config.MinHeartsMeetAtTime;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        if (hearts < GetMinHearts(ctx))
            return ActionResult.Fail($"needs {GetMinHearts(ctx)} hearts for a meeting");

        int time = req.GetInt("time", 0);
        if (time <= 0) return ActionResult.Fail("meeting time not specified");

        if (time is >= 1 and <= 24) time *= 100;
        int minutes = (time % 100) / 10 * 10;
        time = (time / 100) * 100 + Math.Clamp(minutes, 0, 50);

        if (time < 600 || time > 2500)
            return ActionResult.Fail("meeting time out of range (600..2500)");

        if (time <= Game1.timeOfDay)
            return ActionResult.Fail("meeting time must be in the future");

        string? loc = req.GetString("location");
        if (string.IsNullOrWhiteSpace(loc)) return ActionResult.Fail("meeting location not specified");

        var resolvedLoc = LocationResolver.Resolve(loc);
        if (resolvedLoc == null) return ActionResult.Fail($"unknown location: {loc}");

        return ActionResult.Ok($"will meet at {time} in {resolvedLoc.NameOrUniqueName}")
            .WithHud("hud.meet-time", new { name = req.NpcName, time, location = resolvedLoc.NameOrUniqueName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        int time = req.GetInt("time", 1800);
        if (time is >= 1 and <= 24) time *= 100;
        int minutes = (time % 100) / 10 * 10;
        time = (time / 100) * 100 + Math.Clamp(minutes, 0, 50);

        string loc = req.GetString("location") ?? "Town";
        var resolvedLoc = LocationResolver.Resolve(loc);

        var job = new NpcJob { NpcName = req.NpcName, Kind = JobKind.MeetAtTime, CreatedTick = Game1.ticks };
        job.Data["meetTime"]     = time;
        job.Data["locationName"] = resolvedLoc?.NameOrUniqueName ?? "Town";
        return job;
    }
}

public sealed class TakeBreakAction : IPhysicalAction
{
    public string Name              => "take_break";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Take a coffee or tea break together with the farmer on the farm, resting and sharing a pleasant moment.";
    public string PromptSchema       => "{\"action\":\"take_break\"}";
    public int    MinHearts          => 0;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts) =>
        ActionResult.Ok($"{req.NpcName} took a break with the farmer")
            .WithHud("hud.take-break", new { name = req.NpcName });

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.TakeBreak, CreatedTick = Game1.ticks };
}

public sealed class TakePhotoAction : IPhysicalAction
{
    public string Name              => "take_photo";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Pose together next to the farmer for a memorable photo on the farm.";
    public string PromptSchema       => "{\"action\":\"take_photo\"}";
    public int    MinHearts          => 0;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts) =>
        ActionResult.Ok($"{req.NpcName} posed for a photo with the farmer")
            .WithHud("hud.take-photo", new { name = req.NpcName });

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.TakePhoto, CreatedTick = Game1.ticks };
}

public sealed class PicnicAction : IPhysicalAction
{
    public string Name              => "picnic";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Share an impromptu picnic snack with the farmer outside on the farm, chatting and enjoying good food.";
    public string PromptSchema       => "{\"action\":\"picnic\"}";
    public int    MinHearts          => 0;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts) =>
        ActionResult.Ok($"{req.NpcName} is having a picnic with the farmer")
            .WithHud("hud.picnic", new { name = req.NpcName });

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.Picnic, CreatedTick = Game1.ticks };
}

public sealed class AdmireFarmAction : IPhysicalAction
{
    public string Name              => "admire_farm";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Walk to nearby flowers or crops to admire the scenery and peaceful atmosphere of the farm together.";
    public string PromptSchema       => "{\"action\":\"admire_farm\"}";
    public int    MinHearts          => 0;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts) =>
        ActionResult.Ok($"{req.NpcName} is admiring the farm flowers")
            .WithHud("hud.admire", new { name = req.NpcName });

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.AdmireFarm, CreatedTick = Game1.ticks };
}

// ══════════════════════════════════════════════════════════════
//  NEW v2.0 SOCIAL ACTIONS
// ══════════════════════════════════════════════════════════════

public sealed class PlayMusicAction : IPhysicalAction
{
    public string Name              => "play_music";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Play an instrument or hum a pleasant melody alongside the farmer.";
    public string PromptSchema       => "{\"action\":\"play_music\"}";
    public int    MinHearts          => 0;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts) =>
        ActionResult.Ok($"{req.NpcName} played music with the farmer")
            .WithHud("hud.play-music", new { name = req.NpcName });

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.PlayMusic, CreatedTick = Game1.ticks };
}

public sealed class DanceTogetherAction : IPhysicalAction
{
    public string Name              => "dance_together";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Share a cheerful little dance alongside the farmer.";
    public string PromptSchema       => "{\"action\":\"dance_together\"}";
    public int    MinHearts          => 0;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts) =>
        ActionResult.Ok($"{req.NpcName} danced with the farmer")
            .WithHud("hud.dance-together", new { name = req.NpcName });

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.DanceTogether, CreatedTick = Game1.ticks };
}

public sealed class StargazeAction : IPhysicalAction
{
    public string Name              => "stargaze";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Watch the night stars together with the farmer in a calm, reflective moment.";
    public string PromptSchema       => "{\"action\":\"stargaze\"}";
    public int    MinHearts          => 0;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts) =>
        ActionResult.Ok($"{req.NpcName} stargazed with the farmer")
            .WithHud("hud.stargaze", new { name = req.NpcName });

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx) =>
        new() { NpcName = req.NpcName, Kind = JobKind.Stargaze, CreatedTick = Game1.ticks };
}

public sealed class SendLetterAction : IPhysicalAction
{
    public string Name              => "send_letter_tomorrow";
    public ActionCategory Category  => ActionCategory.Social;
    public string PromptDescription => "Send a letter to the farmer's mailbox for tomorrow morning with a personal note or an optional attached gift item.";
    public string PromptSchema       => "{\"action\":\"send_letter_tomorrow\", \"title\":\"<subject>\", \"message\":\"<body>\", \"item\":\"<optional item name>\"}";
    public int    MinHearts          => 0;

    public bool IsEnabled(ActionContext ctx) => ctx.Config.EnableMod;

    public ActionResult Validate(ActionRequest req, ActionContext ctx, int hearts)
    {
        string? msg = req.GetStringAny("message", "content", "texto");
        if (string.IsNullOrWhiteSpace(msg)) return ActionResult.Fail("message is required");

        string? item = req.GetString("item");
        if (!string.IsNullOrWhiteSpace(item))
        {
            string? qualifiedId = SafeItemResolver.ResolveObjectQualifiedId(item);
            if (qualifiedId == null)
                return ActionResult.Fail($"item '{item}' could not be resolved");
            if (!SafeItemResolver.IsAllowedAsNpcGift(qualifiedId, ctx.Config, hearts, out string failReason))
                return ActionResult.Fail(failReason);
        }

        return ActionResult.Ok($"{req.NpcName} scheduled a letter for tomorrow")
            .WithHud("hud.send-letter", new { name = req.NpcName });
    }

    public NpcJob? BuildJob(ActionRequest req, ActionContext ctx)
    {
        var job = new NpcJob { NpcName = req.NpcName, Kind = JobKind.SendLetterTomorrow, CreatedTick = Game1.ticks };
        job.Data["title"]   = req.GetStringAny("title", "subject", "titulo") ?? "A letter for you";
        job.Data["message"] = req.GetStringAny("message", "content", "texto") ?? "...";
        string? item = req.GetString("item");
        if (!string.IsNullOrWhiteSpace(item))
        {
            string? qualifiedId = SafeItemResolver.ResolveObjectQualifiedId(item);
            if (!string.IsNullOrWhiteSpace(qualifiedId))
                job.Data["item"] = qualifiedId;
        }
        return job;
    }
}
