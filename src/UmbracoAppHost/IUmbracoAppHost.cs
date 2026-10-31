using AppBlocks.Connections;
using Umbraco.Cms.Web.Common.ApplicationBuilder;

namespace UmbracoAppHost;

/// <summary>
/// A host's own registrations, kept on its <c>Program</c> class so that <c>Program.cs</c> and a test site apply the
/// same ones: <c>builder.AddUmbracoAppHost&lt;Program&gt;()</c> and <c>app.UseUmbracoAppHostAsync&lt;Program&gt;()</c>
/// call them, and <c>TestSite</c> finds the class in the host's assembly. A member the host does not implement does
/// nothing. Paths to the host's own files start from <see cref="AppHostProject.ProjectRoot"/>, which a test site
/// points at the host project while its content root is a temporary folder.
/// </summary>
public interface IUmbracoAppHost
{
    /// <summary>Registers the host's services, before the library's.</summary>
    static virtual void ConfigureServices(WebApplicationBuilder builder) { }

    /// <summary>Registers the host's connections, such as its application database: the <c>configureConnections</c> callback.</summary>
    static virtual void ConfigureConnections(WebApplicationBuilder builder, AppBlocksBuilder connections) { }

    /// <summary>Adds middleware after Umbraco's and the reports' authentication: the <c>configureMiddleware</c> callback.</summary>
    static virtual void ConfigureMiddleware(IUmbracoApplicationBuilderContext umbraco) { }

    /// <summary>Maps the host's endpoints after Umbraco's: the <c>configureEndpoints</c> callback.</summary>
    static virtual void ConfigureEndpoints(IUmbracoEndpointBuilderContext umbraco) { }
}
