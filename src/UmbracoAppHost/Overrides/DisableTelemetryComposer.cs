using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Cms.Infrastructure.BackgroundJobs.Jobs;

namespace UmbracoAppHost.Overrides;

/// <summary>
/// Removes Umbraco's daily telemetry report to telemetry.umbraco.com.
/// </summary>
/// <remarks>
/// The report is a recurring background job that posts the site identifier every day, and
/// the version and package list at telemetry levels above Minimal. There is no configuration
/// switch for it: the lowest level is Minimal, not off. Umbraco registers the job with
/// <c>AddRecurringBackgroundJob&lt;ReportSiteJob&gt;()</c>, which is a plain singleton
/// registration, so removing that registration before the container is built is enough.
/// Boot fails if the registration is not found, so an Umbraco upgrade that changes how the job
/// is registered cannot quietly turn telemetry back on.
/// </remarks>
public sealed class DisableTelemetryComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        var services = builder.Services;
        var registrations = services
            .Where(d => d.ServiceType == typeof(IRecurringBackgroundJob) && d.ImplementationType == typeof(ReportSiteJob))
            .ToList();

        if (registrations.Count == 0)
        {
            throw new InvalidOperationException(
                $"{nameof(ReportSiteJob)} is no longer registered as an {nameof(IRecurringBackgroundJob)}; " +
                $"update {nameof(DisableTelemetryComposer)} for this Umbraco version.");
        }

        foreach (var registration in registrations)
            services.Remove(registration);
    }
}
