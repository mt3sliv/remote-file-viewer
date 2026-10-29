namespace RemoteFrame.Core;

public static class FramePacing
{
    /// <summary>Subtract work from the interval; overruns start once ready without accumulating debt.</summary>
    public static TimeSpan RemainingDelay(int framesPerSecond, TimeSpan work)
    {
        if (framesPerSecond is < 1 or > 60 || work < TimeSpan.Zero) throw new ArgumentOutOfRangeException();
        var remaining = TimeSpan.FromSeconds(1.0 / framesPerSecond) - work;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}
