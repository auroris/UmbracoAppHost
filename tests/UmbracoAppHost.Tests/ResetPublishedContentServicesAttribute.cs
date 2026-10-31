using System.Reflection;
using Xunit.v3;

namespace UmbracoAppHost.Tests;

/// <summary>Isolates page-rendering tests from Umbraco's cached services belonging to a disposed test host.</summary>
public sealed class ResetPublishedContentServicesAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test) => UmbracoTestSupport.ResetPublishedContentServices();
    public override void After(MethodInfo methodUnderTest, IXunitTest test) => UmbracoTestSupport.ResetPublishedContentServices();
}
