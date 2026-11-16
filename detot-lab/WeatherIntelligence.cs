using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

public sealed class WeatherIntelligence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConsentPolicy.ConsentRequirement[] RunConsent =
    [
        new("public_weather", "proactive_analysis", "local_only"),
        new("public_weather", "proactive_analysis", "external_service"),
        new("public_weather", "proactive_analysis", "DeepSeek"),
    ];
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private readonly string directory;
    private readonly IDataProtector protector;
    private readonly ConsentPolicy consent;
    private readonly OpenMeteoProvider provider;
    private readonly DeepSeekProactiveAnalyzer analyzer;
    private readonly IProactiveEngine engine;
    private readonly ILogger<WeatherIntelligence> logger;
    private readonly ConcurrentDictionary<string, OwnerStore> stores = new(StringComparer.Ordinal);

    public WeatherIntelligence(string directory, IDataProtectionProvider protection, IHttpClientFactory clients,
        ConsentPolicy consent, string? deepSeekKey, ILogger<WeatherIntelligence> logger)
    {
        this.directory = directory;
        protector = protection.CreateProtector("Detot.WeatherIntelligence.v1");
        this.consent = consent;
        provider = new OpenMeteoProvider(clients);
        analyzer = new DeepSeekProactiveAnalyzer(clients, deepSeekKey);
        engine = new ProactiveEngine(consent, logger);
        this.logger = logger;
    }

    public async Task<WeatherSnapshot> GetAsync(string owner, CancellationToken cancellationToken)
    {
        var lease = Begin(owner, "weather_read");
        var store = Store(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.CancellationToken, store.Epoch.Token);
        await store.Gate.WaitAsync(linked.Token);
        try
        {
            if (!consent.ExecuteIfCurrent(lease, () => { EnsureLoaded(owner, store); return true; }))
                throw new OperationCanceledException(lease.CancellationToken);
            return CreateSnapshotIfCurrent(owner, store, lease);
        }
        finally { store.Gate.Release(); }
    }

    public async Task<WeatherSnapshot> SetLocationAsync(string owner, string query, CancellationToken cancellationToken)
    {
        if (query is not { Length: > 1 and <= 100 } || string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Nhập tên thành phố hoặc khu vực (tối đa 100 ký tự).");
        var lease = Begin(owner, "weather_location_set");
        var store = Store(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.CancellationToken, store.Epoch.Token);
        var location = await provider.ResolveLocationAsync(query.Trim(), linked.Token);
        await store.Gate.WaitAsync(linked.Token);
        try
        {
            if (!consent.ExecuteIfCurrent(lease, () => { EnsureLoaded(owner, store); return true; }))
                throw new OperationCanceledException(lease.CancellationToken);
            var previous = store.State;
            store.State = previous with { Location = location, Cache = null, Suggestions = [], LastUpdated = DateTimeOffset.UtcNow };
            store.LastError = null;
            if (!consent.ExecuteIfCurrent(lease, () => { Save(owner, store); return true; }))
            { store.State = previous; throw new OperationCanceledException(lease.CancellationToken); }
        }
        finally { store.Gate.Release(); }
        return await RefreshAsync(owner, true, cancellationToken);
    }

    public async Task<WeatherSnapshot> RefreshAsync(string owner, bool force, CancellationToken cancellationToken)
    {
        var lease = Begin(owner, "weather_refresh");
        var store = Store(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.CancellationToken, store.Epoch.Token);
        await store.Gate.WaitAsync(linked.Token);
        try
        {
            if (!consent.ExecuteIfCurrent(lease, () => { EnsureLoaded(owner, store); return true; }))
                throw new OperationCanceledException(lease.CancellationToken);
            if (store.State.Location is null)
            {
                store.LastError = null;
                return CreateSnapshotIfCurrent(owner, store, lease);
            }
            if (!force && store.State.Cache is { } cached && DateTimeOffset.UtcNow - cached.FetchedAt < CacheDuration)
                return CreateSnapshotIfCurrent(owner, store, lease);

            var feedback = TryLoadFeedback(owner, store);
            var suppressBelowNinety = feedback?.Counts.GetValueOrDefault("heavy_rain") is { Dismissed: >= 3 };
            var policy = new WeatherSignalPolicy(suppressBelowNinety);
            var request = new ProviderRequest(owner, Guid.NewGuid(), DateTimeOffset.UtcNow,
                JsonSerializer.SerializeToElement(store.State.Location, Json));
            var evaluation = await engine.RefreshAsync(owner, provider, policy, analyzer, request, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            var forecastEvidence = evaluation.Observations.Evidence.Single();
            var observationFacts = forecastEvidence.Facts.Deserialize<WeatherObservationFacts>(Json)
                ?? throw new InvalidDataException("Weather observation facts failed validation.");
            var candidatesBySuggestionId = evaluation.Candidates.ToDictionary(
                item => Guid.ParseExact(item.CandidateId.PadRight(32, '0'), "N"));
            var suggestions = evaluation.Suggestions.Select(suggestion =>
                policy.ToWeatherSuggestion(candidatesBySuggestionId[suggestion.Id], suggestion, evaluation.Observations)).ToArray();
            var updated = store.State with
            {
                Cache = new ForecastCache(now, observationFacts.Forecast),
                Suggestions = suggestions.Where(item => item.EndsAt > now).DistinctBy(item => item.Id)
                    .OrderBy(item => item.StartsAt).Take(2).ToArray(),
                LastUpdated = now,
            };
            var previous = store.State;
            store.State = updated;
            store.LastError = analyzer.IsConfigured ? null : "Chưa cấu hình DeepSeek cho Weather Intelligence.";
            if (!engine.CommitIfCurrent(evaluation, () => { Save(owner, store); return true; }))
            { store.State = previous; throw new OperationCanceledException(lease.CancellationToken); }
            return CreateSnapshotIfCurrent(owner, store, lease);
        }
        catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            if (!consent.IsCurrent(lease)) throw new OperationCanceledException(lease.CancellationToken);
            store.LastError = SafeError(error);
            logger.LogWarning("Weather refresh failed safely ({ErrorType}).", error.GetType().Name);
            return CreateSnapshotIfCurrent(owner, store, lease);
        }
        finally { store.Gate.Release(); }
    }

    public void StartWorker(string owner, CancellationToken stoppingToken) =>
        engine.StartWorker(owner, provider.ProviderId, RunConsent,
            token => RefreshAsync(owner, false, token), TimeSpan.FromMinutes(10), stoppingToken);

    public async Task<bool> RecordFeedbackAsync(string owner, string id, string decision, CancellationToken cancellationToken)
    {
        if (decision is not ("interested" or "dismissed") || id is not { Length: 16 } || id.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Phản hồi không hợp lệ.");
        var lease = consent.TryBegin(owner,
            [new ConsentPolicy.ConsentRequirement("public_weather", "proactive_analysis", "local_only"),
                new ConsentPolicy.ConsentRequirement("proactive_feedback", "skill_learning", "local_only")], "weather_feedback");
        if (lease is null) throw new UnauthorizedAccessException("Chưa cấp quyền học từ phản hồi gợi ý.");
        var store = Store(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.CancellationToken, store.Epoch.Token);
        await store.Gate.WaitAsync(linked.Token);
        try
        {
            if (!consent.ExecuteIfCurrent(lease, () => { EnsureLoaded(owner, store); EnsureFeedbackLoaded(owner, store); return true; }))
                throw new OperationCanceledException(lease.CancellationToken);
            var suggestion = store.State.Suggestions.FirstOrDefault(item => item.Id == id);
            if (suggestion is null) return false;
            var feedback = store.Feedback.Counts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var counts = feedback.GetValueOrDefault(suggestion.Kind) ?? new FeedbackCounts();
            feedback[suggestion.Kind] = decision == "interested"
                ? counts with { Interested = counts.Interested + 1 }
                : counts with { Dismissed = counts.Dismissed + 1 };
            var dismissed = decision == "dismissed"
                ? store.Feedback.DismissedIds.Append(id).Distinct(StringComparer.Ordinal).TakeLast(100).ToArray()
                : store.Feedback.DismissedIds;
            var previous = store.Feedback;
            store.Feedback = new FeedbackState(feedback, dismissed, FeedbackConsentMarker(owner));
            if (!consent.ExecuteIfCurrent(lease, () => { SaveFeedback(owner, store); return true; }))
            { store.Feedback = previous; throw new OperationCanceledException(lease.CancellationToken); }
            return true;
        }
        finally { store.Gate.Release(); }
    }

    public async Task RevokeWeatherConsentAsync(string owner, CancellationToken cancellationToken)
    {
        var store = Store(owner);
        store.Epoch.Cancel();
        await store.Gate.WaitAsync(cancellationToken);
        try
        {
            store.State = EmptyState();
            store.Feedback = EmptyFeedback();
            store.FeedbackLoaded = false;
            store.FeedbackError = null;
            store.Loaded = true;
            store.LoadError = null;
            store.LastError = null;
            Delete(owner);
            store.Epoch.Dispose();
            store.Epoch = new CancellationTokenSource();
        }
        finally { store.Gate.Release(); }
    }

    public async Task ClearLocationAsync(string owner, CancellationToken cancellationToken)
    {
        var store = Store(owner);
        store.Epoch.Cancel();
        await store.Gate.WaitAsync(cancellationToken);
        try
        {
            store.State = EmptyState();
            store.Feedback = EmptyFeedback();
            store.FeedbackLoaded = false;
            store.FeedbackError = null;
            store.Loaded = true;
            store.LoadError = null;
            store.LastError = null;
            Delete(owner);
            store.Epoch.Dispose();
            store.Epoch = new CancellationTokenSource();
        }
        finally { store.Gate.Release(); }
    }

    public async Task RevokeFeedbackConsentAsync(string owner, CancellationToken cancellationToken)
    {
        var store = Store(owner);
        await store.Gate.WaitAsync(cancellationToken);
        try
        {
            store.Feedback = EmptyFeedback();
            store.FeedbackLoaded = true;
            store.FeedbackError = null;
            DeleteFeedback(owner);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        finally { store.Gate.Release(); }
    }

    private ConsentPolicy.ConsentLease Begin(string owner, string action) =>
        consent.TryBegin(owner, RunConsent, action) ?? throw new UnauthorizedAccessException("Weather Intelligence cần quyền cục bộ, gửi địa điểm tới Open-Meteo và phân tích bằng DeepSeek.");

    private OwnerStore Store(string owner) => stores.GetOrAdd(owner, static _ => new OwnerStore());

    private void EnsureLoaded(string owner, OwnerStore store)
    {
        if (store.Loaded) return;
        store.Loaded = true;
        try
        {
            var path = FilePath(owner);
            if (!File.Exists(path)) { store.State = EmptyState(); return; }
            var encrypted = File.ReadAllText(path, Encoding.UTF8);
            var plaintext = protector.Unprotect(encrypted);
            var loaded = JsonSerializer.Deserialize<WeatherState>(plaintext, Json);
            if (!ValidState(loaded)) throw new InvalidDataException("Weather state failed validation.");
            if (loaded!.ConsentMarker != WeatherConsentMarker(owner))
            {
                store.State = EmptyState();
                Delete(owner);
                return;
            }
            store.State = loaded;
        }
        catch (Exception error) when (error is IOException or CryptographicException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            store.State = EmptyState();
            store.LoadError = "Không đọc được dữ liệu Weather Intelligence cục bộ.";
            logger.LogWarning("Weather state load failed safely ({ErrorType}).", error.GetType().Name);
        }
    }

    // Call only after the owner has both weather-local and feedback-learning consent.
    private void EnsureFeedbackLoaded(string owner, OwnerStore store)
    {
        if (store.FeedbackLoaded) return;
        store.FeedbackLoaded = true;
        try
        {
            var path = FeedbackFilePath(owner);
            if (!File.Exists(path)) { store.Feedback = EmptyFeedback(); return; }
            var encrypted = File.ReadAllText(path, Encoding.UTF8);
            var plaintext = protector.Unprotect(encrypted);
            var loaded = JsonSerializer.Deserialize<FeedbackState>(plaintext, Json);
            if (!ValidFeedback(loaded)) throw new InvalidDataException("Weather feedback state failed validation.");
            if (loaded!.ConsentMarker != FeedbackConsentMarker(owner))
            {
                store.Feedback = EmptyFeedback();
                DeleteFeedback(owner);
                return;
            }
            store.Feedback = loaded;
        }
        catch (Exception error) when (error is IOException or CryptographicException or JsonException or UnauthorizedAccessException or InvalidDataException)
        {
            store.Feedback = EmptyFeedback();
            store.FeedbackError = "Không đọc được phản hồi thời tiết cục bộ.";
            logger.LogWarning("Weather feedback load failed safely ({ErrorType}).", error.GetType().Name);
        }
    }

    private void Save(string owner, OwnerStore store)
    {
        Directory.CreateDirectory(directory);
        store.State = store.State with { ConsentMarker = WeatherConsentMarker(owner) };
        var path = FilePath(owner);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, protector.Protect(JsonSerializer.Serialize(store.State, Json)), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private void SaveFeedback(string owner, OwnerStore store)
    {
        Directory.CreateDirectory(directory);
        store.Feedback = store.Feedback with { ConsentMarker = FeedbackConsentMarker(owner) };
        var path = FeedbackFilePath(owner);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, protector.Protect(JsonSerializer.Serialize(store.Feedback, Json)), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private void Delete(string owner)
    {
        try { File.Delete(FilePath(owner)); } catch (IOException) { }
        try { File.Delete(FilePath(owner) + ".tmp"); } catch (IOException) { }
        DeleteFeedback(owner);
    }

    private void DeleteFeedback(string owner)
    {
        try { File.Delete(FeedbackFilePath(owner)); } catch (IOException) { }
        try { File.Delete(FeedbackFilePath(owner) + ".tmp"); } catch (IOException) { }
    }

    private string FilePath(string owner)
    {
        var ownerHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner))).ToLowerInvariant();
        return Path.Combine(directory, $"weather-{ownerHash}.bin");
    }

    private string FeedbackFilePath(string owner)
    {
        var ownerHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner))).ToLowerInvariant();
        return Path.Combine(directory, $"feedback-{ownerHash}.bin");
    }

    private string WeatherConsentMarker(string owner) => string.Join("|", RunConsent.Select(requirement =>
        consent.GetGrantChangedAt(owner, requirement.Source, requirement.Purpose, requirement.Destination)?.UtcDateTime.Ticks
            .ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"));

    private string FeedbackConsentMarker(string owner) => WeatherConsentMarker(owner) + "|" +
        (consent.GetGrantChangedAt(owner, "proactive_feedback", "skill_learning", "local_only")?.UtcDateTime.Ticks
            .ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-");

    private WeatherSnapshot CreateSnapshot(string owner, OwnerStore store)
    {
        var feedback = TryLoadFeedback(owner, store);
        var feedbackAllowed = feedback is not null;
        return Snapshot(store.State, feedback, store.LoadError,
            feedbackAllowed ? store.FeedbackError : null, store.LastError, feedbackAllowed);
    }

    private WeatherSnapshot CreateSnapshotIfCurrent(string owner, OwnerStore store, ConsentPolicy.ConsentLease lease)
    {
        WeatherSnapshot? snapshot = null;
        if (!consent.ExecuteIfCurrent(lease, () => { snapshot = CreateSnapshot(owner, store); return true; }))
            throw new OperationCanceledException(lease.CancellationToken);
        return snapshot!;
    }

    private FeedbackState? TryLoadFeedback(string owner, OwnerStore store)
    {
        if (!consent.IsAllowed(owner, "public_weather", "proactive_analysis", "local_only") ||
            !consent.IsAllowed(owner, "proactive_feedback", "skill_learning", "local_only")) return null;
        var lease = consent.TryBegin(owner,
            [new ConsentPolicy.ConsentRequirement("public_weather", "proactive_analysis", "local_only"),
                new ConsentPolicy.ConsentRequirement("proactive_feedback", "skill_learning", "local_only")], "weather_feedback_read");
        if (lease is null) return null;
        FeedbackState? feedback = null;
        var executed = consent.ExecuteIfCurrent(lease, () => { EnsureFeedbackLoaded(owner, store); feedback = store.Feedback; return true; });
        return executed ? feedback : null;
    }

    private static WeatherSnapshot Snapshot(WeatherState state, FeedbackState? feedback, string? loadError,
        string? feedbackError, string? lastError, bool feedbackAllowed)
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = state.Cache is { } cache && now - cache.FetchedAt <= TimeSpan.FromHours(2);
        var current = state.Cache is { } currentCache && now - currentCache.FetchedAt <= CacheDuration;
        var suggestions = fresh ? state.Suggestions.Where(item => item.EndsAt > now &&
            (feedback is null || !feedback.DismissedIds.Contains(item.Id, StringComparer.Ordinal)))
            .OrderBy(item => item.StartsAt).Take(2).ToArray() : [];
        return new WeatherSnapshot(state.Location?.Name, state.Cache?.FetchedAt, suggestions, current,
            loadError ?? feedbackError ?? lastError, feedbackAllowed, feedback?.DismissedIds.Length ?? 0);
    }

    private static WeatherState EmptyState() => new(null, null, [], null, DateTimeOffset.UtcNow);
    private static FeedbackState EmptyFeedback(string? consentMarker = null) => new(new Dictionary<string, FeedbackCounts>(), [], consentMarker);
    private static bool ValidState(WeatherState? state)
    {
        if (state is null || state.Suggestions is null || state.Suggestions.Length > 2) return false;
        if (state.ConsentMarker is not { Length: > 0 and <= 300 }) return false;
        if (state.Location is { } location && (location.Name is not { Length: > 0 and <= 200 } ||
            !double.IsFinite(location.Latitude) || location.Latitude is < -90 or > 90 ||
            !double.IsFinite(location.Longitude) || location.Longitude is < -180 or > 180 ||
            location.TimeZone is not { Length: > 0 and <= 100 })) return false;
        if (state.Cache is { } cache && (cache.Forecast is null || cache.Forecast.Hours is null || cache.Forecast.Hours.Length is 0 or > 72 ||
            cache.Forecast.Hours.Any(hour => hour is null || hour.Probability is < 0 or > 100 || hour.Millimeters is < 0 or > 1000))) return false;
        if (state.Suggestions.Any(item => item is null || item.Id is not { Length: 16 } || item.Id.Any(character => !Uri.IsHexDigit(character)) ||
            item.Title is not { Length: > 0 and <= 80 } || item.Reason is not { Length: > 0 and <= 220 } ||
            item.Advice is not ("Mang áo mưa hoặc ô nếu phải ra ngoài trong khung giờ này." or
                "Nếu lịch linh hoạt, cân nhắc chuyển hoạt động ngoài trời vào trong nhà.") ||
            item.Probability is < 0 or > 100 || item.Source != "Open-Meteo" || item.SourceUrl != "https://open-meteo.com/en/docs")) return false;
        return true;
    }

    private static bool ValidFeedback(FeedbackState? feedback)
    {
        if (feedback is null || feedback.Counts is null || feedback.Counts.Count > 20 || feedback.DismissedIds is null ||
            feedback.DismissedIds.Length > 100 || feedback.ConsentMarker is not { Length: > 0 and <= 400 }) return false;
        if (feedback.Counts.Any(pair => pair.Key is not { Length: > 0 and <= 40 } || pair.Value is null ||
            pair.Value.Interested is < 0 or > 1_000_000 || pair.Value.Dismissed is < 0 or > 1_000_000)) return false;
        return feedback.DismissedIds.All(id => id is { Length: 16 } && id.All(Uri.IsHexDigit));
    }
    private static string SafeError(Exception error) => error is HttpRequestException ? "Không kết nối được tới nhà cung cấp thời tiết hoặc DeepSeek." :
        error is TaskCanceledException ? "Nhà cung cấp phản hồi quá thời hạn." : "Dữ liệu thời tiết không hợp lệ; chưa tạo gợi ý.";

    private sealed class OwnerStore
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource Epoch { get; set; } = new();
        public WeatherState State { get; set; } = EmptyState();
        public FeedbackState Feedback { get; set; } = EmptyFeedback();
        public bool Loaded { get; set; }
        public bool FeedbackLoaded { get; set; }
        public string? LoadError { get; set; }
        public string? FeedbackError { get; set; }
        public string? LastError { get; set; }
    }

    private sealed record WeatherState(WeatherLocation? Location, ForecastCache? Cache, WeatherSuggestion[] Suggestions,
        string? ConsentMarker, DateTimeOffset LastUpdated);
    private sealed record FeedbackState(Dictionary<string, FeedbackCounts> Counts, string[] DismissedIds, string? ConsentMarker);
    private sealed record ForecastCache(DateTimeOffset FetchedAt, WeatherForecastData Forecast);
    private sealed record FeedbackCounts(int Interested = 0, int Dismissed = 0);
    public sealed record WeatherSuggestion(string Id, string Kind, string Title, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
        double Probability, string Certainty, string Reason, string Advice, string Source, string SourceUrl, DateTimeOffset ForecastIssuedAt);
    public sealed record WeatherSnapshot(string? Location, DateTimeOffset? FetchedAt, WeatherSuggestion[] Suggestions,
        bool ForecastCurrent, string? Error, bool FeedbackAllowed, int DismissedCount);
}
