using System.Runtime.Versioning;
using ActiveDirectory;
using Microsoft.Extensions.Options;
using UmbracoAppHost.DirectoryAccounts;

namespace UmbracoAppHost.Tests;

/// <summary>Regression checks for the people lookup, without a domain.</summary>
[SupportedOSPlatform("windows")]
public sealed class DirectoryLookupTests
{
    private static ActiveDirectoryLookup Lookup() => new(new StaticOptionsMonitor(new DirectoryOptions()));

    [Fact]
    public void Caller_filter_fetches_extra_attributes_and_searches_only_display_name()
    {
        var search = Lookup().CreatePeopleSearch(new DirectoryOptions(), ["Smith*"], DirectoryScope.Global, false, new TestFilter(), DirectorySearchMode.DisplayName);
        Assert.Contains("extensionAttribute1", search.PropertiesToLoad);
        Assert.Contains("(givenName=*)", search.AdditionalFilter);
        Assert.Contains(@"(displayName=*Smith\2a*)", search.AdditionalFilter);
        Assert.DoesNotContain("sAMAccountName", search.AdditionalFilter);
        Assert.DoesNotContain("employeeID", search.AdditionalFilter);
        var normal = Lookup().CreatePeopleSearch(new DirectoryOptions(), ["Smith"], DirectoryScope.Local, false);
        Assert.DoesNotContain("(givenName=*)", normal.AdditionalFilter);
        Assert.DoesNotContain("extensionAttribute1", normal.PropertiesToLoad);
        Assert.Contains("sAMAccountName", normal.AdditionalFilter);
    }

    private sealed class TestFilter : IDirectoryUserFilter
    {
        public IReadOnlyList<string> AdditionalProperties => ["extensionAttribute1"];
        public string LdapFilter => "(givenName=*)";
        public bool IsIncluded(ADUser user) => true;
    }

    [Fact]
    public void Picker_search_requires_enabled_people_without_telephone_directory_rules()
    {
        var search = Lookup().CreatePeopleSearch(new DirectoryOptions(), [], DirectoryScope.Global, includeDisabled: false);
        Assert.Equal("&(objectCategory=person)(!(userAccountControl:1.2.840.113556.1.4.803:=2))", search.AdditionalFilter);
        Assert.Contains(ADProperties.User.EmployeeID, search.PropertiesToLoad);
        Assert.Contains(ADProperties.ObjectSid, search.PropertiesToLoad);
        Assert.Contains(ADProperties.User.Email, search.PropertiesToLoad);
    }

    [Theory]
    [InlineData("j smith")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Global_search_is_validated_before_contacting_the_directory(string query)
        => await Assert.ThrowsAsync<DirectoryQueryException>(() => Lookup().SearchUsersAsync(query, scope: DirectoryScope.Global, cancellationToken: TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("not an id")]
    [InlineData("*")]
    [InlineData("S-1-5-21-x")]
    public async Task User_lookup_rejects_malformed_identifiers(string id)
        => await Assert.ThrowsAsync<DirectoryQueryException>(() => Lookup().FindUserAsync(id, TestContext.Current.CancellationToken));

    [Fact]
    public void Sid_lookup_matches_the_binary_directory_attribute()
    {
        var id = DirectoryId.Parse("s-1-5-21-1-2-3-1001", allowAccountName: true);
        Assert.Equal("S-1-5-21-1-2-3-1001", id.Value);
        Assert.Equal(@"(objectSid=\01\05\00\00\00\00\00\05\15\00\00\00\01\00\00\00\02\00\00\00\03\00\00\00\e9\03\00\00)", id.Assertion());
    }

    [Fact]
    public async Task Cancellation_is_honoured_before_querying()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Lookup().SearchUsersAsync("smith", cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Lookup().FindUserAsync("S-1-5-21-1-2-3-1001", cancelled.Token));
    }

    private sealed class StaticOptionsMonitor(DirectoryOptions value) : IOptionsMonitor<DirectoryOptions>
    {
        public DirectoryOptions CurrentValue => value;
        public DirectoryOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<DirectoryOptions, string?> listener) => null;
    }
}
