using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

sealed class Practice(IDataProtector protector, string directory)
{
    private readonly object chatLearningGate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string ProfilePath => Path.Combine(directory, "notes.bin");
    private string LearningPath => Path.Combine(directory, "learning.bin");
    private string WorkMemoryPath => Path.Combine(directory, "current-work.bin");
    private string StructuredMemoryPath => Path.Combine(directory, "structured-memory.bin");
    private readonly object memoryGate = new();
    private string MemoryOwnersPath => Path.Combine(directory, "memory-owners.bin");
    private string DreamingStatePath => Path.Combine(directory, "memory-dreaming-state.bin");
    private string OwnerProposalsPath(string owner) => Path.Combine(directory, $"memory-proposals-{StableSkillKey("owner", owner)}.bin");
    private string OwnerMemoryPath(string owner) => Path.Combine(directory, $"memory-{StableSkillKey("owner", owner)}.bin");
    private string MemoryChoicePath(string owner, Guid id) =>
        Path.Combine(directory, $"memory-choice-{StableSkillKey("owner", owner)}-{id:N}.bin");
    public bool HasMemoryChoice(string owner, Guid? source) => source is { } id && id != Guid.Empty &&
        File.Exists(MemoryChoicePath(owner, id));
    private string ChatLearningPath => Path.Combine(directory, "chat-learning.bin");
    private string CasePath(Guid id) => Path.Combine(directory, $"{id:N}.bin");

