using System.Net;
using System.Text;
using System.Text.Json;
using NDIJobConfigurator.Core;

internal static class JobNameRegression
{
    internal static async Task Run(OnboardingRequest settings)
    {
        var names = new[] { "a", "SHOW", "123", "show with spaces", "演出-é & stage!", new string('x', 248), new string('é', 124) };
        foreach (var name in names)
        {
            InputValidation.Validate(settings with { JobName = name });
            var fixture = new Clients();
            var connection = new KiloLinkConnectionService(new KiloLinkCredentialStore(), new KiloLinkServerClient(fixture));
            try
            {
                // Explicit factory credentials avoid reading the real credential vault. The mock
                // rejects the password change after recording it, so no credential is persisted.
                await connection.ConnectAsync(new("192.0.2.1", 80, "admin", "Kiloview001", name), CancellationToken.None);
                throw new Exception("The mock rejection was hidden.");
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("fixture rejection")) { }
            if (fixture.Password != name) throw new Exception("An NDI-compatible name was rejected or changed before the vendor request.");
        }
        foreach (var name in new[] { "", "   ", "a,b", "a\0b", "a\nb", new string('x', 249), new string('é', 125) })
        {
            try { InputValidation.Validate(settings with { JobName = name }); }
            catch (ArgumentException) { continue; }
            throw new Exception("An invalid NDI group name was accepted.");
        }
    }

    private sealed class Clients : IHttpClientFactory
    {
        internal string? Password { get; set; }
        public HttpClient CreateClient(string name) => new(new Handler(this));
    }

    private sealed class Handler(Clients fixture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = """{"result":"ok","data":{"dn":"cn=admin","uid":"admin","cn":"admin","type":"user","changed":false}}""";
            if (path == "/api/tools/changeSelfPassword.json")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var cfg = body.RootElement.GetProperty("cfg");
                var password = cfg.GetProperty("userPassword").GetString()!;
                if (password != cfg.GetProperty("confirmNewPassword").GetString()) throw new Exception("Password confirmation differs.");
                fixture.Password = Uri.UnescapeDataString(password);
                response = """{"result":"error","msg":"fixture rejection"}""";
            }
            else if (path != "/api/tools/login.json") throw new Exception("Unexpected request: " + path);
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
