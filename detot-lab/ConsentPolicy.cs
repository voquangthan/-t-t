using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

public sealed class ConsentPolicy
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ConsentOption[] SupportedOptions =
    [
        new("memory_core", "Memory Core", "dreaming", "Dreaming", "local_only", "Embedding local", "Cho phép so khớp ký ức trên máy này."),
        new("memory_core", "Memory Core", "dreaming", "Dreaming", "DeepSeek", "Phân tích bằng DeepSeek", "Cho phép gửi các cặp ký ức phù hợp tới DeepSeek để tạo đề xuất."),
        new("memory_core", "Memory Core", "proactive_analysis", "Phân tích chủ động", "local_only", "Dùng Memory để tạo gợi ý", "Quyền dự kiến; hiện chưa có bộ phân tích chủ động nào đọc Memory."),
        new("memory_core", "Memory Core", "proactive_analysis", "Phân tích chủ động", "DeepSeek", "Phân tích Memory chủ động", "Quyền dự kiến; hiện chưa có bộ phân tích chủ động nào đọc hoặc gửi Memory."),
        new("memory_core", "Memory Core", "direct_request", "Yêu cầu trực tiếp", "DeepSeek", "Dùng ký ức trong DeepSeek", "Cho phép gửi một ký ức phù hợp tới DeepSeek để soạn câu trả lời. Khi tắt, kết quả ký ức được trả nguyên văn từ Backend."),
        new("memory_core", "Memory Core", "direct_request", "Yêu cầu trực tiếp", "local_only", "Tìm ký ức cục bộ", "Cho phép truy xuất Memory trên thiết bị khi bạn yêu cầu trực tiếp."),
        new("memory_core", "Memory Core", "direct_request", "Yêu cầu trực tiếp", "external_service", "Sao lưu Memory lên Drive", "Cho phép gửi bản sao Memory đã mã hóa tới Google Drive khi bạn trực tiếp tạo backup."),
        new("chat_history", "Nội dung Chat", "skill_learning", "Học kỹ năng", "DeepSeek", "Học kỹ năng từ Chat", "Cho phép phân tích lượt Chat nhằm cập nhật hồ sơ học tập tổng hợp."),
        new("chat_history", "Nội dung Chat", "skill_learning", "Học kỹ năng", "local_only", "Lưu phiên cho AutoSkill", "Quyền dự kiến; AutoSkill capture, archive và extraction hiện đang tắt."),
        new("chat_history", "Nội dung Chat", "direct_request", "Yêu cầu trực tiếp", "DeepSeek", "Dùng hội thoại trước trong Chat", "Cho phép gửi các lượt hội thoại trước trong yêu cầu hiện tại tới DeepSeek."),
        new("chat_history", "Nội dung Chat", "direct_request", "Yêu cầu trực tiếp", "local_only", "Lưu lịch sử Chat cục bộ", "Cho phép xem và lưu lịch sử Chat trên máy này."),
        new("computer_use", "Computer Use", "direct_request", "Chế độ thông minh — UI đã lọc", "OpenClaw Gateway", "openclaw/default · nhà cung cấp theo cấu hình", "Khi tắt, các hành động xác định như mở ứng dụng được duyệt vẫn chạy cục bộ và không gửi nhãn UI cho model. Khi bật, tác vụ nhiều bước có thể gửi cấu trúc UI và nhãn mục tiêu đã lọc qua route `openclaw/default`; nội dung, giá trị trường, ảnh, tiêu đề và URL vẫn bị giữ lại. OpenClaw chọn nhà cung cấp theo cấu hình Gateway đang hoạt động; quyền này không xác nhận riêng DeepSeek. Mặc định tắt."),
        new("learning_profile", "Hồ sơ học tập", "direct_request", "Yêu cầu trực tiếp", "DeepSeek", "Dùng hồ sơ trong Chat", "Cho phép đưa hồ sơ học tập tổng hợp vào câu trả lời Chat."),
        new("learning_profile", "Hồ sơ học tập", "direct_request", "Yêu cầu trực tiếp", "local_only", "Xem hồ sơ học tập cục bộ", "Cho phép xem hồ sơ học tập trên thiết bị."),
        new("learning_notes", "Ghi chú học tập", "direct_request", "Yêu cầu trực tiếp", "DeepSeek", "Dùng ghi chú khi luyện nhanh", "Cho phép đưa ghi chú học tập vào lượt luyện Chat."),
        new("learning_notes", "Ghi chú học tập", "direct_request", "Yêu cầu trực tiếp", "local_only", "Đọc ghi chú học tập cục bộ", "Cho phép xem ghi chú luyện tập trên thiết bị."),
        new("personal_notes", "Ghi chú cá nhân", "direct_request", "Yêu cầu trực tiếp", "local_only", "Đọc ghi chú cục bộ", "Cho phép truy xuất ghi chú cá nhân trên thiết bị khi bạn yêu cầu trực tiếp."),
        new("task_memory", "Công việc trong Memory", "direct_request", "Yêu cầu trực tiếp", "DeepSeek", "Dùng công việc trong Chat", "Cho phép đưa việc đang mở vào yêu cầu tóm tắt hôm nay."),
        new("task_memory", "Công việc trong Memory", "direct_request", "Yêu cầu trực tiếp", "local_only", "Đọc công việc cục bộ", "Cho phép truy xuất công việc trong Memory khi bạn yêu cầu trực tiếp."),
        new("calendar", "Google Calendar", "direct_request", "Yêu cầu trực tiếp", "local_only", "Gợi ý dựa trên lịch", "Cho phép Chat đọc lịch để trả lời yêu cầu gợi ý thời gian rảnh, xử lý cục bộ trong trình duyệt."),
        new("calendar", "Google Calendar", "proactive_analysis", "Phân tích chủ động", "local_only", "Dùng lịch để tạo gợi ý", "Quyền dự kiến; hiện chưa có bộ phân tích lịch nền nào hoạt động."),
        new("calendar", "Google Calendar", "proactive_analysis", "Phân tích chủ động", "DeepSeek", "Gửi lịch cho phân tích nền", "Quyền dự kiến; hiện chưa có worker nào đọc hoặc gửi lịch."),
        new("calendar", "Google Calendar", "direct_request", "Yêu cầu trực tiếp", "DeepSeek", "Dùng lịch trong Chat", "Cho phép đưa sự kiện lịch vào yêu cầu tóm tắt hôm nay."),
        new("calendar", "Google Calendar", "direct_request", "Yêu cầu trực tiếp", "external_service", "Thay đổi Google Calendar", "Cho phép gửi yêu cầu tạo, sửa hoặc xóa sự kiện tới Google Calendar."),
        new("personal_notes", "Ghi chú cá nhân", "direct_request", "Yêu cầu trực tiếp", "DeepSeek", "Dùng ghi chú trong Chat", "Cho phép đưa ghi chú gần đây vào yêu cầu tóm tắt hôm nay."),
        new("personal_notes", "Ghi chú cá nhân", "proactive_analysis", "Phân tích chủ động", "local_only", "Phân tích ghi chú cục bộ", "Quyền dự kiến; chưa có bộ phân tích nền nào đọc ghi chú."),
        new("personal_notes", "Ghi chú cá nhân", "proactive_analysis", "Phân tích chủ động", "DeepSeek", "Gửi ghi chú cho phân tích nền", "Quyền dự kiến; chưa có bộ phân tích nền nào đọc hoặc gửi ghi chú."),
        new("gmail", "Gmail", "direct_request", "Yêu cầu trực tiếp", "DeepSeek", "Dùng Gmail trong Chat", "Cho phép gửi nội dung thư tới DeepSeek khi có luồng tích hợp phù hợp."),
        new("gmail", "Gmail", "direct_request", "Yêu cầu trực tiếp", "local_only", "Đọc Gmail cục bộ", "Cho phép tìm thư và xem bản xem trước trong giao diện Đệ Tốt."),
        new("gmail", "Gmail", "proactive_analysis", "Phân tích chủ động", "local_only", "Phân tích Gmail cục bộ", "Quyền dự kiến; hiện chưa có worker nào quét Gmail."),
        new("gmail", "Gmail", "proactive_analysis", "Phân tích chủ động", "DeepSeek", "Gửi Gmail cho phân tích nền", "Quyền dự kiến; hiện chưa có worker nào quét hoặc gửi Gmail."),
        new("google_drive", "Google Drive", "direct_request", "Yêu cầu trực tiếp", "local_only", "Đọc Drive trong Nguồn học", "Cho phép truy cập danh sách thư mục nguồn học đã chọn, chỉ dùng cục bộ."),
        new("google_drive", "Google Drive", "direct_request", "Yêu cầu trực tiếp", "DeepSeek", "Dùng Drive trong Chat", "Cho phép gửi nội dung Drive tới DeepSeek khi có luồng tích hợp phù hợp."),
        new("google_drive", "Google Drive", "proactive_analysis", "Phân tích chủ động", "local_only", "Phân tích Drive cục bộ", "Quyền dự kiến; hiện chưa có worker nào quét Drive để tạo gợi ý."),
        new("google_drive", "Google Drive", "proactive_analysis", "Phân tích chủ động", "DeepSeek", "Gửi Drive cho phân tích nền", "Quyền dự kiến; hiện chưa có worker nào quét hoặc gửi nội dung Drive."),
        new("uploaded_files", "Tệp người dùng gửi", "direct_request", "Yêu cầu trực tiếp", "DeepSeek", "Gửi nội dung tệp tới model", "Cho phép gửi văn bản hoặc ảnh từ tệp đã chọn tới model Chat."),
        new("uploaded_files", "Tệp người dùng gửi", "direct_request", "Yêu cầu trực tiếp", "local_only", "Đọc tệp cục bộ", "Cho phép Backend đọc nội dung tệp bạn chọn trong lượt hiện tại."),
        new("public_web", "Reader", "direct_request", "Yêu cầu trực tiếp", "DeepSeek", "Gửi nội dung Reader tới model", "Cho phép gửi nội dung URL công khai tới model Chat."),
        new("public_web", "Tìm kiếm", "direct_request", "Yêu cầu trực tiếp", "external_service", "Gửi truy vấn tìm kiếm", "Cho phép gửi truy vấn không chứa dữ liệu cá nhân tới dịch vụ tìm kiếm ngoài."),
        new("trusted_learning", "Nguồn học đã duyệt", "skill_learning", "Học kỹ năng", "external_service", "Đồng bộ nguồn học nền", "Cho phép worker đồng bộ đúng nguồn học đã được duyệt."),
        new("trusted_learning", "Nguồn học đã duyệt", "direct_request", "Yêu cầu trực tiếp", "DeepSeek", "Dùng nguồn học trong Chat", "Cho phép đưa trích đoạn từ nguồn học đã duyệt vào câu trả lời Chat."),
        new("trusted_learning", "Nguồn học đã duyệt", "direct_request", "Yêu cầu trực tiếp", "local_only", "Xem nội dung nguồn học cục bộ", "Cho phép tìm và xem nội dung đã lưu trên thiết bị."),
        new("public_weather", "Thời tiết", "proactive_analysis", "Phân tích chủ động", "local_only", "Lưu vị trí và dự báo trên máy", "Cho phép lưu thành phố đã chọn, dự báo đã lấy và tối đa hai gợi ý thời tiết trong kho mã hóa cục bộ."),
        new("public_weather", "Thời tiết", "proactive_analysis", "Phân tích chủ động", "external_service", "Gửi thành phố tới Open-Meteo", "Cho phép gửi thành phố đã nhập tới Open-Meteo để tìm tọa độ và lấy dự báo. Tọa độ không được gửi tới DeepSeek."),
        new("public_weather", "Thời tiết", "proactive_analysis", "Phân tích chủ động", "DeepSeek", "Phân tích dự báo bằng DeepSeek", "Cho phép gửi chỉ số dự báo theo giờ (không có tọa độ hoặc tên thành phố) tới DeepSeek để chọn tối đa hai gợi ý."),
        new("google_weather_alerts", "Cảnh báo chính thức NCHMF qua Google", "proactive_analysis", "Phân tích chủ động", "external_service", "Tra cứu cảnh báo theo địa điểm đã chọn", "Cho phép gửi tọa độ Open-Meteo đã trả về cho thành phố/khu vực bạn tự chọn (không phải GPS hiện tại) tới Google Public Alerts để lấy cảnh báo chính thức từ NCHMF. Không gửi nội dung này tới DeepSeek; kết quả chỉ giữ tạm trong RAM tối đa 5 phút, không ghi vào file."),
        new("proactive_feedback", "Phản hồi gợi ý", "skill_learning", "Học từ phản hồi", "local_only", "Lưu phản hồi thời tiết cục bộ", "Cho phép đếm phản hồi hữu ích/bỏ qua và lưu gợi ý đã bỏ qua trên máy. Dữ liệu không gửi tới DeepSeek."),
        new("device_status", "Trạng thái thiết bị", "proactive_analysis", "Phân tích chủ động", "local_only", "Kiểm tra thiết bị cục bộ", "Chưa có bộ quan sát thiết bị; cấp quyền này chưa kích hoạt việc quét máy."),
    ];

    private readonly object gate = new();
    private readonly string directory;
    private readonly IDataProtector protector;
    private readonly Dictionary<string, OwnerState> owners = new(StringComparer.Ordinal);

    public ConsentPolicy(string directory, IDataProtectionProvider protection)
    {
        this.directory = directory;
        protector = protection.CreateProtector("Detot.ConsentPolicy.v1");
    }

    public ConsentSnapshot Get(string owner)
    {
        lock (gate)
        {
            var state = GetState(owner);
            var grants = SupportedOptions.Select(option =>
            {
                var current = state.Grants.FirstOrDefault(item => Same(item, option));
                return new ConsentGrantView(option.Source, option.Purpose, option.Destination,
                    current?.Allowed ?? false, current?.ChangedAt);
            }).ToArray();
            return new ConsentSnapshot(SupportedOptions, grants, state.Audit.TakeLast(200).Reverse().ToArray());
        }
    }

    public DateTimeOffset? GetGrantChangedAt(string owner, string source, string purpose, string destination)
    {
        lock (gate)
        {
            var state = GetState(owner);
            return state.Grants.FirstOrDefault(grant => grant.Source == source && grant.Purpose == purpose &&
                grant.Destination == destination)?.ChangedAt;
        }
    }

    public ConsentChange Set(string owner, string source, string purpose, string destination, bool allowed,
        string actor, Guid? requestId = null)
    {
        lock (gate)
        {
            var option = FindOption(source, purpose, destination)
                ?? throw new ArgumentException("Quyền này không được hỗ trợ.");
            var state = GetState(owner);
            var now = DateTimeOffset.UtcNow;
            var previous = state.Grants.FirstOrDefault(item => Same(item, option));
            var changed = previous?.Allowed != allowed;
            var revoked = previous?.Allowed == true && !allowed;
            if (changed)
            {
                var grants = state.Grants.Where(item => !Same(item, option)).Append(
                    new ConsentGrant(source, purpose, destination, allowed, now)).ToArray();
                state = state with { Grants = grants, Revision = state.Revision + 1 };
                // Cancel every in-flight operation for this owner before acknowledging a policy change.
                state.Epoch.Cancel();
                state = state with { Epoch = new CancellationTokenSource() };
            }
            state = AddAudit(state, new ConsentAudit(source, purpose, destination,
                allowed ? "granted" : "revoked", now, actor, requestId, state.Revision));
            owners[owner] = state;
            Save(owner, state);
            return new ConsentChange(changed, allowed, now, state.Revision, revoked);
        }
    }

    public bool IsAllowed(string owner, string source, string purpose, string destination)
    {
        lock (gate) return IsAllowed(GetState(owner), source, purpose, destination);
    }

    public bool Check(string owner, IEnumerable<ConsentRequirement> requirements, string actor,
        Guid? requestId, out ConsentRequirement[] denied)
    {
        lock (gate)
        {
            var state = GetState(owner);
            var checkedItems = requirements.Distinct().ToArray();
            denied = checkedItems.Where(item => !IsAllowed(state, item.Source, item.Purpose, item.Destination)).ToArray();
            var now = DateTimeOffset.UtcNow;
            foreach (var item in checkedItems)
                state = AddAudit(state, new ConsentAudit(item.Source, item.Purpose, item.Destination,
                    FindOption(item.Source, item.Purpose, item.Destination) is null || denied.Contains(item) ? "denied" : "allowed",
                    now, actor, requestId, state.Revision));
            owners[owner] = state;
            Save(owner, state);
            return denied.Length == 0;
        }
    }

    public void RecordExplicitAction(string owner, ConsentRequirement requirement, string actor, Guid? requestId)
    {
        lock (gate)
        {
            var state = GetState(owner);
            state = AddAudit(state, new ConsentAudit(requirement.Source, requirement.Purpose, requirement.Destination,
                "explicit_action", DateTimeOffset.UtcNow, actor, requestId, state.Revision));
            if (!ReferenceEquals(state, owners[owner]))
            {
                Save(owner, state);
                owners[owner] = state;
            }
        }
    }

    public ConsentLease? TryBegin(string owner, IEnumerable<ConsentRequirement> requirements, string actor = "worker")
    {
        lock (gate)
        {
            var state = GetState(owner);
            var required = requirements.Distinct().ToArray();
            if (required.Length == 0 || required.Any(item => !IsAllowed(state, item.Source, item.Purpose, item.Destination)))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var item in required.Where(item => !IsAllowed(state, item.Source, item.Purpose, item.Destination)))
                    state = AddAudit(state, new ConsentAudit(item.Source, item.Purpose, item.Destination,
                        "denied", now, actor, null, state.Revision));
                Save(owner, state);
                owners[owner] = state;
                return null;
            }
            var lease = new ConsentLease(owner, required, state.Revision, state.Epoch);
            foreach (var item in required)
                state = AddAudit(state, new ConsentAudit(item.Source, item.Purpose, item.Destination,
                    "allowed", DateTimeOffset.UtcNow, actor, null, state.Revision));
            if (!ReferenceEquals(state, owners[owner]))
            {
                Save(owner, state);
                owners[owner] = state;
            }
            return lease;
        }
    }

    public bool IsCurrent(ConsentLease lease)
    {
        lock (gate)
        {
            var state = GetState(lease.Owner);
            return ReferenceEquals(state.Epoch, lease.Epoch) && !lease.Epoch.IsCancellationRequested &&
                lease.Revision == state.Revision && lease.Requirements.All(item =>
                    IsAllowed(state, item.Source, item.Purpose, item.Destination));
        }
    }

    public bool ExecuteIfCurrent(ConsentLease lease, Func<bool> action)
    {
        lock (gate)
        {
            var state = GetState(lease.Owner);
            if (!ReferenceEquals(state.Epoch, lease.Epoch) || lease.Epoch.IsCancellationRequested ||
                lease.Revision != state.Revision || lease.Requirements.Any(item =>
                    !IsAllowed(state, item.Source, item.Purpose, item.Destination))) return false;
            return action();
        }
    }

    private static bool IsAllowed(OwnerState state, string source, string purpose, string destination) =>
        state.Grants.Any(item => item.Source == source && item.Purpose == purpose &&
            item.Destination == destination && item.Allowed);

    private static bool Same(ConsentGrant grant, ConsentOption option) =>
        grant.Source == option.Source && grant.Purpose == option.Purpose && grant.Destination == option.Destination;
    private static bool Same(ConsentAudit audit, ConsentOption option) =>
        audit.Source == option.Source && audit.Purpose == option.Purpose && audit.Destination == option.Destination;

    private static ConsentOption? FindOption(string source, string purpose, string destination) =>
        SupportedOptions.FirstOrDefault(item => item.Source == source && item.Purpose == purpose && item.Destination == destination);

    private OwnerState GetState(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner) || owner.Length > 256) throw new ArgumentException("Owner ID không hợp lệ.");
        if (owners.TryGetValue(owner, out var cached)) return cached;
        var path = PathFor(owner);
        OwnerState state;
        if (!File.Exists(path)) state = OwnerState.Empty;
        else
        {
            try
            {
                var protectedText = File.ReadAllText(path, Encoding.UTF8);
                var json = protector.Unprotect(protectedText);
                state = JsonSerializer.Deserialize<OwnerStateDto>(json, Json) is { Version: 1, Grants: not null, Audit: not null } dto &&
                    dto.Revision >= 0 && dto.Grants.All(grant => grant is not null &&
                        FindOption(grant.Source, grant.Purpose, grant.Destination) is not null) &&
                    dto.Audit.All(item => item is not null && item.Source is not null && item.Purpose is not null && item.Destination is not null)
                    ? new OwnerState(dto.Grants, dto.Audit, dto.Revision, new CancellationTokenSource())
                    : OwnerState.Empty;
            }
            catch (Exception error) when (error is IOException or JsonException or CryptographicException or ArgumentException)
            {
                // Invalid or unreadable consent state never falls back to an allow decision.
                state = OwnerState.Empty;
            }
        }
        owners[owner] = state;
        return state;
    }

    private void Save(string owner, OwnerState state)
    {
        Directory.CreateDirectory(directory);
        var dto = new OwnerStateDto(1, state.Revision, state.Grants, state.Audit.TakeLast(1000).ToArray());
        var protectedText = protector.Protect(JsonSerializer.Serialize(dto, Json));
        var path = PathFor(owner);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, protectedText, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    private string PathFor(string owner) => Path.Combine(directory,
        "consent-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner)))[..32] + ".bin");

    private static OwnerState AddAudit(OwnerState state, ConsentAudit item)
    {
        if (item.RequestId is null && state.Audit.LastOrDefault() is { RequestId: null } previous && previous.Source == item.Source &&
            previous.Purpose == item.Purpose && previous.Destination == item.Destination &&
            previous.Decision == item.Decision && previous.Actor == item.Actor &&
            item.At - previous.At < TimeSpan.FromMinutes(5)) return state;
        return state with { Audit = state.Audit.Append(item).TakeLast(1000).ToArray() };
    }

    private sealed record OwnerStateDto(int Version, long Revision, ConsentGrant[] Grants, ConsentAudit[] Audit);
    private sealed record OwnerState(ConsentGrant[] Grants, ConsentAudit[] Audit, long Revision, CancellationTokenSource Epoch)
    {
        public static OwnerState Empty => new([], [], 0, new CancellationTokenSource());
    }

    public sealed record ConsentOption(string Source, string SourceLabel, string Purpose, string PurposeLabel,
        string Destination, string DestinationLabel, string Description);
    public sealed record ConsentGrant(string Source, string Purpose, string Destination, bool Allowed, DateTimeOffset ChangedAt);
    public sealed record ConsentGrantView(string Source, string Purpose, string Destination, bool Allowed, DateTimeOffset? ChangedAt);
    public sealed record ConsentAudit(string Source, string Purpose, string Destination, string Decision,
        DateTimeOffset At, string Actor, Guid? RequestId, long PolicyRevision);
    public sealed record ConsentSnapshot(ConsentOption[] Options, ConsentGrantView[] Grants, ConsentAudit[] Audit);
    public sealed record ConsentRequirement(string Source, string Purpose, string Destination);
    public sealed record ConsentChange(bool Changed, bool Allowed, DateTimeOffset ChangedAt, long Revision, bool Revoked);
    public sealed record ConsentLease(string Owner, ConsentRequirement[] Requirements, long Revision,
        CancellationTokenSource Epoch)
    {
        public CancellationToken CancellationToken => Epoch.Token;
    }
}
