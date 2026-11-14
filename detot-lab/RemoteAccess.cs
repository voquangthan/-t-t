using System.Security.Cryptography;
using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

sealed class RemoteAccess
{
    public const string OwnerId = "workspace-owner";
    private const string CookieName = "__Host-detot_session";
    private readonly byte[] passwordHash;
    private readonly byte[]? memoryMemberHash;
    private readonly IMemoryCache sessions;
    private readonly object loginLock = new();
    private DateTimeOffset nextAttempt;

    private readonly TimeSpan sessionLifetime;

    public RemoteAccess(string? password, IMemoryCache sessions, string? memoryMemberPassword = null, int sessionSeconds = 28800)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 24)
            throw new InvalidOperationException("Remote access requires a random DETOT_REMOTE_PASSWORD of at least 24 characters.");
        passwordHash = Hash(password);
        sessionLifetime = TimeSpan.FromSeconds(Math.Clamp(sessionSeconds, 5, 28800));
        if (memoryMemberPassword is not null)
        {
            if (memoryMemberPassword.Length < 24 || CryptographicOperations.FixedTimeEquals(passwordHash, Hash(memoryMemberPassword)))
                throw new InvalidOperationException("Memory member requires a distinct password of at least 24 characters.");
            memoryMemberHash = Hash(memoryMemberPassword);
        }
        this.sessions = sessions;
    }

    public static Uri? ParseOrigin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.HostNameType != UriHostNameType.Dns || uri.IsLoopback || uri.UserInfo.Length != 0 ||
            !string.Equals(value.TrimEnd('/'), uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("DETOT_PUBLIC_ORIGIN must be an HTTPS origin such as https://detot.example.com.");
        return uri;
    }

    public async Task Handle(HttpContext context, RequestDelegate next, string origin)
    {
        var request = context.Request;
        var session = request.Cookies[CookieName];
        string? userId = null;
        var authenticated = session is not null && sessions.TryGetValue("remote:" + session, out userId) && userId is not null;
        var login = request.Path.Equals("/login", StringComparison.OrdinalIgnoreCase);
        var logout = request.Path.Equals("/logout", StringComparison.OrdinalIgnoreCase);
        if (logout || (login && HttpMethods.IsPost(request.Method)))
        {
            if (!HttpMethods.IsPost(request.Method) || request.Headers.Origin != origin)
            {
                context.Response.StatusCode = 403;
                return;
            }
            if (logout)
            {
                if (session is not null) sessions.Remove("remote:" + session);
                context.Response.Cookies.Delete(CookieName, Cookie());
                context.Response.Cookies.Delete("detot_google", new CookieOptions
                {
                    Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/api/google/callback"
                });
                context.Response.Redirect("/login");
                return;
            }
            if (!request.HasFormContentType)
            {
                context.Response.StatusCode = 415;
                return;
            }
            var form = await request.ReadFormAsync(context.RequestAborted);
            bool allowed;
            lock (loginLock)
            {
                var now = DateTimeOffset.UtcNow;
                allowed = now >= nextAttempt;
                if (allowed) nextAttempt = now.AddSeconds(2);
            }
            var digest = Hash(form["password"].ToString());
            var loginId = CryptographicOperations.FixedTimeEquals(passwordHash, digest) ? OwnerId :
                memoryMemberHash is not null && CryptographicOperations.FixedTimeEquals(memoryMemberHash, digest) ? "memory-member" : null;
            if (!allowed || loginId is null)
            {
                await LoginPage(context, allowed ? 401 : 429, allowed ? "Mật khẩu không đúng." : "Hãy thử lại sau vài giây.");
                return;
            }
            if (session is not null) sessions.Remove("remote:" + session);
            session = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            sessions.Set("remote:" + session, loginId, new MemoryCacheEntryOptions
            {
                SlidingExpiration = TimeSpan.FromMinutes(15),
                AbsoluteExpirationRelativeToNow = sessionLifetime
            });
            context.Response.Cookies.Append(CookieName, session, Cookie());
            context.Response.Redirect("/workspace");
            return;
        }
        if (login && HttpMethods.IsGet(request.Method))
        {
            if (authenticated) context.Response.Redirect("/workspace");
            else await LoginPage(context, 200, "");
            return;
        }
        if (authenticated)
        {
            context.User = Principal(userId!);
            if (userId != OwnerId && !request.Path.StartsWithSegments("/api/memory"))
            {
                context.Response.StatusCode = 403;
                return;
            }
            await next(context);
            return;
        }
        if (request.Path.StartsWithSegments("/api") && !request.Path.Equals("/api/google/callback", StringComparison.OrdinalIgnoreCase))
            context.Response.StatusCode = 401;
        else
            context.Response.Redirect("/login");
    }

    public static ClaimsPrincipal Principal(string id) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "DetotWorkspace"));

    private static Task LoginPage(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        return context.Response.WriteAsync($"""
            <!doctype html><html lang="vi"><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Đăng nhập Đệ Tốt</title>
            <main><h1>Đăng nhập Đệ Tốt</h1><p role="status">{message}</p>
            <form method="post" action="/login" autocomplete="off">
            <label>Mật khẩu <input type="password" name="password" required></label>
            <button>Đăng nhập</button></form></main></html>
            """);
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    private static CookieOptions Cookie() => new() { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" };
}
