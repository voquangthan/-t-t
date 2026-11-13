using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

static class GoogleAccounts
{
    private const string Callback = "http://127.0.0.1:5183/api/google/callback";
    private const string GmailScope = "https://www.googleapis.com/auth/gmail.readonly";

    public static void Map(WebApplication app, string directory)
    {
        var group = app.MapGroup("/api/google");
        var clientId = app.Configuration["DETOT_GOOGLE_CLIENT_ID"];
        var secret = app.Configuration["DETOT_GOOGLE_CLIENT_SECRET"];
        var enabled = OperatingSystem.IsWindows() && app.Configuration.GetValue<bool>("DETOT_GOOGLE_RELEASE_ENABLED")
            && !string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(secret);
        var mutex = new SemaphoreSlim(1);
        var path = Path.Combine(directory, "accounts.bin");
        var protector = enabled ? app.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("Detot.GoogleAccounts.v1") : null;
        List<Account> Read() => !File.Exists(path) ? [] :
            JsonSerializer.Deserialize<List<Account>>(protector!.Unprotect(File.ReadAllText(path)))!;
        void Save(List<Account> accounts)
        {
            Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, protector!.Protect(JsonSerializer.Serialize(accounts)));
                File.Move(temp, path, true);
            }
            finally { File.Delete(temp); }
        }

        async Task<IResult> Run(HttpContext ctx, Func<Task<IResult>> action)
        {
            if (ctx.Request.Host.Value != "127.0.0.1:5183") return Results.StatusCode(403);
            if (!HttpMethods.IsGet(ctx.Request.Method) &&
                ctx.Request.Headers.Origin != "http://127.0.0.1:5183") return Results.StatusCode(403);
            if (!enabled) return Results.StatusCode(503);
            await mutex.WaitAsync(ctx.RequestAborted);
            try { return await action(); }
            catch (Exception error) when (error is HttpRequestException or JsonException or IOException or CryptographicException
                or KeyNotFoundException or InvalidOperationException or OperationCanceledException)
            {
                return Results.Problem("Không hoàn tất được yêu cầu Google. Kiểm tra kết nối hoặc cấp lại quyền.", statusCode: 502);
            }
            finally { mutex.Release(); }
        }

        group.MapGet("/accounts", Task<IResult> (HttpContext ctx) => !enabled
            ? Task.FromResult<IResult>(Results.Ok(new { enabled = false, accounts = Array.Empty<object>() }))
            : Run(ctx, () => Task.FromResult<IResult>(Results.Ok(new
            {
                enabled = true,
                accounts = Read().Select(account => new { account.Id, account.Email })
            }))));

        group.MapPost("/connect", Task<IResult> (HttpContext ctx) => Run(ctx, () =>
        {
            var state = RandomToken();
            var verifier = RandomToken();
            var cookie = protector!.Protect(state + ":" + verifier + ":" + DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds());
            ctx.Response.Cookies.Append("detot_google", cookie, Cookie());
            var url = QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", new Dictionary<string, string?>
            {
                ["client_id"] = clientId, ["redirect_uri"] = Callback, ["response_type"] = "code",
                ["scope"] = "openid email " + GmailScope, ["access_type"] = "offline",
                ["prompt"] = "consent select_account", ["state"] = state,
                ["code_challenge"] = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
                ["code_challenge_method"] = "S256"
            });
            return Task.FromResult<IResult>(Results.Ok(new { url }));
        }));

        group.MapGet("/callback", (HttpContext ctx, IHttpClientFactory factory) => Run(ctx, async () =>
        {
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            ctx.Response.Cookies.Delete("detot_google", Cookie());
            if (!ctx.Request.Cookies.TryGetValue("detot_google", out var cookie)) return Results.BadRequest();
            var state = protector!.Unprotect(cookie).Split(':');
            if (state.Length != 3 || !long.TryParse(state[2], out var expiry) ||
                expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state[0]),
                    Encoding.UTF8.GetBytes(ctx.Request.Query["state"].ToString()))) return Results.BadRequest();
            var code = ctx.Request.Query["code"].ToString();
            if (string.IsNullOrWhiteSpace(code)) return Results.Redirect("/app/index.html?google=denied");
            using var http = factory.CreateClient("google");
            using var tokens = await Token(http, new()
            {
                ["grant_type"] = "authorization_code", ["code"] = code,
                ["redirect_uri"] = Callback, ["code_verifier"] = state[1]
            }, ctx.RequestAborted);
            var token = tokens.RootElement;
            if (!Required(token, "scope").Split(' ').Contains(GmailScope) ||
                !token.TryGetProperty("refresh_token", out var refresh) || string.IsNullOrWhiteSpace(refresh.GetString()))
                return Results.Redirect("/app/index.html?google=denied");
            using var identity = await GoogleGet(http, "https://openidconnect.googleapis.com/v1/userinfo",
                Required(token, "access_token"), ctx.RequestAborted);
            if (!identity.RootElement.GetProperty("email_verified").GetBoolean()) return Results.BadRequest();
            var account = new Account(Required(identity.RootElement, "sub"), Required(identity.RootElement, "email"), refresh.GetString()!);
            var accounts = Read();
            accounts.RemoveAll(item => item.Id == account.Id);
            accounts.Add(account);
            Save(accounts);
            return Results.Redirect("/app/index.html?google=connected");
        }));

        group.MapDelete("/accounts/{id}", (string id, HttpContext ctx, IHttpClientFactory factory) => Run(ctx, async () =>
        {
            var accounts = Read();
            var account = accounts.Find(item => item.Id == id);
            if (account is null) return Results.NotFound();
            using var http = factory.CreateClient("google");
            using var body = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = account.RefreshToken });
            using var response = await http.PostAsync("https://oauth2.googleapis.com/revoke", body, ctx.RequestAborted);
            if (!response.IsSuccessStatusCode) return Results.Problem("Google chưa xác nhận thu hồi quyền.", statusCode: 502);
            accounts.Remove(account);
            Save(accounts);
            return Results.NoContent();
        }));

        group.MapPost("/accounts/{id}/search", (string id, GmailQuery query, HttpContext ctx, IHttpClientFactory factory) => Run(ctx, async () =>
        {
            if (query.Query is not { Length: <= 200 }) return Results.BadRequest();
            var account = Read().Find(item => item.Id == id);
            if (account is null) return Results.NotFound();
            using var http = factory.CreateClient("google");
            using var token = await Token(http, new()
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = account.RefreshToken
            }, ctx.RequestAborted);
            var access = Required(token.RootElement, "access_token");
            const string messages = "https://gmail.googleapis.com/gmail/v1/users/me/messages";
            using var list = await GoogleGet(http, QueryHelpers.AddQueryString(messages,
                new Dictionary<string, string?> { ["maxResults"] = "8", ["q"] = query.Query }), access, ctx.RequestAborted);
            if (!list.RootElement.TryGetProperty("messages", out var rows)) return Results.Ok(Array.Empty<object>());
            var results = await Task.WhenAll(rows.EnumerateArray().Take(8).Select(async row =>
            {
                var messageId = Required(row, "id");
                using var detail = await GoogleGet(http, messages + "/" + Uri.EscapeDataString(messageId) +
                    "?format=metadata&metadataHeaders=Subject&metadataHeaders=From", access, ctx.RequestAborted);
                var headers = detail.RootElement.GetProperty("payload").GetProperty("headers").EnumerateArray().ToArray();
                string Header(string name) => headers.Where(h => string.Equals(Required(h, "name"), name, StringComparison.OrdinalIgnoreCase))
                    .Select(h => Required(h, "value")).FirstOrDefault() ?? "";
                return new { id = messageId, subject = Header("Subject"), from = Header("From") };
            }));
            return Results.Ok(results);
        }));

        async Task<JsonDocument> Token(HttpClient http, Dictionary<string, string> values, CancellationToken ct)
        {
            values["client_id"] = clientId!;
            values["client_secret"] = secret!;
            using var body = new FormUrlEncodedContent(values);
            using var response = await http.PostAsync("https://oauth2.googleapis.com/token", body, ct);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
    }

    private static async Task<JsonDocument> GoogleGet(HttpClient http, string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string RandomToken() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    private static string Required(JsonElement value, string key) =>
        value.GetProperty(key).GetString() ?? throw new JsonException();
    private static CookieOptions Cookie() => new()
    {
        HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/api/google/callback", MaxAge = TimeSpan.FromMinutes(5)
    };
    private sealed record Account(string Id, string Email, string RefreshToken);
    internal sealed record GmailQuery(string? Query);
}
