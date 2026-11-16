using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

var root = Path.Combine(Path.GetTempPath(), "detot-weather-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var protection = new EphemeralDataProtectionProvider();
    var consent = new ConsentPolicy(Path.Combine(root, "consent"), protection);
    var handler = new WeatherTestHandler();
    var service = NewService(Path.Combine(root, "weather"), protection, consent, handler);
    var ownerA = "synthetic-weather-owner-a";
    var ownerB = "synthetic-weather-owner-b";

    var denied = false;
    try { await service.SetLocationAsync(ownerA, "Đà Nẵng", CancellationToken.None); }
    catch (UnauthorizedAccessException) { denied = true; }
    Check(denied && handler.Requests == 0, "default deny must happen before geocoding or any provider request");

    GrantWeather(consent, ownerA);
    var legacyOwner = "synthetic-legacy-weather-owner";
    GrantWeather(consent, legacyOwner);
    var legacyDirectory = Path.Combine(root, "legacy-weather");
    WriteLegacyWeatherState(legacyDirectory, protection, consent, legacyOwner);
    var legacySnapshot = await NewService(legacyDirectory, protection, consent, new WeatherTestHandler())
        .GetAsync(legacyOwner, CancellationToken.None);
    Check(legacySnapshot.Location == "Legacy Test City" && legacySnapshot.Suggestions.Single().Id == "0123456789abcdef",
        "the pre-migration owner-encrypted weather storage schema must remain readable");

    var snapshot = await service.SetLocationAsync(ownerA, "Đà Nẵng", CancellationToken.None);
    AssertWeatherApiGolden(snapshot);
    Check(snapshot.Location == "Đà Nẵng, Đà Nẵng, Việt Nam" && snapshot.Suggestions.Length == 1,
        "qualified rain should create one sourced suggestion");
    Check(snapshot.Suggestions[0].Title == "Có thể mưa lớn" && snapshot.Suggestions[0].Probability == 85 &&
        snapshot.Suggestions[0].Reason.Contains("18.0 mm", StringComparison.Ordinal) && snapshot.Suggestions[0].Source == "Open-Meteo" &&
        snapshot.Suggestions[0].Advice.Contains("mang", StringComparison.OrdinalIgnoreCase), "suggestion facts and provider advice should be grounded");
    Check(handler.DeepSeekCalls == 1 && !handler.DeepSeekPayload.Contains("Đà Nẵng", StringComparison.Ordinal) &&
        !handler.DeepSeekPayload.Contains("16.05", StringComparison.Ordinal) && !handler.DeepSeekPayload.Contains("108.2", StringComparison.Ordinal),
        "DeepSeek must receive forecast fields without city name or coordinates");
    var officialDenied = false;
    try { await service.GetOfficialAlertsAsync(ownerA, CancellationToken.None); }
    catch (UnauthorizedAccessException) { officialDenied = true; }
    Check(officialDenied && handler.OfficialAlertCalls == 0,
        "official alert lookup must default-deny before sending the selected location to Google");
    consent.Set(ownerA, "google_weather_alerts", "proactive_analysis", "external_service", true, "owner", Guid.NewGuid());
    var officialAlerts = await service.GetOfficialAlertsAsync(ownerA, CancellationToken.None);
    Check(handler.OfficialAlertCalls == 1 && officialAlerts.Count == 1 &&
        officialAlerts[0].EventType == "RAIN" &&
        officialAlerts[0].AuthorityName == "National Center for Hydrometeorological Forecasting" &&
        officialAlerts[0].AreaName.Contains("Nha Trang", StringComparison.Ordinal),
        "Google Public Alerts must return sanitized active NCHMF alerts for the selected place");
    Check(handler.OfficialAlertRequestUri?.Query.Contains("location.latitude=16.05", StringComparison.Ordinal) == true &&
        handler.OfficialAlertRequestUri.Query.Contains("location.longitude=108.2", StringComparison.Ordinal) &&
        !handler.OfficialAlertRequestUri.Query.Contains(ownerA, StringComparison.Ordinal),
        "only the manually selected place coordinate, never owner identity or device GPS, may be sent to Google");
    _ = await service.GetOfficialAlertsAsync(ownerA, CancellationToken.None);
    Check(handler.OfficialAlertCalls == 1, "official alert polling must use a short volatile cache instead of repeated billable requests");
    consent.Set(ownerA, "google_weather_alerts", "proactive_analysis", "external_service", false, "owner", Guid.NewGuid());
    service.RevokeOfficialAlertsConsent(ownerA);
    var revokedAlertDenied = false;
    try { await service.GetOfficialAlertsAsync(ownerA, CancellationToken.None); }
    catch (UnauthorizedAccessException) { revokedAlertDenied = true; }
    Check(revokedAlertDenied && handler.OfficialAlertCalls == 1,
        "revoked alert consent must prevent both provider access and reuse of the volatile cache");
    consent.Set(ownerA, "google_weather_alerts", "proactive_analysis", "external_service", true, "owner", Guid.NewGuid());
    _ = await service.GetOfficialAlertsAsync(ownerA, CancellationToken.None);
    Check(handler.OfficialAlertCalls == 2, "regrant must perform a fresh source request rather than resurrect revoked cache");
    var encryptedWeather = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "weather"), "weather-*.bin").Single());
    Check(!encryptedWeather.Contains("Đà Nẵng", StringComparison.Ordinal) && !encryptedWeather.Contains("16.05", StringComparison.Ordinal),
        "local weather location/cache must be encrypted at rest");

    var deceptiveHandler = new WeatherTestHandler { DeceptiveModel = true };
    var deceptiveService = NewService(Path.Combine(root, "deceptive-weather"), protection, consent, deceptiveHandler);
    var grounded = await deceptiveService.SetLocationAsync(ownerA, "Đà Nẵng", CancellationToken.None);
    Check(grounded.Suggestions.Length == 1 && grounded.Suggestions[0].Title == "Có thể mưa lớn" &&
        grounded.Suggestions[0].Probability == 85 && grounded.Suggestions[0].Reason.Contains("18.0 mm", StringComparison.Ordinal) &&
        !grounded.Suggestions[0].Reason.Contains("evacuate", StringComparison.OrdinalIgnoreCase) &&
        grounded.Suggestions[0].Advice == "Mang áo mưa hoặc ô nếu phải ra ngoài trong khung giờ này.",
        "model-written warning, measurements, reason and advice text must be ignored; backend facts and advice remain bounded");
    var unsafeAdviceHandler = new WeatherTestHandler { UnsupportedAdvice = true };
    var unsafeAdvice = await NewService(Path.Combine(root, "unsafe-advice"), protection, consent, unsafeAdviceHandler)
        .SetLocationAsync(ownerA, "Đà Nẵng", CancellationToken.None);
    Check(unsafeAdvice.Suggestions.Length == 0, "unknown AI advice codes must fail closed without creating a suggestion");

    _ = await service.RefreshAsync(ownerA, false, CancellationToken.None);
    Check(handler.ForecastCalls == 1 && handler.DeepSeekCalls == 1, "fresh cache must prevent duplicate provider and model calls");
    var feedbackDenied = false;
    try { await service.RecordFeedbackAsync(ownerA, snapshot.Suggestions[0].Id, "dismissed", CancellationToken.None); }
    catch (UnauthorizedAccessException) { feedbackDenied = true; }
    Check(feedbackDenied, "feedback must be separately denied without skill_learning consent");
    consent.Set(ownerA, "proactive_feedback", "skill_learning", "local_only", true, "owner", Guid.NewGuid());
    Check(await service.RecordFeedbackAsync(ownerA, snapshot.Suggestions[0].Id, "dismissed", CancellationToken.None), "authorized feedback should be saved");
    Check((await service.GetAsync(ownerA, CancellationToken.None)).Suggestions.Length == 0, "dismissal must suppress the duplicate suggestion");
    _ = await service.RecordFeedbackAsync(ownerA, snapshot.Suggestions[0].Id, "dismissed", CancellationToken.None);
    _ = await service.RecordFeedbackAsync(ownerA, snapshot.Suggestions[0].Id, "dismissed", CancellationToken.None);
    var learned = await service.RefreshAsync(ownerA, true, CancellationToken.None);
    Check(learned.Suggestions.Length == 0 && handler.DeepSeekCalls == 1,
        "three local dismissals should suppress repeated sub-90% rain nudges before model analysis");

    var restarted = NewService(Path.Combine(root, "weather"), protection, consent, handler);
    var restartedSnapshot = await restarted.GetAsync(ownerA, CancellationToken.None);
    Check(restartedSnapshot.Location == snapshot.Location && restartedSnapshot.Suggestions.Length == 0,
        "encrypted location and dismissal should survive restart under the same owner");
    var feedbackFile = Directory.GetFiles(Path.Combine(root, "weather"), "feedback-*.bin").Single();
    var staleFeedbackCopy = Path.Combine(root, "stale-feedback.bin");
    File.Copy(feedbackFile, staleFeedbackCopy);
    consent.Set(ownerA, "proactive_feedback", "skill_learning", "local_only", false, "owner", Guid.NewGuid());
    await restarted.RevokeFeedbackConsentAsync(ownerA, CancellationToken.None);
    consent.Set(ownerA, "proactive_feedback", "skill_learning", "local_only", true, "owner", Guid.NewGuid());
    File.Copy(staleFeedbackCopy, feedbackFile);
    var afterFeedbackRegrant = await NewService(Path.Combine(root, "weather"), protection, consent, handler).GetAsync(ownerA, CancellationToken.None);
    Check(afterFeedbackRegrant.DismissedCount == 0 && !File.Exists(feedbackFile),
        "a stale feedback file must not reappear after revoke and regrant");
    var wrongKey = NewService(Path.Combine(root, "weather"), new EphemeralDataProtectionProvider(), consent, handler);
    var unreadable = await wrongKey.GetAsync(ownerA, CancellationToken.None);
    Check(unreadable.Location is null && unreadable.Suggestions.Length == 0 && unreadable.Error is not null,
        "unreadable encrypted state must fail closed");

    var weatherFile = Directory.GetFiles(Path.Combine(root, "weather"), "weather-*.bin").Single();
    var staleWeatherCopy = Path.Combine(root, "stale-weather.bin");
    File.Copy(weatherFile, staleWeatherCopy);
    consent.Set(ownerA, "public_weather", "proactive_analysis", "external_service", false, "owner", Guid.NewGuid());
    await restarted.RevokeWeatherConsentAsync(ownerA, CancellationToken.None);
    consent.Set(ownerA, "public_weather", "proactive_analysis", "external_service", true, "owner", Guid.NewGuid());
    File.Copy(staleWeatherCopy, weatherFile);
    var afterWeatherRegrant = await NewService(Path.Combine(root, "weather"), protection, consent, handler).GetAsync(ownerA, CancellationToken.None);
    Check(afterWeatherRegrant.Location is null && !File.Exists(weatherFile),
        "a stale location/cache file must not reappear after weather revocation and regrant");
    GrantWeather(consent, ownerB);
    var isolated = await restarted.GetAsync(ownerB, CancellationToken.None);
    Check(isolated.Location is null && isolated.Suggestions.Length == 0, "owner B must not see owner A's location or suggestions");

    var badProvider = new WeatherTestHandler { FailForecast = true };
    var failing = NewService(Path.Combine(root, "bad-weather"), protection, consent, badProvider);
    var failedSnapshot = await failing.SetLocationAsync(ownerB, "Đà Nẵng", CancellationToken.None);
    Check(failedSnapshot.Suggestions.Length == 0 && failedSnapshot.Error is not null,
        "provider failure must produce no fabricated alert and a safe status");

    var offlineHandler = new WeatherTestHandler { FailForecastNetwork = true };
    var offline = await NewService(Path.Combine(root, "offline-weather"), protection, consent, offlineHandler)
        .SetLocationAsync(ownerB, "Đà Nẵng", CancellationToken.None);
    Check(offline.Suggestions.Length == 0 && offline.Error?.Contains("Không kết nối", StringComparison.Ordinal) == true,
        "network loss during forecast retrieval must not fabricate an alert and must return a safe status");

    var failedModelHandler = new WeatherTestHandler { FailDeepSeek = true };
    var failedModel = await NewService(Path.Combine(root, "bad-model"), protection, consent, failedModelHandler)
        .SetLocationAsync(ownerB, "Đà Nẵng", CancellationToken.None);
    Check(failedModel.Suggestions.Length == 0 && failedModel.Error is not null,
        "DeepSeek errors must not create or preserve a new unsupported suggestion");

    var staleOwner = "synthetic-weather-stale-owner";
    GrantWeather(consent, staleOwner);
    var staleDirectory = Path.Combine(root, "stale-weather");
    var freshState = await NewService(staleDirectory, protection, consent, new WeatherTestHandler())
        .SetLocationAsync(staleOwner, "Đà Nẵng", CancellationToken.None);
    Check(freshState.Suggestions.Length == 1, "fresh fixture should establish a cached suggestion before aging it");
    var staleFile = Directory.GetFiles(staleDirectory, "weather-*.bin").Single();
    var weatherProtector = protection.CreateProtector("Detot.WeatherIntelligence.v1");
    var stateJson = JsonNode.Parse(weatherProtector.Unprotect(File.ReadAllText(staleFile)))!;
    stateJson["cache"]!["fetchedAt"] = DateTimeOffset.UtcNow.AddHours(-3).ToString("O");
    File.WriteAllText(staleFile, weatherProtector.Protect(stateJson.ToJsonString()));
    var staleRead = await NewService(staleDirectory, protection, consent, new WeatherTestHandler { FailForecast = true })
        .RefreshAsync(staleOwner, true, CancellationToken.None);
    Check(staleRead.Suggestions.Length == 0 && !staleRead.ForecastCurrent && staleRead.Error is not null,
        "a forecast older than two hours must not be shown as an active suggestion when refresh fails");

    var blocking = new WeatherTestHandler { BlockDeepSeek = true };
    var racing = NewService(Path.Combine(root, "race-weather"), protection, consent, blocking);
    var run = racing.SetLocationAsync(ownerB, "Đà Nẵng", CancellationToken.None);
    await blocking.DeepSeekStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    consent.Set(ownerB, "public_weather", "proactive_analysis", "DeepSeek", false, "owner", Guid.NewGuid());
    await racing.RevokeWeatherConsentAsync(ownerB, CancellationToken.None);
    var cancelled = false;
    try { await run; } catch (OperationCanceledException) { cancelled = true; } catch (UnauthorizedAccessException) { cancelled = true; }
    Check(cancelled && blocking.DeepSeekCancelled.Task.IsCompleted, "revocation during model evaluation must cancel the job");
    Check(!File.Exists(Directory.GetFiles(Path.Combine(root, "race-weather"), "weather-*.bin").FirstOrDefault() ?? ""),
        "revocation must delete encrypted cache, location and derived suggestions");
    var afterRevoke = false;
    try { await racing.GetAsync(ownerB, CancellationToken.None); } catch (UnauthorizedAccessException) { afterRevoke = true; }
    Check(afterRevoke, "revocation must block subsequent reads");

    service.RevokeOfficialAlertsConsent(ownerA);
    handler.BlockOfficialAlerts = true;
    var alertsInFlight = service.GetOfficialAlertsAsync(ownerA, CancellationToken.None);
    await handler.OfficialAlertStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    consent.Set(ownerA, "google_weather_alerts", "proactive_analysis", "external_service", false, "owner", Guid.NewGuid());
    var alertCancelled = false;
    try { await alertsInFlight; } catch (OperationCanceledException) { alertCancelled = true; }
    Check(alertCancelled && handler.OfficialAlertCancelled.Task.IsCompleted,
        "revoking Google alert consent must cancel an in-flight location request before returning data");

    await ProactiveContractTests.RunAsync();

    await restarted.ClearLocationAsync(ownerA, CancellationToken.None);
    Check((await restarted.GetAsync(ownerA, CancellationToken.None)).Location is null &&
        Directory.GetFiles(Path.Combine(root, "weather"), "weather-*.bin").Length == 0,
        "explicit location deletion must cancel work and remove its encrypted local store");
    if (args.Contains("--live", StringComparer.Ordinal)) await LiveWeatherSmoke.RunAsync();
    Console.WriteLine("PASS: weather default deny, owner isolation, encrypted restart, cache/dedupe, grounded AI output, stale data, network/provider/model failure, feedback consent and mid-request revocation.");
}
finally { try { Directory.Delete(root, recursive: true); } catch { } }

