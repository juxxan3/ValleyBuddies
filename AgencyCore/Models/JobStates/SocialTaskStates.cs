namespace ValleyBuddies.AgencyCore;

using Microsoft.Xna.Framework;

public sealed class AdmireFarmState
{
    public Vector2? ScenicTile { get; set; }
    public bool Admired { get; set; }
}

public sealed class ShowEmoteState
{
    public bool Played { get; set; }
    public int EmoteId { get; set; } = 32;
}

public sealed class FacePlayerState
{
    public bool Played { get; set; }
}

public sealed class DoAnimationState
{
    public bool Played { get; set; }
    public string? AnimationName { get; set; }
}

public sealed class HugOrCelebrateState
{
    public bool Played { get; set; }
}

public sealed class GiveItemState
{
    public bool Played { get; set; }
    public string? ItemName { get; set; }
    public int Quantity { get; set; } = 1;
}

public sealed class MeetAtTimeState
{
    public int MeetTime { get; set; }
    public string? LocationName { get; set; }
}

public sealed class PicnicState
{
    public bool Initialized { get; set; }
    public Point OriginTile { get; set; }
    public Point SeatTile { get; set; }
    public bool WalkingToBlanket { get; set; }
    public bool ArrivedOnBlanket { get; set; }
    public ulong NextChatterTick { get; set; }
    public bool SnackDelivered { get; set; }
    public int StaminaGranted { get; set; }
    public ulong LastStaminaTick { get; set; }
}
