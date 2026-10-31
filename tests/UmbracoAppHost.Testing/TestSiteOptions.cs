using System.Reflection;
using AppBlocks.Connections;
using Microsoft.AspNetCore.Builder;

namespace UmbracoAppHost.Testing;

/// <summary>How <see cref="TestSite.StartAsync"/> boots a host.</summary>
public sealed class TestSiteOptions
{
    /// <summary>
    /// The host's own assembly, such as <c>typeof(Program).Assembly</c>. Its name becomes the
    /// application name, which static web assets and views are found by.
    /// </summary>
    public required Assembly Application { get; init; }

    /// <summary>
    /// The host project's folder, which holds its <c>appsettings.json</c>, <c>uSync/</c> export, <c>Views/</c> and
    /// <c>Schema/</c>; when null, <see cref="TestSite.FindProjectFolder"/> finds it from the application's location.
    /// </summary>
    public string? ProjectFolder { get; init; }

    /// <summary>The host's <c>IUmbracoAppHost</c>; when null, <see cref="TestSite.FindHost"/> finds it in the application.</summary>
    public Type? Host { get; init; }

    /// <summary>The content root and the parent of the instance folder; a new temporary folder, deleted with the site, when null.</summary>
    public string? Root { get; init; }

    /// <summary>
    /// Starts from a copy of an install and first-boot import made once per process for these options, which is what
    /// takes a new site most of its time. Each site still has its own copy, so what a test changes stays with it. It
    /// does not apply with <see cref="Root"/> or <see cref="ConfigureConnections"/>. Set it to false to test first boot
    /// itself, or anything that <see cref="ConfigureBuilder"/> changes about it.
    /// </summary>
    public bool UseInstallSnapshot { get; init; } = true;

    public string EnvironmentName { get; init; } = "Development";

    /// <summary><c>Umbraco:CMS:Hosting:ApplicationVirtualPath</c>, such as <c>/wing</c>; null hosts the site at the root.</summary>
    public string? VirtualPath { get; init; }

    /// <summary>The unattended install's administrator password.</summary>
    public string AdminPassword { get; init; } = "Local-test-only-Site91!";

    /// <summary><c>uSync:Settings:FirstBootGroup</c>: <c>All</c> imports the saved export's content, <c>Settings</c> only its definitions.</summary>
    public string FirstBootGroup { get; init; } = "All";

    /// <summary>Further configuration, applied after the project's settings and the site's own test settings so it can override them.</summary>
    public IDictionary<string, string?> Settings { get; } = new Dictionary<string, string?>();

    /// <summary>Registers services and anything else on the builder before the host's and the library's registrations.</summary>
    public Action<WebApplicationBuilder>? ConfigureBuilder { get; init; }

    /// <summary>Registers further connections, after the host's own.</summary>
    public Action<AppBlocksBuilder>? ConfigureConnections { get; init; }

    /// <summary>Maps test-only endpoints after <c>UseUmbracoAppHostAsync</c>, before the site starts.</summary>
    public Action<WebApplication>? ConfigureApp { get; init; }

    /// <summary>
    /// Maps <c>GET /__tests/login/{id}?scheme=member|backoffice&amp;name=...</c>, which signs that id in with
    /// the member cookie or the cookie Umbraco exposes a backoffice sign-in to the website with, and no
    /// password. Over plain HTTP the cookie loses its Secure attribute so a cookie container sends it.
    /// Only a test host ever has this route.
    /// </summary>
    public bool TestLoginRoute { get; init; } = true;

    /// <summary>Waits until the startup handlers have given the built-in administrator its configured username.</summary>
    public bool WaitForAdministratorRename { get; init; } = true;

    public TimeSpan InstallTimeout { get; init; } = TimeSpan.FromMinutes(3);
}
