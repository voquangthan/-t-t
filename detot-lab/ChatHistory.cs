using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

sealed class ChatHistory(IDataProtector protector, string directory)
{
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string PathFor(Guid id) => Path.Combine(directory, $"{id:D}.bin");

    public IResult List()
    {
        lock (gate)
        {
            if (!Directory.Exists(directory)) return Results.Ok(Array.Empty<Entry>());
            return Results.Ok(Directory.EnumerateFiles(directory, "*.bin").Select(Read)
                .OrderByDescending(entry => entry.Updated).ToArray());
        }
    }

    public IResult Save(Guid id, Update update)
    {
        var data = update.Conversation;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("id", out var key) ||
            key.ValueKind != JsonValueKind.String || !key.TryGetGuid(out var value) || value != id ||
            !data.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String ||
            !data.TryGetProperty("pinned", out var pinned) || pinned.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            !data.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array ||
            messages.EnumerateArray().Any(message => !ValidMessage(message)))
            return Results.BadRequest();

        lock (gate)
        {
            var path = PathFor(id);
            var existing = File.Exists(path) ? Read(path) : null;
            if (existing?.Revision != update.Revision) return Results.Conflict();
            var entry = new Entry(data, Guid.NewGuid(), DateTimeOffset.UtcNow);
            Directory.CreateDirectory(directory);
            var temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, protector.Protect(JsonSerializer.Serialize(entry, Json)));
                File.Move(temp, path, overwrite: true);
            }
            finally { File.Delete(temp); }
            return Results.Ok(entry);
        }
    }

    public void Delete(Guid id)
    {
        lock (gate)
        {
            if (Directory.Exists(directory)) File.Delete(PathFor(id));
        }
    }

    public Guid? AppendReply(Guid id, Guid revision, object reply)
    {
        var result = JsonSerializer.SerializeToElement(reply, Json);
        if (!result.TryGetProperty("reply", out var text) || text.ValueKind != JsonValueKind.String) return null;
        lock (gate)
        {
            var path = PathFor(id);
            if (!File.Exists(path)) return null;
            var entry = Read(path);
            if (entry.Revision != revision) return null;
            var data = System.Text.Json.Nodes.JsonNode.Parse(entry.Conversation.GetRawText())!;
            data["messages"]!.AsArray().Add(JsonSerializer.SerializeToNode(new
            {
                id = Guid.NewGuid(),
                role = "assistant",
                text = text.GetString(),
                tool = IsTrue("googleCalendarCreated") ? "Google Calendar" :
                    IsTrue("calendarCreated") ? "Đã tạo sự kiện trong lịch thử" :
                    IsTrue("toolUsed") ? "Công cụ lab đã hoàn tất" : null,
                calendarContext = result.TryGetProperty("calendarContext", out var calendar) ? calendar.GetString() : null
            }, Json));
            return Save(id, new Update(JsonSerializer.SerializeToElement(data, Json), revision)) is
                IValueHttpResult { Value: Entry saved } ? saved.Revision : null;
        }

        bool IsTrue(string name) => result.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    }

    private Entry Read(string path) => JsonSerializer.Deserialize<Entry>(protector.Unprotect(File.ReadAllText(path)), Json)
        ?? throw new CryptographicException("Unreadable chat history.");

    private static bool ValidMessage(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object &&
        message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String &&
        message.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.String &&
        role.GetString() is ("user" or "assistant") &&
        message.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String;

    public sealed record Update(JsonElement Conversation, Guid? Revision);
    private sealed record Entry(JsonElement Conversation, Guid Revision, DateTimeOffset Updated);
}
