using System.Reflection;
using Xunit.v3;

namespace UmbracoAppHost.Tests;

/// <summary>
/// Resets Umbraco's cached backoffice path around a test that boots its own hosts rather than a
/// <see cref="TestSite"/>, which resets it itself; see <see cref="UmbracoTestSupport.ResetBackOfficePath"/>.
/// </summary>
internal sealed class ResetBackOfficePathAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test) => UmbracoTestSupport.ResetBackOfficePath();
    public override void After(MethodInfo methodUnderTest, IXunitTest test) => UmbracoTestSupport.ResetBackOfficePath();
}
