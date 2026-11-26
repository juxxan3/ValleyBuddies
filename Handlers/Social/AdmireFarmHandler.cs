namespace ValleyBuddies.Handlers.Social;

using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using ValleyBuddies.AgencyCore;
using ValleyBuddies.Execution;

public sealed class AdmireFarmHandler : INpcJobHandler
{
    public JobKind Kind => JobKind.AdmireFarm;

    public void Tick(JobExecutionContext ctx)
    {
        var farm = Game1.getFarm();
        var loc = ctx.Npc.currentLocation;
        if (loc == null || farm == null) { ctx.Job.Done = true; return; }

        var state = ctx.Job.GetOrCreateState<AdmireFarmState>();

        Vector2 scenicTile;
        if (state.ScenicTile.HasValue)
        {
            scenicTile = state.ScenicTile.Value;
        }
        else
        {
            // Find nearest crop, flowers, or scenic porch area on the farm
            var match = loc.terrainFeatures.Pairs
                .Where(p => ctx.World.IsSafePlayableFarmTile(loc, p.Key)
                         && p.Value is StardewValley.TerrainFeatures.HoeDirt hd
                         && hd.crop != null)
                .OrderBy(p => Vector2.Distance(ctx.Npc.Tile, p.Key))
                .FirstOrDefault();

            scenicTile = match.Value != null
                ? match.Key
                : (farm is Farm f ? new Vector2(ctx.Navigator.GetSafePorchTile(f).X + 3, ctx.Navigator.GetSafePorchTile(f).Y + 2) : new Vector2(67, 17));

            state.ScenicTile = scenicTile;
        }

        float dist = Vector2.Distance(ctx.Npc.Tile, scenicTile);
        if (dist > 2.0f)
        {
            if (!ctx.Job.Data.TryGetValue("admire_start_tick", out var stObj) || stObj is not ulong startTick)
            {
                startTick = ctx.GameTick;
                ctx.Job.Data["admire_start_tick"] = startTick;
            }
            if (ctx.GameTick >= startTick + 480)
            {
                ctx.Navigator.StopCleanly(ctx.Npc);
                ctx.Job.Done = true;
                return;
            }
            Point walkTile = ctx.Navigator.GetAdjacentWalkableTile(loc, new Point((int)scenicTile.X, (int)scenicTile.Y), ctx.Npc.TilePoint);
            ctx.Navigator.MoveTowards(ctx.Job, ctx.Npc, loc, walkTile, ctx.GameTick);
        }
        else
        {
            var player = ctx.Owner ?? Game1.player;
            ctx.Npc.controller = null;
            ctx.Npc.Halt();
            ctx.Npc.faceGeneralDirection(scenicTile * 64f);

            loc.playSound("healSound");
            ctx.Npc.shake(250);
            ctx.Npc.doEmote(Game1.random.NextDouble() > 0.5 ? 24 : 20); // Music or Heart
            player?.doEmote(32); // Smile

            try
            {
                NpcActionExecutor.TryAwardDailyActivityFriendship(player, ctx.Npc, "admire_farm", 15, 15);
            }
            catch { }

            string admirePhrase = Utilities.I18n.Get($"bubble.activity.admire_{Game1.random.Next(1, 4)}");
            ctx.Npc.showTextAboveHead(admirePhrase, duration: 4000);
            ctx.RecordMemory(ctx.Npc.Name, "We took a peaceful stroll admiring the beautiful flowers, crops, and lovely scenery on the farm.");
            state.Admired = true;
            ctx.Job.Done = true;
        }
    }
}
