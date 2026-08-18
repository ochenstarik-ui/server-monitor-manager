using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ServerMonitorManager.Control;

public sealed class PasswordSessionAuthenticationHandler(
    PasswordSessionService sessionService,
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private readonly PasswordSessionService _sessionService = sessionService;

    public const string SchemeName = "PasswordSession";
    public const string SessionCookieName = "smm_session";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_sessionService.IsEnabled)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string? token = null;

        if (Request.Headers.TryGetValue("Authorization", out var authHeaderValue)
            && !string.IsNullOrWhiteSpace(authHeaderValue))
        {
            var headerStr = authHeaderValue.ToString();
            if (headerStr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = headerStr["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(token) && Request.Cookies.TryGetValue(SessionCookieName, out var cookieValue))
        {
            token = cookieValue;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var session = _sessionService.ValidateSession(token);
        if (session is null)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.Username),
            new(ClaimTypes.Role, "Operator")
        };

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
