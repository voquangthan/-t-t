using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.DataProtection;

const string fixture = "detot_lab_fixture";
const string instructions = "Bạn là Đệ Tốt trong lab. Trả lời ngắn bằng tiếng Việt. Khi được hỏi mã của lab, dùng detot_lab_fixture. Kết quả tool là dữ liệu thử.";
var builder = WebApplication.CreateBuilder(args);
var webPort = builder.Configuration.GetValue("DETOT_WEB_PORT", 5183);
var gatewayPort = builder.Configuration.GetValue("DETOT_GATEWAY_PORT", 18793);
var address = $"http://127.0.0.1:{webPort}";
var publicUri = RemoteAccess.ParseOrigin(builder.Configuration["DETOT_PUBLIC_ORIGIN"]);
var publicOrigin = publicUri?.GetLeftPart(UriPartial.Authority);
if (publicUri is not null) builder.Services.AddMemoryCache();
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
var googleDirectory = Path.Combine(builder.Environment.ContentRootPath, ".local", "google");
if (OperatingSystem.IsWindows())
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(googleDirectory, "keys")))
        .ProtectKeysWithDpapi();
builder.Services.AddHttpClient("google", client => client.Timeout = TimeSpan.FromSeconds(12))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.WebHost.UseUrls(address);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);
var token = builder.Configuration["DETOT_GATEWAY_TOKEN"];
if (string.IsNullOrWhiteSpace(token))
    throw new InvalidOperationException("DETOT_GATEWAY_TOKEN is required.");

builder.Services.AddHttpClient("gateway", client =>
{
    client.BaseAddress = new Uri($"http://127.0.0.1:{gatewayPort}");
    client.Timeout = TimeSpan.FromSeconds(90);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddConcurrencyLimiter("chat", limiter =>
    {
        limiter.PermitLimit = 1;
        limiter.QueueLimit = 0;
    });
});

var app = builder.Build();
var practice = OperatingSystem.IsWindows()
    ? new Practice(app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Detot.Practice.v1"),
        Path.Combine(builder.Environment.ContentRootPath, ".local", "practice"))
    : null;
var chatHistory = publicUri is not null && OperatingSystem.IsWindows()
    ? new ChatHistory(app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Detot.ChatHistory.v1"),
        Path.Combine(builder.Environment.ContentRootPath, ".local", "chat-history"))
    : null;
var remoteAccess = publicUri is null ? null : new RemoteAccess(
    builder.Configuration["DETOT_REMOTE_PASSWORD"],
    app.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>());
var createGoogleEvent = GoogleAccounts.Map(app, googleDirectory, publicOrigin ?? address, publicUri is not null);
GoogleDriveStorage.Map(app, googleDirectory, publicOrigin ?? address, publicUri is not null,
    builder.Configuration["DETOT_GOOGLE_DRIVE_LOGIN_HINT"] ?? "voquangthan1234");
