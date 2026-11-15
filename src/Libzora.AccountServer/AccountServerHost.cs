using System.Net;
using System.Threading.RateLimiting;
using Libzora.Core;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace Libzora.AccountServer;

public static class AccountServerHost
{
    public static async Task<WebApplication> BuildAsync(string[] args, TimeProvider? clock = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (!Enum.TryParse<AccountRegion>(builder.Configuration["AccountServer:Region"] ?? "China", true, out var region)
            || !Enum.IsDefined(region)) throw new InvalidOperationException("请选择 China 或 International 服务器区域。");
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls(AccountServerEndpoints.LocalDevelopmentServerUrl(region));
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AccountJsonContext.Default));
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var proxy in builder.Configuration.GetSection("AccountServer:KnownProxies").Get<string[]>() ?? [])
                options.KnownProxies.Add(IPAddress.Parse(proxy));
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.AddPolicy("credentials", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsJsonAsync(new AccountApiError("尝试次数过多，请稍后再试。"),
                    AccountJsonContext.Default.AccountApiError, cancellationToken: token);
            };
        });
        var directory = builder.Configuration["AccountServer:DataDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "data", "accounts");
        var regionDirectory = Path.Combine(directory, AccountRegions.Slug(region));
        var store = new AccountDatabase(regionDirectory, clock ?? TimeProvider.System, region);
        await store.InitializeAsync();
        builder.Services.AddSingleton(store);
        var app = builder.Build();
        app.UseForwardedHeaders();
        // Reject insecure remote authentication; local development and a trusted
        // HTTPS reverse proxy work without shipping a private signing key.
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            // Removed endpoints fail closed for every method, including old
            // signed transfer links. Historical files are left untouched.
            if (context.Request.Path.StartsWithSegments("/api/sync")
                || context.Request.Path.StartsWithSegments("/sync-blobs"))
            {
                context.Response.StatusCode = StatusCodes.Status410Gone;
                context.Response.Headers["X-Libzora-Error"] = "official_cloud_removed";
                await context.Response.WriteAsJsonAsync(new AccountApiError(MemberEntitlements.AccountCloudRequirement),
                    AccountJsonContext.Default.AccountApiError);
                return;
            }
            // Retired feedback routes never authenticate, parse a request body,
            // access historical feedback, or create attachment/log directories.
            if (context.Request.Path.StartsWithSegments("/api/v1/feedback"))
            {
                context.Response.StatusCode = StatusCodes.Status410Gone;
                context.Response.Headers["X-Libzora-Error"] = "feedback_removed";
                await context.Response.WriteAsJsonAsync(new AccountApiError("账号服务不再接收反馈、截图或软件日志。"),
                    AccountJsonContext.Default.AccountApiError);
                return;
            }
            if (context.Request.Path.StartsWithSegments("/api/accounts") && !context.Request.IsHttps
                && (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new AccountApiError("账号服务须通过 HTTPS 访问。"),
                    AccountJsonContext.Default.AccountApiError);
                return;
            }
            try { await next(context); }
            catch (AccountRequestException exception)
            {
                context.Response.StatusCode = exception.StatusCode;
                await context.Response.WriteAsJsonAsync(new AccountApiError(exception.Message), AccountJsonContext.Default.AccountApiError);
            }
        });
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/accounts")
                && context.Request.Headers["X-Libzora-Region"] != AccountRegions.Slug(region))
                throw new AccountRequestException(StatusCodes.Status403Forbidden, "账号服务区域不匹配。");
            await next(context);
        });
        app.UseRateLimiter();
        app.MapGet("/health", () => Results.Ok(new { service = "libzora-accounts", region }));
        app.MapPost("/api/accounts/register", async (AccountCredentials credentials, CancellationToken token) =>
            Results.Json(await store.RegisterAsync(credentials, token), AccountJsonContext.Default.AccountSessionResponse,
                statusCode: StatusCodes.Status201Created)).RequireRateLimiting("credentials");
        app.MapPost("/api/accounts/login", async (AccountCredentials credentials, CancellationToken token) =>
            Results.Json(await store.SignInAsync(credentials, token), AccountJsonContext.Default.AccountSessionResponse))
            .RequireRateLimiting("credentials");
        app.MapGet("/api/accounts/session", async (HttpContext context, CancellationToken token) =>
            Results.Json(await store.GetSessionAsync(Bearer(context), token), AccountJsonContext.Default.AccountSessionInfo));
        app.MapPost("/api/accounts/logout", async (HttpContext context, CancellationToken token) =>
        {
            await store.RevokeAsync(Bearer(context), token);
            return Results.NoContent();
        });
        return app;
    }

    internal static string Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new AccountRequestException(StatusCodes.Status401Unauthorized, "请先登录。");
        var token = header[7..];
        if (token.Length != 43 || !token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            throw new AccountRequestException(StatusCodes.Status401Unauthorized, "会话无效，请重新登录。");
        return token;
    }
}

internal sealed class AccountRequestException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
