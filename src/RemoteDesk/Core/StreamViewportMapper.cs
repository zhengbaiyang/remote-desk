namespace RemoteDesk;

internal readonly record struct ViewportRect(
    int OffsetX,
    int OffsetY,
    int DrawWidth,
    int DrawHeight);

internal static class StreamViewportMapper
{
    public const uint CodecMaskH264 = 0x01u;
    public const uint CodecMaskHevc = 0x02u;
    public const uint CodecMaskAv1 = 0x04u;

    public const int NativePrefCodecAuto = 0;
    public const int NativePrefCodecH264 = 1;
    public const int NativePrefCodecHevc = 2;
    public const int NativePrefCodecAv1 = 3;

    public static ViewportRect ComputeLetterboxRect(int hostWidth, int hostHeight, int streamWidth, int streamHeight)
    {
        if (hostWidth <= 0 || hostHeight <= 0 || streamWidth <= 0 || streamHeight <= 0)
            return new ViewportRect(0, 0, Math.Max(1, hostWidth), Math.Max(1, hostHeight));

        var hostAspect = (double)hostWidth / hostHeight;
        var streamAspect = (double)streamWidth / streamHeight;

        var drawW = hostWidth;
        var drawH = hostHeight;
        var offX = 0;
        var offY = 0;

        if (hostAspect > streamAspect)
        {
            drawH = hostHeight;
            drawW = Math.Clamp((int)Math.Round(hostHeight * streamAspect), 1, hostWidth);
            offX = (hostWidth - drawW) / 2;
        }
        else if (hostAspect < streamAspect)
        {
            drawW = hostWidth;
            drawH = Math.Clamp((int)Math.Round(hostWidth / streamAspect), 1, hostHeight);
            offY = (hostHeight - drawH) / 2;
        }

        return new ViewportRect(offX, offY, drawW, drawH);
    }

    public static (short StreamX, short StreamY) MapClientToStream(
        double clientX,
        double clientY,
        int hostWidth,
        int hostHeight,
        int streamWidth,
        int streamHeight)
    {
        var rect = ComputeLetterboxRect(hostWidth, hostHeight, streamWidth, streamHeight);
        var relX = Math.Clamp(clientX - rect.OffsetX, 0.0, rect.DrawWidth);
        var relY = Math.Clamp(clientY - rect.OffsetY, 0.0, rect.DrawHeight);

        var sx = (int)Math.Round(relX / rect.DrawWidth * streamWidth);
        var sy = (int)Math.Round(relY / rect.DrawHeight * streamHeight);

        return (
            (short)Math.Clamp(sx, 0, Math.Max(1, streamWidth - 1)),
            (short)Math.Clamp(sy, 0, Math.Max(1, streamHeight - 1)));
    }

    public static (int NativeCodecId, VideoCodecPreference EffectiveCodec, bool FellBackToH264) ResolveEffectiveCodec(
        VideoCodecPreference requested,
        uint supportedMask)
    {
        var hasHevc = (supportedMask & CodecMaskHevc) != 0;
        var hasAv1 = (supportedMask & CodecMaskAv1) != 0;

        return requested switch
        {
            VideoCodecPreference.Av1 when hasAv1 => (NativePrefCodecAv1, VideoCodecPreference.Av1, false),
            VideoCodecPreference.Av1 when hasHevc => (NativePrefCodecHevc, VideoCodecPreference.H265, false),
            VideoCodecPreference.Av1 => (NativePrefCodecH264, VideoCodecPreference.H264, true),

            VideoCodecPreference.H265 when hasHevc => (NativePrefCodecHevc, VideoCodecPreference.H265, false),
            VideoCodecPreference.H265 => (NativePrefCodecH264, VideoCodecPreference.H264, true),

            VideoCodecPreference.H264 => (NativePrefCodecH264, VideoCodecPreference.H264, false),

            _ when hasHevc => (NativePrefCodecHevc, VideoCodecPreference.H265, false),
            _ => (NativePrefCodecH264, VideoCodecPreference.H264, false),
        };
    }
}
