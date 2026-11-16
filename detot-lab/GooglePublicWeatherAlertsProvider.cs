using System.Text;
using System.Text.Json;

public sealed class GooglePublicWeatherAlertsProvider(IHttpClientFactory clients, string? apiKey)
{
    private const int MaximumResponseBytes = 512 * 1024;
    private static readonly Uri Endpoint = new("https://weather.googleapis.com/v1/publicAlerts:lookup");

    public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKey);

    public async Task<IReadOnlyList<OfficialWeatherAlert>> GetAsync(WeatherLocation selectedLocation,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new InvalidOperationException("Google Weather Alerts is not configured.");
        if (!double.IsFinite(selectedLocation.Latitude) || selectedLocation.Latitude is < -90 or > 90 ||
            !double.IsFinite(selectedLocation.Longitude) || selectedLocation.Longitude is < -180 or > 180)
            throw new InvalidDataException("Selected alert location is invalid.");

        // This is the center of the place the owner typed in, never a device GPS fix.
        var query = $"key={Uri.EscapeDataString(apiKey!)}&location.latitude={selectedLocation.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
            $"&location.longitude={selectedLocation.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}&languageCode=vi-VN";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint + "?" + query));
        using var response = await clients.CreateClient("weather-alerts").SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await ReadLimitedAsync(response.Content, cancellationToken));
        if (!document.RootElement.TryGetProperty("weatherAlerts", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];

        var now = DateTimeOffset.UtcNow;
        return items.EnumerateArray().Take(25).Select(Parse).Where(item => item is not null)
            .Select(item => item!)
            .Where(item => item.ExpiresAt is null || item.ExpiresAt > now)
            .DistinctBy(item => item.Id, StringComparer.Ordinal)
            .OrderByDescending(item => SeverityRank(item.Severity))
            .ThenBy(item => item.ExpiresAt ?? DateTimeOffset.MaxValue)
            .ToArray();
    }

    private static OfficialWeatherAlert? Parse(JsonElement item)
    {
        var id = ReadString(item, "alertId", 256);
        var title = ReadLocalizedText(item, "alertTitle", 300);
        var area = ReadString(item, "areaName", 500);
        var eventType = ReadString(item, "eventType", 80);
        if (id.Length == 0 || title.Length == 0 || area.Length == 0 || eventType.Length == 0) return null;

        var dataSource = item.TryGetProperty("dataSource", out var source) && source.ValueKind == JsonValueKind.Object
            ? source : default;
        var sourceName = ReadString(dataSource, "name", 160);
        var sourceUriText = ReadString(dataSource, "authorityUri", 500);
        if (!Uri.TryCreate(sourceUriText, UriKind.Absolute, out var parsedUri) ||
            parsedUri.Scheme != Uri.UriSchemeHttps ||
            !(parsedUri.Host.Equals("nchmf.gov.vn", StringComparison.OrdinalIgnoreCase) ||
              parsedUri.Host.EndsWith(".nchmf.gov.vn", StringComparison.OrdinalIgnoreCase) ||
              parsedUri.Host.Equals("kttv.gov.vn", StringComparison.OrdinalIgnoreCase) ||
              parsedUri.Host.EndsWith(".kttv.gov.vn", StringComparison.OrdinalIgnoreCase)))
            return null;
        var sourceUri = parsedUri;
        if (sourceName.Length == 0) return null;
        var instructions = ReadTextArray(item, "instruction", 1800);
        var recommendations = ReadRecommendations(item, 1800);
        var description = ReadString(item, "description", 1800);

        return new OfficialWeatherAlert(id, title, eventType, area, description,
            ReadString(item, "severity", 40), ReadString(item, "certainty", 40), ReadString(item, "urgency", 40),
            ReadTime(item, "startTime"), ReadTime(item, "expirationTime"), sourceName, sourceUri,
            instructions, recommendations);
    }

    private static int SeverityRank(string severity) => severity.ToUpperInvariant() switch
    {
        "EXTREME" => 4, "SEVERE" => 3, "MODERATE" => 2, "MINOR" => 1, _ => 0,
    };

    private static string ReadLocalizedText(JsonElement item, string name, int limit)
    {
        if (!item.TryGetProperty(name, out var value)) return "";
        if (value.ValueKind == JsonValueKind.String) return Clean(value.GetString(), limit);
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("text", out var text)
            ? Clean(text.ValueKind == JsonValueKind.String ? text.GetString() : null, limit) : "";
    }

    private static string ReadString(JsonElement item, string name, int limit) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Clean(value.GetString(), limit) : "";

    private static string ReadTextArray(JsonElement item, string name, int limit)
    {
        if (!item.TryGetProperty(name, out var value)) return "";
        if (value.ValueKind == JsonValueKind.String) return Clean(value.GetString(), limit);
        if (value.ValueKind != JsonValueKind.Array) return "";
        return Clean(string.Join("\n", value.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.String)
            .Select(entry => entry.GetString()).Where(entry => !string.IsNullOrWhiteSpace(entry))), limit);
    }

    private static string ReadRecommendations(JsonElement item, int limit)
    {
        if (!item.TryGetProperty("safetyRecommendations", out var value) || value.ValueKind != JsonValueKind.Array) return "";
        var lines = value.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object).Select(entry =>
        {
            var directive = ReadString(entry, "directive", 500);
            var subtext = ReadString(entry, "subtext", 700);
            return string.Join(" ", new[] { directive, subtext }.Where(text => text.Length > 0));
        });
        return Clean(string.Join("\n", lines), limit);
    }

    private static DateTimeOffset? ReadTime(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    private static string Clean(string? value, int limit) => new string((value ?? "")
        .Where(character => !char.IsControl(character) || character is '\n' or '\r' or '\t')
        .Take(limit).ToArray()).Trim();

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > MaximumResponseBytes)
            throw new InvalidDataException("Google alert response exceeded its size limit.");
        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(block, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("Google alert response exceeded its size limit.");
            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }
}

public sealed record OfficialWeatherAlert(string Id, string Title, string EventType, string AreaName,
    string Description, string Severity, string Certainty, string Urgency, DateTimeOffset? StartsAt,
    DateTimeOffset? ExpiresAt, string AuthorityName, Uri AuthorityUri, string Instructions, string SafetyRecommendations);
