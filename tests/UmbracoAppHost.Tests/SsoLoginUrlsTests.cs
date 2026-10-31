using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Tests;

public sealed class SsoLoginUrlsTests
{
    [Theory]
    [InlineData("https://other.example/private")]
    [InlineData("//other.example/private")]
    [InlineData("/\\other.example/private")]
    [InlineData("relative/path")]
    [InlineData("/page\nLocation: https://other.example")]
    [InlineData(null)]
    public void Unsafe_return_urls_fall_back_inside_the_current_application(string? target)
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/wing";
        Assert.Equal("/wing/", SsoLoginUrls.CleanReturnUrl(context, target));
    }

    [Fact]
    public void Fallback_keeps_page_parameters_but_success_removes_attempt_and_surface_transport_markers()
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/wing";
        const string original = "/wing/private/?id=4&tag=a&tag=b&culture=fr#details";
        var fallback = SsoLoginUrls.LocalMemberUrl(context, original, "SSO unavailable");
        Assert.Contains("sso-local=1", fallback);
        Assert.EndsWith("#details", fallback);
        Assert.Equal(original, SsoLoginUrls.CleanReturnUrl(context, QueryHelpers.AddQueryString(fallback, "ufprt", "encrypted-route")));
        Assert.Equal("/wing/private/?id=4", SsoLoginUrls.CleanReturnUrl(context, "~/private/?id=4"));
    }

    [Fact]
    public void Member_failure_recovers_the_page_from_protected_authentication_properties()
    {
        var context = new DefaultHttpContext();
        context.Request.PathBase = "/wing";
        var properties = new AuthenticationProperties { RedirectUri = "/wing/private/?ufprt=encrypted-route" };
        properties.Items[SsoLoginUrls.MemberReturnUrlProperty] = "/wing/private/?id=4";
        Assert.Equal("/wing/private/?id=4", SsoLoginUrls.MemberReturnUrl(context, properties));
        properties.Items[SsoLoginUrls.MemberReturnUrlProperty] = "//other.example/";
        Assert.Equal("/wing/", SsoLoginUrls.MemberReturnUrl(context, properties));
    }
}