var trialCalendar = new ConcurrentDictionary<Guid, TrialEvent>();
var calendarReceipts = new ConcurrentDictionary<Guid, TrialEvent>();
var deletedCalendarRequests = new HashSet<Guid>();
var calendarLock = new object();
var calendarTrialEnabled = builder.Configuration.GetValue<bool>("DETOT_ENABLE_CALENDAR_TRIAL");
app.Use(async (context, next) =>
{
    var validHost = publicUri is null
        ? context.Request.Host.Value == $"127.0.0.1:{webPort}" || context.Request.Host.Value == $"localhost:{webPort}"
        : string.Equals(context.Request.Host.Value, publicUri.Authority, StringComparison.OrdinalIgnoreCase);
    if (!validHost)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    if (remoteAccess is null) await next(context);
    else
    {
        // The loopback-only listener sits behind the configured HTTPS proxy.
        context.Request.Scheme = "https";
        await remoteAccess.Handle(context, next, publicOrigin!);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapGet("/workspace", () => Results.Redirect("/app/index.html"));
app.MapGet("/api/practice/notes", () => practice is null
    ? Results.StatusCode(503) : Results.Ok(new { notes = practice.Notes, learning = practice.LearningSummary }));
app.MapPut("/api/practice/notes", (Practice.NoteEdit edit, HttpContext context) =>
{
    if (!IsLocalMutation(context)) return Results.StatusCode(403);
    if (practice is null) return Results.StatusCode(503);
    if (edit.Notes is not { Length: <= 2000 } notes) return Results.BadRequest();
    practice.SaveNotes(notes);
    return Results.NoContent();
}).RequireRateLimiting("chat");
app.MapGet("/api/chat/history", () => chatHistory?.List() ?? Results.StatusCode(503));
app.MapPut("/api/chat/history/{id:guid}", (Guid id, ChatHistory.Update update, HttpContext context) =>
    !IsLocalMutation(context) ? Results.StatusCode(403) : chatHistory?.Save(id, update) ?? Results.StatusCode(503))
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2 * 1024 * 1024));
app.MapDelete("/api/chat/{id:guid}", async Task<IResult> (Guid id, HttpContext context) =>
{
    if (!IsLocalMutation(context)) return Results.StatusCode(403);
    var node = builder.Configuration["DETOT_LAB_NODE"];
    var runtime = builder.Configuration["DETOT_LAB_RUNTIME"];
    if (!File.Exists(node) || !File.Exists(runtime))
        return Results.Problem("Khởi động qua Start-Lab.ps1 để xóa cuộc trò chuyện trên Gateway.", statusCode: 503);
    var info = new System.Diagnostics.ProcessStartInfo(node!)
    {
        UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
    };
    var parameters = JsonSerializer.Serialize(new { key = ConversationKey(id) });
    foreach (var arg in new[] { runtime!, "gateway", "call", "sessions.delete", "--json", "--params", parameters })
        info.ArgumentList.Add(arg);
    info.Environment["OPENCLAW_GATEWAY_TOKEN"] = token;
    try
    {
        using var child = System.Diagnostics.Process.Start(info)!;
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            return Results.Problem("Gateway chưa xác nhận xóa. Hãy thử lại.", statusCode: 504);
        }
        await Task.WhenAll(output, errors);
        if (child.ExitCode == 0)
        {
            using var result = JsonDocument.Parse(await output);
            if (result.RootElement.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)
            {
                chatHistory?.Delete(id);
                practice?.SaveCase(id, null);
                return Results.NoContent();
            }
        }
        return Results.Problem("Gateway chưa xác nhận xóa. Lịch sử trên trình duyệt vẫn được giữ.", statusCode: 502);
    }
    catch (JsonException)
    {
        return Results.Problem("Không đọc được xác nhận xóa của Gateway.", statusCode: 502);
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return Results.Problem("Không khởi chạy được lệnh xóa Gateway.", statusCode: 503);
    }
}).RequireRateLimiting("chat");
app.MapGet("/api/calendar", () => Results.Ok(new
{
    enabled = calendarTrialEnabled,
    events = trialCalendar.OrderBy(item => item.Value.Start)
        .Select(item => new { id = item.Key, item.Value.Title, item.Value.Start, item.Value.End })
}));
app.MapPut("/api/calendar/{id:guid}", (Guid id, CalendarEdit edit, HttpContext context) =>
{
    if (!calendarTrialEnabled || !IsLocalMutation(context)) return Results.StatusCode(403);
    var title = edit.Title?.Trim();
    if (title is not { Length: > 0 and <= 120 } || edit.End <= edit.Start || edit.Start == default)
        return Results.BadRequest(new { error = "Nhập tên sự kiện và giờ kết thúc sau giờ bắt đầu." });
    lock (calendarLock)
    {
        if (!trialCalendar.TryGetValue(id, out var current)) return Results.NotFound();
        if (trialCalendar.Any(item => item.Key != id && edit.Start < item.Value.End && edit.End > item.Value.Start))
            return Results.Conflict(new { error = "Khung giờ trùng với sự kiện khác." });
        trialCalendar[id] = current with { Title = title, Start = edit.Start, End = edit.End };
        return Results.NoContent();
    }
});
app.MapDelete("/api/calendar/{id:guid}", (Guid id, HttpContext context) =>
{
    if (!calendarTrialEnabled || !IsLocalMutation(context)) return Results.StatusCode(403);
    lock (calendarLock)
    {
        if (!trialCalendar.TryRemove(id, out _)) return Results.NotFound();
        calendarReceipts.TryRemove(id, out _);
        deletedCalendarRequests.Add(id);
        return Results.NoContent();
    }
});
app.MapPost("/api/chat", async Task<IResult> (HttpContext context, IHttpClientFactory factory, CancellationToken cancellationToken) =>
{
    if (!IsLocalMutation(context) || !context.Request.HasJsonContentType())
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    try
    {
        var request = await context.Request.ReadFromJsonAsync<ChatRequest>(cancellationToken);
        if (request?.Message is not { Length: > 0 and <= 1500 } message || string.IsNullOrWhiteSpace(message))
            return Results.BadRequest(new { error = "Nhập từ 1 đến 1500 ký tự." });
        context.Items["conversationId"] = request.ConversationId;

        using var gateway = factory.CreateClient("gateway");
        if (request.ConversationId is Guid conversationId)
            gateway.DefaultRequestHeaders.Add("x-openclaw-session-key", ConversationKey(conversationId));
        var tools = new List<object> { new
        {
            type = "function",
            name = fixture,
            description = "Trả mã kiểm thử cố định của Đệ Tốt AI lab.",
            parameters = new { type = "object", properties = new { }, additionalProperties = false }
        } };
        var caseState = request.ConversationId is Guid caseId ? practice?.Case(caseId) : null;
        var practicing = request.StartPractice || caseState is not null;
        var previous = practicing ? null : request.CalendarContext;
        if (previous is { Length: > 1500 } || (previous is not null && !IsCalendarCommand(previous)))
            return Results.BadRequest(new { error = "Ngữ cảnh tạo lịch không hợp lệ." });
        var command = !practicing && IsCalendarCommand(message) ? message : previous;
        var googleCalendarRequest = command is not null && IsGoogleCalendarCommand(command);
        var calendarRequest = command is not null && (googleCalendarRequest || calendarTrialEnabled);
        var calendarTool = googleCalendarRequest ? "detot_google_calendar_create" : "detot_trial_calendar_create";
        var calendarInput = calendarRequest && previous is not null && !IsCalendarCommand(message)
            ? previous + "\nThông tin bổ sung từ người dùng: " + message
            : message;
        if (calendarRequest && calendarInput.Length > 1500)
            return Results.BadRequest(new { error = "Hãy gửi lại yêu cầu tạo lịch đầy đủ trong một tin nhắn." });
        var history = request.History ?? [];
        if (history.Length > 8 || history.Any(turn => turn is null ||
            turn.Role is not ("user" or "assistant") || turn.Text is not { Length: > 0 and <= 450 }))
            return Results.BadRequest(new { error = "Lịch sử trò chuyện không hợp lệ." });
        if (practice is not null && request.ConversationId is Guid practiceId &&
            (request.StartPractice || caseState is not null))
            return Results.Ok(new { reply = practice.Process(practiceId, message, request.StartPractice).Reply });
        object gatewayInput = history.Length == 0 || calendarRequest
            ? calendarInput
            : history.Select(turn => new { type = "message", role = turn.Role, content = turn.Text })
                .Append(new { type = "message", role = "user", content = calendarInput }).ToArray();
        if (calendarRequest && !googleCalendarRequest && request.RequestId is Guid existingId)
        {
            lock (calendarLock)
            {
                if (deletedCalendarRequests.Contains(existingId))
                    return Results.Conflict(new { error = "Sự kiện của yêu cầu này đã bị xóa." });
                if (calendarReceipts.TryGetValue(existingId, out var existing))
                    return existing.Message == calendarInput ? CalendarReply(existing) : Results.Conflict();
            }
        }
        if (calendarRequest)
            tools.Add(new
            {
                type = "function", name = calendarTool,
                description = "Tạo một sự kiện khi có tên, giờ bắt đầu và giờ kết thúc hoặc thời lượng rõ ràng. Nếu thiếu hoặc mơ hồ, hỏi lại." +
                    (googleCalendarRequest ? " Đọc tất cả mốc nhắc được yêu cầu, kể cả trong ngữ cảnh bổ sung. Hỏi lại nếu mốc nhắc mơ hồ." : ""),
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string" },
                        start = new { type = "string", description = "yyyy-MM-ddTHH:mm:sszzz" },
                        end = new { type = "string", description = "yyyy-MM-ddTHH:mm:sszzz" },
                        reminderMinutes = new { type = "array", items = new { type = "integer", minimum = 0, maximum = 40320 },
                            maxItems = 5, uniqueItems = true,
                            description = "Chỉ dùng với Google Calendar. Số phút trước sự kiện. Bỏ trường nếu không yêu cầu nhắc, mảng rỗng nếu yêu cầu tắt nhắc." }
                    },
                    required = new[] { "title", "start", "end" },
                    additionalProperties = false
                }
            });
        using var first = await CallGateway(gateway, new
        {
            model = "openclaw/default",
            input = gatewayInput,
            instructions = calendarRequest
                ? instructions + $" Múi giờ mặc định UTC+07:00. Giờ hiện tại: {DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)):O}. Hỏi lại nếu thời gian không rõ. Không tự đặt thời lượng. Chỉ xử lý yêu cầu tạo một sự kiện."
                : instructions,
            tools
        }, cancellationToken);

        var calls = first.RootElement.GetProperty("output").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "function_call").ToArray();
        if (calls.Length == 0)
            return Reply(first.RootElement, false, calendarRequest ? calendarInput : null);
        if (calendarRequest && calls.Length == 1 && calls[0].GetProperty("name").GetString() == calendarTool)
        {
            if (request.RequestId is not Guid requestId || requestId == Guid.Empty)
                return Results.BadRequest(new { error = "Thiếu mã yêu cầu tạo lịch." });
            using var eventArguments = JsonDocument.Parse(calls[0].GetProperty("arguments").GetString() ?? "");
            var fields = eventArguments.RootElement;
            if (fields.ValueKind != JsonValueKind.Object ||
                !ReadString(fields, "title", out var title) || title.Length > 120 ||
                !ReadString(fields, "start", out var startText) ||
                !ReadString(fields, "end", out var endText) ||
                !DateTimeOffset.TryParseExact(startText, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
                !DateTimeOffset.TryParseExact(endText, "yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) ||
                end <= start)
                return Results.BadRequest(new { error = "Bạn cho mình tên sự kiện, giờ bắt đầu và giờ kết thúc nhé." });

            if (googleCalendarRequest)
            {
                int[]? reminders = null;
                if (fields.TryGetProperty("reminderMinutes", out var values))
                {
                    if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 5 ||
                        values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)))
                        return Results.BadRequest(new { error = "Mốc nhắc phải là số phút nguyên, tối đa 5 mốc." });
                    reminders = values.EnumerateArray().Select(value => value.GetInt32()).ToArray();
                    if (!GoogleAccounts.ValidReminderMinutes(reminders))
                        return Results.BadRequest(new { error = "Chọn các mốc nhắc khác nhau từ 0 đến 40320 phút." });
                }
                var result = await createGoogleEvent(new(title, start, end, requestId, reminders), context, factory);
                if (result is IValueHttpResult { Value: GoogleAccounts.CalendarConflict conflict })
                    return Results.Ok(new { reply = conflict.Reply, calendarContext = calendarInput });
                return result;
            }

            var proposed = new TrialEvent(calendarInput, title, start, end);
            lock (calendarLock)
            {
                if (deletedCalendarRequests.Contains(requestId))
                    return Results.Conflict(new { error = "Sự kiện của yêu cầu này đã bị xóa." });
                if (calendarReceipts.TryGetValue(requestId, out var receipt))
                    return receipt.Message == calendarInput ? CalendarReply(receipt) : Results.Conflict();
                if (trialCalendar.Values.Any(item => start < item.End && end > item.Start))
                    return Results.Conflict(new { error = "Khung giờ này trùng với một sự kiện trong lịch thử. Bạn chọn giờ khác nhé." });
                trialCalendar[requestId] = proposed;
                calendarReceipts[requestId] = proposed;
                return CalendarReply(proposed);
            }
        }
        if (calls.Length != 1 || calls[0].GetProperty("name").GetString() != fixture)
            return Results.Problem("Gateway đề xuất tool ngoài hợp đồng lab.", statusCode: 502);

        using var arguments = JsonDocument.Parse(calls[0].GetProperty("arguments").GetString() ?? "");
        if (arguments.RootElement.ValueKind != JsonValueKind.Object || arguments.RootElement.EnumerateObject().Any())
            return Results.Problem("Tham số tool lab không hợp lệ.", statusCode: 502);

        using var second = await CallGateway(gateway, new
        {
            model = "openclaw/default",
            previous_response_id = first.RootElement.GetProperty("id").GetString(),
            instructions = instructions + " Khi đã có kết quả công cụ, trả lời trực tiếp từ kết quả đó.",
            tools,
            input = new[] { new
            {
                type = "function_call_output",
                call_id = calls[0].GetProperty("call_id").GetString(),
                output = "{\"code\":\"DETOT-LAB-003\"}"
            } }
        }, cancellationToken);
        if (second.RootElement.GetProperty("output").EnumerateArray().Any(item => item.GetProperty("type").GetString() == "function_call"))
            return Results.Problem("Gateway đề xuất gọi lại tool đã hoàn tất.", statusCode: 502);
        return Reply(second.RootElement, true);
    }
    catch (BadHttpRequestException)
    {
        return Results.BadRequest(new { error = "Yêu cầu không hợp lệ." });
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return Results.Problem("Gateway hết thời gian phản hồi.", statusCode: 504);
    }
    catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
    {
        app.Logger.LogWarning("Chat failed ({ErrorType}).", error.GetType().Name);
        return Results.Problem("Gateway không trả được kết quả. Kiểm tra tiến trình lab và cấu hình provider.", statusCode: 502);
    }
}).AddEndpointFilter(async (invocation, next) =>
{
    var context = invocation.HttpContext;
    var result = await next(invocation);
    if (chatHistory is not null && context.Items["conversationId"] is Guid id &&
        Guid.TryParse(context.Request.Headers["X-Detot-History-Revision"], out var revision) &&
        result is IValueHttpResult { Value: { } reply })
    {
        var saved = chatHistory.AppendReply(id, revision, reply);
        if (saved is Guid updated) context.Response.Headers["X-Detot-History-Revision"] = updated.ToString();
    }
    return result;
}).RequireRateLimiting("chat");

