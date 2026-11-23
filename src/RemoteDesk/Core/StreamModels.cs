using System.Text.Json.Serialization;

namespace RemoteDesk;

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum ConnectionMode
{
    Stream,
    Office,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum StreamPresetKind
{
    Ultra120,
    High2K,
    Saver,
    Custom,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum VideoCodecPreference
{
    Auto,
    H265,
    H264,
    Av1,
}

internal sealed record StreamOptions(
    StreamPresetKind Preset,
    int Width,
    int Height,
    int Fps,
    int BitrateMbps,
    VideoCodecPreference Codec,
    bool HardwareDecoding)
{
    public const int MinWidth = 640;
    public const int MaxWidth = 7680;
    public const int MinHeight = 480;
    public const int MaxHeight = 4320;
    public const int MinFps = 30;
    public const int MaxFps = 240;
    public const int MinBitrateMbps = 2;
    public const int MaxBitrateMbps = 150;

    public static StreamOptions FromPreset(StreamPresetKind preset) => preset switch
    {
        StreamPresetKind.Ultra120 => new(StreamPresetKind.Ultra120, 1920, 1080, 120, 45, VideoCodecPreference.H265, true),
        StreamPresetKind.High2K => new(StreamPresetKind.High2K, 2560, 1440, 60, 35, VideoCodecPreference.H265, true),
        StreamPresetKind.Saver => new(StreamPresetKind.Saver, 1920, 1080, 60, 12, VideoCodecPreference.H264, true),
        StreamPresetKind.Custom => new(StreamPresetKind.Custom, 2560, 1440, 60, 35, VideoCodecPreference.H265, true),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "不支持的串流预设。"),
    };

    public static StreamOptions Create(
        StreamPresetKind preset,
        int width,
        int height,
        int fps,
        int bitrateMbps,
        VideoCodecPreference codec,
        bool hardwareDecoding) =>
        new StreamOptions(preset, width, height, fps, bitrateMbps, codec, hardwareDecoding).Validate();

    public static StreamOptions Normalize(StreamOptions? options)
    {
        if (options is null)
            return FromPreset(StreamPresetKind.High2K);

        var preset = Enum.IsDefined(options.Preset) ? options.Preset : StreamPresetKind.Custom;
        var codec = Enum.IsDefined(options.Codec) ? options.Codec : VideoCodecPreference.Auto;

        return new StreamOptions(
            preset,
            Math.Clamp(options.Width, MinWidth, MaxWidth),
            Math.Clamp(options.Height, MinHeight, MaxHeight),
            Math.Clamp(options.Fps, MinFps, MaxFps),
            Math.Clamp(options.BitrateMbps, MinBitrateMbps, MaxBitrateMbps),
            codec,
            options.HardwareDecoding);
    }

    public StreamOptions Validate()
    {
        if (!Enum.IsDefined(Preset))
            throw new ArgumentOutOfRangeException(nameof(Preset), Preset, "不支持的串流预设。");
        if (Width is < MinWidth or > MaxWidth)
            throw new ArgumentOutOfRangeException(nameof(Width), Width, $"分辨率宽度必须在 {MinWidth} 到 {MaxWidth} 之间。");
        if (Height is < MinHeight or > MaxHeight)
            throw new ArgumentOutOfRangeException(nameof(Height), Height, $"分辨率高度必须在 {MinHeight} 到 {MaxHeight} 之间。");
        if (Fps is < MinFps or > MaxFps)
            throw new ArgumentOutOfRangeException(nameof(Fps), Fps, $"帧率必须在 {MinFps} 到 {MaxFps} FPS 之间。");
        if (BitrateMbps is < MinBitrateMbps or > MaxBitrateMbps)
            throw new ArgumentOutOfRangeException(nameof(BitrateMbps), BitrateMbps, $"码率必须在 {MinBitrateMbps} 到 {MaxBitrateMbps} Mbps 之间。");
        if (!Enum.IsDefined(Codec))
            throw new ArgumentOutOfRangeException(nameof(Codec), Codec, "不支持的视频编码格式。");
        return this;
    }

    public string GetResolutionArgument() => $"{Width}x{Height}";

    public int GetBitrateKbps() => BitrateMbps * 1000;

    public string GetCodecArgument() => Codec switch
    {
        VideoCodecPreference.Auto => "auto",
        VideoCodecPreference.H264 => "H.264",
        VideoCodecPreference.H265 => "HEVC",
        VideoCodecPreference.Av1 => "AV1",
        _ => throw new ArgumentOutOfRangeException(nameof(Codec), Codec, "不支持的视频编码格式。"),
    };

    public string GetDecoderArgument() => HardwareDecoding ? "hardware" : "software";
}
