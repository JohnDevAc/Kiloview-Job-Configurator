using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NDIJobConfigurator.Core;

public sealed class DeviceUiGatewayService(ILogger<DeviceUiGatewayService> logger) : IDisposable
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    private static readonly HashSet<string> RequestHeadersToSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Cookie", "Origin", "Referer", "Accept-Encoding", "Connection", "Content-Length"
    };
    private static readonly HashSet<string> ResponseHeadersToSkip = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Content-Length", "Content-Encoding", "Set-Cookie"
    };

    private readonly ConcurrentDictionary<string, GatewaySession> sessions = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> CreateAsync(ManagedDevice device, int servicePort, CancellationToken ct)
    {
        if (device.Family is not (DeviceFamily.N6 or DeviceFamily.N60))
            throw new InvalidOperationException("Automatic Device UI login is available only for Kiloview N6 and N60 devices.");

        RemoveExpiredSessions();
        var host = AllocateLoopbackHost();
        var session = await GatewaySession.CreateAsync(device, ct);
        if (!sessions.TryAdd(host, session))
        {
            session.Dispose();
            throw new InvalidOperationException("A loopback Device UI session could not be allocated.");
        }
        logger.LogInformation("Created loopback Device UI session for {DeviceId} on {GatewayHost}", device.Id, host);
        return $"http://{host}:{servicePort}/dashboard?gateway={Guid.NewGuid():N}";
    }

    public bool TryGetSession(HttpContext context, out GatewaySession? session)
    {
        session = null;
        var remoteAddress = context.Connection.RemoteIpAddress;
        if (remoteAddress?.IsIPv4MappedToIPv6 == true) remoteAddress = remoteAddress.MapToIPv4();
        if (remoteAddress is null || !IPAddress.IsLoopback(remoteAddress)) return false;
        var host = context.Request.Host.Host;
        if (!host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (!sessions.TryGetValue(host, out session)) return false;
        if (DateTimeOffset.UtcNow - session.LastUsedUtc <= SessionLifetime) return true;
        if (sessions.TryRemove(host, out var expired)) expired.Dispose();
        session = null;
        return false;
    }

    public async Task ProxyAsync(HttpContext context, GatewaySession session, CancellationToken ct)
    {
        session.LastUsedUtc = DateTimeOffset.UtcNow;
        foreach (var cookie in session.BrowserCookies)
        {
            context.Response.Cookies.Append(cookie.Key, cookie.Value, new CookieOptions
            {
                Path = "/",
                HttpOnly = false,
                Secure = false,
                SameSite = SameSiteMode.Lax,
                MaxAge = SessionLifetime
            });
        }
        var target = new Uri(session.Client.BaseAddress!, context.Request.Path + context.Request.QueryString);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        foreach (var header in context.Request.Headers)
        {
            if (RequestHeadersToSkip.Contains(header.Key)) continue;
            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
            if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(context.Request.ContentType);
        }

        using var response = await session.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (ResponseHeadersToSkip.Contains(header.Key)) continue;
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }
        context.Response.Headers.Remove("transfer-encoding");
        var injectLogin = response.Content.Headers.ContentType?.MediaType?.Equals(
            "text/html",
            StringComparison.OrdinalIgnoreCase) == true;
        if (injectLogin)
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
            var html = await response.Content.ReadAsStringAsync(ct);
            var loginJsonString = JsonSerializer.Serialize(session.LoginInfoJson);
            var bootstrap = $"<script>sessionStorage.setItem('loginInfo',{loginJsonString});sessionStorage.setItem('language','en');</script>";
            var insertion = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
            html = insertion >= 0
                ? html.Insert(insertion + "<head>".Length, bootstrap)
                : bootstrap + html;
            await context.Response.WriteAsync(html, ct);
            return;
        }
        await response.Content.CopyToAsync(context.Response.Body, ct);
    }

    private string AllocateLoopbackHost()
    {
        for (var attempt = 0; attempt < 1_024; attempt++)
        {
            // Browsers resolve *.localhost to loopback, including when Kestrel
            // listens only on 127.0.0.1. A random hostname isolates cookies,
            // sessionStorage and SPA assets across devices and process restarts.
            var host = $"kv-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}.localhost";
            if (!sessions.ContainsKey(host)) return host;
        }
        throw new InvalidOperationException("A unique loopback Device UI session address could not be allocated.");
    }

    private void RemoveExpiredSessions()
    {
        var cutoff = DateTimeOffset.UtcNow - SessionLifetime;
        foreach (var pair in sessions.Where(pair => pair.Value.LastUsedUtc < cutoff).ToArray())
            if (sessions.TryRemove(pair.Key, out var expired)) expired.Dispose();
    }

    public void Dispose()
    {
        foreach (var session in sessions.Values) session.Dispose();
        sessions.Clear();
    }

    public sealed class GatewaySession : IDisposable
    {
        private readonly SocketsHttpHandler handler;
        public HttpClient Client { get; }
        public IReadOnlyDictionary<string, string> BrowserCookies { get; }
        public string LoginInfoJson { get; }
        public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;

        private GatewaySession(
            SocketsHttpHandler handler,
            HttpClient client,
            IReadOnlyDictionary<string, string> browserCookies,
            string loginInfoJson)
        {
            this.handler = handler;
            Client = client;
            BrowserCookies = browserCookies;
            LoginInfoJson = loginInfoJson;
        }

        public static async Task<GatewaySession> CreateAsync(ManagedDevice device, CancellationToken ct)
        {
            var cookies = new CookieContainer();
            var handler = new SocketsHttpHandler
            {
                UseCookies = true,
                CookieContainer = cookies,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AutomaticDecompression = DecompressionMethods.All
            };
            var client = new HttpClient(handler)
            {
                BaseAddress = new Uri($"http://{device.IpAddress}:{device.WebPort}"),
                Timeout = TimeSpan.FromSeconds(30)
            };
            var browserCookies = new Dictionary<string, string>(StringComparer.Ordinal);
            string loginInfoJson;
            try
            {
                if (device.Family == DeviceFamily.N6)
                {
                    using var response = await client.PostAsJsonAsync(
                        "/api/user/authorize.json",
                        new { user = device.Credentials.Username, password = device.Credentials.Password },
                        ct);
                    var data = await LoginDataAsync(response, "N6", ct);
                    loginInfoJson = data.GetRawText();
                    AddCookie(cookies, browserCookies, client.BaseAddress, "username", device.Credentials.Username);
                    AddCookie(cookies, browserCookies, client.BaseAddress, "user", device.Credentials.Username);
                    AddCookie(cookies, browserCookies, client.BaseAddress, "alias", Text(data, "alias", "Admin"));
                    AddCookie(cookies, browserCookies, client.BaseAddress, "token", Text(data, "token"));
                }
                else
                {
                    client.DefaultRequestHeaders.TryAddWithoutValidation("App", "{\"language\":\"en\"}");
                    using var response = await client.PostAsJsonAsync(
                        "/api/systemctrl/users/login",
                        new { username = device.Credentials.Username, password = device.Credentials.Password },
                        ct);
                    var data = await LoginDataAsync(response, "N60", ct);
                    var loginInfo = JsonNode.Parse(data.GetRawText())!.AsObject();
                    loginInfo["user"] = device.Credentials.Username;
                    loginInfo["title"] = "N60";
                    loginInfoJson = loginInfo.ToJsonString();
                    var token = Text(data, "token");
                    client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", token);
                    AddCookie(cookies, browserCookies, client.BaseAddress, "language", "en");
                    AddCookie(cookies, browserCookies, client.BaseAddress, "user", device.Credentials.Username);
                    AddCookie(cookies, browserCookies, client.BaseAddress, "alias", Text(data, "alias", "Admin"));
                    AddCookie(cookies, browserCookies, client.BaseAddress, "token", token);
                }
                return new GatewaySession(handler, client, browserCookies, loginInfoJson);
            }
            catch
            {
                client.Dispose();
                handler.Dispose();
                throw;
            }
        }

        private static async Task<JsonElement> LoginDataAsync(HttpResponseMessage response, string family, CancellationToken ct)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"{family} Device UI login failed with HTTP {(int)response.StatusCode}.");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!body.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"{family} Device UI login did not return a session token.");
            return data.Clone();
        }

        private static string Text(JsonElement element, string property, string fallback = "") =>
            element.TryGetProperty(property, out var value) ? value.ToString() : fallback;

        private static void AddCookie(
            CookieContainer cookies,
            IDictionary<string, string> browserCookies,
            Uri uri,
            string name,
            string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            cookies.Add(uri, new Cookie(name, value));
            browserCookies[name] = value;
        }

        public void Dispose()
        {
            Client.Dispose();
            handler.Dispose();
        }
    }
}
