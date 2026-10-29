namespace RemoteFrame.Core;

/// <summary>Capture is the application viewport; stream is the PNG size. Both use physical pixels.</summary>
public sealed record FrameConfiguration(int FramesPerSecond = 5, int CaptureWidth = 1280,
    int CaptureHeight = 800, int StreamWidth = 1280, int StreamHeight = 800)
{
    public void Validate(int maxFps = 60, int maxWidth = 3840, int maxHeight = 2160, long maxPixels = 8294400)
    {
        if (FramesPerSecond < 1 || FramesPerSecond > maxFps || maxFps > 60 ||
            maxWidth > 3840 || maxHeight > 2160 || maxPixels > 8294400 ||
            CaptureWidth < 320 || CaptureHeight < 200 || CaptureWidth > maxWidth || CaptureHeight > maxHeight ||
            StreamWidth < 160 || StreamHeight < 100 || StreamWidth > CaptureWidth || StreamHeight > CaptureHeight ||
            (long)CaptureWidth * CaptureHeight > maxPixels || (long)StreamWidth * StreamHeight > maxPixels)
            throw new ArgumentException("Invalid frame settings: FPS 1–60, capture 320x200–3840x2160, stream at least 160x100 and no larger than capture; server limits also apply.");
    }
}
