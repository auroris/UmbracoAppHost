using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Services;
using UmbracoAppHost.Testing;

namespace UmbracoAppHost.Tests;

/// <summary>A site started from the install snapshot has the first boot's content and nothing of the sites before it.</summary>
[Collection("Umbraco hosts")]
public sealed class TestSiteSnapshotTests
{
    [Fact]
    [ResetPublishedContentServices]
    public async Task Sites_share_an_install_but_not_what_a_test_changes()
    {
        string root;
        await using (var first = await TestSite.StartAsync(new TestSiteOptions { Application = typeof(Program).Assembly }, TestContext.Current.CancellationToken))
        {
            root = first.Root;
            first.CreateMember("only-in-the-first", "only-in-the-first@example.invalid", "Only in the first site");
            using var scope = first.CreateScope();
            // Published as soon as the site is up: the import came with the copy rather than running behind the start.
            Assert.True(scope.ServiceProvider.GetRequiredService<IContentService>().GetById(ApplicationStarter.FormPageKey)!.Published);
        }

        await using var second = await TestSite.StartAsync(new TestSiteOptions { Application = typeof(Program).Assembly }, TestContext.Current.CancellationToken);
        Assert.NotEqual(root, second.Root);
        using var secondScope = second.CreateScope();
        Assert.Null(secondScope.ServiceProvider.GetRequiredService<IMemberService>().GetByUsername("only-in-the-first"));
        Assert.True(secondScope.ServiceProvider.GetRequiredService<IContentService>().GetById(ApplicationStarter.FormPageKey)!.Published);
    }

    [Fact]
    [ResetPublishedContentServices]
    public async Task A_site_can_still_run_first_boot_itself()
    {
        await using var site = await TestSite.StartAsync(new TestSiteOptions { Application = typeof(Program).Assembly, UseInstallSnapshot = false }, TestContext.Current.CancellationToken);
        await site.WaitForPublishedAsync(ApplicationStarter.FormPageKey, TimeSpan.FromMinutes(2));
    }
}
