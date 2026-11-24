using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

internal sealed class SyncApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<string?, bool, (HttpClient Client, Func<string?> GetObservedThumbprint, Func<(string Expected, string Actual)?> GetMismatch, bool DisposeClient)> _clientFactory;

    public SyncApiClient(HttpMessageHandler? customHandler = null)
    {
        if (customHandler is not null)
        {
            var client = new HttpClient(customHandler, disposeHandler: false)
            {
                Timeout = DefaultTimeout,
            };
            _clientFactory = (_, _) => (client, () => null, () => null, false);
        }
        else
        {
            _clientFactory = CreatePinnedClient;
        }
    }

    public static string NormalizeServerUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            throw new ArgumentException("请输入同步服务器地址（例如 https://203.0.113.10:11020）。", nameof(rawUrl));

        var trimmed = rawUrl.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "https://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("同步服务器地址无效，请输入合法的 https:// 或 http:// 地址。", nameof(rawUrl));
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath.TrimEnd('/'),
            Query = "",
            Fragment = "",
        };
        return builder.Uri.ToString().TrimEnd('/');
    }

    public async Task<(SyncHealthResponse Health, string? ObservedThumbprint)> CheckHealthAsync(
        string serverUrl,
        string? expectedThumbprint,
        bool allowTrustOnFirstUse = true,
        CancellationToken ct = default)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        var (payload, observed) = await SendJsonAsync<SyncHealthResponse>(
            baseUrl,
            HttpMethod.Get,
            "/api/v1/health",
            token: null,
            body: null,
            expectedThumbprint,
            allowTrustOnFirstUse,
            ct).ConfigureAwait(false);
        return (payload, observed);
    }

    public async Task<SyncAuthResult> RegisterAsync(
        string serverUrl,
        string username,
        string password,
        string inviteCode,
        string? expectedThumbprint,
        bool allowTrustOnFirstUse = true,
        CancellationToken ct = default)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        var (dto, observed) = await SendJsonAsync<AuthWireDto>(
            baseUrl,
            HttpMethod.Post,
            "/api/v1/auth/register",
            token: null,
            body: new { username, password, inviteCode },
            expectedThumbprint,
            allowTrustOnFirstUse,
            ct).ConfigureAwait(false);

        return new SyncAuthResult(dto.Token, dto.User, dto.MeshConfig, observed);
    }

    public async Task<SyncAuthResult> LoginAsync(
        string serverUrl,
        string username,
        string password,
        string? expectedThumbprint,
        bool allowTrustOnFirstUse = true,
        CancellationToken ct = default)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        var (dto, observed) = await SendJsonAsync<AuthWireDto>(
            baseUrl,
            HttpMethod.Post,
            "/api/v1/auth/login",
            token: null,
            body: new { username, password },
            expectedThumbprint,
            allowTrustOnFirstUse,
            ct).ConfigureAwait(false);

        return new SyncAuthResult(dto.Token, dto.User, dto.MeshConfig, observed);
    }

    public async Task LogoutAsync(
        string serverUrl,
        string sessionToken,
        string? expectedThumbprint,
        CancellationToken ct = default)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        _ = await SendJsonAsync<OkWireDto>(
            baseUrl,
            HttpMethod.Post,
            "/api/v1/auth/logout",
            token: sessionToken,
            body: new { },
            expectedThumbprint,
            allowTrustOnFirstUse: false,
            ct).ConfigureAwait(false);
    }

    public async Task<SyncProfileResult> GetProfileAsync(
        string serverUrl,
        string sessionToken,
        string? expectedThumbprint,
        bool allowTrustOnFirstUse = false,
        CancellationToken ct = default)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        var (dto, observed) = await SendJsonAsync<ProfileWireDto>(
            baseUrl,
            HttpMethod.Get,
            "/api/v1/account/profile",
            token: sessionToken,
            body: null,
            expectedThumbprint,
            allowTrustOnFirstUse,
            ct).ConfigureAwait(false);

        return new SyncProfileResult(dto.User, dto.MeshConfig, observed);
    }

    public async Task<SyncCheckinResult> CheckinDeviceAsync(
        string serverUrl,
        string sessionToken,
        SyncCheckinRequest requestBody,
        string? expectedThumbprint,
        bool allowTrustOnFirstUse = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requestBody);
        var baseUrl = NormalizeServerUrl(serverUrl);
        var (dto, observed) = await SendJsonAsync<CheckinWireDto>(
            baseUrl,
            HttpMethod.Post,
            "/api/v1/devices/checkin",
            token: sessionToken,
            body: requestBody,
            expectedThumbprint,
            allowTrustOnFirstUse,
            ct).ConfigureAwait(false);

        return new SyncCheckinResult(dto.Device, dto.Devices, dto.ServerTimeUtc, observed);
    }

    public async Task<SyncDevicesResult> GetDevicesAsync(
        string serverUrl,
        string sessionToken,
        string? expectedThumbprint,
        bool allowTrustOnFirstUse = false,
        CancellationToken ct = default)
    {
        var baseUrl = NormalizeServerUrl(serverUrl);
        var (dto, _) = await SendJsonAsync<SyncDevicesResult>(
            baseUrl,
            HttpMethod.Get,
            "/api/v1/devices",
            token: sessionToken,
            body: null,
            expectedThumbprint,
            allowTrustOnFirstUse,
            ct).ConfigureAwait(false);
        return dto;
    }

    public async Task<SyncedDeviceInfo> RenameDeviceAsync(
        string serverUrl,
        string sessionToken,
        string deviceId,
        string newName,
        string? expectedThumbprint,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var baseUrl = NormalizeServerUrl(serverUrl);
        var (dto, _) = await SendJsonAsync<DeviceWrapWireDto>(
            baseUrl,
            HttpMethod.Patch,
            $"/api/v1/devices/{Uri.EscapeDataString(deviceId)}",
            token: sessionToken,
            body: new { name = newName },
            expectedThumbprint,
            allowTrustOnFirstUse: false,
            ct).ConfigureAwait(false);
        return dto.Device;
    }

    public async Task DeleteDeviceAsync(
        string serverUrl,
        string sessionToken,
        string deviceId,
        string? expectedThumbprint,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var baseUrl = NormalizeServerUrl(serverUrl);
        _ = await SendJsonAsync<OkWireDto>(
            baseUrl,
            HttpMethod.Delete,
            $"/api/v1/devices/{Uri.EscapeDataString(deviceId)}",
            token: sessionToken,
            body: null,
            expectedThumbprint,
            allowTrustOnFirstUse: false,
            ct).ConfigureAwait(false);
    }

    private async Task<(T Result, string? ObservedThumbprint)> SendJsonAsync<T>(
        string baseUrl,
        HttpMethod method,
        string path,
        string? token,
        object? body,
        string? expectedThumbprint,
        bool allowTrustOnFirstUse,
        CancellationToken ct)
    {
        var (client, getObserved, getMismatch, disposeClient) = _clientFactory(expectedThumbprint, allowTrustOnFirstUse);
        try
        {
            using var req = new HttpRequestMessage(method, baseUrl + path);
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            if (body is not null)
            {
                var json = JsonSerializer.Serialize(body, JsonOptions);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            HttpResponseMessage resp;
            try
            {
                resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException && getMismatch() is { } mismatch)
            {
                throw new SyncCertificateMismatchException(mismatch.Expected, mismatch.Actual);
            }
            catch (AuthenticationException) when (getMismatch() is { } mismatch)
            {
                throw new SyncCertificateMismatchException(mismatch.Expected, mismatch.Actual);
            }

            using (resp)
            {
                var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var err = TryParseError(raw);
                    throw new SyncApiException(
                        (int)resp.StatusCode,
                        err?.Error ?? $"HTTP_{(int)resp.StatusCode}",
                        err?.Message ?? $"同步服务器返回错误（HTTP {(int)resp.StatusCode}）。");
                }

                var parsed = JsonSerializer.Deserialize<T>(raw, JsonOptions)
                    ?? throw new InvalidDataException("同步服务器返回了无效的数据格式。");
                return (parsed, getObserved());
            }
        }
        finally
        {
            if (disposeClient)
                client.Dispose();
        }
    }

    private static (HttpClient Client, Func<string?> GetObservedThumbprint, Func<(string Expected, string Actual)?> GetMismatch, bool DisposeClient)
        CreatePinnedClient(string? expectedThumbprint, bool allowTrustOnFirstUse)
    {
        string? observedThumbprint = null;
        (string Expected, string Actual)? mismatch = null;
        var normalizedExpected = string.IsNullOrWhiteSpace(expectedThumbprint)
            ? null
            : expectedThumbprint.Trim().ToUpperInvariant();

        var handler = new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, sslPolicyErrors) =>
            {
                if (sslPolicyErrors == SslPolicyErrors.None)
                    return true;

                if (cert is null)
                    return false;

                var actual = cert.GetCertHashString(HashAlgorithmName.SHA256).ToUpperInvariant();
                if (normalizedExpected is not null)
                {
                    if (string.Equals(actual, normalizedExpected, StringComparison.OrdinalIgnoreCase))
                    {
                        observedThumbprint = actual;
                        return true;
                    }

                    mismatch = (normalizedExpected, actual);
                    return false;
                }

                if (allowTrustOnFirstUse)
                {
                    observedThumbprint = actual;
                    return true;
                }

                return false;
            },
        };

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = DefaultTimeout,
        };
        return (client, () => observedThumbprint, () => mismatch, true);
    }

    private static ErrorWireDto? TryParseError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ErrorWireDto>(raw, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ErrorWireDto(string? Error, string? Message);
    private sealed record OkWireDto(bool Ok);
    private sealed record AuthWireDto(string Token, AccountUserInfo User, AccountMeshConfig MeshConfig);
    private sealed record ProfileWireDto(AccountUserInfo User, AccountMeshConfig MeshConfig);
    private sealed record CheckinWireDto(SyncedDeviceInfo Device, IReadOnlyList<SyncedDeviceInfo> Devices, long ServerTimeUtc);
    private sealed record DeviceWrapWireDto(SyncedDeviceInfo Device);
}
