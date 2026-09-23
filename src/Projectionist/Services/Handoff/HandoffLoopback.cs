using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Projectionist.Services.Handoff;

/// <summary>One loopback request. <see cref="PathAndQuery"/> is server-relative WITHOUT the BaseUrl.</summary>
internal sealed record HandoffHttpRequest(
    HttpMethod Method,
    string PathAndQuery,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[]? JsonBody,
    IPAddress? ClientIp,
    bool CaptureBody,
    TimeSpan Timeout);

/// <summary>Outcome of a loopback request. Status 0 = transport failure / no usable loopback.</summary>
internal sealed record HandoffHttpResponse(int Status, byte[] Body, long Bytes, string? Location);

/// <summary>Sends requests to this same server as if they came from the client.</summary>
internal interface IHandoffLoopback
{
    Task<HandoffHttpResponse> SendAsync(HandoffHttpRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Loopback HTTP to this Jellyfin instance. Every request carries
/// <see cref="HeaderName"/> = "{secret};{client ip}", where the secret is random per process; the
/// handoff middleware (outermost in the pipeline) strips it and, only when the secret matches and
/// the request came over plain http from this host itself, swaps Connection.RemoteIpAddress for
/// the real client's address before auth, forwarded-headers and the LAN/remote bitrate rules run.
///
/// The target is authenticated before any credential is sent to it (SEC-2, SEC-RR-1):
///   - candidates are ONLY Kestrel's own plain-http listening sockets (IServerAddressesFeature; a
///     wildcard bind becomes the loopback address of that family). Nothing is ever guessed: a free
///     loopback port (say [::1]:8096 on an IPv4-only server) could be held by another local
///     process;
///   - a candidate is accepted only when its /System/Ping answers 200 with
///     <see cref="ProofHeaderName"/> = HMAC(key(secret), nonce | the local ip:port the request
///     arrived on), for the random nonce sent in <see cref="ProbeHeaderName"/>. The middleware
///     computes it over its OWN connection's local endpoint, and the client checks it against the
///     endpoint it dialed, so a proof fetched from this server by a relaying process (which can only
///     reach the server on some other socket, e.g. its https port) never verifies for the relay's
///     address. Probes are only answered on plain http, from this host, for /System/Ping;
///   - when no listening http socket proves itself (e.g. RequireHttps redirects http), nothing is
///     sent and the handoff is skipped;
///   - a proof holds for the base URL, and new connections to it are opened later without a new
///     probe, which is sound only while this process holds the port. From ApplicationStopping on
///     (which fires before Kestrel unbinds, for SIGTERM and for an in-process restart alike)
///     nothing is sent at all and no new connection is opened (SEC-R3-1): a process that binds the
///     port once Kestrel lets go never receives a credential.
/// </summary>
public sealed class HandoffLoopbackClient : IHandoffLoopback, IDisposable
{
    public const string HeaderName = "X-Projectionist-Loopback";

    /// <summary>Request header of a loopback probe: a random nonce (hex).</summary>
    public const string ProbeHeaderName = "X-Projectionist-Probe";

    /// <summary>Response header answering a probe: hex HMAC-SHA256(key(secret), nonce | local ip:port).</summary>
    public const string ProofHeaderName = "X-Projectionist-Proof";

    private const int MaxCapturedBody = 8 * 1024 * 1024;

    /// <summary>A validated base URL is re-probed after this long.</summary>
    private static readonly TimeSpan Revalidate = TimeSpan.FromMinutes(10);

    private readonly ILogger<HandoffLoopbackClient> _logger;
    private readonly IServerConfigurationManager? _config;
    private readonly IServiceProvider? _services;
    private readonly Func<IEnumerable<string>>? _candidateOverride;
    private readonly HttpClient _http;
    private readonly string _secret;
    private readonly byte[] _proofKey;
    private readonly bool _fixed;
    private readonly SemaphoreSlim _resolveLock = new(1, 1);
    private volatile string? _baseUrl;
    private DateTime _baseUrlValidUntilUtc = DateTime.MaxValue;
    private DateTime _nextResolveAttemptUtc = DateTime.MinValue;
    private volatile IPAddress[] _listenAddresses = Array.Empty<IPAddress>();
    private volatile bool _stopping;
    private CancellationTokenRegistration _stoppingRegistration;

    public HandoffLoopbackClient(
        ILogger<HandoffLoopbackClient> logger,
        IServerApplicationHost appHost,
        IServerConfigurationManager config,
        IServiceProvider services,
        IHostApplicationLifetime lifetime)
        : this(logger, appHost, config, null, services, null, null, lifetime)
    {
    }

    internal HandoffLoopbackClient(
        ILogger<HandoffLoopbackClient> logger,
        IServerApplicationHost? appHost,
        IServerConfigurationManager? config,
        string? fixedBaseUrl)
        : this(logger, appHost, config, fixedBaseUrl, null, null, null)
    {
    }

    /// <param name="fixedBaseUrl">Tests: use this base as is (no probe).</param>
    /// <param name="services">Where Kestrel's IServer (listening addresses) is resolved from.</param>
    /// <param name="candidates">Tests: the candidate base URLs to probe, in order.</param>
    /// <param name="handler">Tests: the HTTP handler to send through.</param>
    /// <param name="lifetime">Once it signals ApplicationStopping, nothing more is sent (SEC-R3-1).</param>
    internal HandoffLoopbackClient(
        ILogger<HandoffLoopbackClient> logger,
        IServerApplicationHost? appHost,
        IServerConfigurationManager? config,
        string? fixedBaseUrl,
        IServiceProvider? services,
        Func<IEnumerable<string>>? candidates,
        HttpMessageHandler? handler,
        IHostApplicationLifetime? lifetime = null)
    {
        _logger = logger;
        _config = config;
        _services = services;
        _candidateOverride = candidates;
        _baseUrl = fixedBaseUrl;
        _fixed = fixedBaseUrl is not null;
        var secret = RandomNumberGenerator.GetBytes(32);
        _secret = Convert.ToHexString(secret);
        _proofKey = SHA256.HashData(Encoding.ASCII.GetBytes("projectionist-probe:" + _secret));
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, ct),
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        if (lifetime is not null)
        {
            _stoppingRegistration = lifetime.ApplicationStopping.Register(OnApplicationStopping);
        }
    }

    /// <summary>True once the host began shutting down: the loopback sends nothing more.</summary>
    internal bool IsStopping => _stopping;

    /// <summary>
    /// ApplicationStopping (SEC-R3-1): the listening sockets are about to be closed and the port
    /// may then be bound by anyone, so the proven base URL is no longer trusted and every later
    /// request, and every new connection, is refused.
    /// </summary>
    internal void OnApplicationStopping()
    {
        _stopping = true;
        if (!_fixed)
        {
            _baseUrl = null;
        }
    }

    /// <summary>
    /// Opens the TCP connection of a loopback request. Refuses once the host is stopping, so a
    /// request that got past the check in SendAsync still never reaches a socket that is no
    /// longer this server's.
    /// </summary>
    internal async ValueTask<System.IO.Stream> ConnectAsync(DnsEndPoint endPoint, CancellationToken cancellationToken)
    {
        if (_stopping)
        {
            throw new HttpRequestException("[Projectionist] the server is stopping; no new loopback connection is opened");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endPoint, cancellationToken).ConfigureAwait(false);
            if (_stopping)
            {
                throw new HttpRequestException("[Projectionist] the server is stopping; no new loopback connection is opened");
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The answer to a probe carrying <paramref name="nonce"/> that arrived on the local endpoint
    /// <paramref name="localIp"/>:<paramref name="localPort"/>: hex HMAC-SHA256 under a key derived
    /// from the secret (the secret itself never leaves the process) over the nonce AND that
    /// endpoint, so the proof only verifies for a request that really reached this socket. Null for
    /// a malformed nonce or an unknown endpoint.
    /// </summary>
    public string? ComputeProof(string? nonce, IPAddress? localIp, int localPort)
    {
        if (!IsValidNonce(nonce) || localIp is null || localPort is <= 0 or > 65535)
        {
            return null;
        }

        var input = nonce + "|" + EndpointKey(localIp, localPort);
        return Convert.ToHexString(HMACSHA256.HashData(_proofKey, Encoding.ASCII.GetBytes(input)));
    }

    /// <summary>Canonical "ip:port" of an endpoint (IPv4-mapped addresses as IPv4, no IPv6 scope).</summary>
    internal static string EndpointKey(IPAddress ip, int port)
    {
        ip = Normalize(ip);
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            ip = new IPAddress(ip.GetAddressBytes());
        }

        return ip + ":" + port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Probe nonces are 16-64 hex characters; anything else is ignored.</summary>
    internal static bool IsValidNonce(string? nonce)
        => nonce is { Length: >= 16 and <= 64 } && nonce.All(Uri.IsHexDigit);

    private bool VerifyProof(string nonce, IPAddress dialedIp, int dialedPort, string? proof)
    {
        var expected = ComputeProof(nonce, dialedIp, dialedPort);
        if (expected is null || string.IsNullOrEmpty(proof) || proof.Length != expected.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected.ToUpperInvariant()),
            Encoding.ASCII.GetBytes(proof.ToUpperInvariant()));
    }

    /// <summary>
    /// True when <paramref name="peer"/> (a connection's real remote address) is this host itself:
    /// a loopback address, the connection's own local address, or one of the addresses Kestrel
    /// listens on. The loopback marker is only honoured from such a peer.
    /// </summary>
    public bool IsOwnAddress(IPAddress? peer, IPAddress? local)
    {
        if (peer is null)
        {
            return false;
        }

        peer = Normalize(peer);
        if (IPAddress.IsLoopback(peer))
        {
            return true;
        }

        if (local is not null && peer.Equals(Normalize(local)))
        {
            return true;
        }

        foreach (var a in ListenAddresses())
        {
            if (peer.Equals(a))
            {
                return true;
            }
        }

        return false;
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;

    /// <summary>Kestrel's listening addresses (specific IPs only), refreshed from IServerAddressesFeature.</summary>
    private IPAddress[] ListenAddresses()
    {
        var cached = _listenAddresses;
        if (cached.Length > 0)
        {
            return cached;
        }

        var list = new List<IPAddress>();
        foreach (var address in ServerAddresses())
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri)
                && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)
                && !ip.Equals(IPAddress.Any)
                && !ip.Equals(IPAddress.IPv6Any))
            {
                list.Add(Normalize(ip));
            }
        }

