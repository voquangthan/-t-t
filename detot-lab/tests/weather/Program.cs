using System.Net;
using System.Text;
using System.Text.Json;
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
    var snapshot = await service.SetLocationAsync(ownerA, "Đà Nẵng", CancellationToken.None);
    Check(snapshot.Location == "Đà Nẵng, Đà Nẵng, Việt Nam" && snapshot.Suggestions.Length == 1,
        "qualified rain should create one sourced suggestion");
    Check(snapshot.Suggestions[0].Title == "Có thể mưa lớn" && snapshot.Suggestions[0].Probability == 85 &&
        snapshot.Suggestions[0].Reason.Contains("18.0 mm", StringComparison.Ordinal) && snapshot.Suggestions[0].Source == "Open-Meteo" &&
        snapshot.Suggestions[0].Advice.Contains("mang", StringComparison.OrdinalIgnoreCase), "suggestion facts and provider advice should be grounded");
    Check(handler.DeepSeekCalls == 1 && !handler.DeepSeekPayload.Contains("Đà Nẵng", StringComparison.Ordinal) &&
        !handler.DeepSeekPayload.Contains("16.05", StringComparison.Ordinal) && !handler.DeepSeekPayload.Contains("108.2", StringComparison.Ordinal),
        "DeepSeek must receive forecast fields without city name or coordinates");
    var encryptedWeather = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "weather"), "weather-*.bin").Single());
    Check(!encryptedWeather.Contains("Đà Nẵng", StringComparison.Ordinal) && !encryptedWeather.Contains("16.05", StringComparison.Ordinal),
        "local weather location/cache must be encrypted at rest");

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

    await restarted.ClearLocationAsync(ownerA, CancellationToken.None);
    Check((await restarted.GetAsync(ownerA, CancellationToken.None)).Location is null &&
        Directory.GetFiles(Path.Combine(root, "weather"), "weather-*.bin").Length == 0,
        "explicit location deletion must cancel work and remove its encrypted local store");
    Console.WriteLine("PASS: weather default deny, owner isolation, encrypted restart, cache and dedupe, source-grounded suggestions, feedback consent, provider failure and mid-request revocation.");
}
finally { try { Directory.Delete(root, recursive: true); } catch { } }

static WeatherIntelligence NewService(string path, IDataProtectionProvider protection, ConsentPolicy consent, HttpMessageHandler handler) =>
    new(path, protection, new WeatherTestClientFactory(handler), consent, "synthetic-deepseek-key", NullLogger<WeatherIntelligence>.Instance);

static void GrantWeather(ConsentPolicy consent, string owner)
{
    consent.Set(owner, "public_weather", "proactive_analysis", "local_only", true, "owner", Guid.NewGuid());
    consent.Set(owner, "public_weather", "proactive_analysis", "external_service", true, "owner", Guid.NewGuid());
    consent.Set(owner, "public_weather", "proactive_analysis", "DeepSeek", true, "owner", Guid.NewGuid());
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
    public bool FailForecast { get; init; }
    public bool BlockDeepSeek { get; init; }
    public string DeepSeekPayload { get; private set; } = "";
    public TaskCompletionSource DeepSeekStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource DeepSeekCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests++;
        var url = request.RequestUri?.ToString() ?? "";
        if (url.Contains("geocoding-api.open-meteo.com", StringComparison.Ordinal))
            return JsonResponse("""{"results":[{"name":"Đà Nẵng","admin1":"Đà Nẵng","country":"Việt Nam","latitude":16.05,"longitude":108.2,"timezone":"Asia/Ho_Chi_Minh"}]}""");
        if (url.Contains("api.open-meteo.com", StringComparison.Ordinal))
        {
            ForecastCalls++;
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
            if (BlockDeepSeek)
            {
                DeepSeekStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { DeepSeekCancelled.TrySetResult(); throw; }
            }
            var opaque = "c0";
            var content = JsonSerializer.Serialize(new { suggestions = new[] { new { id = opaque, adviceCode = "carry_rain_gear" } } });
            return JsonResponse(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content } } } }));
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
