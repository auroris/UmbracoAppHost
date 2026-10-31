using System.Xml.Linq;

namespace UmbracoAppHost.Hosting;

/// <summary>
/// The sample nodes of the saved export: everything under Sample Application and Website theme demo, and the Data
/// sources root's Orders sources, Orders report definition and order lists. The root itself and the Welcome report
/// definition belong to every site. A host that sets <see cref="Setting"/> to false leaves these out of its working
/// uSync folder, so its first boot never imports them.
/// </summary>
public static class SampleContent
{
    /// <summary>Whether a host gets the samples: this content, the <c>sample.orders</c> resource and the <c>orders</c> report.</summary>
    public const string Setting = "UmbracoAppHost:Samples";

    public static readonly IReadOnlySet<Guid> Keys = new HashSet<Guid>
    {
        new("2b6e4d1a-7c3f-4f0e-9a51-0d2c8e4f1a05"), // /SampleApplication
        new("2b6e4d1a-7c3f-4f0e-9a51-0d2c8e4f1a06"), // /SampleApplication/About
        new("2b6e4d1a-7c3f-4f0e-9a51-0d2c8e4f1a07"), // /SampleApplication/Orders
        new("33796329-b17d-b566-849c-03401489c175"), // /SampleApplication/OrdersForm
        new("dfa6b0e6-ff22-1969-c26c-6526f38715bb"), // /SampleApplication/OrdersInteractive
        new("0637733a-446d-ae2a-5f5b-81c740dfe821"), // /SampleApplication/Layouts
        new("d8617a8c-a488-e130-9de0-ba26015573d6"), // /SampleApplication/ContentBlocks
        new("02e367e2-5764-7de6-4563-77cf40dff99d"), // /SampleApplication/orders-paper-report
        new("46c6d75c-6ca1-ce7e-7113-12daa137f1dc"), // /SampleApplication/orders-paper-report-fr
        new("47ac6c79-ea57-f66b-9333-36226899f3bc"), // /WebsiteThemeDemo
        new("9ecfe96b-aeb2-a429-0c24-7978b8d0089f"), // /WebsiteThemeDemo/WebsiteAuthoring
        new("a700039a-bd68-9cc3-ab49-e4e50cb66e7b"), // /WebsiteThemeDemo/WebsiteLayouts
        new("d9a69f9f-f8e4-dc51-76c8-9d1ec178b238"), // /WebsiteThemeDemo/WebsiteContentBlocks
        new("1dff8fb1-7b3b-928e-f192-912465c45e73"), // /SampleApplication/OrdersSchedule
        new("930b1ec9-c108-28f8-1729-4edc4afb5273"), // /WebsiteThemeDemo/WebsiteOrdersSchedule
        new("96b50a62-a1fc-26b8-4ddf-7cc26eb56d55"), // /DataSources/Orders
        new("a23e1227-4a80-2bc1-cd86-77f3b4c6f797"), // /DataSources/Orderslines
        new("3b3f9622-a0ac-d50a-513b-a6176d4db760"), // /DataSources/OrdersPrintRows
        new("6da25a99-e438-80b2-9fc0-e4d5b8c139fd"), // /DataSources/Ordersreport
        new("438190db-53c7-ab6d-21fd-59da3dad5334"), // /DataSources/Orderregionchoices
        new("75feb3fa-9967-9950-29fe-05b53c819228"), // /DataSources/Orderstatuschoices
        new("27cd6880-80bd-80ca-4f58-ccc0f3023641"), // /DataSources/Ordersschedulerows
        new("c0833c94-0452-3875-63ad-8cd4e501f0f1"), // /DataSources/Ordersschedulebars
        new("44b5551d-a199-f6ce-265d-3bac021d56bd"), // /DataSources/Ordersschedule
    };

    /// <summary>Whether a uSync <c>Content/</c> file is one of the samples, by its node's <c>Key</c>.</summary>
    public static bool IsSample(XElement contentFile)
        => Guid.TryParse(contentFile.Attribute("Key")?.Value, out var key) && Keys.Contains(key);
}
