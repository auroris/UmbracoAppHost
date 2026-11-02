using System.Security.Claims;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using AppBlocks.Authentication;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// Who is looking at a website page: a signed-in member, a backoffice user of any group whose login
/// Umbraco exposes to the website, or nobody. A page under Public Access admits a backoffice user
/// only when they may edit it, which Umbraco's routing decides before any view runs. Views resolve it once per request and use it to gate
/// content and to offer sign-in and sign-out, so the report page and the application shell
/// share one rule. Registered per request; inject it into a view and call <see cref="ResolveAsync"/>.
/// </summary>
public sealed class SiteVisitor(IMemberManager memberManager, SsoProviderCatalog providers, IHttpContextAccessor httpContextAccessor,
    ApplicationAccessPolicy access, IOptions<IdentityServerSettings> identityServer, IUserService users)
{
    private bool resolved;

    /// <summary>The display name of the signed-in member or backoffice user; null for an anonymous visitor.</summary>
    public string? Name { get; private set; }

    /// <summary>The account's username, as Umbraco knows it; null for an anonymous visitor.</summary>
    public string? UserName { get; private set; }

    /// <summary>The account's email address, when it has one.</summary>
    public string? Email { get; private set; }

    public bool IsSignedIn => Name is not null;

    /// <summary>A member signs out on the website; a backoffice user signs out in the backoffice.</summary>
    public bool IsMember { get; private set; }

    public bool IsBackOfficeUser { get; private set; }

    /// <summary>The sign-in providers to offer members, one button each.</summary>
    public IReadOnlyList<SsoProvider> Providers { get; } = providers.MemberLogins.ToList();

    /// <summary>The configured identity server starts member sign-in; other providers remain explicit choices.</summary>
    public SsoProvider? AutomaticProvider => identityServer.Value is { Enabled: true, MemberEnabled: true } settings
        ? Providers.FirstOrDefault(provider => provider.MemberScheme == settings.MemberScheme)
        : null;

    /// <summary>Only a fresh visit to a sign-in view starts SSO, never a callback or a submitted form.</summary>
    public bool ShouldStartMemberSignIn(bool hasErrors, bool requiresTwoFactor)
    {
        var request = httpContextAccessor.HttpContext?.Request;
        return !IsMember && AutomaticProvider is not null && request is not null
            && HttpMethods.IsGet(request.Method) && !hasErrors && !requiresTwoFactor
            && !request.Query.ContainsKey("ufprt")
            && !request.Query.ContainsKey(SsoLoginUrls.LocalParameter)
            && !request.Query.ContainsKey(SsoRefusal.DeniedParameter);
    }

    /// <summary>The reason a provider put on the query string after refusing a sign-in, if any.</summary>
    public string? Denied =>
        httpContextAccessor.HttpContext?.Request.Query[SsoRefusal.DeniedParameter].ToString() is { Length: > 0 } reason ? reason : null;

    /// <summary>Resolves the visitor; later calls in the same request return at once.</summary>
    public async Task<SiteVisitor> ResolveAsync()
    {
        if (resolved)
            return this;
        resolved = true;

        var context = httpContextAccessor.HttpContext;
        if (context is null)
            return this;

        if (memberManager.IsLoggedIn())
        {
            var member = await memberManager.GetCurrentMemberAsync();
            if (member is not null)
            {
                Name = string.IsNullOrWhiteSpace(member.Name) ? member.UserName : member.Name;
                UserName = member.UserName;
                Email = member.Email;
                IsMember = true;
                return this;
            }
        }

        if (await access.BackOfficeUserAsync(context) is { } backOffice)
        {
            context.User = backOffice;
            var key = backOffice.FindFirstValue(ClaimTypes.NameIdentifier);
            // The cookie names the account; the account itself supplies what the cookie leaves out.
            var user = Guid.TryParse(key, out var userKey) ? await users.GetAsync(userKey) : null;
            Name = backOffice.Identity?.Name ?? user?.Name ?? key;
            UserName = user?.Username ?? backOffice.Identity?.Name;
            Email = user?.Email ?? backOffice.FindFirstValue(ClaimTypes.Email);
            IsBackOfficeUser = true;
        }
        return this;
    }
}
