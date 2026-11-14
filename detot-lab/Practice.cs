using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

sealed class Practice(IDataProtector protector, string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string ProfilePath => Path.Combine(directory, "notes.bin");
    private string LearningPath => Path.Combine(directory, "learning.bin");
    private string CasePath(Guid id) => Path.Combine(directory, $"{id:N}.bin");

    public string Notes => Read<string>(ProfilePath) ?? "";
    public string LearningSummary => SummarizeLearning(Read<LearningProfile>(LearningPath) ?? LearningProfile.Empty);
    public string? Case(Guid id) => Read<string>(CasePath(id));
    public void SaveNotes(string notes) => Write(ProfilePath, notes.Trim());
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

    private T? Read<T>(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<T>(protector.Unprotect(File.ReadAllText(path)), Json)
        : default;

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
