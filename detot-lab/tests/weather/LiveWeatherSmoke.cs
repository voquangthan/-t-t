using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

static class LiveWeatherSmoke
{
    public static async Task RunAsync()
    {
        var apiKey = Environment.GetEnvironmentVariable("DETOT_WEATHER_DEEPSEEK_KEY") ??
            Environment.GetEnvironmentVariable("DETOT_MEMORY_DEEPSEEK_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("SKIP LIVE: no DeepSeek key in the test process; no external request was made.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "detot-weather-live-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string owner = "synthetic-live-weather-owner";
            var protection = new EphemeralDataProtectionProvider();
            var consent = new ConsentPolicy(Path.Combine(root, "consent"), protection);
            foreach (var destination in new[] { "local_only", "external_service", "DeepSeek" })
                consent.Set(owner, "public_weather", "proactive_analysis", destination, true, "synthetic-test", Guid.NewGuid());

            var clients = new LiveWeatherClientFactory();
            var service = new WeatherIntelligence(Path.Combine(root, "weather"), protection, clients, consent,
                apiKey, Microsoft.Extensions.Logging.Abstractions.NullLogger<WeatherIntelligence>.Instance);
            var snapshot = await service.SetLocationAsync(owner, "Singapore", CancellationToken.None);
            Check(snapshot.Location?.Contains("Singapore", StringComparison.OrdinalIgnoreCase) == true &&
                snapshot.FetchedAt is not null && snapshot.Error is null && snapshot.Suggestions.Length <= 2,
                "live Open-Meteo collection failed or returned an unsafe Weather response");
            var weatherDeepSeekCalls = clients.DeepSeekCalls;

            if (weatherDeepSeekCalls == 0)
            {
                // A real forecast can correctly produce no candidate. Exercise the analyzer with
                // synthetic, non-personal facts without inventing or persisting a user alert.
                var analyzer = new DeepSeekProactiveAnalyzer(clients, apiKey);
                using var safeFacts = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    startsAt = DateTimeOffset.UtcNow.AddHours(8),
                    endsAt = DateTimeOffset.UtcNow.AddHours(11),
                    probability = 85,
                    millimeters3Hours = 18.0
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                var decisions = await analyzer.EvaluateAsync(
                    [new AnalysisCandidate("c0", "heavy_rain", safeFacts.RootElement.Clone())], CancellationToken.None);
                Check(decisions.All(item => item.CandidateId == "c0" &&
                    item.DecisionCode is "carry_rain_gear" or "plan_indoor"),
                    "live DeepSeek returned a decision outside the bounded analyzer contract");
            }

            Check(clients.DeepSeekCalls > 0 && clients.OutboundWeatherPayloadSafe,
                "DeepSeek live smoke did not send a bounded forecast projection");
            Console.WriteLine("PASS LIVE: Open-Meteo forecast retrieved for a public test city; DeepSeek accepted only a bounded non-personal projection.");
            Console.WriteLine(weatherDeepSeekCalls > 0
                ? "PASS LIVE E2E: the real forecast produced a candidate selected by DeepSeek."
                : "SKIP LIVE E2E: the real forecast produced no qualifying rain candidate; analyzer passed separately with synthetic facts.");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class LiveWeatherClientFactory : IHttpClientFactory
    {
        public int DeepSeekCalls { get; private set; }
        public bool OutboundWeatherPayloadSafe { get; private set; } = true;

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(new LiveWeatherHandler(this), disposeHandler: true);
            client.Timeout = name == "weather-deepseek" ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(20);
            return client;
        }

        private sealed class LiveWeatherHandler(LiveWeatherClientFactory owner) : HttpMessageHandler
        {
            private readonly HttpMessageInvoker inner = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var uri = request.RequestUri;
                if (uri is null || uri.Scheme != Uri.UriSchemeHttps ||
                    !(uri.Host == "api.open-meteo.com" || uri.Host == "geocoding-api.open-meteo.com" ||
                      uri.Host == "api.deepseek.com"))
                    throw new HttpRequestException("Live smoke blocked an unapproved destination.");
                if (uri.Host == "api.deepseek.com")
                {
                    owner.DeepSeekCalls++;
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    owner.OutboundWeatherPayloadSafe &= !body.Contains("Singapore", StringComparison.OrdinalIgnoreCase) &&
                        !body.Contains("synthetic-live-weather-owner", StringComparison.Ordinal) &&
                        !body.Contains("1.35", StringComparison.Ordinal) && !body.Contains("103.82", StringComparison.Ordinal);
                    if (!owner.OutboundWeatherPayloadSafe)
                        throw new InvalidDataException("Location or owner data was included in the DeepSeek request.");
                }
                return await inner.SendAsync(request, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
