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
                var recent = entries.Where(entry => entry.Status != "deleted").TakeLast(10).Reverse();
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

    private void WriteMemories(string owner, List<MemoryEntry> entries) =>
        Write(OwnerMemoryPath(owner), JsonSerializer.Serialize(entries, Json));

    public IResult QueryMemory(string owner, Guid? id, string? query, bool history = false)
    {
        lock (memoryGate)
        {
            var entries = ReadOwnerMemories(owner).Where(entry => entry.Status != "deleted");
            if (id is { } key)
            {
                var entry = entries.FirstOrDefault(item => item.Id == key);
                return entry is null ? Results.NotFound() :
                    history ? Results.Ok(entry.History) : Results.Ok(entry);
            }
            return Results.Ok(entries.Where(entry => string.IsNullOrWhiteSpace(query) ||
                Normalize(entry.Text).Contains(Normalize(query), StringComparison.Ordinal)).TakeLast(100).ToArray());
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
        DateTimeOffset? CreatedAt, string Status, string Source, double? Confidence, MemoryChange[] History, string? OwnerId = null);
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
