using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class WeatherSignalPolicy(bool suppressBelowNinetyProbability = false) : IProactiveDomainPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlySet<string> DecisionCodes =
        new HashSet<string>(["carry_rain_gear", "plan_indoor"], StringComparer.Ordinal);

    public string DomainId => "weather";
    public int MaximumSuggestions => 2;

    public IReadOnlyList<EvidenceCandidate> CreateCandidates(ObservationBatch observations)
    {
        var evidence = observations.Evidence.SingleOrDefault() ??
            throw new InvalidDataException("Weather provider must return exactly one forecast evidence item.");
        var facts = evidence.Facts.Deserialize<WeatherObservationFacts>(Json) ??
            throw new InvalidDataException("Weather forecast evidence failed validation.");
        var forecast = facts.Forecast;
        if (forecast.Hours is null || forecast.Hours.Length is 0 or > 72 ||
            forecast.Hours.Any(hour => hour is null || hour.Probability is < 0 or > 100 ||
                hour.Millimeters is < 0 or > 1000) || facts.LocationIdentity is not { Length: > 0 and <= 100 })
            throw new InvalidDataException("Weather forecast facts failed validation.");

        var now = DateTimeOffset.UtcNow;
        return forecast.Hours.Where(hour => hour.Time > now && hour.Time <= now.AddHours(48))
            .GroupBy(hour => hour.Time.Date)
            .Select(group =>
            {
                var hours = group.OrderBy(item => item.Time).ToArray();
                var triggered = Enumerable.Range(0, Math.Max(0, hours.Length - 2)).Select(index =>
                {
                    var window = hours.Skip(index).Take(3).ToArray();
                    return new { Window = window, Total = window.Sum(hour => hour.Millimeters) };
                }).FirstOrDefault(item => item.Window.All(hour => hour.Probability >= 70) &&
                    (item.Window.Max(hour => hour.Millimeters) >= 5 || item.Total >= 15));
                if (triggered is null) return null;
                var peak = triggered.Window.Max(hour => hour.Probability);
                var identity = FormattableString.Invariant($"{facts.LocationIdentity}|rain|{group.Key:yyyy-MM-dd}");
                var candidateId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..16];
                var startsAt = triggered.Window[0].Time;
                var endsAt = triggered.Window[^1].Time.AddHours(1);
                if (suppressBelowNinetyProbability && peak < 90) return null;
                var safeFacts = JsonSerializer.SerializeToElement(new WeatherAnalysisFacts(startsAt, endsAt,
                    peak, Math.Round(triggered.Total, 1)), Json);
                return new EvidenceCandidate(candidateId, observations.ProviderId, DomainId, "heavy_rain",
                    [evidence.EvidenceId], ["three_hour_heavy_rain"], peak,
                    "peak hourly precipitation probability percent", Min(observations.ExpiresAt, endsAt), safeFacts);
            }).Where(item => item is not null).Select(item => item!).OrderBy(item => item.ExpiresAt)
            .Take(MaximumSuggestions).ToArray();
    }

    public IReadOnlySet<string> AllowedDecisionCodes(EvidenceCandidate candidate) => DecisionCodes;

    public ProactiveSuggestion CreateSuggestion(ObservationBatch observations, EvidenceCandidate candidate,
        string decisionCode, DateTimeOffset createdAt)
    {
        var facts = candidate.SafeAnalysisFacts.Deserialize<WeatherAnalysisFacts>(Json) ??
            throw new InvalidDataException("Weather analysis facts are invalid.");
        var explanation = $"Dự báo 3 giờ có khoảng {facts.Millimeters3Hours.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} mm mưa; xác suất theo giờ đạt đỉnh {facts.Probability:0}%.";
        var stableId = Guid.ParseExact(candidate.CandidateId.PadRight(32, '0'), "N");
        return new ProactiveSuggestion(stableId, candidate.ProviderId, candidate.DomainId, candidate.SignalType,
            "Có thể mưa lớn", explanation, decisionCode, candidate.EvidenceIds,
            candidate.EventProbability, candidate.EvidenceConfidence, candidate.Relevance,
            createdAt, candidate.ExpiresAt, "proposed", 1);
    }

    public WeatherIntelligence.WeatherSuggestion ToWeatherSuggestion(EvidenceCandidate candidate,
        ProactiveSuggestion suggestion, ObservationBatch observations)
    {
        var facts = candidate.SafeAnalysisFacts.Deserialize<WeatherAnalysisFacts>(Json) ??
            throw new InvalidDataException("Weather analysis facts are invalid.");
        var advice = suggestion.SuggestedActionCode switch
        {
            "carry_rain_gear" => "Mang áo mưa hoặc ô nếu phải ra ngoài trong khung giờ này.",
            "plan_indoor" => "Nếu lịch linh hoạt, cân nhắc chuyển hoạt động ngoài trời vào trong nhà.",
            _ => throw new InvalidDataException("Weather analyzer decision is not supported.")
        };
        var probability = candidate.EventProbability ??
            throw new InvalidDataException("Weather signal is missing its source probability.");
        var certainty = probability >= 85 ? "cao" : probability >= 70 ? "khá cao" : "cần lưu ý";
        var evidence = observations.Evidence.Single(item => item.EvidenceId == candidate.EvidenceIds.Single());
        return new WeatherIntelligence.WeatherSuggestion(candidate.CandidateId, candidate.SignalType,
            suggestion.Title, facts.StartsAt, facts.EndsAt, probability, certainty, suggestion.Explanation,
            advice, evidence.SourceName, "https://open-meteo.com/en/docs", suggestion.CreatedAt);
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}

public sealed record WeatherAnalysisFacts(DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    double Probability, double Millimeters3Hours);
