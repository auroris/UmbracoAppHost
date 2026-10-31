using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;

namespace UmbracoAppHost.Authentication;

/// <summary>Resumes backoffice authorization with the native password form after an SSO failure.</summary>
public static class BackOfficeSsoFallback
{
    public static Task RedirectAsync(HttpContext context, AuthenticationProperties? properties, string reason)
    {
        var backOffice = context.Request.PathBase + "/umbraco";
        var authorize = backOffice + "/management/api/v1/security/back-office/authorize";
        var original = properties?.RedirectUri;
        if (string.IsNullOrEmpty(original))
            original = context.Request.PathBase + context.Request.Path + context.Request.QueryString;

        var continuation = LocalAuthorizeUrl(context.Request, original, authorize);
        // Without the original PKCE request, the provider chooser starts a fresh request.
        // Its logged-out state deliberately skips autoRedirect, unlike the backoffice root.
        var target = continuation is null
            ? backOffice + "/logout"
            : QueryHelpers.AddQueryString(backOffice + "/login", "ReturnUrl", continuation);
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect(QueryHelpers.AddQueryString(target, SsoRefusal.DeniedParameter, reason));
        return Task.CompletedTask;
    }

    private static string? LocalAuthorizeUrl(HttpRequest request, string original, string authorize)
    {
        if (!Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var origin)) return null;
        if (!Uri.TryCreate(origin, original, out var uri)
            || !string.Equals(uri.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Authority, origin.Authority, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.AbsolutePath, authorize, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.Fragment)) return null;

        var query = QueryHelpers.ParseQuery(uri.Query);
        // Keep state and the code challenge unchanged. The browser still owns its matching
        // umb:pkce verifier; choosing Umbraco's internal authorize branch avoids another SSO attempt.
        query.Remove("identity_provider");
        return authorize + QueryString.Create(query);
    }
}
