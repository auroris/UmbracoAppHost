using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// The registered <see cref="IMemberEditingService"/>: Umbraco's own service, except that deleting a
/// member who has signed in, or who is linked to an external login, retires the member instead
/// (<see cref="AccountRetirement"/>), so a new person given the same username cannot become the old
/// one. Members who never signed in, and external-only members, which have no member row to keep,
/// are deleted by Umbraco; <see cref="AccountsSettings.RetireDeletedMembers"/> switches the behaviour
/// off, and every other member forwards to Umbraco's service unchanged.
/// </summary>
public sealed class RetiringMemberEditingService(
    IMemberEditingService inner,
    IMemberService memberService,
    Lazy<AccountRetirement> retirement,
    IOptionsMonitor<AccountsSettings> settings) : IMemberEditingService
{
    /// <summary>The backoffice Delete. A retired member is reported as deleted, as the backoffice expects.</summary>
    public async Task<Attempt<IMember?, MemberEditingStatus>> DeleteAsync(Guid key, Guid userKey)
    {
        if (!settings.CurrentValue.RetireDeletedMembers)
            return await inner.DeleteAsync(key, userKey);

        var member = memberService.GetById(key);
        if (member is null || !retirement.Value.HasSignedIn(member))
            return await inner.DeleteAsync(key, userKey);

        var retired = await retirement.Value.RetireMemberAsync(member, userKey);
        var status = new MemberEditingStatus
        {
            MemberEditingOperationStatus = retired ? MemberEditingOperationStatus.Success : MemberEditingOperationStatus.CancelledByNotificationHandler,
            ContentEditingOperationStatus = retired ? ContentEditingOperationStatus.Success : ContentEditingOperationStatus.CancelledByNotification,
        };
        return retired
            ? Attempt.SucceedWithStatus<IMember?, MemberEditingStatus>(status, member)
            : Attempt.FailWithStatus<IMember?, MemberEditingStatus>(status, member);
    }

    // Everything below forwards to Umbraco's service unchanged, including the members the interface
    // implements by default, which Umbraco's service overrides.

    public Task<IMember?> GetAsync(Guid key) => inner.GetAsync(key);

    public Task<Attempt<ContentValidationResult, ContentEditingOperationStatus>> ValidateCreateAsync(MemberCreateModel createModel)
        => inner.ValidateCreateAsync(createModel);

    public Task<Attempt<ContentValidationResult, ContentEditingOperationStatus>> ValidateUpdateAsync(Guid key, MemberUpdateModel updateModel)
        => inner.ValidateUpdateAsync(key, updateModel);

    public Task<Attempt<MemberCreateResult, MemberEditingStatus>> CreateAsync(MemberCreateModel createModel, IUser user)
        => inner.CreateAsync(createModel, user);

    public Task<Attempt<MemberUpdateResult, MemberEditingStatus>> UpdateAsync(Guid key, MemberUpdateModel updateModel, IUser user)
        => inner.UpdateAsync(key, updateModel, user);

    public Task<bool> IsExternalMemberAsync(Guid key) => inner.IsExternalMemberAsync(key);

    public Task<ExternalMemberIdentity?> GetExternalMemberAsync(Guid key) => inner.GetExternalMemberAsync(key);
}
