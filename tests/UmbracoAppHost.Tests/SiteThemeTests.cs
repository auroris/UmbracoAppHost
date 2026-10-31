using UmbracoAppHost.Themes;

namespace UmbracoAppHost.Tests;

public sealed class SiteThemeTests
{
    [Theory]
    [InlineData(null, "application", "application")]
    [InlineData("", "appHostWebsite", "website")]
    [InlineData("website", "application", "website")]
    [InlineData(" Application ", "appHostWebsite", "application")]
    [InlineData("removed-theme", "application", "application")]
    public void Explicit_choice_overrides_root_type_and_existing_roots_keep_their_default(string? alias, string rootType, string expected)
    {
        Assert.Equal(expected, new SiteThemeCatalog([]).Resolve(alias, rootType).Alias);
    }

    // ResolveForAsync reads the site's theme and template, then decides as these cases do.
    [Theory]
    [InlineData(null, "appHostWebsite", null, "ApplicationSite", "application")]
    [InlineData(null, "appHostWebsite", null, "WebsiteHome", "website")]
    [InlineData(null, "application", null, null, "application")]
    [InlineData(null, "appHostWebsite", "application", "WebsiteHome", "application")]
    [InlineData(null, "application", "website", "ApplicationSite", "website")]
    [InlineData("website", "appHostWebsite", "application", "ApplicationSite", "website")]
    [InlineData(" Application ", "appHostWebsite", "website", null, "application")]
    [InlineData("removed-theme", "appHostWebsite", "website", "ApplicationSite", "website")]
    [InlineData("removed-theme", "appHostWebsite", null, "ApplicationSite", "application")]
    public void A_site_takes_its_explicit_theme_then_the_view_default_then_its_template_then_its_type(
        string? alias, string rootType, string? viewDefault, string? siteTemplate, string expected)
    {
        var fallback = viewDefault switch { "application" => SiteThemeCatalog.Application, "website" => SiteThemeCatalog.Website, _ => null };
        Assert.Equal(expected, new SiteThemeCatalog([]).Resolve(alias, rootType, fallback, siteTemplate).Alias);
    }

    [Fact]
    public void A_view_default_uses_a_registered_replacement_of_that_theme()
    {
        var replacement = new SiteTheme("website", "Department.cshtml", "DepartmentSignIn.cshtml");
        Assert.Same(replacement, new SiteThemeCatalog([replacement]).Resolve(null, "application", SiteThemeCatalog.Website, "ApplicationSite"));
    }

    [Fact]
    public void A_registered_theme_needs_no_new_document_type()
    {
        var theme = new SiteTheme("department", "Department.cshtml", "DepartmentSignIn.cshtml");
        var themes = new SiteThemeCatalog([theme]);
        foreach (var type in new[] { "application", "appHostWebsite" })
        {
            var selected = themes.Resolve("department", type);
            Assert.Same(theme, selected);
            Assert.Equal("Department.cshtml", selected.LayoutFor(true));
            Assert.Equal("DepartmentSignIn.cshtml", selected.LayoutFor(false));
        }
    }
}