app.Run();

static bool IsLocalMutation(HttpContext context) =>
    context.Request.Headers.Origin == $"{context.Request.Scheme}://{context.Request.Host.Value}";

static string ConversationKey(Guid id) => $"agent:main:detot-chat-{id:N}";

static async Task<JsonDocument> CallGateway(HttpClient client, object body, CancellationToken cancellationToken)
{
    using var response = await client.PostAsJsonAsync("/v1/responses", body, cancellationToken);
    response.EnsureSuccessStatusCode();
    return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
}

static IResult Reply(JsonElement result, bool toolUsed, string? calendarContext = null)
{
    var text = GatewayText(result);
    return string.IsNullOrWhiteSpace(text)
        ? Results.Problem("Gateway không có câu trả lời văn bản.", statusCode: 502)
        : Results.Ok(new { reply = text, toolUsed, calendarContext = calendarContext is { Length: <= 1500 } ? calendarContext : null });
}

static string GatewayText(JsonElement result) =>
    string.Join("\n", result.GetProperty("output").EnumerateArray()
        .Where(item => item.GetProperty("type").GetString() == "message")
        .SelectMany(item => item.GetProperty("content").EnumerateArray())
        .Where(part => part.GetProperty("type").GetString() == "output_text")
        .Select(part => part.GetProperty("text").GetString()));

