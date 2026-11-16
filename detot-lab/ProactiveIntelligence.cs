using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

public interface IProactiveProvider
{
    string ProviderId { get; }
    ProactiveConsentPlan ConsentPlan { get; }
    Task<ObservationBatch> CollectAsync(ProviderRequest request, CancellationToken cancellationToken);
}

public interface IProactiveDomainPolicy
{
    string DomainId { get; }
    int MaximumSuggestions { get; }
    IReadOnlyList<EvidenceCandidate> CreateCandidates(ObservationBatch observations);
    IReadOnlySet<string> AllowedDecisionCodes(EvidenceCandidate candidate);
    ProactiveSuggestion? CreateSuggestion(ObservationBatch observations, EvidenceCandidate candidate,
        string decisionCode, DateTimeOffset createdAt);
}

public interface IProactiveAnalyzer
{
    string AnalyzerId { get; }
    IReadOnlyList<ConsentPolicy.ConsentRequirement> RequirementsFor(AnalysisProjection projection);
    Task<IReadOnlyList<AnalyzerDecision>> EvaluateAsync(IReadOnlyList<AnalysisCandidate> candidates,
        CancellationToken cancellationToken);
}

public interface IProactiveEngine
{
    Task<ProactiveEvaluation> RefreshAsync(string ownerId, IProactiveProvider provider,
        IProactiveDomainPolicy policy, IProactiveAnalyzer analyzer, ProviderRequest request,
        CancellationToken cancellationToken);
    bool CommitIfCurrent(ProactiveEvaluation evaluation, Func<bool> commit);
    void StartWorker(string ownerId, string providerId,
        IReadOnlyList<ConsentPolicy.ConsentRequirement> requirements,
        Func<CancellationToken, Task> operation, TimeSpan interval, CancellationToken stoppingToken);
}

public sealed record ProactiveConsentPlan(
    IReadOnlyList<ConsentPolicy.ConsentRequirement> CollectionRequirements,
    IReadOnlyList<ConsentPolicy.ConsentRequirement> LocalProcessingRequirements,
    IReadOnlyList<ConsentPolicy.ConsentRequirement> PersistenceRequirements,
    IReadOnlyList<ConsentPolicy.ConsentRequirement> FeedbackRequirements);

public sealed record ProviderRequest(string OwnerId, Guid RunId, DateTimeOffset RequestedAt, JsonElement Settings);

public sealed record ObservationBatch(string ProviderId, int SchemaVersion, DateTimeOffset RetrievedAt,
    DateTimeOffset? SourceUpdatedAt, DateTimeOffset ExpiresAt, IReadOnlyList<ProactiveEvidence> Evidence);

public sealed record ProactiveEvidence(string EvidenceId, string SourceName, Uri SourceUri,
    DateTimeOffset? ObservedAt, DateTimeOffset RetrievedAt, DateTimeOffset ExpiresAt,
    string ContentHash, string LicenseId, Uri? LicenseUri, string Attribution, int SchemaVersion,
    JsonElement Facts);

public sealed record EvidenceCandidate(string CandidateId, string ProviderId, string DomainId, string SignalType,
    IReadOnlyList<string> EvidenceIds, IReadOnlyList<string> ReasonCodes, double? EventProbability,
    string? ProbabilityMeaning, DateTimeOffset ExpiresAt, JsonElement SafeAnalysisFacts,
    double? EvidenceConfidence = null, double? Relevance = null);

public sealed record AnalysisProjection(string ProviderId, string DomainId,
    IReadOnlyList<AnalysisCandidate> Candidates);

public sealed record AnalysisCandidate(string CandidateId, string SignalType, JsonElement SafeFacts);

public sealed record AnalyzerDecision(string CandidateId, string DecisionCode);

public sealed record ProactiveSuggestion(Guid Id, string ProviderId, string DomainId, string SignalType,
    string Title, string Explanation, string SuggestedActionCode, IReadOnlyList<string> EvidenceIds,
    double? EventProbability, double? EvidenceConfidence, double? Relevance,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string State, int SchemaVersion);

public sealed record ProactiveEvaluation(ObservationBatch Observations, IReadOnlyList<EvidenceCandidate> Candidates,
    IReadOnlyList<ProactiveSuggestion> Suggestions, ConsentPolicy.ConsentLease Lease);

public sealed class ProactiveEngine(ConsentPolicy consent, ILogger? logger = null) : IProactiveEngine
{
    private const int MaximumEvidence = 128;
    private const int MaximumCandidates = 64;
    private readonly ConcurrentDictionary<string, byte> workers = new(StringComparer.Ordinal);

