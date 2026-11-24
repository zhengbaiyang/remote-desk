using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace RemoteDesk;

internal sealed record DeviceProfile
{
    public const string DefaultStreamAppName = "Desktop";

    [JsonConstructor]
    public DeviceProfile(
        string name,
        string hostAddress,
        StreamOptions? stream = null,
        string? streamAppName = null)
    {
        ArgumentNullException.ThrowIfNull(name);

        name = name.Trim();
        if (name.Length is < 1 or > 40 || name.Any(char.IsControl))
            throw new ArgumentException("请输入 1 到 40 个字符的电脑名称。", nameof(name));

        var normalizedHost = ValidateHostAddress(hostAddress, nameof(hostAddress));
        var normalizedStream = (stream ?? StreamOptions.FromPreset(StreamPresetKind.High2K)).Validate();
        var normalizedAppName = NormalizeStreamAppName(streamAppName);

        Name = name;
        HostAddress = normalizedHost;
        Stream = normalizedStream;
        StreamAppName = normalizedAppName;
    }

    public string Name { get; }

    public string HostAddress { get; }

    public StreamOptions Stream { get; }

    public string StreamAppName { get; }

    [JsonIgnore]
    public bool SupportsStream => !string.IsNullOrEmpty(HostAddress);

    public static DeviceProfile Create(
        string name,
        string hostAddress,
        StreamOptions? stream = null,
        string? streamAppName = null) =>
        new(name, hostAddress, stream, streamAppName);

    internal static string ValidateHostAddress(string? hostAddress, string paramName = "hostAddress")
    {
        if (hostAddress is null)
            throw new ArgumentException("请输入有效的主机 IP 地址或域名。", paramName);

        if (hostAddress.Any(char.IsControl))
            throw new ArgumentException("主机地址不能包含控制字符。", paramName);

        var trimmed = hostAddress.Trim();
        if (trimmed.Length is < 1 or > 253)
            throw new ArgumentException("请输入有效的主机 IP 地址或域名。", paramName);

        if (trimmed[0] == '-' || trimmed.Any(char.IsWhiteSpace))
            throw new ArgumentException("主机地址不能包含空白字符或以连字符开头。", paramName);

        if (IsValidIPv4(trimmed) || IsValidIPv6(trimmed) || IsValidHostname(trimmed))
            return trimmed;

        throw new ArgumentException("请输入有效的 IPv4、IPv6 或主机名（禁止包含特殊字符）。", paramName);
    }

    internal static string NormalizeStreamAppName(string? streamAppName, string paramName = "streamAppName")
    {
        if (streamAppName is null)
            return DefaultStreamAppName;

        if (streamAppName.Any(char.IsControl))
            throw new ArgumentException("串流应用名称不能包含控制字符。", paramName);

        var trimmed = streamAppName.Trim();
        if (trimmed.Length is < 1 or > 64 || trimmed[0] == '-')
            throw new ArgumentException("串流应用名称需为 1 到 64 个字符，且不能以连字符开头。", paramName);

        return trimmed;
    }

    private static bool IsValidIPv4(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 4)
            return false;

        foreach (var part in parts)
        {
            if (part.Length is < 1 or > 3 || !part.All(char.IsAsciiDigit))
                return false;
            if (part.Length > 1 && part[0] == '0')
                return false;
            if (!byte.TryParse(part, out _))
                return false;
        }

        return true;
    }

    private static bool IsValidIPv6(string value)
    {
        if (!value.Contains(':'))
            return false;

        foreach (var c in value)
        {
            if (!Uri.IsHexDigit(c) && c is not (':' or '.'))
                return false;
        }

        return IPAddress.TryParse(value, out var address) &&
               address.AddressFamily == AddressFamily.InterNetworkV6 &&
               address.ScopeId == 0;
    }

    private static bool IsValidHostname(string value)
    {
        if (value[0] == '.' || value[^1] == '.')
            return false;

        var labels = value.Split('.');
        if (labels.Length == 0)
            return false;

        var allNumeric = true;
        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63)
                return false;
            if (!char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1]))
                return false;

            foreach (var c in label)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                    return false;
                if (!char.IsAsciiDigit(c))
                    allNumeric = false;
            }
        }

        // Reject pure numeric strings like "12345" or invalid dotted numeric strings like "999.999.999.999".
        if (allNumeric)
            return false;

        // Multi-label DNS names cannot have an all-numeric TLD (RFC 3696).
        if (labels.Length > 1 && labels[^1].All(char.IsAsciiDigit))
            return false;

        return true;
    }
}