static WeatherIntelligence NewService(string path, IDataProtectionProvider protection, ConsentPolicy consent, HttpMessageHandler handler) =>
    new(path, protection, new WeatherTestClientFactory(handler), consent, "synthetic-deepseek-key",
        NullLogger<WeatherIntelligence>.Instance, "synthetic-google-weather-key");

static void GrantWeather(ConsentPolicy consent, string owner)
{
    consent.Set(owner, "public_weather", "proactive_analysis", "local_only", true, "owner", Guid.NewGuid());
    consent.Set(owner, "public_weather", "proactive_analysis", "external_service", true, "owner", Guid.NewGuid());
    consent.Set(owner, "public_weather", "proactive_analysis", "DeepSeek", true, "owner", Guid.NewGuid());
}

static void WriteLegacyWeatherState(string directory, IDataProtectionProvider protection, ConsentPolicy consent, string owner)
{
    Directory.CreateDirectory(directory);
    var now = DateTimeOffset.UtcNow;
    var marker = string.Join("|", new[] { "local_only", "external_service", "DeepSeek" }.Select(destination =>
        consent.GetGrantChangedAt(owner, "public_weather", "proactive_analysis", destination)!.Value.UtcDateTime.Ticks
            .ToString(System.Globalization.CultureInfo.InvariantCulture)));
    var legacy = new
    {
        location = new { name = "Legacy Test City", latitude = 1.25, longitude = 2.5, timeZone = "UTC" },
        cache = new
        {
            fetchedAt = now,
            forecast = new
            {
                hours = new[] { new { time = now.AddHours(1), probability = 25d, millimeters = 0d } },
                timeZone = "UTC", timeZoneAbbreviation = "UTC", generatedAt = now
            }
        },
        suggestions = new[]
        {
            new
            {
                id = "0123456789abcdef", kind = "heavy_rain", title = "Có thể mưa lớn",
                startsAt = now.AddHours(1), endsAt = now.AddHours(2), probability = 85d, certainty = "cao",
                reason = "Dự báo 3 giờ có khoảng 18.0 mm mưa; xác suất theo giờ đạt đỉnh 85%.",
                advice = "Mang áo mưa hoặc ô nếu phải ra ngoài trong khung giờ này.", source = "Open-Meteo",
                sourceUrl = "https://open-meteo.com/en/docs", forecastIssuedAt = now
            }
        },
        consentMarker = marker,
        lastUpdated = now
    };
    var json = JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var protector = protection.CreateProtector("Detot.WeatherIntelligence.v1");
    var ownerHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner))).ToLowerInvariant();
    File.WriteAllText(Path.Combine(directory, $"weather-{ownerHash}.bin"), protector.Protect(json));
}

