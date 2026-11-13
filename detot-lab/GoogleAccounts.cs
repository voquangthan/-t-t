using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;

static class GoogleAccounts
{
    private const string GmailScope = "https://www.googleapis.com/auth/gmail.readonly";
    private const string CalendarScope = "https://www.googleapis.com/auth/calendar.events.owned";
    private const string EventsUrl = "https://www.googleapis.com/calendar/v3/calendars/primary/events";

    public static Func<GoogleEventEdit, HttpContext, IHttpClientFactory, Task<IResult>> Map(
        WebApplication app, string directory, string origin, bool secure)
    {
        const string callbackPath = "/api/google/callback";
        var callback = origin + callbackPath;
        var host = new Uri(origin).Authority;
        CookieOptions Cookie() => new()
        {
            HttpOnly = true, Secure = secure, SameSite = SameSiteMode.Lax,
            Path = callbackPath
        };
        var group = app.MapGroup("/api/google");
        var clientId = app.Configuration["DETOT_GOOGLE_CLIENT_ID"];
        var secret = app.Configuration["DETOT_GOOGLE_CLIENT_SECRET"];
        var configured = (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(secret))
            || File.Exists(Path.Combine(Path.GetDirectoryName(directory)!, "google-client.json"));
        var enabled = OperatingSystem.IsWindows()
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
            if (!string.Equals(ctx.Request.Host.Value, host, StringComparison.OrdinalIgnoreCase)) return Results.StatusCode(403);
            if (!HttpMethods.IsGet(ctx.Request.Method) &&
                ctx.Request.Headers.Origin != origin) return Results.StatusCode(403);
            if (!enabled) return Results.Problem("Kết nối Google đang tắt trong cấu hình lab.", statusCode: 503);
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
            ? Task.FromResult<IResult>(Results.Ok(new
            {
                enabled = false,
                configured,
                reason = !OperatingSystem.IsWindows() ? "platform" : "credentials",
                accounts = Array.Empty<object>()
            }))
            : Run(ctx, () => Task.FromResult<IResult>(Results.Ok(new
            {
                enabled = true,
                accounts = Read().Where(account => !account.Calendar)
                    .Select(account => new { account.Id, account.Email, account.Notes, account.Selected })
            }))));

        group.MapPost("/connect", Task<IResult> (HttpContext ctx) => Run(ctx, () =>
        {
            var state = RandomToken();
            var calendar = ctx.Request.Query["service"] == "calendar";
            var verifier = RandomToken();
            var cookie = protector!.Protect(state + ":" + verifier + ":" + DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds() + ":" + (calendar ? "calendar" : "gmail"));
            ctx.Response.Cookies.Append("detot_google", cookie, Cookie());
            var url = QueryHelpers.AddQueryString("https://accounts.google.com/o/oauth2/v2/auth", new Dictionary<string, string?>
            {
                ["client_id"] = clientId, ["redirect_uri"] = callback, ["response_type"] = "code",
                ["scope"] = "openid email " + (calendar ? CalendarScope : GmailScope), ["access_type"] = "offline",
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
            if (state.Length != 4 || !long.TryParse(state[2], out var expiry) ||
                expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
                !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state[0]),
                    Encoding.UTF8.GetBytes(ctx.Request.Query["state"].ToString()))) return Results.BadRequest();
            var code = ctx.Request.Query["code"].ToString();
            var calendar = state[3] == "calendar";
            string Outcome(string value) => "/app/index.html?" + (calendar ? "calendar" : "google") + "=" + value;
            if (string.IsNullOrWhiteSpace(code)) return Results.Redirect(Outcome("denied"));
            using var http = factory.CreateClient("google");
            using var tokens = await Token(http, new()
            {
                ["grant_type"] = "authorization_code", ["code"] = code,
                ["redirect_uri"] = callback, ["code_verifier"] = state[1]
            }, ctx.RequestAborted);
            var token = tokens.RootElement;
            var scope = Required(token, "scope");
            var expectedScope = calendar ? CalendarScope : GmailScope;
            if (!scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(expectedScope, StringComparer.Ordinal))
                return Results.Redirect(Outcome("gmail_scope_missing"));
            if (!IsGrant(scope, expectedScope))
                return Results.Redirect(Outcome("unexpected_scope"));
            if (!token.TryGetProperty("refresh_token", out var refresh) || string.IsNullOrWhiteSpace(refresh.GetString()))
                return Results.Redirect(Outcome("refresh_missing"));
            using var identity = await GoogleGet(http, "https://openidconnect.googleapis.com/v1/userinfo",
                Required(token, "access_token"), ctx.RequestAborted);
            if (!identity.RootElement.GetProperty("email_verified").GetBoolean()) return Results.BadRequest();
            var account = new Account(Required(identity.RootElement, "sub"), Required(identity.RootElement, "email"), refresh.GetString()!, scope, calendar);
            var accounts = Read();
            var previous = accounts.Find(item => item.Id == account.Id && item.Calendar == calendar);
            if (previous is not null)
                account = account with { Notes = previous.Notes, Selected = previous.Selected };
            accounts.RemoveAll(item => item.Id == account.Id && item.Calendar == calendar);
            accounts.Add(account);
            Save(accounts);
            return Results.Redirect(Outcome("connected"));
        }));

