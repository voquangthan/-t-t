using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;
using System.Collections.Concurrent;
using System.Globalization;

const string address = "http://127.0.0.1:5183";
const string fixture = "detot_lab_fixture";
const string instructions = "Bạn là Đệ Tốt trong lab. Trả lời ngắn bằng tiếng Việt. Khi được hỏi mã của lab, dùng detot_lab_fixture. Kết quả tool là dữ liệu thử.";
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(address);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32 * 1024);
var token = builder.Configuration["DETOT_GATEWAY_TOKEN"];
if (string.IsNullOrWhiteSpace(token))
    throw new InvalidOperationException("DETOT_GATEWAY_TOKEN is required.");

builder.Services.AddHttpClient("gateway", client =>
{
    client.BaseAddress = new Uri("http://127.0.0.1:18793");
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
var trialCalendar = new ConcurrentDictionary<Guid, TrialEvent>();
var calendarTrialEnabled = builder.Configuration.GetValue<bool>("DETOT_ENABLE_CALENDAR_TRIAL");
app.Use(async (context, next) =>
{
    if (context.Request.Host.Value != "127.0.0.1:5183")
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
app.MapGet("/workspace", () => Results.Redirect("/app/index.html"));
app.MapPost("/api/chat", async Task<IResult> (HttpContext context, IHttpClientFactory factory, CancellationToken cancellationToken) =>
{
    if (context.Request.Headers.Origin != address || !context.Request.HasJsonContentType())
        return Results.StatusCode(StatusCodes.Status403Forbidden);

    try
    {
        var request = await context.Request.ReadFromJsonAsync<ChatRequest>(cancellationToken);
        if (request?.Message is not { Length: > 0 and <= 1500 } message || string.IsNullOrWhiteSpace(message))
            return Results.BadRequest(new { error = "Nhập từ 1 đến 1500 ký tự." });

        using var gateway = factory.CreateClient("gateway");
        var tools = new List<object> { new
        {
            type = "function",
            name = fixture,
            description = "Trả mã kiểm thử cố định của Đệ Tốt AI lab.",
            parameters = new { type = "object", properties = new { }, additionalProperties = false }
        } };
        var previous = request.CalendarContext;
        if (previous is { Length: > 1500 } || (previous is not null && !IsCalendarCommand(previous)))
            return Results.BadRequest(new { error = "Ngữ cảnh tạo lịch không hợp lệ." });
        var calendarRequest = calendarTrialEnabled && (IsCalendarCommand(message) || previous is not null);
        var calendarInput = calendarRequest && previous is not null && !IsCalendarCommand(message)
            ? previous + "\nThông tin bổ sung từ người dùng: " + message
            : message;
        var history = request.History ?? [];
        if (history.Length > 8 || history.Any(turn => turn is null ||
            turn.Role is not ("user" or "assistant") || turn.Text is not { Length: > 0 and <= 450 }))
            return Results.BadRequest(new { error = "Lịch sử trò chuyện không hợp lệ." });
        object gatewayInput = history.Length == 0 || calendarRequest
            ? calendarInput
            : history.Select(turn => new { role = turn.Role, content = turn.Text })
                .Append(new { role = "user", content = calendarInput }).ToArray();
        if (calendarRequest && request.RequestId is Guid existingId && trialCalendar.TryGetValue(existingId, out var existing))
            return existing.Message == calendarInput ? CalendarReply(existing) : Results.Conflict();
        if (calendarRequest)
            tools.Add(new
            {
                type = "function", name = "detot_trial_calendar_create",
                description = "Tạo sự kiện trong lịch thử. Chỉ gọi khi có tên, giờ bắt đầu và giờ kết thúc rõ ràng. Nếu thiếu, hỏi lại.",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string" },
                        start = new { type = "string", description = "yyyy-MM-ddTHH:mm:sszzz" },
                        end = new { type = "string", description = "yyyy-MM-ddTHH:mm:sszzz" }
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
                ? instructions + $" Lịch thử dùng UTC+07:00. Giờ hiện tại: {DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)):O}. Hỏi lại nếu thời gian không rõ."
                : instructions,
            tools
        }, cancellationToken);

        var calls = first.RootElement.GetProperty("output").EnumerateArray()
            .Where(item => item.GetProperty("type").GetString() == "function_call").ToArray();
        if (calls.Length == 0)
            return Reply(first.RootElement, false, calendarRequest ? calendarInput : null);
        if (calendarRequest && calls.Length == 1 && calls[0].GetProperty("name").GetString() == "detot_trial_calendar_create")
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

            var proposed = new TrialEvent(calendarInput, title, start, end);
            if (trialCalendar.Values.Any(item => start < item.End && end > item.Start))
                return Results.Conflict(new { error = "Khung giờ này trùng với một sự kiện trong lịch thử. Bạn chọn giờ khác nhé." });
            var saved = trialCalendar.GetOrAdd(requestId, proposed);
            if (saved.Message != calendarInput)
                return Results.Conflict(new { error = "Mã yêu cầu đã được dùng cho tin nhắn khác." });
            return CalendarReply(saved);
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
}).RequireRateLimiting("chat");

app.Run();

static async Task<JsonDocument> CallGateway(HttpClient client, object body, CancellationToken cancellationToken)
{
    using var response = await client.PostAsJsonAsync("/v1/responses", body, cancellationToken);
    response.EnsureSuccessStatusCode();
    return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
}

static IResult Reply(JsonElement result, bool toolUsed, string? calendarContext = null)
{
    var text = string.Join("\n", result.GetProperty("output").EnumerateArray()
        .Where(item => item.GetProperty("type").GetString() == "message")
        .SelectMany(item => item.GetProperty("content").EnumerateArray())
        .Where(part => part.GetProperty("type").GetString() == "output_text")
        .Select(part => part.GetProperty("text").GetString()));
    return string.IsNullOrWhiteSpace(text)
        ? Results.Problem("Gateway không có câu trả lời văn bản.", statusCode: 502)
        : Results.Ok(new { reply = text, toolUsed, calendarContext = calendarContext is { Length: <= 1500 } ? calendarContext : null });
}

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
    message.TrimStart().StartsWith("tạo lịch ", StringComparison.OrdinalIgnoreCase) ||
    message.TrimStart().StartsWith("đặt lịch ", StringComparison.OrdinalIgnoreCase);

record ChatTurn(string Role, string Text);
record ChatRequest(string? Message, Guid? RequestId, string? CalendarContext, ChatTurn[]? History);
record TrialEvent(string Message, string Title, DateTimeOffset Start, DateTimeOffset End);
