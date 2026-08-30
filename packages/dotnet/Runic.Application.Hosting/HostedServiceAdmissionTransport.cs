using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Application.Hosting;

/// <summary>Registers and maps the bounded C#-owned hosted-service admission surface.</summary>
public static class HostedServiceAdmissionTransport
{
    /// <summary>The C#-owned encrypted-cookie authentication scheme.</summary>
    public const string AuthenticationScheme = "RunicHostedServiceCookie";

    /// <summary>Registers encrypted host-only cookie authentication and antiforgery validation.</summary>
    public static AuthenticationBuilder AddRunicHostedServiceAdmission(
        this IServiceCollection services,
        HostedServiceAdmissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(policy);
        services.AddSingleton(policy);
        services.AddAntiforgery(options => options.HeaderName = HostedServiceAdmissionPolicy.AntiforgeryHeaderName);
        return services.AddAuthentication(AuthenticationScheme).AddCookie(AuthenticationScheme, options =>
        {
            options.Cookie.Name = HostedServiceAdmissionPolicy.SessionCookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.SlidingExpiration = false;
        });
    }

    /// <summary>Accepts forwarded scheme and host headers only from the policy's proxy snapshot.</summary>
    public static IApplicationBuilder UseRunicHostedServiceForwardedHeaders(
        this IApplicationBuilder application,
        HostedServiceAdmissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(policy);
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
            RequireHeaderSymmetry = true,
        };
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (IPAddress proxy in policy.TrustedProxyAddresses) options.KnownProxies.Add(proxy);
        return application.UseForwardedHeaders(options);
    }

    /// <summary>Maps the protected C# service group and its sanitized session and antiforgery endpoints.</summary>
    public static RouteGroupBuilder MapRunicHostedService(this IEndpointRouteBuilder endpoints, HostedServiceAdmissionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(policy);
        RouteGroupBuilder group = endpoints.MapGroup(HostedServiceAdmissionPolicy.ServiceRoutePrefix)
            .AddEndpointFilter(new AdmissionFilter(policy));
        group.MapGet("/session", static (HttpContext context) => Results.Ok(HostedServiceSession.From(context.User)));
        group.MapGet("/csrf", static (HttpContext context, IAntiforgery antiforgery) =>
        {
            AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new HostedServiceCsrfToken(tokens.RequestToken!));
        });
        return group;
    }

    /// <summary>
    /// Applies the required role to a C#-owned unsafe service command.
    /// Map the command with <see cref="EndpointRouteBuilderExtensions.MapPost(IEndpointRouteBuilder, string, Delegate)"/>
    /// in the consuming application, then call this method on the returned builder.
    /// Keeping the handler at the call site lets the Request Delegate Generator inspect it for NativeAOT.
    /// </summary>
    public static RouteHandlerBuilder RequireRunicServiceRole(
        this RouteHandlerBuilder builder,
        string requiredRole)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredRole);
        return builder.AddEndpointFilter(new RoleFilter(requiredRole));
    }

    private sealed class AdmissionFilter(HostedServiceAdmissionPolicy policy) : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            HttpContext http = context.HttpContext;
            if (http.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();
            AuthenticateResult authentication = await http.AuthenticateAsync(AuthenticationScheme).ConfigureAwait(false);
            if (!authentication.Succeeded || authentication.Principal is null ||
                authentication.Properties?.ExpiresUtc is DateTimeOffset expiry && expiry <= DateTimeOffset.UtcNow)
            {
                return Results.Unauthorized();
            }
            http.User = authentication.Principal;
            try { _ = HostedServiceSession.From(http.User); }
            catch (HostedServiceAdmissionException) { return Results.Unauthorized(); }
            if (IsUnsafe(http.Request.Method))
            {
                if (!string.Equals(http.Request.Headers.Origin, policy.PublicOrigin.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http).ConfigureAwait(false); }
                catch (AntiforgeryValidationException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            }
            return await next(context).ConfigureAwait(false);
        }
    }

    private sealed class RoleFilter(string requiredRole) : IEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
            context.HttpContext.User.IsInRole(requiredRole)
                ? next(context)
                : ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));
    }

    private static bool IsUnsafe(string method) => method is "POST" or "PUT" or "PATCH" or "DELETE";
}

/// <summary>A bounded service-session projection with no cookie, token, or unbounded claim data.</summary>
public sealed record HostedServiceSession(string Subject, string? DisplayName, IReadOnlyList<string> Roles)
{
    /// <summary>Builds a sanitized session projection from the C#-validated principal.</summary>
    public static HostedServiceSession From(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        string? subject = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 128) throw new HostedServiceAdmissionException();
        string? displayName = principal.Identity?.Name;
        if (displayName?.Length > 128) throw new HostedServiceAdmissionException();
        string[] roles = principal.FindAll(ClaimTypes.Role).Select(claim => claim.Value).Distinct(StringComparer.Ordinal).OrderBy(role => role, StringComparer.Ordinal).ToArray();
        if (roles.Length > 16 || roles.Any(role => string.IsNullOrWhiteSpace(role) || role.Length > 64)) throw new HostedServiceAdmissionException();
        return new HostedServiceSession(subject, displayName, roles);
    }
}

/// <summary>Returns the request token required for an unsafe same-origin service request.</summary>
public sealed record HostedServiceCsrfToken(string RequestToken);

internal sealed class HostedServiceAdmissionException : Exception;