        var result = list.ToArray();
        if (result.Length > 0)
        {
            _listenAddresses = result;
        }

        return result;
    }

    /// <summary>Kestrel's IServerAddressesFeature, if the server exposes it (empty otherwise).</summary>
    private IReadOnlyCollection<string> ServerAddresses()
    {
        try
        {
            var server = _services?.GetService<IServer>();
            var addresses = server?.Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addresses is not null)
            {
                return addresses.ToArray();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Projectionist] handoff: could not read the server's listening addresses");
        }

        return Array.Empty<string>();
    }

    /// <summary>The header value this process accepts for <paramref name="clientIp"/>.</summary>
    internal string FormatHeader(IPAddress? clientIp)
        => clientIp is null ? _secret : _secret + ";" + clientIp.ToString();

    /// <summary>
    /// True only when <paramref name="headerValue"/> carries this process's secret.
    /// <paramref name="clientIp"/> is the effective client address to impersonate (null = keep).
    /// </summary>
    public bool TryValidateHeader(string? headerValue, out IPAddress? clientIp)
        => TryParseHeader(headerValue, _secret, out clientIp);

    internal static bool TryParseHeader(string? headerValue, string secret, out IPAddress? clientIp)
    {
        clientIp = null;
        if (string.IsNullOrEmpty(headerValue) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        var sep = headerValue.IndexOf(';', StringComparison.Ordinal);
        var presented = sep < 0 ? headerValue : headerValue[..sep];
        var a = Encoding.ASCII.GetBytes(presented);
        var b = Encoding.ASCII.GetBytes(secret);
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
        {
            return false;
        }

        if (sep >= 0 && IPAddress.TryParse(headerValue.AsSpan(sep + 1), out var ip))
        {
            clientIp = ip;
        }

        return true;
    }

    async Task<HandoffHttpResponse> IHandoffLoopback.SendAsync(HandoffHttpRequest request, CancellationToken cancellationToken)
    {
        var baseUrl = _stopping ? null : await ResolveBaseUrlAsync(cancellationToken).ConfigureAwait(false);
        if (baseUrl is null || _stopping)
        {
            return new HandoffHttpResponse(0, Array.Empty<byte>(), 0, null);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(request.Timeout);
        using var msg = new HttpRequestMessage(request.Method, baseUrl + request.PathAndQuery);
        foreach (var (name, value) in request.Headers)
        {
            msg.Headers.TryAddWithoutValidation(name, value);
        }

        msg.Headers.TryAddWithoutValidation(HeaderName, FormatHeader(request.ClientIp));
        if (request.JsonBody is not null)
        {
            msg.Content = new ByteArrayContent(request.JsonBody);
            msg.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        try
        {
            using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long total = 0;
                using var captured = request.CaptureBody ? new System.IO.MemoryStream() : null;
                int n;
                while ((n = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (captured is not null && captured.Length + n <= MaxCapturedBody)
                    {
                        captured.Write(buffer, 0, n);
                    }
                }

                return new HandoffHttpResponse(
                    (int)resp.StatusCode,
                    captured?.ToArray() ?? Array.Empty<byte>(),
                    total,
                    resp.Headers.Location?.ToString());
            }
        }
        catch (HttpRequestException ex)
        {
            // Connection-level failure: forget the cached base so the next call re-probes.
            _logger.LogDebug(ex, "[Projectionist] handoff loopback {Method} failed", request.Method);
            if (!_fixed)
            {
                _baseUrl = null;
            }

            return new HandoffHttpResponse(0, Array.Empty<byte>(), 0, null);
        }
    }

    private async Task<string?> ResolveBaseUrlAsync(CancellationToken cancellationToken)
    {
        if (_stopping)
        {
            return null;
        }

        var cached = _baseUrl;
        if (cached is not null && (_fixed || DateTime.UtcNow < _baseUrlValidUntilUtc))
        {
            return cached;
        }

        await _resolveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_baseUrl is not null && (_fixed || DateTime.UtcNow < _baseUrlValidUntilUtc))
            {
                return _baseUrl;
            }

            // Re-probe (first use, after a connection failure, or periodically): nothing is sent to
            // a base that has not just proven it is this process.
            _baseUrl = null;
            if (DateTime.UtcNow < _nextResolveAttemptUtc)
            {
                return null;
            }

            foreach (var candidate in Candidates())
            {
                if (_stopping)
                {
                    return null;
                }

                if (await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false) && !_stopping)
                {
                    if (!string.Equals(cached, candidate, StringComparison.Ordinal))
                    {
                        _logger.LogInformation(
                            "[Projectionist] handoff loopback base URL: {Url} (a listening socket of this server, proof verified)",
                            candidate);
                    }

                    _baseUrlValidUntilUtc = DateTime.UtcNow + Revalidate;
                    _baseUrl = candidate;
                    return candidate;
                }
            }

            _nextResolveAttemptUtc = DateTime.UtcNow.AddMinutes(1);
            _logger.LogWarning(
                "[Projectionist] handoff: none of this server's plain-http listening sockets answered the loopback probe with a valid proof (is http redirected to https?); feature handoff is skipped");
            return null;
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    private IEnumerable<string> Candidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _candidateOverride?.Invoke() ?? DefaultCandidates())
        {
            if (seen.Add(c))
            {
                yield return c;
            }
        }
    }

    /// <summary>
    /// Only Kestrel's real listening sockets (SEC-RR-1): no hard-coded 127.0.0.1/[::1] or
    /// GetApiUrlForLocalAccess guesses, which a local process could squat when the server does not
    /// listen there.
    /// </summary>
    private IEnumerable<string> DefaultCandidates()
        => FromServerAddresses(ServerAddresses(), NormalizeBaseUrl(_config?.GetNetworkConfiguration().BaseUrl));

    /// <summary>
    /// Loopback base URLs from Kestrel's listening addresses: plain http only; a wildcard
    /// (0.0.0.0, [::], +, *, localhost) becomes 127.0.0.1 / [::1] on that port.
    /// </summary>
    internal static IEnumerable<string> FromServerAddresses(IEnumerable<string> addresses, string baseUrl)
    {
        foreach (var address in addresses)
        {
            var a = address.Replace("://+:", "://0.0.0.0:", StringComparison.Ordinal).Replace("://*:", "://0.0.0.0:", StringComparison.Ordinal);
            if (!Uri.TryCreate(a, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var port = uri.Port.ToString(CultureInfo.InvariantCulture);
            var host = uri.Host.Trim('[', ']');
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"http://127.0.0.1:{port}{baseUrl}";
                yield return $"http://[::1]:{port}{baseUrl}";
                continue;
            }

            if (IPAddress.TryParse(host, out var any) && any.Equals(IPAddress.Any))
            {
                yield return $"http://127.0.0.1:{port}{baseUrl}";
                continue;
            }

            if (any is not null && any.Equals(IPAddress.IPv6Any))
            {
                // [::] is usually dual-stack.
                yield return $"http://[::1]:{port}{baseUrl}";
                yield return $"http://127.0.0.1:{port}{baseUrl}";
                continue;
            }

            if (IPAddress.TryParse(host, out var ip))
            {
                yield return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                    ? $"http://[{ip}]:{port}{baseUrl}"
                    : $"http://{ip}:{port}{baseUrl}";
            }
        }
    }

    internal static string NormalizeBaseUrl(string? baseUrl)
    {
        var b = (baseUrl ?? string.Empty).Trim().Trim('/');
        return b.Length == 0 ? string.Empty : "/" + b;
    }

    private async Task<bool> ProbeAsync(string candidate, CancellationToken cancellationToken)
    {
        // The proof is bound to the endpoint dialed, so it must be an IP literal (every candidate
        // FromServerAddresses yields is).
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var dialedIp))
        {
            _logger.LogDebug("[Projectionist] handoff loopback candidate {Url} is not an IP endpoint; skipped", candidate);
            return false;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            // No credentials and no secret on a probe: only a nonce the real server must answer.
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            using var msg = new HttpRequestMessage(HttpMethod.Get, candidate + "/System/Ping");
            msg.Headers.TryAddWithoutValidation(ProbeHeaderName, nonce);
            using var resp = await _http.SendAsync(msg, cts.Token).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogDebug("[Projectionist] handoff loopback probe {Url} -> {Status}", candidate, (int)resp.StatusCode);
                return false;
            }

            var proof = resp.Headers.TryGetValues(ProofHeaderName, out var values) ? values.FirstOrDefault() : null;
            if (VerifyProof(nonce, dialedIp, uri.Port, proof))
            {
                return true;
            }

            _logger.LogWarning(
                "[Projectionist] handoff: {Url} answered the loopback probe but is not this server's socket (no valid proof); not using it",
                candidate);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug("[Projectionist] handoff loopback probe {Url} failed: {Error}", candidate, ex.Message);
        }

        return false;
    }

    public void Dispose()
    {
        _stoppingRegistration.Dispose();
        _http.Dispose();
        _resolveLock.Dispose();
    }
}
