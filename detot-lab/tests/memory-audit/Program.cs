using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;

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
    Console.WriteLine("PASS: production parser rejected false confirmation, malformed JSON and invalid schema.");
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