    public string Notes => Read<string>(ProfilePath) ?? "";
    public string LearningSummary => SummarizeLearning(Read<LearningProfile>(LearningPath) ?? LearningProfile.Empty);
    public LearningSnapshot LearningMap => BuildLearningMap(Read<LearningProfile>(LearningPath) ?? LearningProfile.Empty);
    public ChatLearningSnapshot ChatLearningMap
    {
        get { lock (chatLearningGate) return BuildChatLearningMap(Read<ChatLearningProfile>(ChatLearningPath) ?? ChatLearningProfile.Empty); }
    }
    public string? Case(Guid id) => Read<string>(CasePath(id));
    public void SaveNotes(string notes) => Write(ProfilePath, notes.Trim());
    public void ClearChatLearning()
    public string ApplyMemory(string owner, string intent, string? value, string? category, Guid? source, Guid? requestId, ILogger logger)
    {
        lock (memoryGate)
        {
            var entries = ReadOwnerMemories(owner);

            if (intent == "recall")
            {
                if (entries.Count == 0) return "Bạn chưa lưu ký ức nào.";
                var recent = entries.Where(entry => entry.Status is not ("deleted" or "archived")).TakeLast(10).Reverse();
                return "Ký ức đã lưu gần đây:\n\n" + string.Join("\n", recent.Select(entry =>
                    $"- {entry.Text} · {(entry.Status == "done" ? "đã xong" : entry.Status == "open" ? "đang mở" : "đã lưu")} · mã {entry.Id:D}"));
            }

            if (requestId is not { } retry || retry == Guid.Empty)
                return "Chưa lưu. Yêu cầu ghi nhớ thiếu mã để chống lưu trùng.";
            var previous = entries.Find(entry => entry.History.Any(change => change.RequestId == retry));
            if (previous is not null)
                return $"Yêu cầu này đã được xử lý · mã {previous.Id:D}.";

            var now = DateTimeOffset.UtcNow;
            if (intent == "save" && value is not null && category is ("task" or "device" or "person" or "preference" or "event"))
            {
                if (entries.Count >= 1000) return "Kho ký ức đã đạt giới hạn 1.000 mục.";
                var entry = new MemoryEntry(Guid.NewGuid(), category, value, source, now,
                    category == "task" ? "open" : "recorded", "user-request", 1,
                    [new MemoryChange("created", now, source, requestId, null,
                        category == "task" ? "open" : "recorded", owner)], owner);
                entries.Add(entry);
                WriteMemories(owner, entries);
                if (ReadOwnerMemories(owner).Any(saved => saved.Id == entry.Id &&
                    saved.History.Any(change => change.RequestId == retry)) != true)
                    throw new IOException("Memory write could not be verified.");
                logger.LogInformation("Memory persistence verified after create.");
                return $"Đã lưu ký ức · mã {entry.Id:D}.";
            }
            if (intent == "complete")
            {
                var pending = HasMemoryChoice(owner, source) ? Read<MemoryChoice>(MemoryChoicePath(owner, source!.Value)) : null;
                if (pending?.ExpiresAt <= now) pending = null;
                MemoryCandidate? selected = null;
                if (Guid.TryParse(value, out var id))
                {
                    selected = pending?.Candidates.FirstOrDefault(candidate => candidate.Id == id);
                    if (pending is not null && selected is null)
                        return "Mã này không thuộc danh sách đang chọn. Hãy chọn một việc trong danh sách.";
                }
                else if (pending is not null && int.TryParse(value, out var ordinal) &&
                    ordinal >= 1 && ordinal <= pending.Candidates.Length)
                {
                    selected = pending.Candidates[ordinal - 1];
                    id = selected.Id;
                }
                else
                {
                    var open = entries.Where(entry => entry.Category == "task" && entry.Status == "open").ToArray();
                    var terms = Normalize(value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var matches = terms.Length == 0 ? open : open.Where(entry =>
                        terms.All(term => ContainsAny(Normalize(entry.Text), term))).ToArray();
                    if (source is not { } chat || chat == Guid.Empty || matches.Length == 0 || matches.Length > 10)
                        return "Bạn muốn hoàn thành việc nào? Cho mình tên thiết bị, phòng hoặc mã công việc.";
                    var choice = new MemoryChoice(now.AddMinutes(30),
                        matches.Select(entry => new MemoryCandidate(entry.Id, entry.History.Length)).ToArray());
                    Write(MemoryChoicePath(owner, chat), JsonSerializer.Serialize(choice, Json));
                    return "Chọn đúng công việc bằng số thứ tự hoặc mã:\n\n" +
                        string.Join("\n", matches.Select((entry, i) => $"{i + 1}. {entry.Text} · mã {entry.Id:D}"));
                }
                var index = entries.FindIndex(entry => entry.Id == id && entry.Category == "task");
                if (index < 0) return "Không tìm thấy công việc với mã này. Hãy hỏi lại danh sách việc đã lưu.";
                var entry = entries[index];
                if (entry.Status == "done") return "Việc này đã được đánh dấu hoàn thành.";
                if (entry.Status != "open" || selected is not null && selected.Version != entry.History.Length)
                    return "Công việc đã thay đổi. Hãy xem lại trạng thái trước khi chọn.";
                entries[index] = entry with { Status = "done", History =
                    [..entry.History, new MemoryChange("completed", now, source, requestId, entry.Status, "done", owner)] };
                WriteMemories(owner, entries);
                if (ReadOwnerMemories(owner).Any(saved => saved.Id == id &&
                    saved.Status == "done" && saved.History.Any(change => change.RequestId == retry)) != true)
                    throw new IOException("Memory update could not be verified.");
                logger.LogInformation("Memory persistence verified after completion.");
                return $"Đã đánh dấu hoàn thành: {entry.Text}";
            }
            return "Bạn muốn cập nhật việc nào? Hãy hỏi lại danh sách và cho mình mã của đúng công việc.";
        }
    }

    private List<MemoryEntry> ReadOwnerMemories(string owner) =>
        (Read<List<MemoryEntry>>(OwnerMemoryPath(owner)) ?? []).Where(entry => entry.OwnerId == owner).ToList();

    private void WriteMemories(string owner, List<MemoryEntry> entries)
    {
        Write(OwnerMemoryPath(owner), JsonSerializer.Serialize(entries, Json));
        var owners = Read<List<string>>(MemoryOwnersPath) ?? [];
        if (!owners.Contains(owner, StringComparer.Ordinal))
            Write(MemoryOwnersPath, JsonSerializer.Serialize(owners.Append(owner).Order(StringComparer.Ordinal).ToList(), Json));
    }

    public sealed record ReflectionMemory(Guid Id, string Category, string Text, string Status,
        string Source, Guid? ConversationId, DateTimeOffset? CreatedAt, int Version, string[] History);
    public sealed record ReflectionDraft(string Kind, Guid[] MemoryIds, string Reason, string? ProposedText,
        double Confidence, string Evidence);
    public sealed record ReflectionEvidence(Guid Id, string Text, string Category, string Status,
        string Source, Guid? ConversationId, DateTimeOffset? CreatedAt, int Version, string[] History);
    public sealed record ReflectionSuggestion(Guid Id, string Kind, ReflectionEvidence[] Evidence,
        string Reason, string? ProposedText, double Confidence, string State, DateTimeOffset CreatedAt,
        string Fingerprint, string Assessment = "unverified-inference");
    private sealed record OwnerReflectionProgress(long CompletedGeneration, long TargetGeneration, int Cursor);
    private sealed record DreamingState(Dictionary<string, OwnerReflectionProgress> Owners, Dictionary<string, RetryState> Retries);
    private sealed record RetryState(int Failures, DateTimeOffset NextAttempt);
    public sealed record ReflectionStatus(int Scanned, int Total, bool Pending, int RetryCount, DateTimeOffset? RetryAt);

    public string[] KnownMemoryOwners()
    {
        lock (memoryGate) return (Read<List<string>>(MemoryOwnersPath) ?? []).Distinct(StringComparer.Ordinal).ToArray();
    }

    public void RegisterMemoryOwner(string owner)
    {
        lock (memoryGate)
        {
            var owners = Read<List<string>>(MemoryOwnersPath) ?? [];
            if (!owners.Contains(owner, StringComparer.Ordinal))
                Write(MemoryOwnersPath, JsonSerializer.Serialize(owners.Append(owner).Order(StringComparer.Ordinal).ToList(), Json));
        }
    }

    public sealed record ReflectionBatch(long Generation, int Cursor, int NextCursor, int TotalCount, ReflectionMemory[] Items);

    private static long Generation(List<MemoryEntry> entries) => entries.Sum(entry => (long)entry.History.Length);

    public ReflectionBatch GetReflectionBatch(string owner, int batchSize = 40)
    {
        lock (memoryGate)
        {
            var entries = ReadOwnerMemories(owner);
            var generation = Generation(entries);
            var active = entries.Where(entry => entry.Status is not ("deleted" or "archived"))
                .OrderBy(entry => entry.CreatedAt).ThenBy(entry => entry.Id).ToArray();
            var state = Read<DreamingState>(DreamingStatePath) ?? new([], []);
            var progress = state.Owners.GetValueOrDefault(owner) ?? new OwnerReflectionProgress(0, 0, 0);
            if (progress.TargetGeneration != generation)
            {
                progress = progress with { TargetGeneration = generation, Cursor = 0 };
                var owners = new Dictionary<string, OwnerReflectionProgress>(state.Owners, StringComparer.Ordinal) { [owner] = progress };
                Write(DreamingStatePath, JsonSerializer.Serialize(new DreamingState(owners, state.Retries), Json));
            }
            var cursor = Math.Clamp(progress.Cursor, 0, active.Length);
            var start = cursor == 0 ? 0 : cursor - 1; // one-item overlap checks duplicates at batch boundaries
            var take = Math.Min(active.Length - start, batchSize + (cursor == 0 ? 0 : 1));
            var items = active.Skip(start).Take(take).Select(entry => new ReflectionMemory(entry.Id,
                entry.Category, entry.Text, entry.Status, entry.Source, entry.ConversationId, entry.CreatedAt,
                entry.History.Length, entry.History.Select(change =>
                    $"{change.Action}:{change.BeforeStatus ?? "-"}->{change.AfterStatus ?? "-"}@{change.At:O}").ToArray())).ToArray();
            return new ReflectionBatch(generation, cursor, Math.Min(active.Length, cursor + batchSize), active.Length, items);
        }
    }

    public bool ReflectionIsDue(string owner, DateTimeOffset now)
    {
        lock (memoryGate)
        {
            var state = Read<DreamingState>(DreamingStatePath) ?? new([], []);
            var entries = ReadOwnerMemories(owner);
            var generation = Generation(entries);
            var activeCount = entries.Count(entry => entry.Status is not ("deleted" or "archived"));
            var progress = state.Owners.GetValueOrDefault(owner) ?? new OwnerReflectionProgress(0, 0, 0);
            var retry = state.Retries.GetValueOrDefault(owner);
            return (generation > progress.CompletedGeneration || progress.Cursor < activeCount) &&
                (retry is null || retry.NextAttempt <= now);
        }
    }

    public ReflectionStatus GetReflectionStatus(string owner)
    {
        lock (memoryGate)
        {
            var state = Read<DreamingState>(DreamingStatePath) ?? new([], []);
            var progress = state.Owners.GetValueOrDefault(owner) ?? new OwnerReflectionProgress(0, 0, 0);
            var entries = ReadOwnerMemories(owner);
            var generation = Generation(entries);
            var total = entries.Count(entry => entry.Status is not ("deleted" or "archived"));
            var scanned = generation == progress.TargetGeneration ? progress.Cursor : 0;
            var retry = state.Retries.GetValueOrDefault(owner);
            return new ReflectionStatus(Math.Clamp(scanned, 0, total), total,
                generation > progress.CompletedGeneration || scanned < total,
                retry?.Failures ?? 0, retry?.NextAttempt);
        }
    }

    public void CompleteReflectionRun(string owner, long generation, int nextCursor, int totalCount, bool success)
    {
        lock (memoryGate)
        {
            var current = Generation(ReadOwnerMemories(owner));
            var state = Read<DreamingState>(DreamingStatePath) ?? new([], []);
            var owners = new Dictionary<string, OwnerReflectionProgress>(state.Owners, StringComparer.Ordinal);
            var retries = new Dictionary<string, RetryState>(state.Retries, StringComparer.Ordinal);
            if (success)
            {
                var old = owners.GetValueOrDefault(owner) ?? new OwnerReflectionProgress(0, generation, 0);
                if (current == generation)
                    owners[owner] = nextCursor >= totalCount
                        ? new OwnerReflectionProgress(generation, generation, totalCount)
                        : old with { TargetGeneration = generation, Cursor = Math.Max(old.Cursor, nextCursor) };
                else owners[owner] = old with { TargetGeneration = 0, Cursor = 0 };
                retries.Remove(owner);
            }
            else
            {
                var failures = Math.Min(10, retries.GetValueOrDefault(owner)?.Failures + 1 ?? 1);
                retries[owner] = new RetryState(failures, DateTimeOffset.UtcNow.AddSeconds(Math.Min(1800, 10 * Math.Pow(2, failures - 1))));
            }
            Write(DreamingStatePath, JsonSerializer.Serialize(new DreamingState(owners, retries), Json));
        }
    }

    public bool SaveReflectionDrafts(string owner, long expectedGeneration, ReflectionDraft[] drafts)
    {
        lock (memoryGate)
        {
            var ownerEntries = ReadOwnerMemories(owner);
            if (Generation(ownerEntries) != expectedGeneration) return false;
            var entries = ownerEntries.Where(entry => entry.Status is not ("deleted" or "archived"))
                .ToDictionary(entry => entry.Id);
            var existing = Read<List<ReflectionSuggestion>>(OwnerProposalsPath(owner)) ?? [];
            var added = false;
            foreach (var draft in drafts.Take(12))
            {
                var ids = draft.MemoryIds.Distinct().Take(2).ToArray();
                if (ids.Length is < 1 or > 2 || draft.Kind is not ("merge" or "update" or "supersede" or "archive") ||
                    !double.IsFinite(draft.Confidence) || draft.Confidence is < 0.7 or > 1 ||
                    draft.Reason.Length is < 3 or > 500 || draft.Evidence.Length is < 1 or > 500) continue;
                if ((draft.Kind is "merge" or "supersede") && ids.Length != 2) continue;
                if (draft.Kind == "archive" && ids.Length != 1) continue;
                if (draft.Kind == "update" && ids.Length is < 1 or > 2) continue;
                if (ids.Any(id => !entries.ContainsKey(id))) continue;
                if ((draft.Kind is "merge" or "supersede") && entries[ids[0]].Category != entries[ids[1]].Category) continue;
                if (draft.ProposedText is { Length: > 500 }) continue;
                var evidence = ids.Select(id => entries[id]).Select(entry => new ReflectionEvidence(entry.Id,
                    entry.Text, entry.Category, entry.Status, entry.Source, entry.ConversationId, entry.CreatedAt,
                    entry.History.Length, entry.History.Select(change => $"{change.Action}:{change.BeforeStatus ?? "-"}->{change.AfterStatus ?? "-"}@{change.At:O}").ToArray())).ToArray();
                var fingerprintInput = string.Join("|", new[] { draft.Kind }.Concat(evidence.Select(item =>
                    $"{item.Id:N}:{item.Version}").Order(StringComparer.Ordinal)));
                var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)));
                if (existing.Any(item => item.Fingerprint == fingerprint)) continue;
                existing.Add(new ReflectionSuggestion(Guid.NewGuid(), draft.Kind, evidence,
                    draft.Reason.Trim(), draft.ProposedText?.Trim(), draft.Confidence, "pending", DateTimeOffset.UtcNow, fingerprint));
                added = true;
            }
            if (added) Write(OwnerProposalsPath(owner), JsonSerializer.Serialize(existing.TakeLast(300).ToList(), Json));
            return true;
        }
    }

    public IResult ListReflectionSuggestions(string owner)
    {
        lock (memoryGate)
        {
            var suggestions = Read<List<ReflectionSuggestion>>(OwnerProposalsPath(owner)) ?? [];
            return Results.Ok(suggestions.Where(item => item.State == "pending").OrderByDescending(item => item.CreatedAt).Take(100));
        }
    }

    public IResult ReviewReflectionSuggestion(string owner, Guid id, string action, string? editedText)
    {
        lock (memoryGate)
        {
            var suggestions = Read<List<ReflectionSuggestion>>(OwnerProposalsPath(owner)) ?? [];
            var proposalIndex = suggestions.FindIndex(item => item.Id == id && item.State == "pending");
            if (proposalIndex < 0) return Results.NotFound();
            var proposal = suggestions[proposalIndex];
            if (action is not ("approve" or "edit" or "reject")) return Results.BadRequest();
            var entries = ReadOwnerMemories(owner);
            if (proposal.Evidence.Any(reference => !entries.Any(entry => entry.Id == reference.Id &&
                entry.History.Length == reference.Version && entry.Text == reference.Text && entry.Status == reference.Status)))
            {
                suggestions[proposalIndex] = proposal with { State = "stale" };
                Write(OwnerProposalsPath(owner), JsonSerializer.Serialize(suggestions, Json));
                return Results.Conflict(new { error = "Ký ức đã thay đổi; đề xuất cũ được đánh dấu hết hạn." });
            }
            if (action == "reject")
            {
                suggestions[proposalIndex] = proposal with { State = "rejected" };
                Write(OwnerProposalsPath(owner), JsonSerializer.Serialize(suggestions, Json));
                return Results.NoContent();
            }
            var replacement = action == "edit" ? editedText?.Trim() : proposal.ProposedText?.Trim();
            if ((proposal.Kind is "merge" or "update") && replacement is not { Length: > 0 and <= 500 }) return Results.BadRequest();
            var now = DateTimeOffset.UtcNow;
            foreach (var evidence in proposal.Evidence)
            {
                var index = entries.FindIndex(entry => entry.Id == evidence.Id);
                var entry = entries[index];
                if ((proposal.Kind is "merge" or "update") && evidence.Id == proposal.Evidence[0].Id)
                    entries[index] = entry with { Text = replacement!, History = [..entry.History,
                        new MemoryChange(proposal.Kind == "merge" ? "dreaming-merge-approved" : "dreaming-update-approved",
                            now, null, id, entry.Status, entry.Status, owner)] };
                else if (proposal.Kind == "archive" || proposal.Kind == "merge" ||
                    proposal.Kind == "supersede" && evidence.Id == proposal.Evidence[0].Id ||
                    proposal.Kind == "update" && proposal.Evidence.Length == 2 && evidence.Id != proposal.Evidence[0].Id)
                    entries[index] = entry with { Status = "archived", History = [..entry.History,
                        new MemoryChange(proposal.Kind == "supersede" ? "dreaming-supersede-approved" :
                            proposal.Kind == "update" ? "dreaming-update-archived" : "dreaming-archive-approved",
                            now, null, id, entry.Status, "archived", owner)] };
            }
            WriteMemories(owner, entries);
            if (!ReadOwnerMemories(owner).Where(entry => proposal.Evidence.Any(item => item.Id == entry.Id))
                .All(entry => entry.History.Any(change => change.RequestId == id))) return Results.Problem("Không xác nhận được lịch sử sau duyệt.", statusCode: 500);
            suggestions[proposalIndex] = proposal with { State = action == "edit" ? "edited-approved" : "approved" };
            Write(OwnerProposalsPath(owner), JsonSerializer.Serialize(suggestions, Json));
            return Results.NoContent();
        }
    }

    public IResult ReviewMemoryPatterns(string owner)
    {
        lock (memoryGate)
        {
            var proposals = ReadOwnerMemories(owner)
                .Where(entry => entry.Status is not ("deleted" or "archived"))
                .GroupBy(entry => (entry.Category, Text: Normalize(entry.Text)))
                .Where(group => group.Key.Text.Length > 0 && group.Count() > 1)
                .OrderByDescending(group => group.Max(entry => entry.CreatedAt))
                .Take(20)
                .Select(group => new
                {
                    kind = "possible-duplicate",
                    evidence = group.Select(entry => new
                    {
                        entry.Id, entry.Text, entry.Status, entry.Source, entry.ConversationId, entry.CreatedAt,
                        version = entry.History.Length
                    }).ToArray()
                }).ToArray();
            return Results.Ok(proposals);
        }
    }

    private string MemoryIndexPath(string owner, string version) =>
        OwnerMemoryPath(owner) + "." + MemoryDigest(version) + ".vectors";
    private sealed record MemoryVector(string Digest, float[] Values);
    private sealed record MemoryIndex(string Version, Dictionary<Guid, MemoryVector> Vectors);
    public sealed record MemoryHit(Guid Id, string Text, string Category, string Status, string Source,
        Guid? ConversationId, DateTimeOffset? CreatedAt, double? Confidence, double Score);

    private static string MemoryDigest(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public async Task<MemoryHit[]> SearchMemoryAsync(string owner, string query, string? category, string? status,
        DateTimeOffset? after, DateTimeOffset? before, MemoryEmbeddings embeddings, CancellationToken cancellationToken)
    {
        MemoryEntry[] Candidates() => ReadOwnerMemories(owner)
            .Where(entry => entry.Status is not ("deleted" or "archived") && (category is null || category == entry.Category) &&
                (status is null || status == entry.Status) && (after is null || entry.CreatedAt >= after) &&
                (before is null || entry.CreatedAt <= before))
            .OrderByDescending(entry => entry.CreatedAt).ToArray();

        var version = embeddings.Version;
        MemoryEntry[] candidates;
        Dictionary<Guid, MemoryVector> vectors = [];
        lock (memoryGate)
        {
            candidates = Candidates();
            if (version is not null && Read<MemoryIndex>(MemoryIndexPath(owner, version)) is { } cached && cached.Version == version)
                vectors = cached.Vectors;
        }
        var normalized = Normalize(query);
        float[]? queryVector = null;
        if (version is not null && normalized.Length != 0 && candidates.Length > 0)
        {
            var missing = candidates.Where(entry => !vectors.TryGetValue(entry.Id, out var vector) ||
                vector.Digest != MemoryDigest(entry.Text)).Take(32).ToArray();
            var output = await embeddings.EmbedAsync([query, ..missing.Select(entry => entry.Text)], cancellationToken);
            if (output is not null)
            {
                queryVector = output[0];
                lock (memoryGate)
                {
                    var current = ReadOwnerMemories(owner).Where(entry => entry.Status is not ("deleted" or "archived"))
                        .ToDictionary(entry => entry.Id, entry => MemoryDigest(entry.Text));
                    vectors = Read<MemoryIndex>(MemoryIndexPath(owner, version)) is { } latest && latest.Version == version
                        ? latest.Vectors : [];
                    var pruned = false;
                    foreach (var (id, vector) in vectors.ToArray())
                        if (current.GetValueOrDefault(id) != vector.Digest || vector.Values.Length != queryVector.Length)
                            pruned |= vectors.Remove(id);
                    for (var i = 0; i < missing.Length; i++)
                    {
                        var digest = MemoryDigest(missing[i].Text);
                        if (current.GetValueOrDefault(missing[i].Id) == digest)
                            vectors[missing[i].Id] = new MemoryVector(digest, output[i + 1]);
                    }
                    if (missing.Length > 0 || pruned)
                        Write(MemoryIndexPath(owner, version), JsonSerializer.Serialize(new MemoryIndex(version, vectors), Json));
                }
            }
        }

        lock (memoryGate)
        {
            return Candidates().Select(entry =>
                {
                    var keyword = MemoryRanking.Keyword(normalized, Normalize(entry.Text));
                    var semantic = queryVector is not null && vectors.TryGetValue(entry.Id, out var vector) &&
                        vector.Digest == MemoryDigest(entry.Text) ? MemoryRanking.Cosine(queryVector, vector.Values) : 0;
                    var score = MemoryRanking.Combine(semantic, keyword);
                    return new MemoryHit(entry.Id, entry.Text, entry.Category, entry.Status, entry.Source,
                        entry.ConversationId, entry.CreatedAt, entry.Confidence, score);
                })
                .Where(hit => normalized.Length == 0 || hit.Score > 0)
                .OrderByDescending(hit => hit.Score).ThenByDescending(hit => hit.CreatedAt)
                .Take(100).ToArray();
        }
    }

    public IResult QueryMemory(string owner, Guid? id, string? query, bool history = false,
        string? category = null, string? status = null)
    {
        lock (memoryGate)
        {
            var ownerEntries = ReadOwnerMemories(owner);
            if (id is { } key)
            {
                var entry = ownerEntries.FirstOrDefault(item => item.Id == key && item.Status != "deleted");
                return entry is null ? Results.NotFound() :
                    history ? Results.Ok(entry.History) : Results.Ok(entry);
            }
            var entries = ownerEntries.Where(entry => entry.Status is not ("deleted" or "archived"));
            entries = entries.Where(entry => (category is null || entry.Category == category) &&
                (status is null || entry.Status == status));
            if (string.IsNullOrWhiteSpace(query)) return Results.Ok(entries.TakeLast(100).ToArray());
            var normalized = Normalize(query);
            return Results.Ok(entries.Select(entry => (Entry: entry, Score: MemoryRanking.Keyword(normalized, Normalize(entry.Text))))
                .Where(result => result.Score > 0)
                .OrderByDescending(result => result.Score)
                .ThenByDescending(result => result.Entry.CreatedAt)
                .Take(100).Select(result => result.Entry).ToArray());
        }
    }

    public IResult EditMemory(string owner, Guid id, int version, string? value, bool delete, bool complete = false)
    {
        lock (memoryGate)
        {
            var entries = ReadOwnerMemories(owner);
            var index = entries.FindIndex(entry => entry.Id == id && entry.Status != "deleted");
            if (index < 0) return Results.NotFound();
            var entry = entries[index];
            if (entry.CalendarRequestId is not null && entry.CalendarEventId is null) return Results.Conflict();
            if (entry.History.Length != version) return Results.Conflict();
            if (complete && (entry.Category != "task" || entry.Status != "open")) return Results.Conflict();
            if (!delete && !complete && value?.Trim() is not { Length: > 0 and <= 500 }) return Results.BadRequest();
            var status = delete ? "deleted" : complete ? "done" : entry.Status;
            entries[index] = entry with { Text = delete || complete ? entry.Text : value!.Trim(), Status = status,
                History = [..entry.History, new MemoryChange(delete ? "deleted" : complete ? "completed" : "edited",
                    DateTimeOffset.UtcNow, null, null, entry.Status, status, owner)] };
            WriteMemories(owner, entries);
            if (!ReadOwnerMemories(owner).Any(saved => saved.Id == id && saved.Status == status &&
                saved.History.Length == version + 1)) throw new IOException("Memory edit could not be verified.");
            return Results.NoContent();
        }
    }

    public object[] ListAgenticTasks(string owner)
    {
        lock (memoryGate)
            return ReadOwnerMemories(owner).Where(entry => entry.Category == "task" && entry.Status != "deleted")
                .OrderBy(entry => entry.Status == "open" ? 0 : 1).ThenByDescending(entry => entry.CreatedAt)
                .Take(100).Select(entry => (object)new
                {
                    entry.Id, entry.Text, entry.Status, version = entry.History.Length,
                    entry.CalendarEventId, entry.CalendarReminderAt, entry.CalendarRequestId,
                    history = entry.History.TakeLast(5).ToArray()
                }).ToArray();
    }

    public AgenticTask? GetAgenticTask(string owner, Guid id)
    {
        lock (memoryGate)
        {
            var entry = ReadOwnerMemories(owner).FirstOrDefault(item => item.Id == id && item.Category == "task");
            return entry is null ? null : new AgenticTask(entry.Id, entry.Text, entry.Status, entry.History.Length,
                entry.CalendarEventId, entry.CalendarReminderAt, entry.CalendarRequestId);
        }
    }

    public sealed record AgenticTask(Guid Id, string Text, string Status, int Version,
        string? CalendarEventId, DateTimeOffset? CalendarReminderAt, Guid? CalendarRequestId);

    public (int Status, int Version) ReserveTaskCalendarReminder(string owner, Guid id, int version, Guid requestId)
    {
        lock (memoryGate)
        {
            var entries = ReadOwnerMemories(owner);
            var index = entries.FindIndex(entry => entry.Id == id && entry.Category == "task");
            if (index < 0) return (404, 0);
            var entry = entries[index];
            if (entry.Status != "open" || entry.CalendarEventId is not null) return (409, entry.History.Length);
            if (entry.CalendarRequestId == requestId) return (200, entry.History.Length);
            if (requestId == Guid.Empty || entry.CalendarRequestId is not null || entry.History.Length != version)
                return (409, entry.History.Length);
            entries[index] = entry with
            {
                CalendarRequestId = requestId,
                History = [..entry.History, new MemoryChange("calendar-reminder-reserved", DateTimeOffset.UtcNow,
                    null, requestId, entry.Status, entry.Status, owner)]
            };
            WriteMemories(owner, entries);
            var saved = ReadOwnerMemories(owner).FirstOrDefault(item => item.Id == id);
            return saved?.CalendarRequestId == requestId && saved.History.Length == version + 1
                ? (200, saved.History.Length) : (500, 0);
        }
    }

    public IResult LinkTaskToCalendar(string owner, Guid id, int version, Guid requestId,
        string calendarEventId, DateTimeOffset reminderAt)
    {
        lock (memoryGate)
        {
            if (requestId == Guid.Empty || !calendarEventId.StartsWith("detot", StringComparison.Ordinal) ||
                calendarEventId.Length != 37) return Results.BadRequest();
            var entries = ReadOwnerMemories(owner);
            var index = entries.FindIndex(entry => entry.Id == id && entry.Category == "task" && entry.Status == "open");
            if (index < 0) return Results.NotFound();
            var entry = entries[index];
            if (entry.CalendarRequestId == requestId && entry.CalendarEventId == calendarEventId)
                return Results.Ok(new { id = entry.Id, linked = true, version = entry.History.Length });
            if (entry.History.Length != version || entry.Status != "open" || entry.CalendarRequestId != requestId ||
                entry.CalendarEventId is not null) return Results.Conflict();
            entries[index] = entry with
            {
                CalendarEventId = calendarEventId,
                CalendarReminderAt = reminderAt,
                CalendarRequestId = requestId,
                History = [..entry.History, new MemoryChange("calendar-reminder-linked", DateTimeOffset.UtcNow,
                    null, requestId, entry.Status, entry.Status, owner)]
            };
            WriteMemories(owner, entries);
            var saved = ReadOwnerMemories(owner).FirstOrDefault(item => item.Id == id);
            if (saved?.CalendarEventId != calendarEventId || saved.CalendarRequestId != requestId ||
                saved.History.Length != version + 1) return Results.Problem("Không xác nhận được liên kết nhắc lịch.", statusCode: 500);
            return Results.Ok(new { id, linked = true, version = saved.History.Length });
        }
    }

    public IResult MigrateMemory(string owner)
    {
        lock (memoryGate)
        {
            if (owner != RemoteAccess.OwnerId) return Results.StatusCode(403);
            if (File.Exists(OwnerMemoryPath(owner))) return Results.Conflict();
            var entries = Read<List<MemoryEntry>>(StructuredMemoryPath) ?? [];
            if (entries.Any(entry => entry.OwnerId is not null)) return Results.Conflict();
            if (entries.Count == 0 && Read<string>(WorkMemoryPath) is { Length: > 0 } legacy)
                entries.Add(new MemoryEntry(Guid.NewGuid(), "task", legacy, null, null, "open", "legacy", null, []));
            if (entries.Count == 0) return Results.NotFound();
            foreach (var path in new[] { StructuredMemoryPath, WorkMemoryPath }.Where(File.Exists))
            {
                var bytes = File.ReadAllBytes(path);
                var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                var backup = path + $".{digest}.backup";
                if (!File.Exists(backup))
                {
                    var staging = backup + $".{Guid.NewGuid():N}.tmp";
                    File.WriteAllBytes(staging, bytes);
                    File.Move(staging, backup);
                }
                if (!File.ReadAllBytes(backup).AsSpan().SequenceEqual(bytes))
                    return Results.Problem("Bản sao lưu không khớp dữ liệu gốc.", statusCode: 409);
            }
            WriteMemories(owner, entries.Select(entry => entry with { OwnerId = owner,
                History = entry.History.Select(change => change with { OwnerId = owner }).ToArray() }).ToList());
            return Results.NoContent();
        }
    }

    private sealed record MemoryEntry(Guid Id, string Category, string Text, Guid? ConversationId,
        DateTimeOffset? CreatedAt, string Status, string Source, double? Confidence, MemoryChange[] History,
        string? OwnerId = null, string? CalendarEventId = null, DateTimeOffset? CalendarReminderAt = null,
        Guid? CalendarRequestId = null);
    private sealed record MemoryChange(string Action, DateTimeOffset At, Guid? ConversationId, Guid? RequestId,
        string? BeforeStatus = null, string? AfterStatus = null, string? OwnerId = null);
    private sealed record MemoryChoice(DateTimeOffset ExpiresAt, MemoryCandidate[] Candidates);
    private sealed record MemoryCandidate(Guid Id, int Version);
    {
        lock (chatLearningGate) Write(ChatLearningPath, JsonSerializer.Serialize(ChatLearningProfile.Empty, Json));
    }

    public void RecordChatLearning(Guid observationId, string topic, string skill, string signal, double confidence)
    {
        if (observationId == Guid.Empty || !double.IsFinite(confidence) || confidence < 0.7 || confidence > 1 ||
            signal is not ("progress" or "practice")) return;
        topic = CleanLabel(topic, 48);
        skill = CleanLabel(skill, 64);
        if (topic.Length < 2 || skill.Length < 2) return;

        lock (chatLearningGate)
        {
            var profile = Read<ChatLearningProfile>(ChatLearningPath) ?? ChatLearningProfile.Empty;
            if (profile.ProcessedRequests.Contains(observationId)) return;
            var key = StableSkillKey(topic, skill);
            var skills = new Dictionary<string, ChatSkillProgress>(profile.Skills, StringComparer.Ordinal);
            var current = skills.GetValueOrDefault(key) ?? ChatSkillProgress.Empty(topic, skill);
            var now = DateTimeOffset.UtcNow;
            var updated = current with
            {
                ProgressSignals = current.ProgressSignals + (signal == "progress" ? 1 : 0),
                PracticeSignals = current.PracticeSignals + (signal == "practice" ? 1 : 0),
                Observations = current.Observations + 1,
                LastSeen = now,
                Recent = current.Recent.Append(new ChatSkillSignal(signal, now)).TakeLast(8).ToArray()
            };
            skills[key] = updated;
            if (skills.Count > 40)
            {
                var oldest = skills.OrderBy(pair => pair.Value.LastSeen).First().Key;
                skills.Remove(oldest);
            }
            Write(ChatLearningPath, JsonSerializer.Serialize(new ChatLearningProfile(skills,
                profile.ProcessedRequests.Append(observationId).TakeLast(256).ToArray()), Json));
        }
    }

    private static ChatLearningSnapshot BuildChatLearningMap(ChatLearningProfile profile)
    {
        var skills = profile.Skills.Values.OrderByDescending(item => item.LastSeen).Select(item =>
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-90);
            var recent = item.Recent.Where(signal => signal.At >= cutoff).TakeLast(5).ToArray();
            var recentPractice = recent.Count(signal => signal.Signal == "practice");
            var recentProgress = recent.Count(signal => signal.Signal == "progress");
            var confidence = item.LastSeen < cutoff ? "Cần lượt mới để cập nhật" :
                item.Observations < 3 ? "Đang quan sát thêm" :
                recent.Length < 3 ? "Ít tín hiệu gần đây" :
                item.Observations < 6 ? "Tín hiệu vừa phải" : "Có nhiều lần quan sát";
            var pattern = item.Observations < 3
                ? "Chưa đủ lượt để đề xuất điểm cần ôn."
                : item.LastSeen < cutoff
                    ? "Dữ liệu đã cũ; cần vài lượt mới để xem kỹ năng này hiện ra sao."
                    : recent.Length < 3
                        ? "Chưa có đủ tín hiệu gần đây để đề xuất điểm cần ôn."
                : recentPractice >= 3
                    ? "Gần đây chủ đề này thường cần thêm gợi ý; thử một bài tập ngắn để kiểm tra lại."
                    : recentProgress >= 3 && recentProgress > recentPractice
                        ? "Bạn thường tự giải thích hoặc áp dụng được ý này; thử bài toán có thêm một bước mới."
                        : "Có cả lúc tự áp dụng và lúc cần gợi ý; luyện thêm một tình huống mới để xác định bước tiếp theo.";
            return new ChatLearningTopic(item.Topic, item.Skill, item.Observations,
                item.ProgressSignals, item.PracticeSignals, confidence, pattern, item.LastSeen);
        }).ToArray();
        return new ChatLearningSnapshot(skills, skills.Length == 0
            ? "Khi bạn trao đổi về kiến thức hoặc code, Đệ Tốt sẽ âm thầm ghi nhận tín hiệu lặp lại để cá nhân hóa gợi ý."
            : $"Đã ghi nhận tín hiệu học ở {skills.Length} kỹ năng. Chỉ mẫu hình lặp lại mới được dùng để gợi ý ôn tập.");
    }

    private static string CleanLabel(string value, int maximum)
    {
        var cleaned = Regex.Replace(value.Trim(), @"[\r\n\t\p{Cc}]", " ");
        var compact = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        return compact[..Math.Min(compact.Length, maximum)];
    }

    private static string StableSkillKey(string topic, string skill)
    {
        var normalized = $"{topic.Trim().ToLowerInvariant()}|{skill.Trim().ToLowerInvariant()}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..24];
    }
    public void SaveCase(Guid id, string? state)
    {
        if (state is null) File.Delete(CasePath(id));
        else Write(CasePath(id), state);
    }

    // The simulator returns only authored fixture data. It never executes the command
    // mentioned by the user or passes a practice turn to an external tool.
    public Turn Process(Guid conversationId, string message, bool start)
    {
        var state = start ? null : ReadState(Case(conversationId));
        if (state is null)
        {
            var scenario = SelectScenario(message, Read<LearningProfile>(LearningPath) ?? LearningProfile.Empty);
            state = NewScenarioState(scenario);
            SaveState(conversationId, state);
            return new Turn(Opening(conversationId, scenario), true);
        }

        var active = Scenarios.First(scenario => scenario.Id == state.ScenarioId);
        var text = Normalize(message);
        if (ContainsAny(text, "doi ca", "chuyen sang ca", "tao ca moi", "switch scenario", "new incident"))
        {
            var replacement = SelectScenario(message);
            state = NewScenarioState(replacement);
            SaveState(conversationId, state);
            return new Turn(Opening(conversationId, replacement), true);
        }
        if (ContainsAny(text, "ket thuc", "dong ca", "dung ca", "end incident", "finish the case"))
        {
            var progress = RecordCompletion(active, state, goodHandoff: false, endedEarly: true);
            SaveCase(conversationId, null);
            return new Turn($"{Resolution(active, state, "Đã kết thúc ca theo yêu cầu.")}\n\n{progress}", true);
        }

        if (state.Stage == "handoff")
        {
            var (feedback, goodHandoff) = EvaluateHandoff(active, message);
            var progress = RecordCompletion(active, state, goodHandoff, endedEarly: false);
            SaveCase(conversationId, null);
            return new Turn($"{feedback}\n\n{Resolution(active, state, "Ca trực đã hoàn tất.")}\n\n{progress}", true);
        }

        if (IsDiagnosisAttempt(text))
            return Diagnose(conversationId, active, state, text);

        var check = active.Checks.FirstOrDefault(candidate => candidate.Triggers.Any(trigger => text.Contains(Normalize(trigger), StringComparison.Ordinal)));
        if (check is not null)
        {
            var evidence = state.Evidence.Contains(check.Id, StringComparer.Ordinal)
                ? state.Evidence
                : [.. state.Evidence, check.Id];
            state = state with { Evidence = evidence };
            SaveState(conversationId, state);
            return new Turn($"**Kết quả giả lập — {check.Label}:**\n{check.Result}\n\nĐã thu thập {evidence.Count} mục dữ kiện. Bạn muốn kiểm tra gì tiếp? Dữ liệu và lệnh trong ca này đều là giả lập.", true);
        }

        var options = string.Join(" · ", active.Checks.Select(item => item.Label));
        return new Turn($"Ca trực vẫn đang mở. Bạn có thể yêu cầu một bước kiểm tra ({options}), nêu giả thuyết nguyên nhân, hoặc nói **kết thúc ca** để xem lời giải. Mình chỉ trả dữ kiện giả lập; không chạy lệnh trên máy thật.", true);
    }

    private Turn Diagnose(Guid id, Scenario scenario, ScenarioState state, string normalized)
    {
        var correct = scenario.RootTerms.Any(term => normalized.Contains(Normalize(term), StringComparison.Ordinal));
        if (!correct)
        {
            var updatedWrong = state with { WrongDiagnoses = state.WrongDiagnoses + 1 };
            SaveState(id, updatedWrong);
            return new Turn("Giả thuyết này chưa khớp với dữ kiện đã thu thập. Hãy đối chiếu thêm kết quả rồi thử lại; nguyên nhân vẫn được giữ kín trong ca.", true);
        }

        if (state.Evidence.Count < state.RequiredEvidence)
            return new Turn($"Bạn có thể đang đi đúng hướng, nhưng chưa đủ bằng chứng để chốt. Ca này cần ít nhất {state.RequiredEvidence} kết quả kiểm tra; hãy thu thập thêm dữ kiện rồi nêu lại nguyên nhân.", true);

        var solvedState = state with { Stage = "handoff", Solved = true };
        SaveState(id, solvedState);
        return new Turn($"**Đúng — nguyên nhân đã được xác nhận:** {scenario.Cause}\n\n**Cách khắc phục trong tình huống giả lập:** {scenario.Fix}\n\nBạn đã dùng {state.Evidence.Count} mục dữ kiện. Hãy viết hai câu bàn giao bằng tiếng Anh: triệu chứng/nguyên nhân và bước xử lý/khôi phục.", true);
    }

    private static string Opening(Guid id, Scenario scenario) =>
        $"**Sự cố #INC-{id.ToString("N")[..8].ToUpperInvariant()} — {scenario.Title}**\n\n{scenario.Report}\n\nBạn đang trực IT. Bạn muốn kiểm tra gì trước? Có thể hỏi: {string.Join(", ", scenario.Checks.Select(item => item.Label))}.\n\nĐây là tình huống hoàn toàn giả lập; không có lệnh nào được chạy trên máy thật.";

    private static string Resolution(Scenario scenario, ScenarioState state, string heading) =>
        $"**{heading}**\nNguyên nhân: {scenario.Cause}\nKhắc phục đề xuất trong môi trường giả lập: {scenario.Fix}\nDữ kiện đã xem: {(state.Evidence.Count == 0 ? "chưa thu thập" : string.Join(", ", state.Evidence))}.";

    private static (string Feedback, bool Good) EvaluateHandoff(Scenario scenario, string handoff)
    {
        var terms = Normalize(handoff);
        var accurate = scenario.RootTerms.Any(term => terms.Contains(Normalize(term), StringComparison.Ordinal));
        var sentenceCount = Regex.Split(handoff.Trim(), @"(?<=[.!?])\s+").Count(sentence => sentence.Length > 0);
        if (accurate && sentenceCount >= 2)
            return ("**Bàn giao tốt:** bạn nêu đúng nguyên nhân và viết đủ hai câu. Khi dùng trong thực tế, hãy thêm người/nhóm phụ trách và trạng thái theo dõi nếu cần.", true);
        if (!accurate)
            return ($"**Góp ý bàn giao:** cần nói rõ nguyên nhân là “{scenario.Cause}” và trạng thái xử lý. Hãy giữ phần này như bản nháp trong ca giả lập.", false);
        return ("**Góp ý bàn giao:** nội dung đã đúng hướng; hãy tách thành hai câu rõ ràng: một câu mô tả sự cố/nguyên nhân, một câu nêu xử lý và kết quả.", false);
    }

    private void SaveState(Guid id, ScenarioState state) => SaveCase(id, JsonSerializer.Serialize(state, Json));

    private static ScenarioState? ReadState(string? value)
    {
        if (value is null) return null;
        try { return JsonSerializer.Deserialize<ScenarioState>(value, Json); }
        catch (JsonException) { return null; }
    }

    private T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        using var stored = JsonDocument.Parse(protector.Unprotect(File.ReadAllText(path)));
        // Earlier versions wrote structured profiles as JSON inside a JSON string.
        if (typeof(T) != typeof(string) && stored.RootElement.ValueKind == JsonValueKind.String)
            return JsonSerializer.Deserialize<T>(stored.RootElement.GetString()!, Json);
        return stored.RootElement.Deserialize<T>(Json);
    }

    private void Write(string path, string value)
    {
        Directory.CreateDirectory(directory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, protector.Protect(JsonSerializer.Serialize(value, Json)));
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }

    private ScenarioState NewScenarioState(Scenario scenario)
    {
        var profile = Read<LearningProfile>(LearningPath) ?? LearningProfile.Empty;
        var progress = profile.Domains.GetValueOrDefault(scenario.Id) ?? DomainProgress.Empty;
        var requiredEvidence = progress.Completed >= 2 && progress.CorrectDiagnoses >= 2 && progress.GoodHandoffs >= 2 ? 3 : 2;
        return new ScenarioState(scenario.Id, [], "investigate", requiredEvidence, 0, false);
    }

    private string RecordCompletion(Scenario scenario, ScenarioState state, bool goodHandoff, bool endedEarly)
    {
        var profile = Read<LearningProfile>(LearningPath) ?? LearningProfile.Empty;
        var domains = new Dictionary<string, DomainProgress>(profile.Domains, StringComparer.Ordinal);
        var previous = domains.GetValueOrDefault(scenario.Id) ?? DomainProgress.Empty;
        domains[scenario.Id] = previous with
        {
            Completed = previous.Completed + 1,
            CorrectDiagnoses = previous.CorrectDiagnoses + (state.Solved ? 1 : 0),
            WrongDiagnoses = previous.WrongDiagnoses + state.WrongDiagnoses,
            ChecksUsed = previous.ChecksUsed + state.Evidence.Count,
            GoodHandoffs = previous.GoodHandoffs + (goodHandoff ? 1 : 0),
            EarlyEnds = previous.EarlyEnds + (endedEarly ? 1 : 0),
        };
        var updated = new LearningProfile(profile.Completed + 1, scenario.Id, domains);
        Write(LearningPath, JsonSerializer.Serialize(updated, Json));
        var recommendation = SelectScenario("", updated);
        var level = NewScenarioState(recommendation).RequiredEvidence == 3 ? "nâng cao · 3 dữ kiện" : "cơ bản · 2 dữ kiện";
        return $"**Đã cập nhật hồ sơ học tập:** hoàn thành {updated.Completed} ca; chẩn đoán đúng {domains.Values.Sum(item => item.CorrectDiagnoses)}/{updated.Completed}; bàn giao đạt {domains.Values.Sum(item => item.GoodHandoffs)}/{updated.Completed}.\nChủ đề gợi ý tiếp theo: {recommendation.Title} ({level}). Hồ sơ chỉ lưu số liệu tổng hợp, không lưu tin nhắn, log hay mã nguồn.";
    }

    private string SummarizeLearning(LearningProfile profile)
    {
        if (profile.Completed == 0)
            return "Chưa có ca nào hoàn tất. Sau mỗi ca, Đệ Tốt chỉ lưu số liệu tiến độ tổng hợp để đề xuất chủ đề và độ khó tiếp theo.";

        var accurate = profile.Domains.Values.Sum(item => item.CorrectDiagnoses);
        var handoffs = profile.Domains.Values.Sum(item => item.GoodHandoffs);
        var next = SelectScenario("", profile);
        var level = NewScenarioState(next).RequiredEvidence == 3 ? "nâng cao · cần 3 dữ kiện" : "cơ bản · cần 2 dữ kiện";
        return $"Đã hoàn tất {profile.Completed} ca · chẩn đoán đúng {accurate}/{profile.Completed} · bàn giao đạt {handoffs}/{profile.Completed}. Chủ đề Đệ Tốt đề xuất: {next.Title} ({level}).";
    }

    private LearningSnapshot BuildLearningMap(LearningProfile profile)
    {
        var topics = Scenarios.Select(scenario =>
        {
            var progress = profile.Domains.GetValueOrDefault(scenario.Id) ?? DomainProgress.Empty;
            var sample = progress.Completed;
            var diagnosis = (progress.CorrectDiagnoses + 1d) / (sample + 2d);
            var handoff = (progress.GoodHandoffs + 1d) / (sample + 2d);
            var completion = (sample - progress.EarlyEnds + 1d) / (sample + 2d);
            var mastery = (int)Math.Round(100 * (diagnosis * 0.55 + handoff * 0.35 + completion * 0.10));
            var confidence = sample switch
            {
                0 => "Chưa luyện",
                < 3 => "Đang thu thập thêm dữ liệu",
                _ => "Có vài lượt luyện để so sánh"
            };
            var signal = sample < 3
                ? "Chưa đủ lượt để kết luận"
                : progress.WrongDiagnoses >= progress.CorrectDiagnoses + 2
                    ? "Thử kiểm chứng giả thuyết trước khi chốt nguyên nhân"
                    : progress.GoodHandoffs * 2 < sample
                        ? "Luyện tóm tắt nguyên nhân và bước xử lý khi bàn giao"
                        : progress.EarlyEnds * 2 >= sample
                            ? "Thử đi thêm một bước kiểm tra trước khi kết thúc ca"
                            : "Đang tiến bộ ổn định, có thể tăng độ khó";

            return new LearningTopic(scenario.Id, scenario.Title, sample, progress.CorrectDiagnoses,
                progress.WrongDiagnoses, progress.GoodHandoffs, progress.EarlyEnds, mastery, confidence, signal);
        }).Where(topic => topic.Completed > 0).OrderBy(topic => topic.Mastery).ToArray();

        var next = SelectScenario("", profile);
        var weak = topics.FirstOrDefault(topic => topic.Completed >= 3);
        var nextStep = weak is null
            ? $"Thử ca «{next.Title}» để Đệ Tốt có thêm dữ liệu cá nhân hóa."
            : $"Ca tiếp theo «{next.Title}». Chủ đề cần luyện thêm là «{weak.Title}»: {weak.Signal}.";
        return new LearningSnapshot(profile.Completed, SummarizeLearning(profile), nextStep, topics);
    }

    private static Scenario SelectScenario(string prompt, LearningProfile? profile = null)
    {
        var text = Normalize(prompt);
        var selected = Scenarios
            .Select(scenario => new
            {
                Scenario = scenario,
                Score = scenario.SelectTerms.Where(term => text.Contains(Normalize(term), StringComparison.Ordinal))
                    .Select(term => Normalize(term).Length).DefaultIfEmpty(0).Max()
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();
        if (selected is not null) return selected.Scenario;

        profile ??= LearningProfile.Empty;
        var unpracticed = Scenarios.FirstOrDefault(scenario =>
            scenario.Id != profile.LastScenarioId && !profile.Domains.ContainsKey(scenario.Id));
        if (unpracticed is not null) return unpracticed;

        return Scenarios
            .Where(scenario => scenario.Id != profile.LastScenarioId)
            .OrderBy(scenario => Mastery(scenario, profile))
            .FirstOrDefault()
            ?? Scenarios[0];
    }

    private static double Mastery(Scenario scenario, LearningProfile profile)
    {
        var progress = profile.Domains.GetValueOrDefault(scenario.Id) ?? DomainProgress.Empty;
        if (progress.Completed == 0) return 0;
        var diagnosis = (double)progress.CorrectDiagnoses / progress.Completed;
        var handoff = (double)progress.GoodHandoffs / progress.Completed;
        return (diagnosis + handoff) / 2;
    }

    private static bool IsDiagnosisAttempt(string text) => ContainsAny(text,
        "nguyen nhan", "toi nghi", "toi doan", "theo toi", "ket luan", "root cause", "cause is", "because", "chac la", "co the la");

    private static bool ContainsAny(string text, params string[] terms) =>
        terms.Any(term => text.Contains(Normalize(term), StringComparison.Ordinal));

    private static string Normalize(string value)
    {
        var decomposed = value.Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            result.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }
        return string.Join(' ', result.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed record Check(string Id, string Label, string[] Triggers, string Result);
    private sealed record Scenario(string Id, string Title, string Report, string Cause, string Fix,
        string[] SelectTerms, string[] RootTerms, Check[] Checks);
    private sealed record ScenarioState(string ScenarioId, List<string> Evidence, string Stage,
        int RequiredEvidence = 2, int WrongDiagnoses = 0, bool Solved = false);
    private sealed record DomainProgress(int Completed, int CorrectDiagnoses, int WrongDiagnoses,
        int ChecksUsed, int GoodHandoffs, int EarlyEnds)
    {
        public static DomainProgress Empty => new(0, 0, 0, 0, 0, 0);
    }
    private sealed record LearningProfile(int Completed, string? LastScenarioId, Dictionary<string, DomainProgress> Domains)
    {
        public static LearningProfile Empty => new(0, null, new Dictionary<string, DomainProgress>(StringComparer.Ordinal));
    }
    public sealed record NoteEdit(string? Notes);
    public sealed record Turn(string Reply, bool ToolUsed);
    public sealed record LearningSnapshot(int Completed, string Summary, string NextStep, LearningTopic[] Topics);
    public sealed record LearningTopic(string Id, string Title, int Completed, int CorrectDiagnoses,
        int WrongDiagnoses, int GoodHandoffs, int EarlyEnds, int Mastery, string Confidence, string Signal);
    public sealed record ChatLearningSnapshot(ChatLearningTopic[] Skills, string Summary);
    public sealed record ChatLearningTopic(string Topic, string Skill, int Observations, int ProgressSignals,
        int PracticeSignals, string Confidence, string Pattern, DateTimeOffset LastSeen);
    private sealed record ChatSkillSignal(string Signal, DateTimeOffset At);
    private sealed record ChatSkillProgress(string Topic, string Skill, int Observations, int ProgressSignals,
        int PracticeSignals, DateTimeOffset LastSeen, ChatSkillSignal[] Recent)
    {
        public static ChatSkillProgress Empty(string topic, string skill) => new(topic, skill, 0, 0, 0, DateTimeOffset.UtcNow, []);
    }
    private sealed record ChatLearningProfile(Dictionary<string, ChatSkillProgress> Skills, Guid[] ProcessedRequests)
    {
        public static ChatLearningProfile Empty => new(new Dictionary<string, ChatSkillProgress>(StringComparer.Ordinal), []);
    }

    private static readonly Scenario[] Scenarios =
    [
        new("printer", "Không in được qua máy in mạng",
            "Nhân viên phòng kế toán báo không in được tài liệu. Các máy khác vẫn in bình thường.",
            "Dịch vụ Print Spooler trên máy trạm đang dừng.",
            "Trong kịch bản, khởi động lại Print Spooler và xác nhận hàng đợi in xử lý xong. Đây chỉ là hướng dẫn mô phỏng, không thực hiện thao tác.",
            ["helpdesk", "máy in", "printer", "không in"], ["spooler", "print spooler", "dịch vụ in"],
            [
                new("ip", "ipconfig", ["ipconfig", "địa chỉ ip", "ip máy"], "Máy trạm nhận IP 10.20.4.37/24, gateway 10.20.4.1. Máy cùng VLAN và không có dấu hiệu trùng IP."),
                new("ping", "ping máy in", ["ping", "kết nối máy in", "reach máy in"], "Ping 10.20.4.80 thành công, độ trễ ổn định 2 ms; máy in phản hồi bình thường."),
                new("spooler", "trạng thái Print Spooler", ["spooler", "dịch vụ in", "hàng đợi in", "print service"], "Trên máy trạm: Print Spooler = Stopped. Hàng đợi còn 3 job; dịch vụ trên máy khác vẫn Running."),
                new("driver", "driver máy in", ["driver", "trình điều khiển"], "Driver đúng phiên bản và máy in được ánh xạ đúng. Chưa có dấu hiệu lỗi driver."),
                new("log", "Event Viewer", ["event viewer", "log", "nhật ký", "event id"], "System log giả lập ghi nhận Service Control Manager Event 7031: Print Spooler terminated unexpectedly lúc 09:12 trên máy trạm."),
            ]),
        new("windows-dns", "Máy chủ nội bộ không phân giải được tên miền",
            "Một nhóm người dùng không mở được ứng dụng nội bộ bằng tên miền; truy cập bằng IP vẫn được. Các dịch vụ khác trên máy chủ còn hoạt động.",
            "DNS forwarder trên máy chủ đang trỏ tới địa chỉ cũ không còn phản hồi.",
            "Trong kịch bản, cập nhật forwarder tới DNS dự phòng đã được phê duyệt rồi kiểm tra phân giải lại. Không thay đổi cấu hình máy thật.",
            ["windows server", "dns", "máy chủ", "server"], ["dns forwarder", "forwarder", "máy chủ dns", "dns cũ"],
            [
                new("ip", "ipconfig /all", ["ipconfig", "địa chỉ ip", "dns client"], "Máy trạm có IP và gateway hợp lệ. DNS được cấp là 10.20.0.10."),
                new("lookup", "nslookup", ["nslookup", "phân giải", "resolve"], "Truy vấn app.intra tới DNS 10.20.0.10 hết thời gian chờ; truy vấn trực tiếp tới DNS dự phòng 10.20.0.11 trả về 10.20.8.25."),
                new("service", "dịch vụ DNS Server", ["dịch vụ dns", "dns service", "dns server status"], "DNS Server service đang Running và các truy vấn nội bộ đã cache vẫn trả lời."),
                new("log", "DNS Server log", ["event viewer", "log", "nhật ký"], "DNS log giả lập báo forwarder 192.0.2.53 không phản hồi qua nhiều lần thử."),
            ]),
        new("sql-pool", "API chậm khi tải cao",
            "API quản lý nhân viên bắt đầu trả 503 vào giờ cao điểm. Health endpoint của ứng dụng vẫn phản hồi, nhưng request cần truy cập dữ liệu bị chậm.",
            "Connection pool tới SQL Server bị cạn do các request không giải phóng kết nối đúng cách.",
            "Trong kịch bản, sửa vòng đời connection để luôn dispose, sau đó theo dõi pool usage và chạy lại tải thử. Không kết nối database thật.",
            ["sql server", "sql", "database", "cơ sở dữ liệu", "503"], ["connection pool", "pool cạn", "hết kết nối", "leak connection", "rò rỉ kết nối"],
            [
                new("health", "health endpoint", ["health", "healthcheck", "tình trạng api"], "Ứng dụng = Healthy; dependency SQL = Degraded. Các endpoint không truy vấn dữ liệu vẫn hoạt động."),
                new("logs", "log ứng dụng", ["log", "nhật ký", "exception", "lỗi"], "Log giả lập lặp lại Timeout while acquiring a connection from pool; không có lỗi đăng nhập SQL."),
                new("pool", "connection pool metrics", ["pool", "connection", "kết nối database", "sql connection"], "Active connections = 100/100, wait queue tăng; số connection không giảm sau khi request hoàn tất."),
                new("deploy", "lịch sử triển khai", ["deploy", "triển khai", "release", "phiên bản"], "Không có bản deploy trong 24 giờ. Lượng request tăng khoảng 4 lần từ 10:00."),
            ]),
        new("api-token", "Tích hợp API trả về 401",
            "Một dịch vụ nội bộ không lấy được hồ sơ nhân viên từ API đối tác. DNS và kết nối mạng bình thường; lỗi bắt đầu sau một khoảng thời gian chạy ổn định.",
            "Access token của tích hợp đã hết hạn và tiến trình không làm mới token trước khi gửi request.",
            "Trong kịch bản, sửa luồng refresh token và xác nhận lại bằng credential test. Không gọi API hay sử dụng credential thật.",
            ["api", "backend", "401", "token", "endpoint"], ["token hết hạn", "expired token", "access token", "jwt hết hạn", "không refresh token"],
            [
                new("health", "health endpoint", ["health", "healthcheck"], "Service Healthy; endpoint status 401, không phải lỗi mạng hoặc DNS."),
                new("logs", "log request", ["log", "nhật ký", "response", "header"], "Log giả lập ghi HTTP 401 invalid_token; request tới được host và TLS handshake thành công."),
                new("token", "thời hạn token", ["token", "jwt", "authorization", "xác thực"], "Token hết hạn lúc 08:45. Tiến trình vẫn dùng token cũ; refresh job báo chưa được đăng ký."),
                new("deploy", "cấu hình refresh", ["refresh", "cấu hình", "config"], "Cấu hình giả lập có client ID test nhưng thiếu lịch refresh token; không có secret nào được hiển thị."),
            ]),
        new("docker-env", "Container khởi động rồi thoát",
            "Sau lần triển khai gần nhất, một container API liên tục restart. Các container khác trong cùng môi trường vẫn chạy.",
            "Container thiếu biến môi trường ConnectionStrings__Main nên ứng dụng thoát khi khởi tạo.",
            "Trong kịch bản, bổ sung biến môi trường từ secret store của môi trường test rồi xác nhận health check. Không chạy Docker hoặc đọc secret thật.",
            ["docker", "container", "compose", "crashloop"], ["connectionstrings", "biến môi trường", "environment variable", "thiếu env", "missing env"],
            [
                new("status", "trạng thái container", ["docker ps", "container status", "restart", "trạng thái"], "Container API có restart count = 7; các container database và proxy vẫn healthy."),
                new("logs", "docker logs", ["docker logs", "log", "nhật ký", "stderr"], "Log giả lập: Configuration key 'ConnectionStrings:Main' was not found; process exited with code 1."),
                new("env", "danh sách tên biến môi trường", ["env", "environment", "biến môi trường", "docker inspect"], "Danh sách tên biến cho thấy thiếu ConnectionStrings__Main. Giá trị secret không được lưu hoặc hiển thị."),
                new("deploy", "deployment manifest", ["compose", "manifest", "yaml", "deploy", "triển khai"], "Manifest giả lập của service API không khai báo ConnectionStrings__Main; revision vừa deploy thay đổi phần env."),
            ]),
        new("security-lockout", "Tài khoản dịch vụ bị khóa",
            "Một tác vụ đồng bộ nội bộ ngừng chạy. Bảng điều khiển ghi nhận nhiều lần đăng nhập thất bại từ máy chủ tác vụ; chưa có dấu hiệu truy cập thành công bất thường.",
            "Tác vụ đang dùng credential cũ sau khi tài khoản dịch vụ được xoay vòng mật khẩu, dẫn tới khóa tài khoản.",
            "Trong kịch bản, xác minh chủ sở hữu và cập nhật credential trong secret store được phê duyệt; sau đó kiểm tra audit log. Không đăng nhập, đổi mật khẩu hay sửa quyền thật.",
            ["security", "bảo mật", "tài khoản", "đăng nhập", "lockout"], ["credential cũ", "mật khẩu cũ", "tài khoản dịch vụ", "service account", "mật khẩu đã đổi"],
            [
                new("events", "authentication events", ["event", "log", "đăng nhập", "authentication", "audit"], "Audit log giả lập ghi các lần đăng nhập thất bại từ đúng máy chủ tác vụ trong 5 phút; không có đăng nhập thành công lạ."),
                new("account", "trạng thái tài khoản", ["tài khoản", "account status", "lockout", "bị khóa"], "Tài khoản svc-sync đang Locked sau 10 lần thử sai; MFA và nhóm quyền không thay đổi."),
                new("rotation", "lịch sử credential", ["credential", "mật khẩu", "password", "rotate", "xoay vòng"], "Bản ghi giả lập cho thấy credential được xoay vòng lúc 08:00; tác vụ vẫn tham chiếu revision cũ."),
                new("owner", "chủ sở hữu tác vụ", ["owner", "chủ sở hữu", "tác vụ", "scheduled task"], "Tác vụ thuộc nhóm IT Operations; không có thay đổi chủ sở hữu hoặc quyền truy cập gần đây."),
            ]),
    ];
}
