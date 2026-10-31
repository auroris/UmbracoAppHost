namespace UmbracoAppHost.Authentication;

/// <summary>
/// Names used only to find stored links from the removed direct Windows provider.
/// These are not registered authentication schemes and new directory accounts use IdentityServer.
/// </summary>
internal static class LegacyWindowsLoginSchemes
{
    public const string BackOffice = "Umbraco.Windows";
    public const string Member = "UmbracoMembers.Windows";
}
