using Microsoft.Extensions.Configuration;
using UmbracoAppHost.Authentication;

namespace UmbracoAppHost.Tests;

public sealed class AccountsSettingsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Test Team .*")]
    [InlineData("^Test Team (Admin|Users)$")]
    public void Accounts_section_binds_and_accepts_the_optional_regex(string? pattern)
    {
        var values = new Dictionary<string, string?>
        {
            ["Accounts:BackOfficeUsersManagedBy"] = "ActiveDirectory",
            ["Accounts:MembersManagedBy"] = "ActiveDirectory",
        };
        if (pattern is not null) values["Accounts:ActiveDirectoryGroupPattern"] = pattern;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var settings = configuration.GetSection(AccountsSettings.SectionName).Get<AccountsSettings>()!;

        Assert.Equal(pattern, settings.ActiveDirectoryGroupPattern);
        Assert.True(new AccountsSettingsValidator().Validate(null, settings).Succeeded);
    }

    [Theory]
    [InlineData(AccountSource.ActiveDirectory, AccountSource.Umbraco, true)]
    [InlineData(AccountSource.Umbraco, AccountSource.ActiveDirectory, true)]
    [InlineData(AccountSource.ActiveDirectory, AccountSource.ActiveDirectory, true)]
    [InlineData(AccountSource.Umbraco, AccountSource.Umbraco, false)]
    public void Invalid_regex_is_rejected_when_either_audience_uses_Active_Directory(AccountSource users, AccountSource members, bool invalid)
    {
        var result = new AccountsSettingsValidator().Validate(null, new AccountsSettings
        {
            BackOfficeUsersManagedBy = users,
            MembersManagedBy = members,
            ActiveDirectoryGroupPattern = "[",
        });

        Assert.Equal(invalid, result.Failed);
        if (invalid) Assert.Contains("Accounts:ActiveDirectoryGroupPattern", result.FailureMessage);
    }
}
