using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record SearchDiscoveryRequest(string? Topic, string[]? Domains, string? DocumentType);
public sealed record SearchDiscoveryCandidate(string Title, string Url, string Domain, string Description, string DocumentType,
    string LicenseStatus, bool OfficialSource, string? Version = null, DateTimeOffset? LastUpdatedUtc = null);
public sealed record SearchDiscoveryResponse(string Provider, IReadOnlyList<SearchDiscoveryCandidate> Candidates);
public sealed record ProviderSearchRequest(string Query, int MaxResults, string Language);
public sealed record ProviderSearchResult(string Title, string Url, string Description, DateTimeOffset? LastUpdatedUtc = null);

public interface ISearchDiscoveryProvider
{
    string Name { get; }
    Task<IReadOnlyList<ProviderSearchResult>> SearchAsync(ProviderSearchRequest request, CancellationToken cancellationToken);
}

public sealed class SearchDiscoveryService(ISearchDiscoveryProvider? provider)
{
    private static readonly HashSet<string> DocumentTypes = new(StringComparer.OrdinalIgnoreCase)
        { "all", "web", "markdown", "github" };

    public bool IsConfigured => provider is not null;
    public string ProviderName => provider?.Name ?? "Chưa cấu hình";

    public async Task<SearchDiscoveryResponse> SearchAsync(SearchDiscoveryRequest request, CancellationToken cancellationToken)
    {
        if (provider is null) throw new InvalidOperationException("Search Provider chưa được cấu hình. Hãy đặt DETOT_SEARCH_PROVIDER=microsoft-web và DETOT_SEARCH_API_KEY trên Backend.");
        var topic = request.Topic?.Trim() ?? "";
        if (topic.Length is < 2 or > 200) throw new ArgumentException("Chủ đề tìm kiếm phải dài từ 2 đến 200 ký tự.");
        var type = string.IsNullOrWhiteSpace(request.DocumentType) ? "all" : request.DocumentType.Trim().ToLowerInvariant();
        if (!DocumentTypes.Contains(type)) throw new ArgumentException("Loại tài liệu tìm kiếm không hợp lệ.");

        var domains = (request.Domains ?? []).Select(NormalizeDomain).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToArray();
        if (request.Domains is { Length: > 5 }) throw new ArgumentException("Có thể lọc tối đa 5 domain.");
        var query = topic;
        if (domains.Length > 0)
            query += " (" + string.Join(" OR ", domains.Select(domain => "site:" + domain)) + ")";
        if (type == "markdown") query += " (filetype:md OR filetype:markdown)";
        if (type == "github") query += " site:github.com";

        var results = await provider.SearchAsync(new ProviderSearchRequest(query, 12, "vi"), cancellationToken);
        var candidates = results.Select(result => ToCandidate(result))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .Where(candidate => domains.Length == 0 || domains.Any(domain => IsDomainMatch(candidate.Domain, domain)))
            .Where(candidate => !LooksLikeSpam(candidate))
            .Where(candidate => type switch
            {
                "markdown" => candidate.Url.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || candidate.Url.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase),
                "github" => candidate.Domain.Equals("github.com", StringComparison.OrdinalIgnoreCase),
                "web" => candidate.DocumentType == "Web",
                _ => true,
            })
            .DistinctBy(candidate => CanonicalUrl(candidate.Url), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(candidate => candidate.OfficialSource)
            .ThenBy(candidate => DomainPriority(candidate.Domain))
            .ThenByDescending(candidate => candidate.LastUpdatedUtc ?? DateTimeOffset.MinValue)
            .ThenBy(candidate => candidate.Title, StringComparer.OrdinalIgnoreCase)
            .Take(10).ToArray();
        return new SearchDiscoveryResponse(provider.Name, candidates);
    }

    private static SearchDiscoveryCandidate? ToCandidate(ProviderSearchResult result)
    {
        if (!Uri.TryCreate(result.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(uri.Host, out _))
            return null;
        var cleanUrl = new UriBuilder(uri) { Fragment = "", Query = "" }.Uri.AbsoluteUri.TrimEnd('/');
        var path = uri.AbsolutePath;
        var isGithub = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2;
        var type = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase)
            ? "Markdown" : isGithub ? "GitHub README" : "Web";
        var description = Regex.Replace(WebUtility.HtmlDecode(result.Description ?? ""), @"\s+", " ").Trim();
        if (description.Length > 700) description = description[..700];
        var title = Regex.Replace(WebUtility.HtmlDecode(result.Title ?? ""), @"\s+", " ").Trim();
        if (title.Length is 0 or > 240) title = uri.Host + path;
        var version = Regex.Match(title + " " + description, @"\b(?:v|version\s*)\d+(?:\.\d+){1,3}\b", RegexOptions.IgnoreCase).Value;
        return new SearchDiscoveryCandidate(title, cleanUrl, uri.Host, description, type, "Chưa kiểm tra", IsOfficialDomain(uri.Host),
            string.IsNullOrWhiteSpace(version) ? null : version, result.LastUpdatedUtc);
    }

