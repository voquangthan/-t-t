using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

if (args is ["agentic"])
{
    var agenticRoot = Path.Combine(Path.GetTempPath(), "detot-agentic-memory-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(agenticRoot);
    try
    {
        var protection = new EphemeralDataProtectionProvider();
        var practice = new Practice(protection.CreateProtector("Detot.Practice.v1"), agenticRoot);
        var owner = "owner-a";
        var otherOwner = "owner-b";
        var saveRequest = Guid.NewGuid();
        var saveReply = practice.ApplyMemory(owner, "save", "agentic reminder test", "task", null,
            saveRequest, NullLogger.Instance);
        var idText = saveReply.Split(' ').Last().TrimEnd('.');
        Require(Guid.TryParse(idText, out var taskId));
        var task = practice.GetAgenticTask(owner, taskId)!;
        Require(task.Status == "open" && task.Version == 1 && task.CalendarEventId is null);
        Require(practice.GetAgenticTask(otherOwner, taskId) is null);

        var editedText = "Kiểm tra lại in thử sau khi cập nhật driver.";
        var editRequest = Guid.NewGuid();
        Require(practice.UpdateMemory(owner, taskId, editedText, null, editRequest, NullLogger.Instance)
            .Contains("Đã cập nhật", StringComparison.Ordinal));
        var edited = practice.GetAgenticTask(owner, taskId)!;
        Require(edited.Text == editedText && edited.Version == 2);
        Require(practice.GetAgenticTask(otherOwner, taskId) is null);
        Require(practice.UpdateMemory(owner, taskId, editedText, null, editRequest, NullLogger.Instance)
            .Contains("Đã cập nhật", StringComparison.Ordinal));
        Require(practice.GetAgenticTask(owner, taskId)!.Version == 2);
        Require(practice.UpdateMemory(owner, taskId, "different content", null, editRequest, NullLogger.Instance)
            .Contains("Chưa thay đổi", StringComparison.Ordinal));
        Require(practice.GetAgenticTask(owner, taskId)!.Text == editedText);

        var requestId = Guid.NewGuid();
        var firstReservation = practice.ReserveTaskCalendarReminder(owner, taskId, edited.Version, requestId);
        Require(firstReservation.Status == 200 && firstReservation.Version == 3);
        var retry = practice.ReserveTaskCalendarReminder(owner, taskId, edited.Version, requestId);
        Require(retry == firstReservation);
        Require(practice.ReserveTaskCalendarReminder(owner, taskId, firstReservation.Version, Guid.NewGuid()).Status == 409);
        Require(((IStatusCodeHttpResult)practice.EditMemory(owner, taskId, firstReservation.Version, null, false, complete: true)).StatusCode == 409);

        var eventId = "detot" + requestId.ToString("N");
        Require(((IStatusCodeHttpResult)practice.LinkTaskToCalendar(owner, taskId, firstReservation.Version,
            requestId, eventId, DateTimeOffset.UtcNow.AddDays(1))).StatusCode == 200);
        Require(((IStatusCodeHttpResult)practice.LinkTaskToCalendar(owner, taskId, firstReservation.Version,
            requestId, eventId, DateTimeOffset.UtcNow.AddDays(1))).StatusCode == 200);
        var restarted = new Practice(protection.CreateProtector("Detot.Practice.v1"), agenticRoot);
        var linked = restarted.GetAgenticTask(owner, taskId)!;
        Require(linked.CalendarEventId == eventId && linked.CalendarRequestId == requestId && linked.Version == 4);
        Require(((IStatusCodeHttpResult)restarted.EditMemory(owner, taskId, linked.Version, null, false, complete: true)).StatusCode == 204);
        Require(restarted.GetAgenticTask(owner, taskId) is { Status: "done", CalendarEventId: not null });
        Require(!Directory.GetFiles(agenticRoot, "*.bin").Any(path => File.ReadAllText(path).Contains("agentic reminder test", StringComparison.Ordinal)));
        Console.WriteLine("PASS: encrypted task edit, idempotency, owner isolation, reminder reservation, link, restart and completion.");
    }
    finally { try { Directory.Delete(agenticRoot, recursive: true); } catch { } }
    return;
}

if (args is ["ranking"])
{
    var semantic = MemoryRanking.Cosine([1, 0], [0.98f, 0.1f]);
    var irrelevant = MemoryRanking.Cosine([1, 0], [0, 1]);
    Require(semantic > 0.95 && irrelevant == 0);
    Require(MemoryRanking.Keyword("may in", "may in phong ke toan") == 1);
    Require(MemoryRanking.Keyword("pin", "may in") == 0);
    Require(MemoryRanking.Combine(semantic, 0) > MemoryRanking.Combine(0, 0.5));
    Console.WriteLine("PASS: hybrid ranking arithmetic with synthetic vectors.");
    return;
}

if (args is ["parser"])
{
    foreach (var output in new[] { "Đã ghi nhớ cho bạn.", "{", "[]",
        """{"intent":"save","work":"máy in","category":42}""",
        """{"intent":"save","work":"máy khác","category":"task"}""" })
        Require(MemoryIntentParser.Parse(output, "Nhớ việc máy in", false).Intent == "failed");
    Require(MemoryIntentParser.Parse("""{"intent":"save","work":"máy in","category":"task"}""",
        "Nhớ việc máy in", false).Intent == "save");
    Require(MemoryIntentParser.Parse("""{"intent":"schedule","work":"kiểm tra máy in","category":"task"}""",
        "Nhắc mình kiểm tra máy in ngày mai", false).Intent == "schedule");
    Require(MemoryIntentParser.Parse("""{"intent":"schedule","work":"máy in","category":"device"}""",
        "Nhắc mình máy in ngày mai", false).Intent == "failed");
    var updateId = Guid.NewGuid();
    var updateMessage = $"Cập nhật Memory ID {updateId:D} thành nội dung mới đã xác minh.";
    var parsedUpdate = MemoryIntentParser.Parse(
        $$"""{"intent":"update","work":"nội dung mới đã xác minh","category":null,"memoryId":"{{updateId:D}}"}""",
        updateMessage, false);
    Require(parsedUpdate.Intent == "update" && parsedUpdate.MemoryId == updateId && parsedUpdate.Work == "nội dung mới đã xác minh");
    Require(MemoryIntentParser.Parse(
        $$"""{"intent":"update","work":"nội dung mới","category":null,"memoryId":"{{updateId:D}}"}""",
        "Cập nhật ký ức thành nội dung mới", false).Intent == "failed");
    Console.WriteLine("PASS: production parser rejected false confirmation, malformed schema and unsafe reminder classification.");
    return;
}

if (args.Length != 3 || !OperatingSystem.IsWindows())
    throw new ArgumentException("Pass the isolated Backend content root, encrypted store and QA checkpoint.");

var root = Path.GetFullPath(args[0]);
var keys = Path.Combine(root, ".local", "google", "keys");
var provider = DataProtectionProvider.Create(new DirectoryInfo(keys), options =>
{
    options.SetApplicationName(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar);
    options.ProtectKeysWithDpapi();
});
if (args[2] == "seed-legacy")
{
    var now = DateTimeOffset.UtcNow;
    var conversation = Guid.NewGuid();
    var legacy = new[] { new { id = Guid.NewGuid(), category = "task", text = "legacy printer QA",
        conversationId = conversation, createdAt = now, status = "done", source = "user-request", confidence = 1,
        history = new[] {
            new { action = "created", at = now, conversationId = conversation, requestId = Guid.NewGuid(), beforeStatus = (string?)null, afterStatus = "open" },
            new { action = "completed", at = now.AddMinutes(1), conversationId = conversation, requestId = Guid.NewGuid(), beforeStatus = (string?)"open", afterStatus = "done" }
        } } };
    Directory.CreateDirectory(Path.GetDirectoryName(args[1])!);
    File.WriteAllText(args[1], provider.CreateProtector("Detot.Practice.v1").Protect(
        JsonSerializer.Serialize(JsonSerializer.Serialize(legacy))));
    return;
}
var encrypted = File.ReadAllText(args[1]);
if (args[2].EndsWith(".backup", StringComparison.Ordinal))
{
    var protector = provider.CreateProtector("Detot.Practice.v1");
    JsonArray ReadPayload(string payload) =>
        JsonNode.Parse(JsonSerializer.Deserialize<string>(protector.Unprotect(payload))!)!.AsArray();
    var original = ReadPayload(File.ReadAllText(args[2]));
    var migrated = ReadPayload(encrypted);
    foreach (var entry in original)
    {
        entry!["ownerId"] = "workspace-owner";
        foreach (var change in entry["history"]!.AsArray())
            change!["ownerId"] = "workspace-owner";
    }
    if (!JsonNode.DeepEquals(original, migrated)) throw new InvalidDataException("Migration changed legacy fields.");
    Console.WriteLine("PASS: independently decrypted migration preserved all fields and assigned owner to history.");
    return;
}
using var checkpoint = JsonDocument.Parse(File.ReadAllText(args[2]));
var first = checkpoint.RootElement.GetProperty("firstId").GetGuid();
var second = checkpoint.RootElement.GetProperty("secondId").GetGuid();
using var wrapper = JsonDocument.Parse(provider.CreateProtector("Detot.Practice.v1").Unprotect(encrypted));
using var store = JsonDocument.Parse(wrapper.RootElement.GetString()!);
var entries = store.RootElement.EnumerateArray().ToArray();
Check(first, "open", 1);
Check(second, "done", 2);
Console.WriteLine("PASS: independent decrypted audit verified IDs, history, timestamps and sources.");

void Check(Guid id, string status, int count)
{
    var entry = entries.Single(item => item.GetProperty("id").GetGuid() == id);
    Require(entry.GetProperty("status").GetString() == status);
    var changes = entry.GetProperty("history").EnumerateArray().ToArray();
    Require(changes.Length == count);
    var created = changes[0];
    Require(created.GetProperty("action").GetString() == "created");
    Require(created.GetProperty("beforeStatus").ValueKind == JsonValueKind.Null);
    Require(created.GetProperty("afterStatus").GetString() == "open");
    Require(created.GetProperty("at").GetDateTimeOffset() == entry.GetProperty("createdAt").GetDateTimeOffset());
    Require(created.GetProperty("conversationId").GetGuid() == entry.GetProperty("conversationId").GetGuid());
    Require(created.GetProperty("requestId").GetGuid() != Guid.Empty);
    if (count == 1) return;
    var completed = changes[1];
    Require(completed.GetProperty("action").GetString() == "completed");
    Require(completed.GetProperty("beforeStatus").GetString() == "open");
    Require(completed.GetProperty("afterStatus").GetString() == "done");
    Require(completed.GetProperty("at").GetDateTimeOffset() >= created.GetProperty("at").GetDateTimeOffset());
    Require(completed.GetProperty("conversationId").GetGuid() != Guid.Empty);
    Require(completed.GetProperty("requestId").GetGuid() != created.GetProperty("requestId").GetGuid());
}

static void Require(bool condition)
{
    if (!condition) throw new InvalidDataException("Memory audit assertion failed.");
}
