namespace UmbracoAppHost.Authentication;

/// <summary>Configuration for Umbraco's native backoffice external-login options.</summary>
public sealed class BackOfficeAutoLinkSettings
{
    public bool AutoLinkExternalAccount { get; set; } = true;

    /// <summary>User group aliases for newly created users. Empty grants no backoffice groups.</summary>
    public string[] DefaultUserGroups { get; set; } = [];

    public string? DefaultCulture { get; set; }

    public bool AllowManualLinking { get; set; } = true;
}

/// <summary>Configuration for Umbraco's native member external-login options.</summary>
public sealed class MemberAutoLinkSettings
{
    public bool AutoLinkExternalAccount { get; set; } = true;

    public bool DefaultIsApproved { get; set; } = true;

    public string DefaultMemberTypeAlias { get; set; } = "Member";

    public string? DefaultCulture { get; set; }

    public string[] DefaultMemberGroups { get; set; } = [];

    public bool ExternalOnly { get; set; }
}
