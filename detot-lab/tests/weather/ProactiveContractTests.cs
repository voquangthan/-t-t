using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

static class ProactiveContractTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "detot-proactive-contracts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var consent = new ConsentPolicy(Path.Combine(root, "consent"), new EphemeralDataProtectionProvider());
            var provider = new ContractProvider();
            var policy = new ContractPolicy();
            var analyzer = new ContractAnalyzer();
            var engine = new ProactiveEngine(consent);
            var request = new ProviderRequest("synthetic-owner-a", Guid.NewGuid(), DateTimeOffset.UtcNow,
                JsonDocument.Parse("{}").RootElement.Clone());

            await ExpectDenied(() => engine.RefreshAsync(request.OwnerId, provider, policy, analyzer, request, CancellationToken.None));
            Check(provider.Calls == 0 && analyzer.Calls == 0, "default deny must block before provider access");
            Grant(consent, request.OwnerId, "local_only");
            Grant(consent, request.OwnerId, "external_service");
            await ExpectDenied(() => engine.RefreshAsync(request.OwnerId, provider, policy, analyzer, request, CancellationToken.None));
            Check(provider.Calls == 0 && analyzer.Calls == 0,
                "provider grant must not imply DeepSeek permission and all legacy Weather grants preflight before collection");

            Grant(consent, request.OwnerId, "DeepSeek");
            analyzer.DecisionCode = "carry_rain_gear";
            var accepted = await engine.RefreshAsync(request.OwnerId, provider, policy, analyzer, request, CancellationToken.None);
            Check(provider.Calls == 1 && analyzer.Calls == 1 && accepted.Suggestions.Count == 1,
                $"an authorized candidate and allowlisted analyzer decision should produce one suggestion (provider={provider.Calls}, analyzer={analyzer.Calls}, candidates={accepted.Candidates.Count}, suggestions={accepted.Suggestions.Count})");
            Check(analyzer.LastCandidates.Single().CandidateId == "c0" &&
                !analyzer.LastCandidates.Single().SafeFacts.GetRawText().Contains("Synthetic City", StringComparison.Ordinal) &&
                accepted.Suggestions[0].EvidenceConfidence is null && accepted.Suggestions[0].Relevance is null,
                "analyzer sees opaque IDs and minimal facts; unsupported confidence and relevance remain null");

            var otherOwner = request with { OwnerId = "synthetic-owner-b", RunId = Guid.NewGuid() };
            await ExpectDenied(() => engine.RefreshAsync(otherOwner.OwnerId, provider, policy, analyzer, otherOwner, CancellationToken.None));
            Check(provider.Calls == 1, "consent must be isolated by owner");

            analyzer.DecisionCode = "invented_action";
            var rejected = await engine.RefreshAsync(request.OwnerId, provider, policy, analyzer,
                request with { RunId = Guid.NewGuid() }, CancellationToken.None);
            Check(rejected.Suggestions.Count == 0, "unknown action codes must fail closed");

            analyzer.DecisionCode = "carry_rain_gear";
            var pending = await engine.RefreshAsync(request.OwnerId, provider, policy, analyzer,
                request with { RunId = Guid.NewGuid() }, CancellationToken.None);
            consent.Set(request.OwnerId, "public_weather", "proactive_analysis", "DeepSeek", false, "owner", Guid.NewGuid());
            var committed = false;
            Check(!engine.CommitIfCurrent(pending, () => { committed = true; return true; }) && !committed,
                "a lease revoked after analysis must not commit suggestions");

            Grant(consent, request.OwnerId, "DeepSeek");
            var blocking = new ContractAnalyzer { Block = true };
            var inFlight = engine.RefreshAsync(request.OwnerId, provider, policy, blocking,
                request with { RunId = Guid.NewGuid() }, CancellationToken.None);
            await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            consent.Set(request.OwnerId, "public_weather", "proactive_analysis", "DeepSeek", false, "owner", Guid.NewGuid());
            var cancelled = false;
            try { await inFlight; } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && blocking.Cancelled.Task.IsCompleted,
                "revocation during analysis must cancel the analyzer and discard the run");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static void Grant(ConsentPolicy consent, string owner, string destination) =>
        consent.Set(owner, "public_weather", "proactive_analysis", destination, true, "owner", Guid.NewGuid());

    private static async Task ExpectDenied(Func<Task<ProactiveEvaluation>> action)
    {
        var denied = false;
        try { _ = await action(); } catch (UnauthorizedAccessException) { denied = true; }
        Check(denied, "missing explicit consent must deny the Engine run");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class ContractProvider : IProactiveProvider
    {
        public string ProviderId => "public_weather";
        public int Calls { get; private set; }
        public ProactiveConsentPlan ConsentPlan { get; } = new(
            [new("public_weather", "proactive_analysis", "external_service")],
            [new("public_weather", "proactive_analysis", "local_only")],
            [new("public_weather", "proactive_analysis", "local_only")], []);

        public Task<ObservationBatch> CollectAsync(ProviderRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var now = DateTimeOffset.UtcNow;
            using var factsDocument = JsonDocument.Parse("{\"city\":\"Synthetic City\",\"rainProbability\":85}");
            var evidence = new ProactiveEvidence("evidence-1", "Open-Meteo", new Uri("https://open-meteo.com/en/docs"),
                now, now, now.AddHours(2), new string('A', 64), "CC-BY-4.0",
                new Uri("https://creativecommons.org/licenses/by/4.0/"), "Weather data by Open-Meteo.com", 1,
                factsDocument.RootElement.Clone());
            return Task.FromResult(new ObservationBatch(ProviderId, 1, now, now, now.AddHours(2), [evidence]));
        }
    }

    private sealed class ContractPolicy : IProactiveDomainPolicy
    {
        public string DomainId => "weather";
        public int MaximumSuggestions => 2;

        public IReadOnlyList<EvidenceCandidate> CreateCandidates(ObservationBatch observations)
        {
            using var factsDocument = JsonDocument.Parse("{\"rainProbability\":85}");
            return [new EvidenceCandidate("candidate-real-id", observations.ProviderId, DomainId, "heavy_rain",
                [observations.Evidence[0].EvidenceId], ["rain_three_hour_window"], 85,
                "peak_precipitation_probability_percent", observations.ExpiresAt, factsDocument.RootElement.Clone())];
        }

        public IReadOnlySet<string> AllowedDecisionCodes(EvidenceCandidate candidate) =>
            new HashSet<string>(["carry_rain_gear", "plan_indoor"], StringComparer.Ordinal);

        public ProactiveSuggestion CreateSuggestion(ObservationBatch observations, EvidenceCandidate candidate,
            string decisionCode, DateTimeOffset createdAt) => new(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            candidate.ProviderId, candidate.DomainId, candidate.SignalType, "Có thể mưa lớn", "Dựa trên dự báo.",
            decisionCode, candidate.EvidenceIds, candidate.EventProbability, candidate.EvidenceConfidence,
            candidate.Relevance, createdAt, candidate.ExpiresAt, "proposed", 1);
    }

    private sealed class ContractAnalyzer : IProactiveAnalyzer
    {
        public string AnalyzerId => "synthetic-analyzer";
        public string DecisionCode { get; set; } = "carry_rain_gear";
        public bool Block { get; init; }
        public int Calls { get; private set; }
        public IReadOnlyList<AnalysisCandidate> LastCandidates { get; private set; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<ConsentPolicy.ConsentRequirement> RequirementsFor(AnalysisProjection projection) =>
            [new("public_weather", "proactive_analysis", "DeepSeek")];

        public async Task<IReadOnlyList<AnalyzerDecision>> EvaluateAsync(IReadOnlyList<AnalysisCandidate> candidates,
            CancellationToken cancellationToken)
        {
            Calls++;
            LastCandidates = candidates;
            if (Block)
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return candidates.Select(item => new AnalyzerDecision(item.CandidateId, DecisionCode)).ToArray();
        }
    }
}
