using Microsoft.Extensions.Configuration;

public static class GoogleOAuthClientSelection
{
    public static (string? ClientId, string? ClientSecret) Resolve(IConfiguration configuration)
    {
        var clientId = configuration["DETOT_GOOGLE_CLIENT_ID"];
        var clientSecret = configuration["DETOT_GOOGLE_CLIENT_SECRET"];
        if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(clientSecret))
            return (clientId, clientSecret);

        return (configuration["DETOT_GOOGLE_DRIVE_CLIENT_ID"],
            configuration["DETOT_GOOGLE_DRIVE_CLIENT_SECRET"]);
    }
}
