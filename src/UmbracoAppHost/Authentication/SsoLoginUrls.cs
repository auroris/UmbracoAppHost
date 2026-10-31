using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Umbraco.Cms.Core.Web;

namespace UmbracoAppHost.Authentication;

/// <summary>Local destinations shared by automatic SSO and its password fallback.</summary>
public static class SsoLoginUrls
{
    public const string LocalParameter = "sso-local";
    public const string MemberReturnUrlProperty = "UmbracoAppHost.MemberReturnUrl";

    public static bool IsLocalUrl(string? url) => RedirectHttpResult.IsLocalUrl(url);

    /// <summary>
    /// A surface controller's URL, protected with this site's own keys. Umbraco's shorter <c>Url.SurfaceAction</c> keeps
    /// the data protection of the first site that called it in a static field for the life of the process, and any later
    /// site in that process cannot read what it protects, so its action never runs.
    /// </summary>
    public static string SurfaceAction(IUrlHelper url, string action, string controller, object routeValues)
    {
        var services = url.ActionContext.HttpContext.RequestServices;
        return url.SurfaceAction(services.GetRequiredService<IUmbracoContextAccessor>().GetRequiredUmbracoContext(),
            services.GetRequiredService<IDataProtectionProvider>(), action, controller, routeValues);
    }

    /// <summary>Retains the page and its parameters, without carrying login-attempt markers after sign-in.</summary>
    public static string CleanReturnUrl(HttpContext context, string? url)
    {
        if (!IsLocalUrl(url)) return context.Request.PathBase + "/";
        if (url!.StartsWith("~/", StringComparison.Ordinal)) url = context.Request.PathBase + url[1..];
        var fragmentIndex = url.IndexOf('#');
        var fragment = fragmentIndex >= 0 ? url[fragmentIndex..] : "";
        var destination = fragmentIndex >= 0 ? url[..fragmentIndex] : url;
        var queryIndex = destination.IndexOf('?');
        if (queryIndex < 0) return url;
        var query = QueryHelpers.ParseQuery(destination[queryIndex..]);
        query.Remove(LocalParameter);
        query.Remove(SsoRefusal.DeniedParameter);
        query.Remove("ufprt");
        return destination[..queryIndex] + QueryString.Create(query) + fragment;
    }

    public static string LocalMemberUrl(HttpContext context, string? returnUrl, string? reason = null)
    {
        var local = QueryHelpers.AddQueryString(CleanReturnUrl(context, returnUrl), LocalParameter, "1");
        return string.IsNullOrWhiteSpace(reason) ? local : QueryHelpers.AddQueryString(local, SsoRefusal.DeniedParameter, reason);
    }

    public static string MemberReturnUrl(HttpContext context, AuthenticationProperties? properties)
    {
        if (properties?.Items.TryGetValue(MemberReturnUrlProperty, out var original) == true)
            return CleanReturnUrl(context, original);

        // Retain compatibility with callback URLs that carry a normal returnUrl query.
        // Our member controller records it explicitly because native Surface URLs encrypt their route values.
        if (Uri.TryCreate(properties?.RedirectUri, UriKind.RelativeOrAbsolute, out var callback))
        {
            var raw = callback.IsAbsoluteUri ? callback.Query : callback.OriginalString;
            var question = raw.IndexOf('?');
            if (question >= 0 && QueryHelpers.ParseQuery(raw[question..]).TryGetValue("returnUrl", out var value))
                return CleanReturnUrl(context, value.ToString());
        }
        return context.Request.PathBase + "/";
    }
}
