using Umbraco.Cms.Core.Configuration.Models;

namespace UmbracoAppHost;

/// <summary>
/// Branding for the backoffice login screen. A site without any gets Umbraco's own login screen.
/// </summary>
/// <remarks>
/// A site or theme library ships the logo and the panel image as static web assets under
/// <c>wwwroot/umbraco</c>, where Umbraco resolves its login images, and points the three login image
/// settings at them here. A site that configures its own under <c>Umbraco:CMS:Content</c> keeps them,
/// so a theme can brand a site by default and the site can still override.
/// </remarks>
public static class LoginScreen
{
    /// <param name="logoImage">The logo, relative to <c>wwwroot/umbraco</c>, for both login logo settings.</param>
    /// <param name="panelImage">The panel image beside the sign-in form, relative to <c>wwwroot/umbraco</c>.</param>
    public static IServiceCollection AddLoginScreen(this IServiceCollection services, IConfiguration configuration, string logoImage, string panelImage)
    {
        var configured = configuration.GetSection("Umbraco:CMS:Content");
        return services.Configure<ContentSettings>(content =>
        {
            if (string.IsNullOrEmpty(configured["LoginLogoImage"]))
                content.LoginLogoImage = logoImage;
            if (string.IsNullOrEmpty(configured["LoginLogoImageAlternative"]))
                content.LoginLogoImageAlternative = logoImage;
            if (string.IsNullOrEmpty(configured["LoginBackgroundImage"]))
                content.LoginBackgroundImage = panelImage;
        });
    }
}