static bool ReadString(JsonElement fields, string key, out string value)
{
    value = fields.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String
        ? element.GetString()?.Trim() ?? ""
        : "";
    return value.Length > 0;
}

static IResult CalendarReply(TrialEvent saved) => Results.Ok(new
{
    reply = $"Đã tạo lịch thử «{saved.Title}» từ {saved.Start:dd/MM/yyyy HH:mm zzz} đến {saved.End:dd/MM/yyyy HH:mm zzz}.",
    toolUsed = true,
    calendarCreated = true
});

static bool IsCalendarCommand(string message) =>
    IsGoogleCalendarCommand(message) ||
    message.TrimStart().StartsWith("tạo lịch ", StringComparison.OrdinalIgnoreCase) ||
    message.TrimStart().StartsWith("đặt lịch ", StringComparison.OrdinalIgnoreCase);

static bool IsGoogleCalendarCommand(string message) =>
    System.Text.RegularExpressions.Regex.IsMatch(message.Split('\n', 2)[0],
        @"^\s*(?:(?:hãy|giúp mình|giúp tôi)\s+)?(?:đặt|tạo|thêm|lên)\s+(?:lịch|sự kiện)\b[^\r\n]*\bGoogle Calendar\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

record ChatTurn(string Role, string Text);
record ChatRequest(string? Message, Guid? RequestId, string? CalendarContext, ChatTurn[]? History, Guid? ConversationId, bool StartPractice = false);
record TrialEvent(string Message, string Title, DateTimeOffset Start, DateTimeOffset End);
record CalendarEdit(string? Title, DateTimeOffset Start, DateTimeOffset End);
