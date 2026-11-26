using System.Text.Json.Serialization;

namespace RemoteDesk;

internal sealed record EasyTierConfig
{
    private const int MaxTokenLength = 128;
    private const int MaxPeerUrlLength = 256;

    private static readonly HashSet<string> AllowedPeerSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tcp",
        "udp",
        "wg",
        "ws",
        "wss",
    };

    private static readonly char[] ForbiddenTokenChars =
    [
        '&', '|', ';', '<', '>', '$', '`', '"', '\'', '\\',
        '%', '^', '(', ')', '{', '}', '[', ']', '*', '?', '!',
    ];

    private static readonly char[] ForbiddenUrlChars =
    [
        '&', '|', ';', '<', '>', '$', '`', '"', '\'', '\\',
        '%', '^', '(', ')', '{', '}', '*', '?', '!',
    ];

    public bool Enabled { get; init; }

    public string NetworkName { get; init; } = "";

    public string NetworkSecret { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VirtualIpv4 { get; init; }

    public bool AutoAssignIpv4 { get; init; } = true;

    public string PeerUrl { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FallbackPeerUrl { get; init; }

    public bool EnableRelayFallback { get; init; } = true;

    public bool AutoStartWithApp { get; init; }

    public IReadOnlyList<string> GetEffectivePeerUrls()
    {
        var urls = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(PeerUrl))
            urls.Add(PeerUrl.Trim());

        if (!string.IsNullOrWhiteSpace(FallbackPeerUrl))
        {
            var fallback = FallbackPeerUrl.Trim();
            if (!urls.Any(existing => string.Equals(existing, fallback, StringComparison.OrdinalIgnoreCase)))
                urls.Add(fallback);
        }

        return urls;
    }

    public static EasyTierConfig Validate(EasyTierConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var networkName = ValidateNetworkToken(config.NetworkName, nameof(config.NetworkName));
        var networkSecret = ValidateNetworkToken(config.NetworkSecret, nameof(config.NetworkSecret));
        var peerUrl = ValidatePeerUrl(config.PeerUrl);
        string? fallbackPeerUrl = string.IsNullOrEmpty(config.FallbackPeerUrl)
            ? null
            : ValidatePeerUrl(config.FallbackPeerUrl);

        if (fallbackPeerUrl is not null &&
            string.Equals(fallbackPeerUrl, peerUrl, StringComparison.OrdinalIgnoreCase))
        {
            fallbackPeerUrl = null;
        }

        string? virtualIpv4 = config.VirtualIpv4;
        if (!config.AutoAssignIpv4 && !string.IsNullOrEmpty(virtualIpv4))
        {
            virtualIpv4 = ValidateVirtualIpv4(virtualIpv4, nameof(config.VirtualIpv4));
        }

        return config with
        {
            NetworkName = networkName,
            NetworkSecret = networkSecret,
            VirtualIpv4 = virtualIpv4,
            PeerUrl = peerUrl,
            FallbackPeerUrl = fallbackPeerUrl,
        };
    }

    public static string ValidateNetworkToken(string? value, string paramName)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("组网名称或密钥不能为空。", paramName);

        if (value.Length > MaxTokenLength)
            throw new ArgumentException($"组网参数长度不能超过 {MaxTokenLength} 个字符。", paramName);

        if (value[0] == '-')
            throw new ArgumentException("组网参数不能以连字符 '-' 开头。", paramName);

        foreach (var ch in value)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
                throw new ArgumentException("组网参数不能包含控制字符或空白字符。", paramName);

            if (Array.IndexOf(ForbiddenTokenChars, ch) >= 0)
                throw new ArgumentException($"组网参数包含不允许的特殊字符 '{ch}'。", paramName);
        }

        return value;
    }

    public static string ValidatePeerUrl(string? url)
    {
        if (string.IsNullOrEmpty(url))
            return "";

        if (url.Length > MaxPeerUrlLength)
            throw new ArgumentException($"节点地址长度不能超过 {MaxPeerUrlLength} 个字符。", nameof(url));

        if (url[0] == '-')
            throw new ArgumentException("节点地址不能以连字符 '-' 开头。", nameof(url));

        foreach (var ch in url)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
                throw new ArgumentException("节点地址不能包含控制字符或空白字符。", nameof(url));

            if (Array.IndexOf(ForbiddenUrlChars, ch) >= 0)
                throw new ArgumentException($"节点地址包含不允许的特殊字符 '{ch}'。", nameof(url));
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException("请输入有效的 EasyTier 节点地址（例如 tcp://1.2.3.4:11010）。", nameof(url));

        if (!AllowedPeerSchemes.Contains(uri.Scheme))
            throw new ArgumentException("节点地址仅支持 tcp、udp、wg、ws 或 wss 协议。", nameof(url));

        if (!string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("节点地址不能包含用户信息、查询参数或片段。", nameof(url));
        }

        if (uri.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6 or UriHostNameType.Dns) ||
            string.IsNullOrWhiteSpace(uri.DnsSafeHost) ||
            uri.DnsSafeHost[0] == '-')
        {
            throw new ArgumentException("节点地址的主机名或 IP 无效。", nameof(url));
        }

        var isWebSocket = string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);

        if (isWebSocket)
        {
            if (uri.Port == 0 || uri.Port > 65535 || uri.Port < -1)
                throw new ArgumentException("节点地址端口号无效（需在 1-65535 之间）。", nameof(url));
        }
        else if (uri.Port is < 1 or > 65535)
        {
            throw new ArgumentException("节点地址必须包含有效的端口号（1-65535）。", nameof(url));
        }

        if (!isWebSocket && !string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            throw new ArgumentException("tcp/udp/wg 节点地址不能包含路径。", nameof(url));

        return url;
    }

    public static string ValidateVirtualIpv4(string? value, string paramName = "virtualIpv4")
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("虚拟 IPv4 地址不能为空。", paramName);

        if (value[0] == '-')
            throw new ArgumentException("虚拟 IPv4 地址不能以连字符 '-' 开头。", paramName);

        foreach (var ch in value)
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
                throw new ArgumentException("虚拟 IPv4 地址不能包含控制字符或空白字符。", paramName);

            if (Array.IndexOf(ForbiddenTokenChars, ch) >= 0)
                throw new ArgumentException($"虚拟 IPv4 地址包含不允许的特殊字符 '{ch}'。", paramName);
        }

        var slashIndex = value.IndexOf('/');
        string ipPart;
        if (slashIndex >= 0)
        {
            if (value.IndexOf('/', slashIndex + 1) >= 0)
                throw new ArgumentException("虚拟 IPv4 地址格式无效。", paramName);

            ipPart = value[..slashIndex];
            var cidrPart = value[(slashIndex + 1)..];
            if (cidrPart.Length is < 1 or > 2 ||
                !cidrPart.All(char.IsAsciiDigit) ||
                (cidrPart.Length > 1 && cidrPart[0] == '0') ||
                !int.TryParse(cidrPart, out var prefixLength) ||
                prefixLength is < 8 or > 30)
            {
                throw new ArgumentException("虚拟 IPv4 子网掩码前缀长度必须在 8 到 30 之间（如 /24）。", paramName);
            }
        }
        else
        {
            ipPart = value;
        }

        if (!IsValidStrictIpv4(ipPart))
            throw new ArgumentException("请输入有效的虚拟 IPv4 地址（例如 10.126.126.1 或 10.126.126.1/24）。", paramName);

        return value;
    }

    private static bool IsValidStrictIpv4(string value)
    {
        var octets = value.Split('.');
        if (octets.Length != 4)
            return false;

        Span<byte> bytes = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = octets[i];
            if (part.Length is < 1 or > 3 || !part.All(char.IsAsciiDigit))
                return false;
            if (part.Length > 1 && part[0] == '0')
                return false;
            if (!byte.TryParse(part, out bytes[i]))
                return false;
        }

        // Disallow unspecified (0.0.0.0), loopback (127.0.0.0/8), and limited broadcast (255.255.255.255).
        if ((bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0) ||
            bytes[0] == 127 ||
            (bytes[0] == 255 && bytes[1] == 255 && bytes[2] == 255 && bytes[3] == 255))
        {
            return false;
        }

        return true;
    }
}

internal sealed record EasyTierPeerInfo(
    string Hostname,
    string VirtualIpv4,
    string ConnectionType,
    double? LatencyMs,
    string Protocol,
    bool IsSelf);

internal sealed record EasyTierStatus(
    bool Installed,
    bool Running,
    string? CoreExecutablePath,
    string? VirtualIpv4,
    string? NetworkName,
    string? PeerUrl,
    IReadOnlyList<EasyTierPeerInfo> Peers,
    string? DetailMessage = null,
    bool NeedsElevationWarning = false,
    bool ManagedByService = false);