static void AssertWeatherApiGolden(WeatherIntelligence.WeatherSnapshot snapshot)
{
    var payload = JsonSerializer.SerializeToNode(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    payload["fetchedAt"] = "<timestamp>";
    var suggestion = payload["suggestions"]![0]!;
    suggestion["id"] = "<opaque-id>";
    suggestion["startsAt"] = "<timestamp>";
    suggestion["endsAt"] = "<timestamp>";
    suggestion["forecastIssuedAt"] = "<timestamp>";
    var actual = JsonNode.Parse(JsonSerializer.Serialize(payload));
    var goldenPath = Path.Combine(AppContext.BaseDirectory, "weather-response.golden.json");
    var expected = JsonNode.Parse(File.ReadAllText(goldenPath));
    Check(JsonNode.DeepEquals(actual, expected), $"weather API response differs from baseline golden. Actual: {actual}");
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

sealed class WeatherTestClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

sealed class WeatherTestHandler : HttpMessageHandler
{
    public int Requests { get; private set; }
    public int ForecastCalls { get; private set; }
    public int DeepSeekCalls { get; private set; }
    public int OfficialAlertCalls { get; private set; }
    public Uri? OfficialAlertRequestUri { get; private set; }
    public bool BlockOfficialAlerts { get; set; }
    public bool FailForecast { get; init; }
    public bool FailForecastNetwork { get; init; }
    public bool FailDeepSeek { get; init; }
    public bool DeceptiveModel { get; init; }
    public bool UnsupportedAdvice { get; init; }
    public bool BlockDeepSeek { get; init; }
    public string DeepSeekPayload { get; private set; } = "";
    public TaskCompletionSource DeepSeekStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DeepSeekCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource OfficialAlertStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource OfficialAlertCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var url = request.RequestUri?.ToString() ?? "";
        if (url.Contains("weather.googleapis.com/v1/publicAlerts:lookup", StringComparison.Ordinal))
        {
            OfficialAlertCalls++;
            OfficialAlertRequestUri = request.RequestUri;
            if (BlockOfficialAlerts)
            {
                OfficialAlertStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { OfficialAlertCancelled.TrySetResult(); throw; }
            }
            var now = DateTimeOffset.UtcNow;
            var response = JsonSerializer.Serialize(new
            {
                weatherAlerts = new[]
                {
                    new
                    {
                        alertId = "synthetic-nchmf-alert-1",
                        alertTitle = new { text = "Cảnh báo mưa lớn tổng hợp", languageCode = "vi-VN" },
                        eventType = "RAIN", areaName = "Nha Trang, Khánh Hòa", description = "Thông tin cảnh báo tổng hợp.",
                        severity = "MODERATE", certainty = "LIKELY", urgency = "EXPECTED",
                        startTime = now.AddMinutes(-5), expirationTime = now.AddHours(2),
                        instruction = new[] { "Theo dõi bản tin chính thức." },
                        dataSource = new { name = "National Center for Hydrometeorological Forecasting", authorityUri = "https://www.nchmf.gov.vn/kttvSiteE/" }
                    }
                }
            });
            return JsonResponse(response);
        }
        if (url.Contains("geocoding-api.open-meteo.com", StringComparison.Ordinal))
            return JsonResponse("""{"results":[{"name":"Đà Nẵng","admin1":"Đà Nẵng","country":"Việt Nam","latitude":16.05,"longitude":108.2,"timezone":"Asia/Ho_Chi_Minh"}]}""");
        if (url.Contains("api.open-meteo.com", StringComparison.Ordinal))
        {
            ForecastCalls++;
            if (FailForecastNetwork) throw new HttpRequestException("synthetic network unavailable");
            if (FailForecast) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var start = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).AddHours(1);
            if (start.Hour >= 20) start = new DateTimeOffset(start.Date.AddDays(1).AddHours(8), TimeSpan.FromHours(7));
            var times = Enumerable.Range(0, 48).Select(i => start.AddHours(i).ToString("yyyy-MM-dd'T'HH:mm")).ToArray();
            var probability = Enumerable.Range(0, 48).Select(i => i is >= 1 and <= 3 ? 85 : 5).ToArray();
            var precipitation = Enumerable.Range(0, 48).Select(i => i is >= 1 and <= 3 ? 6.0 : 0.0).ToArray();
            return JsonResponse(JsonSerializer.Serialize(new { timezone = "Asia/Ho_Chi_Minh", timezone_abbreviation = "ICT", hourly = new { time = times, precipitation_probability = probability, precipitation } }));
        }
        if (url.Contains("api.deepseek.com", StringComparison.Ordinal))
        {
            DeepSeekCalls++;
            DeepSeekPayload = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (FailDeepSeek) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (BlockDeepSeek)
            {
                DeepSeekStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { DeepSeekCancelled.TrySetResult(); throw; }
            }
            var opaque = "c0";
            var modelItem = DeceptiveModel
                ? new { id = opaque, adviceCode = "carry_rain_gear", title = "Flash Flood Warning", reason = "Evacuate now", probability = 1000 }
                : new { id = opaque, adviceCode = UnsupportedAdvice ? "evacuate" : "carry_rain_gear", title = "", reason = "", probability = 0 };
            var content = JsonSerializer.Serialize(new { suggestions = new[] { modelItem } });
            return JsonResponse(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content } } } }));
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
