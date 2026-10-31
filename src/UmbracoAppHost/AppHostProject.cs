namespace UmbracoAppHost;

/// <summary>
/// Where a host's own files are: its committed uSync export, its schema scripts and anything else its <c>Program.cs</c>
/// names by path. That is the content root unless <c>UmbracoAppHost:ProjectRoot</c> names another folder, as a test
/// site does: it keeps its content root in a temporary folder and points this setting at the host project.
/// </summary>
public static class AppHostProject
{
    public const string RootSetting = "UmbracoAppHost:ProjectRoot";

    /// <summary>The project folder as an absolute path; a relative setting is relative to the content root.</summary>
    public static string Root(IConfiguration configuration, string contentRoot)
        => configuration[RootSetting] is { Length: > 0 } setting ? Path.GetFullPath(setting, contentRoot) : contentRoot;

    /// <summary>The host's project folder, such as <c>Path.Combine(builder.ProjectRoot(), "Schema")</c> for its schema scripts.</summary>
    public static string ProjectRoot(this WebApplicationBuilder builder) => Root(builder.Configuration, builder.Environment.ContentRootPath);
}
