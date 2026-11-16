using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

public sealed class OpenMeteoProvider(IHttpClientFactory clients) : IProactiveProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string ProviderId => "public_weather";
    public ProactiveConsentPlan ConsentPlan { get; } = new(
        [new("public_weather", "proactive_analysis", "external_service")],
        [new("public_weather", "proactive_analysis", "local_only")],
        [new("public_weather", "proactive_analysis", "local_only")], []);

    public async Task<WeatherLocation> ResolveLocationAsync(string query, CancellationToken cancellationToken)
    {
        var url = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(query) + "&count=5&language=vi&format=json";
        using var response = await clients.CreateClient("weather").GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadLimitedAsync(response.Content, 1024 * 1024, cancellationToken));
        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            throw new InvalidDataException("Không tìm thấy thành phố phù hợp.");
        var choices = results.EnumerateArray().Take(5).Select(item =>
        {
            var name = GetString(item, "name");
            var region = GetString(item, "admin1");
            var country = GetString(item, "country");
            return new WeatherLocation(string.Join(", ", new[] { name, region, country }
                    .Where(value => !string.IsNullOrWhiteSpace(value))),
                GetDouble(item, "latitude"), GetDouble(item, "longitude"), GetString(item, "timezone") ?? "UTC");
        }).FirstOrDefault(item => double.IsFinite(item.Latitude) && item.Latitude is >= -90 and <= 90 &&
            double.IsFinite(item.Longitude) && item.Longitude is >= -180 and <= 180 &&
            !string.IsNullOrWhiteSpace(item.Name));
        return choices ?? throw new InvalidDataException("Kết quả địa điểm không hợp lệ.");
    }

    public async Task<ObservationBatch> CollectAsync(ProviderRequest request, CancellationToken cancellationToken)
    {
        var location = request.Settings.Deserialize<WeatherLocation>(Json);
        if (location is null || !double.IsFinite(location.Latitude) || location.Latitude is < -90 or > 90 ||
            !double.IsFinite(location.Longitude) || location.Longitude is < -180 or > 180 ||
            location.TimeZone is not { Length: > 0 and <= 100 })
            throw new InvalidDataException("Weather provider settings are invalid.");

        var forecast = await FetchForecastAsync(location, cancellationToken);
        var retrievedAt = forecast.GeneratedAt;
        var expiresAt = retrievedAt.AddHours(2);
        var facts = new WeatherObservationFacts(forecast,
            FormattableString.Invariant($"{location.Latitude:F3}|{location.Longitude:F3}"));
        var serializedFacts = JsonSerializer.SerializeToElement(facts, Json);
        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serializedFacts.GetRawText())))
            .ToLowerInvariant();
        var evidence = new ProactiveEvidence("forecast-" + contentHash[..24], "Open-Meteo",
            new Uri("https://open-meteo.com/en/docs"), retrievedAt, retrievedAt, expiresAt,
            contentHash, "CC-BY-4.0", new Uri("https://creativecommons.org/licenses/by/4.0/"),
            "Weather data by Open-Meteo.com", 1, serializedFacts);
        return new ObservationBatch(ProviderId, 1, retrievedAt, forecast.GeneratedAt, expiresAt, [evidence]);
    }

    private async Task<WeatherForecastData> FetchForecastAsync(WeatherLocation location, CancellationToken cancellationToken)
    {
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={location.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&longitude={location.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&hourly=precipitation_probability,precipitation&forecast_days=2&timezone=auto&timeformat=iso8601";
        using var response = await clients.CreateClient("weather").GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadLimitedAsync(response.Content, 1024 * 1024, cancellationToken));
        var root = document.RootElement;
        if (!root.TryGetProperty("hourly", out var hourly) ||
            !hourly.TryGetProperty("time", out var times) || times.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Dự báo thời tiết thiếu dữ liệu theo giờ.");
        var timeValues = times.EnumerateArray().Select(item => item.GetString()).ToArray();
        var timeZoneName = GetString(root, "timezone") ?? location.TimeZone;
        TimeZoneInfo timeZone;
        try { timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneName); }
        catch (TimeZoneNotFoundException) { timeZone = TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { timeZone = TimeZoneInfo.Utc; }
        catch (ArgumentException) { timeZone = TimeZoneInfo.Utc; }
        double?[] ReadArray(string name) => hourly.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.Number &&
                item.TryGetDouble(out var number) ? (double?)number : null).ToArray()
            : [];
        var probability = ReadArray("precipitation_probability");
        var precipitation = ReadArray("precipitation");
        if (timeValues.Length is 0 or > 72 || probability.Length != timeValues.Length || precipitation.Length != timeValues.Length)
            throw new InvalidDataException("Dự báo thời tiết sai cấu trúc.");
        var hours = new List<WeatherForecastHour>();
        for (var index = 0; index < timeValues.Length; index++)
        {
            if (!TryForecastTime(timeValues[index], timeZone, out var time)) continue;
            var p = probability[index];
            var millimeters = precipitation[index];
            if (p is < 0 or > 100 || millimeters is < 0 or > 1000)
                throw new InvalidDataException("Dự báo thời tiết chứa giá trị ngoài giới hạn.");
            hours.Add(new WeatherForecastHour(time, p ?? 0, millimeters ?? 0));
        }
        if (hours.Count == 0) throw new InvalidDataException("Dự báo thời tiết không có giờ hợp lệ.");
        return new WeatherForecastData(hours.ToArray(), timeZoneName,
            GetString(root, "timezone_abbreviation") ?? "", DateTimeOffset.UtcNow);
    }

    private static string? GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double GetDouble(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : double.NaN;

    private static async Task<string> ReadLimitedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > limit)
            throw new InvalidDataException("Phản hồi nhà cung cấp vượt giới hạn kích thước.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(block, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > limit) throw new InvalidDataException("Phản hồi nhà cung cấp vượt giới hạn kích thước.");
            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static bool TryForecastTime(string? value, TimeZoneInfo timeZone, out DateTimeOffset result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var timeStart = value.IndexOf('T') + 1;
        var explicitOffset = timeStart > 0 && (value.EndsWith('Z') || value[timeStart..].Contains('+') || value[timeStart..].Contains('-'));
        if (explicitOffset)
            return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out result);
        if (!DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var local)) return false;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        result = new DateTimeOffset(local, timeZone.GetUtcOffset(local));
        return true;
    }
}

public sealed record WeatherLocation(string Name, double Latitude, double Longitude, string TimeZone);
public sealed record WeatherForecastData(WeatherForecastHour[] Hours, string TimeZone,
    string TimeZoneAbbreviation, DateTimeOffset GeneratedAt);
public sealed record WeatherForecastHour(DateTimeOffset Time, double Probability, double Millimeters);
public sealed record WeatherObservationFacts(WeatherForecastData Forecast, string LocationIdentity);
