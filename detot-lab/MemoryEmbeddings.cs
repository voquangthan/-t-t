using System.Net.Http.Json;
using System.Text.Json;

sealed class MemoryEmbeddings(IHttpClientFactory clients, string? model, string? revision, ILogger logger)
{
    public string? Version => string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(revision)
        ? null : $"ollama:{model}:{revision}";

    public async Task<float[][]?> EmbedAsync(string[] input, CancellationToken cancellationToken)
    {
        if (Version is null || input.Length == 0) return null;
        try
        {
            using var client = clients.CreateClient("memory-embeddings");
            using var response = await client.PostAsJsonAsync("http://127.0.0.1:11434/api/embed",
                new { model, input, truncate = false }, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var data = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (!data.RootElement.TryGetProperty("embeddings", out var output)) return null;
            var vectors = output.Deserialize<float[][]>();
            if (vectors?.Length != input.Length || vectors[0] is not { Length: > 0 and <= 4096 }) return null;
            var length = vectors[0].Length;
            return vectors.All(vector => vector is not null && vector.Length == length &&
                vector.All(float.IsFinite) && vector.Any(component => component != 0)) ? vectors : null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException ||
            error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Memory embeddings unavailable ({ErrorType}).", error.GetType().Name);
            return null;
        }
    }
}