    private static bool LooksLikeSpam(SearchDiscoveryCandidate candidate) => Regex.IsMatch(candidate.Title + " " + candidate.Description,
        @"(?i)\b(coupon|promo code|casino|jackpot|free money|cracked software|driver download|buy now)\b") ||
        candidate.Description.Length < 35 && candidate.Title.Length < 10;

    private static bool IsOfficialDomain(string host) => IsDomainMatch(host, "learn.microsoft.com") || IsDomainMatch(host, "docs.microsoft.com") ||
        IsDomainMatch(host, "docs.python.org") || IsDomainMatch(host, "developer.mozilla.org") || IsDomainMatch(host, "developers.google.com") ||
        IsDomainMatch(host, "learn.microsoft.com") || host.EndsWith(".gov", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".gov.vn", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".edu", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".edu.vn", StringComparison.OrdinalIgnoreCase);

    private static int DomainPriority(string host) => IsOfficialDomain(host) ? 0 : host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    private static string CanonicalUrl(string value) => new UriBuilder(value) { Query = "", Fragment = "" }.Uri.AbsoluteUri.TrimEnd('/');

    private static string NormalizeDomain(string value)
    {
        var domain = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (domain.StartsWith("https://", StringComparison.Ordinal) || domain.StartsWith("http://", StringComparison.Ordinal) ||
            domain.Contains('/') || domain.Contains(':') || !Regex.IsMatch(domain, @"^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$"))
            throw new ArgumentException($"Domain không hợp lệ: {value}");
        return domain;
    }

    private static bool IsDomainMatch(string host, string domain) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}

public sealed class MicrosoftWebSearchProvider(HttpClient client, string apiKey) : ISearchDiscoveryProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Name => "Microsoft Web Search";

    public async Task<IReadOnlyList<ProviderSearchResult>> SearchAsync(ProviderSearchRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.microsoft.ai/v3/search/web");
        message.Headers.TryAddWithoutValidation("x-apikey", apiKey);
        message.Content = JsonContent.Create(new
        {
            query = request.Query,
            maxResults = request.MaxResults,
            language = request.Language,
            region = "US",
            maxLength = 700,
            contentFormat = "passage",
            safeSearch = "strict",
        }, options: Json);
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Search Provider từ chối API key hoặc quyền truy cập.");
            if ((int)response.StatusCode == 429) throw new InvalidOperationException("Search Provider đang giới hạn tần suất. Hãy chờ rồi thử lại.");
            throw new HttpRequestException($"Search Provider trả HTTP {(int)response.StatusCode}.");
        }
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("webResults", out var webResults)) return [];
        if (webResults.ValueKind == JsonValueKind.Object && webResults.TryGetProperty("value", out var values)) webResults = values;
        if (webResults.ValueKind != JsonValueKind.Array) return [];
        return webResults.EnumerateArray().Select(item => new ProviderSearchResult(
            ReadString(item, "title"), ReadString(item, "url"), ReadString(item, "content"), ReadTimestamp(item, "lastUpdatedAt")))
            .Where(item => !string.IsNullOrWhiteSpace(item.Url)).ToArray();
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string property) =>
        DateTimeOffset.TryParse(ReadString(element, property), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp) ? timestamp : null;
}

public static class SearchDiscoveryProviderFactory
{
    public static ISearchDiscoveryProvider? Create(IConfiguration configuration, IHttpClientFactory clients)
    {
        if (!string.Equals(configuration["DETOT_SEARCH_PROVIDER"], "microsoft-web", StringComparison.OrdinalIgnoreCase)) return null;
        var key = configuration["DETOT_SEARCH_API_KEY"];
        return string.IsNullOrWhiteSpace(key) ? null : new MicrosoftWebSearchProvider(clients.CreateClient("learning-search"), key);
    }
}
