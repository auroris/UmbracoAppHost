namespace UmbracoAppHost.Authentication;

public enum BackOfficeLoginFlow
{
    Form,
    IdentityServer,
    // Value 2 belonged to the removed Windows flow; preserve the remaining numeric values.
    Debug = 3,
    Auto,
}

/// <summary>Chooses the initial backoffice sign-in flow. Provider settings still control which SSO buttons are available.</summary>
public sealed class BackOfficeLoginSettings
{
    public const string SectionName = "BackOfficeLogin";

    public BackOfficeLoginFlow DefaultFlow { get; set; } = BackOfficeLoginFlow.Auto;

    /// <summary>Overrides DefaultFlow while Umbraco:CMS:Hosting:Debug is true. Null uses DefaultFlow.</summary>
    public BackOfficeLoginFlow? DebugFlow { get; set; } = BackOfficeLoginFlow.Debug;

    public BackOfficeLoginFlow GetEffectiveFlow(bool debug)
    {
        var flow = debug ? DebugFlow ?? DefaultFlow : DefaultFlow;
        // Even an explicit Debug selection must never bypass passwords outside debug mode.
        return flow == BackOfficeLoginFlow.Debug && !debug ? BackOfficeLoginFlow.Form : flow;
    }

    public string? GetAutoRedirectScheme(bool debug, IdentityServerSettings identityServer)
        => GetEffectiveFlow(debug) switch
        {
            BackOfficeLoginFlow.Auto when identityServer.Enabled && identityServer.BackOfficeEnabled => identityServer.BackOfficeScheme,
            BackOfficeLoginFlow.IdentityServer when identityServer.Enabled && identityServer.BackOfficeEnabled => identityServer.BackOfficeScheme,
            BackOfficeLoginFlow.IdentityServer => throw new InvalidOperationException("BackOfficeLogin selects IdentityServer, but IdentityServer:Enabled and IdentityServer:BackOfficeEnabled must both be true."),
            _ => null,
        };
}