        group.MapPut("/accounts/{id}/selection", (string id, HttpContext ctx) => Run(ctx, () =>
        {
            var accounts = Read();
            if (!accounts.Any(item => item.Id == id && !item.Calendar))
                return Task.FromResult<IResult>(Results.NotFound());
            Save(accounts.Select(item => item.Calendar ? item : item with { Selected = item.Id == id }).ToList());
            return Task.FromResult<IResult>(Results.NoContent());
        }));

        group.MapPut("/accounts/{id}/notes", (string id, Note[] notes, HttpContext ctx) => Run(ctx, () =>
        {
            if (notes.Length > 20 || notes.Any(note => note is null || !Guid.TryParse(note.Id, out _) ||
                string.IsNullOrWhiteSpace(note.Text) || note.Text.Length > 500) ||
                notes.Select(note => note.Id).Distinct().Count() != notes.Length)
                return Task.FromResult<IResult>(Results.BadRequest());
            var accounts = Read();
            var index = accounts.FindIndex(item => item.Id == id && !item.Calendar);
            if (index < 0) return Task.FromResult<IResult>(Results.NotFound());
            accounts[index] = accounts[index] with { Notes = notes };
            Save(accounts);
            return Task.FromResult<IResult>(Results.NoContent());
        }));

        group.MapDelete("/accounts/{id}", (string id, HttpContext ctx, IHttpClientFactory factory) => Run(ctx, async () =>
        {
            var accounts = Read();
            var account = accounts.Find(item => item.Id == id && !item.Calendar);
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
            var account = Read().Find(item => item.Id == id && !item.Calendar);
            if (account is null) return Results.NotFound();
            if (!IsReadOnlyGrant(account.Scope))
                return Results.Problem("Kết nối lại tài khoản để xác minh quyền Gmail chỉ đọc.", statusCode: 403);
            using var http = factory.CreateClient("google");
            using var token = await Token(http, new()
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = account.RefreshToken
            }, ctx.RequestAborted);
            if (token.RootElement.TryGetProperty("scope", out var refreshedScope) &&
                !IsReadOnlyGrant(refreshedScope.GetString()))
                return Results.Problem("Google trả về quyền ngoài phạm vi Gmail chỉ đọc.", statusCode: 403);
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

        group.MapGet("/accounts/{id}/messages/{messageId}/preview",
            (string id, string messageId, HttpContext ctx, IHttpClientFactory factory) => Run(ctx, async () =>
        {
            if (messageId.Length is < 1 or > 128 || !messageId.All(char.IsAsciiLetterOrDigit))
                return Results.BadRequest();
            var account = Read().Find(item => item.Id == id && !item.Calendar);
            if (account is null) return Results.NotFound();
            if (!IsReadOnlyGrant(account.Scope)) return Results.StatusCode(403);
            using var http = factory.CreateClient("google");
            using var token = await Token(http, new()
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = account.RefreshToken
            }, ctx.RequestAborted);
            if (token.RootElement.TryGetProperty("scope", out var scope) && !IsReadOnlyGrant(scope.GetString()))
                return Results.StatusCode(403);
            using var mail = await GoogleGet(http, $"https://gmail.googleapis.com/gmail/v1/users/me/messages/{messageId}?format=full&fields=snippet",
                Required(token.RootElement, "access_token"), ctx.RequestAborted);
            var preview = mail.RootElement.TryGetProperty("snippet", out var snippet) ? snippet.GetString() ?? "" : "";
            return Results.Ok(new { preview = preview[..Math.Min(600, preview.Length)] });
        }));

        group.MapGet("/calendar/accounts", Task<IResult> (HttpContext ctx) => Run(ctx, () =>
            Task.FromResult<IResult>(Results.Ok(Read().Where(account => account.Calendar)
                .Select(account => new { account.Id, account.Email })))));

        group.MapGet("/calendar/{id}/events", (string id, HttpContext ctx, IHttpClientFactory factory) => Run(ctx, async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var query = new Dictionary<string, string?>
            {
                ["timeMin"] = now.AddDays(-30).ToString("O"), ["timeMax"] = now.AddDays(90).ToString("O"),
                ["singleEvents"] = "true", ["orderBy"] = "startTime", ["maxResults"] = "250",
                ["fields"] = "nextPageToken,items(id,etag,summary,start,end,recurringEventId,eventType,organizer/self,attendees/email,locked,reminders)"
            };
            var events = new List<JsonElement>();
            for (var page = 0; page < 4; page++)
            {
                using var response = await CalendarCall(id, HttpMethod.Get, QueryHelpers.AddQueryString(EventsUrl, query), null, ctx, factory);
                if (!response.IsSuccessStatusCode) return CalendarError(response);
                using var data = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ctx.RequestAborted), cancellationToken: ctx.RequestAborted);
                if (data.RootElement.TryGetProperty("items", out var items))
                    events.AddRange(items.EnumerateArray().Select(item => item.Clone()));
                if (!data.RootElement.TryGetProperty("nextPageToken", out var next))
                    return Results.Ok(new { events, truncated = false });
                query["pageToken"] = next.GetString();
            }
            return Results.Ok(new { events, truncated = true });
        }));

        group.MapPost("/calendar/{id}/events", (string id, GoogleEventEdit edit, HttpContext ctx, IHttpClientFactory factory) =>
            Run(ctx, () => WriteEvent(id, null, edit, ctx, factory)));
        group.MapPut("/calendar/{id}/events/{eventId}", (string id, string eventId, GoogleEventEdit edit, HttpContext ctx, IHttpClientFactory factory) =>
            Run(ctx, () => WriteEvent(id, eventId, edit, ctx, factory)));
        group.MapDelete("/calendar/{id}/events/{eventId}", (string id, string eventId, HttpContext ctx, IHttpClientFactory factory) =>
            Run(ctx, () => WriteEvent(id, eventId, null, ctx, factory)));

        return (edit, ctx, factory) => Run(ctx, async () =>
        {
            var accounts = Read().Where(account => account.Calendar).ToArray();
            if (accounts.Length != 1)
                return Results.Problem(accounts.Length == 0
                    ? "Hãy kết nối tài khoản trong mục Google Calendar."
                    : "Có nhiều tài khoản Calendar. Hãy mở Google Calendar để chọn lịch cần dùng.", statusCode: 409);

            var id = accounts[0].Id;
            var eventId = "detot" + edit.RequestId.ToString("N");
            var query = new Dictionary<string, string?>
            {
                ["timeMin"] = edit.Start.ToString("O"), ["timeMax"] = edit.End.ToString("O"),
                ["singleEvents"] = "true", ["maxResults"] = "250",
                ["fields"] = "nextPageToken,items(id,status,transparency,summary,start/dateTime,end/dateTime)"
            };
            using var response = await CalendarCall(id, HttpMethod.Get, QueryHelpers.AddQueryString(EventsUrl, query), null, ctx, factory);
            if (!response.IsSuccessStatusCode) return CalendarError(response);
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ctx.RequestAborted));
            var items = data.RootElement.TryGetProperty("items", out var events) ? events.EnumerateArray().ToArray() : [];
            bool Is(JsonElement item, string field, string value) =>
                item.TryGetProperty(field, out var property) && property.GetString() == value;
            var active = items.Where(item => !Is(item, "status", "cancelled")).ToArray();
            if (active.Any(item => Is(item, "id", eventId) && Is(item, "summary", edit.Title!) &&
                item.GetProperty("start").GetProperty("dateTime").GetDateTimeOffset() == edit.Start &&
                item.GetProperty("end").GetProperty("dateTime").GetDateTimeOffset() == edit.End))
                return CreatedReply(alreadyExists: true);
            if (active.Any(item => !Is(item, "transparency", "transparent")))
                return Results.Ok(new CalendarConflict("Khung giờ này đã có lịch bận trên Google Calendar. Bạn muốn chuyển sang giờ nào?"));
            if (data.RootElement.TryGetProperty("nextPageToken", out _))
                return Results.Problem("Chưa kiểm tra được toàn bộ khung giờ trên Google Calendar. Hãy chọn khung giờ ngắn hơn.", statusCode: 409);

            var result = await WriteEvent(id, null, edit, ctx, factory);
            return result is IStatusCodeHttpResult { StatusCode: 204 } ? CreatedReply() : result;

            IResult CreatedReply(bool alreadyExists = false) => Results.Ok(new
            {
                reply = $"Đã tạo «{edit.Title}» trên Google Calendar từ {edit.Start:dd/MM/yyyy HH:mm zzz} đến {edit.End:dd/MM/yyyy HH:mm zzz}." +
                    (alreadyExists ? " Sự kiện đã có trong lịch." :
                    edit.ReminderMinutes is { Length: > 0 } minutes ? $" Google Calendar sẽ nhắc trước {string.Join(", ", minutes.Order())} phút." :
                    edit.ReminderMinutes is [] ? " Đã tắt nhắc nhở cho sự kiện này." : ""),
                googleCalendarCreated = true
            });
        });

        async Task<IResult> WriteEvent(string id, string? eventId, GoogleEventEdit? edit, HttpContext ctx, IHttpClientFactory factory)
        {
            if (edit is not null && (edit.Title?.Trim() is not { Length: > 0 and <= 120 } ||
                edit.Start == default || edit.End <= edit.Start || (eventId is null && edit.RequestId == Guid.Empty) ||
                !ValidReminderMinutes(edit.ReminderMinutes)))
                return Results.BadRequest();
            if (eventId is not null)
            {
                if (eventId.Length is < 1 or > 1024 || !eventId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                    return Results.BadRequest();
                if (!EntityTagHeaderValue.TryParse(ctx.Request.Headers.IfMatch, out var tag) || tag.IsWeak || tag.Tag == "*")
                    return Results.StatusCode(428);
                using var current = await CalendarCall(id, HttpMethod.Get, EventsUrl + "/" + eventId, null, ctx, factory);
                if (!current.IsSuccessStatusCode) return CalendarError(current);
                using var data = JsonDocument.Parse(await current.Content.ReadAsStringAsync(ctx.RequestAborted));
                var item = data.RootElement;
                if (item.TryGetProperty("recurringEventId", out _) || item.TryGetProperty("recurrence", out _) ||
                    item.TryGetProperty("attendees", out var guests) && guests.GetArrayLength() > 0 ||
                    item.TryGetProperty("eventType", out var type) && type.GetString() != "default" ||
                    item.TryGetProperty("locked", out var locked) && locked.GetBoolean() ||
                    !item.TryGetProperty("organizer", out var organizer) ||
                    !organizer.TryGetProperty("self", out var self) || !self.GetBoolean() ||
                    !item.GetProperty("start").TryGetProperty("dateTime", out _))
                    return Results.Problem("Sự kiện này chỉ xem trong Đệ Tốt. Hãy chỉnh trong Google Calendar.", statusCode: 409);
            }
            var url = EventsUrl + (eventId is null ? "" : "/" + eventId) + "?sendUpdates=none";
            var fields = new Dictionary<string, object>();
            if (edit is not null)
            {
                fields["summary"] = edit.Title!.Trim();
                fields["start"] = new { dateTime = edit.Start };
                fields["end"] = new { dateTime = edit.End };
                if (edit.ReminderMinutes is { } minutes)
                    fields["reminders"] = new { useDefault = false, overrides = minutes.Order().Select(value => new { method = "popup", minutes = value }) };
                if (eventId is null) fields["id"] = "detot" + edit.RequestId.ToString("N");
            }
            using var response = await CalendarCall(id, eventId is null ? HttpMethod.Post :
                edit is null ? HttpMethod.Delete : HttpMethod.Patch, url, edit is null ? null : fields, ctx, factory);
            return response.IsSuccessStatusCode ? Results.NoContent() : CalendarError(response);
        }

        async Task<HttpResponseMessage> CalendarCall(string id, HttpMethod method, string url, object? body,
            HttpContext ctx, IHttpClientFactory factory)
        {
            var account = Read().Find(item => item.Id == id && item.Calendar);
            if (account is null || !IsGrant(account.Scope, CalendarScope))
                return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
            using var http = factory.CreateClient("google");
            using var token = await Token(http, new()
            {
                ["grant_type"] = "refresh_token", ["refresh_token"] = account.RefreshToken
            }, ctx.RequestAborted);
            if (token.RootElement.TryGetProperty("scope", out var scope) && !IsGrant(scope.GetString(), CalendarScope))
                return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden);
            using var request = new HttpRequestMessage(method, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Required(token.RootElement, "access_token"));
            if (method == HttpMethod.Patch || method == HttpMethod.Delete)
                request.Headers.IfMatch.ParseAdd(ctx.Request.Headers.IfMatch.ToString());
            if (body is not null) request.Content = JsonContent.Create(body);
            return await http.SendAsync(request, ctx.RequestAborted);
        }

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
    private static IResult CalendarError(HttpResponseMessage response) => (int)response.StatusCode switch
    {
        409 or 412 => Results.Problem("Sự kiện đã thay đổi hoặc đã tồn tại. Hãy tải lại lịch.", statusCode: 409),
        404 or 410 => Results.Problem("Sự kiện không còn trên Google Calendar.", statusCode: 404),
        401 or 403 => Results.Problem("Kiểm tra quyền Calendar và Google Calendar API trong dự án Google Cloud.", statusCode: 403),
        429 => Results.Problem("Google Calendar đang giới hạn yêu cầu. Hãy thử lại sau.", statusCode: 429),
        _ => Results.Problem("Google Calendar chưa xử lý được yêu cầu.", statusCode: 502)
    };

    private static bool IsReadOnlyGrant(string? scope) => IsGrant(scope, GmailScope);
    private static bool IsGrant(string? scope, string expected)
    {
        var grants = scope?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        return grants.Contains(expected, StringComparer.Ordinal) &&
            grants.All(grant => grant == expected || grant is "openid" or "email" or "https://www.googleapis.com/auth/userinfo.email");
    }

    private sealed record Account(string Id, string Email, string RefreshToken, string? Scope = null, bool Calendar = false,
        Note[]? Notes = null, bool Selected = false);
    private sealed record Note(string Id, string Text);
    internal static bool ValidReminderMinutes(int[]? minutes) => minutes is null ||
        (minutes.Length <= 5 && minutes.All(value => value is >= 0 and <= 40320) && minutes.Distinct().Count() == minutes.Length);
    internal sealed record GoogleEventEdit(string? Title, DateTimeOffset Start, DateTimeOffset End, Guid RequestId, int[]? ReminderMinutes = null);
    internal sealed record CalendarConflict(string Reply);
    internal sealed record GmailQuery(string? Query);
}
