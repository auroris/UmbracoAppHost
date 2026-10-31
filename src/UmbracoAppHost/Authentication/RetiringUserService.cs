using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Persistence.Querying;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;

namespace UmbracoAppHost.Authentication;

/// <summary>
/// The registered <see cref="IUserService"/>: Umbraco's own user service, except that deleting a
/// user who has signed in, or who is linked to an external login, retires the user instead
/// (<see cref="AccountRetirement"/>). Umbraco refuses to delete such a user, because history refers
/// to it, so the backoffice Delete would otherwise only report an error. Users who never signed in
/// are still deleted by Umbraco, <see cref="AccountsSettings.RetireDeletedUsers"/> switches the
/// behaviour off, and every other member forwards to Umbraco's service unchanged. Umbraco's
/// <c>UserService</c> is internal, hence a decorator rather than a subclass.
/// </summary>
public sealed class RetiringUserService(
    IUserService inner,
    Lazy<AccountRetirement> retirement,
    ICoreScopeProvider scopeProvider,
    IOptionsMonitor<AccountsSettings> settings,
    ILogger<RetiringUserService> logger) : IUserService
{
    /// <summary>
    /// The backoffice Delete, single or bulk. Umbraco's own checks come first, in its order and with
    /// its statuses (performing user, self, missing users), then the built-in administrator, which is
    /// never removed. Users with sign-in history are retired and the others deleted by Umbraco, in
    /// one scope: as in Umbraco, either every user is handled or none is.
    /// </summary>
    public async Task<UserOperationStatus> DeleteAsync(Guid performingUserKey, ISet<Guid> keys)
    {
        if (!settings.CurrentValue.RetireDeletedUsers || keys.Count == 0)
            return await inner.DeleteAsync(performingUserKey, keys);

        using var scope = scopeProvider.CreateCoreScope();
        var performingUser = await inner.GetAsync(performingUserKey);
        if (performingUser is null) return UserOperationStatus.MissingUser;
        if (keys.Contains(performingUser.Key)) return UserOperationStatus.CannotDeleteSelf;
        var users = (await inner.GetAsync(keys)).ToArray();
        if (users.Length != keys.Count) return UserOperationStatus.UserNotFound;
        if (users.Any(user => user.Key == Constants.Security.SuperUserKey)) return UserOperationStatus.CannotDelete;

        var bySignInHistory = users.ToLookup(retirement.Value.HasSignedIn);
        var neverSignedIn = bySignInHistory[false].Select(user => user.Key).ToHashSet();
        if (neverSignedIn.Count > 0)
        {
            var deleted = await inner.DeleteAsync(performingUserKey, neverSignedIn);
            if (deleted != UserOperationStatus.Success) return deleted;
        }
        foreach (var user in bySignInHistory[true])
        {
            var retired = await retirement.Value.RetireUserAsync(user, performingUserKey);
            if (retired != UserOperationStatus.Success) return retired;
        }

        scope.Complete();
        return UserOperationStatus.Success;
    }

    /// <summary>
    /// The single-user Delete the backoffice calls. Umbraco implements it in the interface by calling
    /// the set overload on the same instance, so it must reach this class's overload rather than
    /// be forwarded, which would bypass retirement.
    /// </summary>
    public Task<UserOperationStatus> DeleteAsync(Guid performingUserKey, Guid key)
        => DeleteAsync(performingUserKey, new HashSet<Guid> { key });

    /// <summary>
    /// The older synchronous API. A permanent delete of a user with sign-in history retires it
    /// instead; anything else, including a disable (<paramref name="deletePermanently"/> false),
    /// is Umbraco's.
    /// </summary>
    public void Delete(IUser user, bool deletePermanently)
    {
        if (!deletePermanently || !settings.CurrentValue.RetireDeletedUsers || !retirement.Value.HasSignedIn(user))
        {
            inner.Delete(user, deletePermanently);
            return;
        }

        var status = retirement.Value.RetireUserAsync(user).GetAwaiter().GetResult();
        if (status != UserOperationStatus.Success)
            logger.LogWarning("Backoffice user {Username} was neither deleted nor retired: {Status}.", user.Username, status);
    }

    // Everything below forwards to Umbraco's service unchanged, including the members the interface
    // implements by default, which Umbraco's service overrides.

    public Guid CreateLoginSession(int userId, string requestingIpAddress) => inner.CreateLoginSession(userId, requestingIpAddress);

    public bool ValidateLoginSession(int userId, Guid sessionId) => inner.ValidateLoginSession(userId, sessionId);

    public void ClearLoginSession(Guid sessionId) => inner.ClearLoginSession(sessionId);

    public int ClearLoginSessions(int userId) => inner.ClearLoginSessions(userId);

    public IDictionary<UserState, int> GetUserStates() => inner.GetUserStates();

    public Task<Attempt<UserCreationResult, UserOperationStatus>> CreateAsync(Guid performingUserKey, UserCreateModel model, bool approveUser = false)
        => inner.CreateAsync(performingUserKey, model, approveUser);

    public Task<Attempt<UserInvitationResult, UserOperationStatus>> InviteAsync(Guid performingUserKey, UserInviteModel model)
        => inner.InviteAsync(performingUserKey, model);

    public Task<Attempt<UserOperationStatus>> VerifyInviteAsync(Guid userKey, string token) => inner.VerifyInviteAsync(userKey, token);

    public Task<Attempt<PasswordChangedModel, UserOperationStatus>> CreateInitialPasswordAsync(Guid userKey, string token, string password)
        => inner.CreateInitialPasswordAsync(userKey, token, password);

    public Task<Attempt<IUser?, UserOperationStatus>> UpdateAsync(Guid performingUserKey, UserUpdateModel model) => inner.UpdateAsync(performingUserKey, model);

    public Task<Attempt<IUser?, UserOperationStatus>> UpdateProfileAsync(Guid userKey, UserUpdateProfileModel model) => inner.UpdateProfileAsync(userKey, model);

    public Task<UserOperationStatus> SetAvatarAsync(Guid userKey, Guid temporaryFileKey) => inner.SetAvatarAsync(userKey, temporaryFileKey);

    public Task<UserOperationStatus> DisableAsync(Guid performingUserKey, ISet<Guid> keys) => inner.DisableAsync(performingUserKey, keys);

    public Task<UserOperationStatus> EnableAsync(Guid performingUserKey, ISet<Guid> keys) => inner.EnableAsync(performingUserKey, keys);

    public Task<Attempt<UserUnlockResult, UserOperationStatus>> UnlockAsync(Guid performingUserKey, params Guid[] keys) => inner.UnlockAsync(performingUserKey, keys);

    public Task<Attempt<PasswordChangedModel, UserOperationStatus>> ChangePasswordAsync(Guid performingUserKey, ChangeUserPasswordModel model)
        => inner.ChangePasswordAsync(performingUserKey, model);

    public Task<UserOperationStatus> ClearAvatarAsync(Guid userKey) => inner.ClearAvatarAsync(userKey);

    public Task<Attempt<ICollection<IIdentityUserLogin>, UserOperationStatus>> GetLinkedLoginsAsync(Guid userKey) => inner.GetLinkedLoginsAsync(userKey);

    public Task<Attempt<PagedModel<IUser>?, UserOperationStatus>> GetAllAsync(Guid performingUserKey, int skip, int take)
        => inner.GetAllAsync(performingUserKey, skip, take);

    public Task<Attempt<PagedModel<IUser>, UserOperationStatus>> FilterAsync(
        Guid userKey,
        UserFilter filter,
        int skip = 0,
        int take = 100,
        UserOrder orderBy = UserOrder.UserName,
        Direction orderDirection = Direction.Ascending)
        => inner.FilterAsync(userKey, filter, skip, take, orderBy, orderDirection);

    public IEnumerable<IUser> GetAll(
        long pageIndex,
        int pageSize,
        out long totalRecords,
        string orderBy,
        Direction orderDirection,
        UserState[]? userState = null,
        string[]? includeUserGroups = null,
        string[]? excludeUserGroups = null,
        IQuery<IUser>? filter = null)
        => inner.GetAll(pageIndex, pageSize, out totalRecords, orderBy, orderDirection, userState, includeUserGroups, excludeUserGroups, filter);

    public IEnumerable<IUser> GetAll(
        long pageIndex,
        int pageSize,
        out long totalRecords,
        string orderBy,
        Direction orderDirection,
        UserState[]? userState = null,
        string[]? userGroups = null,
        string? filter = null)
        => inner.GetAll(pageIndex, pageSize, out totalRecords, orderBy, orderDirection, userState, userGroups, filter);

    public IProfile? GetProfileById(int id) => inner.GetProfileById(id);

    public IProfile? GetProfileByUserName(string username) => inner.GetProfileByUserName(username);

    public Task<IUser?> GetAsync(Guid key) => inner.GetAsync(key);

    public Task<IEnumerable<IUser>> GetAsync(IEnumerable<Guid> keys) => inner.GetAsync(keys);

    public IUser? GetUserById(int id) => inner.GetUserById(id);

    public IEnumerable<IUser> GetUsersById(params int[]? ids) => inner.GetUsersById(ids);

    public void DeleteSectionFromAllUserGroups(string sectionAlias) => inner.DeleteSectionFromAllUserGroups(sectionAlias);

    public Task<Attempt<IEnumerable<NodePermissions>, UserOperationStatus>> GetPermissionsAsync(Guid userKey, params Guid[] nodeKeys)
        => inner.GetPermissionsAsync(userKey, nodeKeys);

    public Task<Attempt<IEnumerable<NodePermissions>, UserOperationStatus>> GetMediaPermissionsAsync(Guid userKey, IEnumerable<Guid> mediaKeys)
        => inner.GetMediaPermissionsAsync(userKey, mediaKeys);

    public Task<Attempt<IEnumerable<NodePermissions>, UserOperationStatus>> GetDocumentPermissionsAsync(Guid userKey, IEnumerable<Guid> contentKeys)
        => inner.GetDocumentPermissionsAsync(userKey, contentKeys);

    public Task<Attempt<IEnumerable<NodePermissions>, UserOperationStatus>> GetElementPermissionsAsync(Guid userKey, IEnumerable<Guid> elementKeys)
        => inner.GetElementPermissionsAsync(userKey, elementKeys);

    public EntityPermissionCollection GetPermissions(IUser? user, params int[] nodeIds) => inner.GetPermissions(user, nodeIds);

    public EntityPermissionCollection GetPermissions(IUserGroup?[] groups, bool fallbackToDefaultPermissions, params int[] nodeIds)
        => inner.GetPermissions(groups, fallbackToDefaultPermissions, nodeIds);

    public EntityPermissionSet GetPermissionsForPath(IUser? user, string? path) => inner.GetPermissionsForPath(user, path);

    public EntityPermissionSet GetPermissionsForPath(IUserGroup[] groups, string path, bool fallbackToDefaultPermissions = false)
        => inner.GetPermissionsForPath(groups, path, fallbackToDefaultPermissions);

    public void ReplaceUserGroupPermissions(int groupId, ISet<string> permissions, params int[] entityIds)
        => inner.ReplaceUserGroupPermissions(groupId, permissions, entityIds);

    public void AssignUserGroupPermission(int groupId, string permission, params int[] entityIds)
        => inner.AssignUserGroupPermission(groupId, permission, entityIds);

    public IEnumerable<IUser> GetAllInGroup(int? groupId) => inner.GetAllInGroup(groupId);

    public IEnumerable<IUser> GetAllNotInGroup(int groupId) => inner.GetAllNotInGroup(groupId);

    public Task<Attempt<UserOperationStatus>> VerifyPasswordResetAsync(Guid userKey, string token) => inner.VerifyPasswordResetAsync(userKey, token);

    public Task<Attempt<PasswordChangedModel, UserOperationStatus>> ResetPasswordAsync(Guid userKey, string token, string password)
        => inner.ResetPasswordAsync(userKey, token, password);

    public Task<Attempt<UserOperationStatus>> SendResetPasswordEmailAsync(string userEmail) => inner.SendResetPasswordEmailAsync(userEmail);

    public Task<Attempt<UserInvitationResult, UserOperationStatus>> ResendInvitationAsync(Guid performingUserKey, UserResendInviteModel model)
        => inner.ResendInvitationAsync(performingUserKey, model);

    public Task<Attempt<PasswordChangedModel, UserOperationStatus>> ResetPasswordAsync(Guid performingUserKey, Guid userKey)
        => inner.ResetPasswordAsync(performingUserKey, userKey);

    public Task<UserClientCredentialsOperationStatus> AddClientIdAsync(Guid userKey, string clientId) => inner.AddClientIdAsync(userKey, clientId);

    public Task<bool> RemoveClientIdAsync(Guid userKey, string clientId) => inner.RemoveClientIdAsync(userKey, clientId);

    public Task<IUser?> FindByClientIdAsync(string clientId) => inner.FindByClientIdAsync(clientId);

    public Task<IEnumerable<string>> GetClientIdsAsync(Guid userKey) => inner.GetClientIdsAsync(userKey);

    public IUser CreateUserWithIdentity(string username, string email) => inner.CreateUserWithIdentity(username, email);

    public int GetCount(MemberCountType countType) => inner.GetCount(countType);

    public bool Exists(string username) => inner.Exists(username);

    public IUser CreateWithIdentity(string username, string email, string passwordValue, string memberTypeAlias)
        => inner.CreateWithIdentity(username, email, passwordValue, memberTypeAlias);

    public IUser CreateWithIdentity(string username, string email, string passwordValue, string memberTypeAlias, bool isApproved)
        => inner.CreateWithIdentity(username, email, passwordValue, memberTypeAlias, isApproved);

    public IUser? GetByProviderKey(object id) => inner.GetByProviderKey(id);

    public IUser? GetByEmail(string email) => inner.GetByEmail(email);

    public IUser? GetByUsername(string? username) => inner.GetByUsername(username);

    /// <summary>Umbraco's single-argument Delete only disables the user, so it stays Umbraco's.</summary>
    public void Delete(IUser membershipUser) => inner.Delete(membershipUser);

    public void Save(IUser entity) => inner.Save(entity);

    public void Save(IEnumerable<IUser> entities) => inner.Save(entities);

    public IEnumerable<IUser> FindByEmail(
        string emailStringToMatch,
        long pageIndex,
        int pageSize,
        out long totalRecords,
        StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        => inner.FindByEmail(emailStringToMatch, pageIndex, pageSize, out totalRecords, matchType);

    public IEnumerable<IUser> FindByUsername(
        string login,
        long pageIndex,
        int pageSize,
        out long totalRecords,
        StringPropertyMatchType matchType = StringPropertyMatchType.StartsWith)
        => inner.FindByUsername(login, pageIndex, pageSize, out totalRecords, matchType);

    public IEnumerable<IUser> GetAll(long pageIndex, int pageSize, out long totalRecords) => inner.GetAll(pageIndex, pageSize, out totalRecords);
}