    public void StartWorker(string ownerId, string providerId,
        IReadOnlyList<ConsentPolicy.ConsentRequirement> requirements,
        Func<CancellationToken, Task> operation, TimeSpan interval, CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(ownerId) || string.IsNullOrWhiteSpace(providerId) ||
            interval <= TimeSpan.Zero || requirements.Count == 0 ||
            requirements.Any(item => !consent.IsAllowed(ownerId, item.Source, item.Purpose, item.Destination))) return;
        var key = ownerId + "\0" + providerId;
        if (!workers.TryAdd(key, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var lease = consent.TryBegin(ownerId, requirements, "proactive_worker");
                    if (lease is null) return;
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lease.CancellationToken);
                    try { await operation(linked.Token); }
                    catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested) { }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception error)
                    {
                        logger?.LogWarning("Proactive worker deferred ({ErrorType}).", error.GetType().Name);
                    }
                    try { await Task.Delay(interval, linked.Token); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (OperationCanceledException) when (lease.CancellationToken.IsCancellationRequested) { }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            finally { workers.TryRemove(key, out _); }
        });
    }

    public async Task<ProactiveEvaluation> RefreshAsync(string ownerId, IProactiveProvider provider,
        IProactiveDomainPolicy policy, IProactiveAnalyzer analyzer, ProviderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(analyzer);
        if (string.IsNullOrWhiteSpace(ownerId) || request.OwnerId != ownerId || request.RunId == Guid.Empty)
            throw new ArgumentException("Proactive request owner or run ID is invalid.");
        if (provider.ProviderId.Length is < 1 or > 80 || policy.DomainId.Length is < 1 or > 80 ||
            policy.MaximumSuggestions is < 1 or > 2)
            throw new InvalidDataException("Proactive provider or policy configuration is invalid.");

        // Preflight the analyzer against an empty projection so an external-analysis grant is
        // required before a provider can be contacted, preserving Weather's three-grant gate.
        var preflight = analyzer.RequirementsFor(new AnalysisProjection(provider.ProviderId, policy.DomainId, []));
        var requirements = provider.ConsentPlan.CollectionRequirements
            .Concat(provider.ConsentPlan.LocalProcessingRequirements)
            .Concat(provider.ConsentPlan.PersistenceRequirements)
            .Concat(provider.ConsentPlan.FeedbackRequirements)
            .Concat(preflight).Distinct().ToArray();
        var lease = consent.TryBegin(ownerId, requirements, "proactive_engine");
        if (lease is null) throw new UnauthorizedAccessException("Proactive processing requires explicit consent for every declared stage.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.CancellationToken);
        EnsureStage(lease, provider.ConsentPlan.CollectionRequirements);
        var observations = await provider.CollectAsync(request, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        ValidateObservations(provider.ProviderId, observations);

        EnsureStage(lease, provider.ConsentPlan.LocalProcessingRequirements);
        var candidates = policy.CreateCandidates(observations);
        ValidateCandidates(provider.ProviderId, policy.DomainId, observations, candidates);
        var projection = CreateProjection(provider.ProviderId, policy.DomainId, candidates);
        var analysisRequirements = analyzer.RequirementsFor(projection).Distinct().ToArray();
        var undeclared = analysisRequirements.Except(lease.Requirements).ToArray();
        if (undeclared.Length != 0)
        {
            // Do not analyze if candidates unexpectedly require a broader grant than preflight.
            consent.Check(ownerId, undeclared, "proactive_engine", request.RunId, out _);
            throw new UnauthorizedAccessException("Analyzer requirements changed after consent preflight.");
        }
        EnsureStage(lease, analysisRequirements);

        var decisions = candidates.Count == 0
            ? Array.Empty<AnalyzerDecision>()
            : await analyzer.EvaluateAsync(projection.Candidates, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (!consent.IsCurrent(lease)) throw new OperationCanceledException(lease.CancellationToken);

        var byOpaqueId = projection.Candidates.Select((item, index) => (item.CandidateId, Candidate: candidates[index]))
            .ToDictionary(item => item.CandidateId, item => item.Candidate, StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var suggestions = new List<ProactiveSuggestion>();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var decision in decisions.Take(MaximumCandidates))
        {
            if (decision is null || !byOpaqueId.TryGetValue(decision.CandidateId, out var candidate) ||
                !selected.Add(candidate.CandidateId) || candidate.ExpiresAt <= now ||
                !policy.AllowedDecisionCodes(candidate).Contains(decision.DecisionCode)) continue;
            var suggestion = policy.CreateSuggestion(observations, candidate, decision.DecisionCode, now);
            if (suggestion is null || suggestion.ProviderId != provider.ProviderId || suggestion.DomainId != policy.DomainId ||
                suggestion.SignalType != candidate.SignalType || suggestion.EvidenceIds.Count == 0 ||
                suggestion.EvidenceIds.Any(id => !candidate.EvidenceIds.Contains(id, StringComparer.Ordinal)) ||
                suggestion.ExpiresAt > candidate.ExpiresAt || suggestion.ExpiresAt <= now ||
                suggestion.EventProbability != candidate.EventProbability ||
                suggestion.EvidenceConfidence != candidate.EvidenceConfidence || suggestion.Relevance != candidate.Relevance)
                continue;
            suggestions.Add(suggestion);
        }

        EnsureStage(lease, provider.ConsentPlan.PersistenceRequirements);
        return new ProactiveEvaluation(observations, candidates,
            suggestions.DistinctBy(item => item.Id).OrderBy(item => item.ExpiresAt).Take(policy.MaximumSuggestions).ToArray(), lease);
    }

    public bool CommitIfCurrent(ProactiveEvaluation evaluation, Func<bool> commit) =>
        consent.ExecuteIfCurrent(evaluation.Lease, commit);

    private void EnsureStage(ConsentPolicy.ConsentLease lease,
        IReadOnlyList<ConsentPolicy.ConsentRequirement> requirements)
    {
        if (requirements.Any(requirement => !lease.Requirements.Contains(requirement)) || !consent.IsCurrent(lease))
            throw new OperationCanceledException(lease.CancellationToken);
    }

    private static void ValidateObservations(string providerId, ObservationBatch batch)
    {
        var now = DateTimeOffset.UtcNow;
        if (batch is null || batch.ProviderId != providerId || batch.SchemaVersion != 1 ||
            batch.RetrievedAt > now.AddMinutes(5) || batch.ExpiresAt <= batch.RetrievedAt ||
            batch.Evidence is null || batch.Evidence.Count is 0 or > MaximumEvidence)
            throw new InvalidDataException("Provider observations failed schema or freshness validation.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in batch.Evidence)
        {
            if (evidence is null || evidence.EvidenceId is not { Length: > 0 and <= 128 } || !ids.Add(evidence.EvidenceId) ||
                evidence.SourceName is not { Length: > 0 and <= 80 } || evidence.SourceUri.Scheme != Uri.UriSchemeHttps ||
                evidence.RetrievedAt != batch.RetrievedAt || evidence.ExpiresAt > batch.ExpiresAt ||
                evidence.ExpiresAt <= now || evidence.SchemaVersion != batch.SchemaVersion ||
                evidence.ContentHash is not { Length: 64 } || !evidence.ContentHash.All(Uri.IsHexDigit) ||
                evidence.LicenseId is not { Length: > 0 and <= 80 } || evidence.Attribution is not { Length: > 0 and <= 200 } ||
                evidence.Facts.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || evidence.Facts.GetRawText().Length > 65536)
                throw new InvalidDataException("Provider evidence failed validation.");
        }
    }

    private static void ValidateCandidates(string providerId, string domainId, ObservationBatch batch,
        IReadOnlyList<EvidenceCandidate> candidates)
    {
        if (candidates is null || candidates.Count > MaximumCandidates) throw new InvalidDataException("Domain policy returned too many candidates.");
        var evidenceIds = batch.Evidence.Select(item => item.EvidenceId).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (candidate is null || candidate.CandidateId is not { Length: > 0 and <= 128 } || !ids.Add(candidate.CandidateId) ||
                candidate.ProviderId != providerId || candidate.DomainId != domainId ||
                candidate.SignalType is not { Length: > 0 and <= 80 } || candidate.EvidenceIds is null or { Count: 0 } ||
                candidate.EvidenceIds.Any(id => !evidenceIds.Contains(id)) || candidate.ReasonCodes is null or { Count: 0 } ||
                candidate.EventProbability is { } probability && (!double.IsFinite(probability) || probability is < 0 or > 100) ||
                candidate.EvidenceConfidence is { } confidence && (!double.IsFinite(confidence) || confidence is < 0 or > 1) ||
                candidate.Relevance is { } relevance && (!double.IsFinite(relevance) || relevance is < 0 or > 1) ||
                candidate.ExpiresAt > batch.ExpiresAt || candidate.ExpiresAt <= batch.RetrievedAt ||
                candidate.SafeAnalysisFacts.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ||
                candidate.SafeAnalysisFacts.GetRawText().Length > 8192)
                throw new InvalidDataException("Domain evidence candidate failed validation.");
        }
    }

    private static AnalysisProjection CreateProjection(string providerId, string domainId,
        IReadOnlyList<EvidenceCandidate> candidates) => new(providerId, domainId,
        candidates.Select((candidate, index) => new AnalysisCandidate($"c{index}", candidate.SignalType,
            candidate.SafeAnalysisFacts.Clone())).ToArray());
}
