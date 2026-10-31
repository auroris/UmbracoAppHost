using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Common.Builders;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;

namespace UmbracoAppHost.DirectoryAccounts;

/// <summary>The body of <c>POST directory/users</c>.</summary>
public sealed class CreateDirectoryUserRequest
{
    /// <summary>The SID of the person's Windows account, as the search returned it.</summary>
    public string? Sid { get; set; }

    /// <summary>The user groups to put the new user in; at least one.</summary>
    public Guid[]? UserGroupKeys { get; set; }
}

/// <summary>The body of <c>POST directory/members</c>.</summary>
public sealed class CreateDirectoryMemberRequest
{
    /// <summary>The SID of the person's Windows account, as the search returned it.</summary>
    public string? Sid { get; set; }

    /// <summary>The member type; empty uses <c>Accounts:MemberTypeAlias</c>.</summary>
    public string? MemberTypeAlias { get; set; }

    /// <summary>The member groups to put the new member in, by name; may be empty.</summary>
    public string[]? MemberGroupNames { get; set; }
}

/// <summary>
/// The management API behind the backoffice's "Add from directory" dialog: searches the directory
/// and creates a backoffice user or a member for the person chosen, linked to their Windows SID. The
/// user endpoints need access to the Users section and the member endpoints to the Members section,
/// as Umbraco's own user and member endpoints do.
/// </summary>
/// <remarks>
/// A search the directory refuses (a term that is too short, say) answers 400 with the reason, and a
/// directory that cannot be reached 503; the dialog shows both as they are.
/// </remarks>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("directory")]
[ApiExplorerSettings(GroupName = "Directory")]
public sealed class DirectoryAccountsController(
    DirectoryAccountService accounts,
    IBackOfficeSecurityAccessor backOfficeSecurity,
    ILogger<DirectoryAccountsController> logger) : ManagementApiControllerBase
{
    /// <summary>Results a search returns unless asked for another number.</summary>
    public const int DefaultTake = 20;

    /// <summary>People in the directory who could be given a backoffice user, and the users some already have.</summary>
    /// <param name="query">Search terms, each at least two characters.</param>
    /// <param name="take">How many people to return, at most 50.</param>
    [HttpGet("users/search")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessUsers)]
    [ProducesResponseType(typeof(DirectorySearchResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> SearchUsers(string? query = null, int take = DefaultTake, CancellationToken cancellationToken = default)
        => FromDirectoryAsync(async () => Ok(await accounts.SearchAsync(DirectoryAccountKind.User, query, take, cancellationToken)));

    /// <summary>People in the directory who could be given a member, and the members some already have.</summary>
    /// <param name="query">Search terms, each at least two characters.</param>
    /// <param name="take">How many people to return, at most 50.</param>
    [HttpGet("members/search")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessMembers)]
    [ProducesResponseType(typeof(DirectorySearchResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> SearchMembers(string? query = null, int take = DefaultTake, CancellationToken cancellationToken = default)
        => FromDirectoryAsync(async () => Ok(await accounts.SearchAsync(DirectoryAccountKind.Member, query, take, cancellationToken)));

    /// <summary>Creates a backoffice user for a person in the directory, linked to their Windows SID.</summary>
    [HttpPost("users")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessUsers)]
    [ProducesResponseType(typeof(CreatedAccount), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> CreateUser(CreateDirectoryUserRequest request, CancellationToken cancellationToken = default)
        => FromDirectoryAsync(async () => Result(await accounts.CreateUserAsync(
            CurrentUserKey(backOfficeSecurity), request.Sid, request.UserGroupKeys ?? [], cancellationToken)));

    /// <summary>Creates a member for a person in the directory, linked to their Windows SID.</summary>
    [HttpPost("members")]
    [Authorize(Policy = AuthorizationPolicies.SectionAccessMembers)]
    [ProducesResponseType(typeof(CreatedAccount), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public Task<IActionResult> CreateMember(CreateDirectoryMemberRequest request, CancellationToken cancellationToken = default)
        => FromDirectoryAsync(async () => Result(await accounts.CreateMemberAsync(
            CurrentUserKey(backOfficeSecurity), request.Sid, request.MemberTypeAlias, request.MemberGroupNames ?? [], cancellationToken)));

    private IActionResult Result(DirectoryAccountOutcome outcome)
        => outcome.Account is { } account
            ? StatusCode(StatusCodes.Status201Created, account)
            : ProblemResult(outcome.Problem!.Status, outcome.Problem.Title, outcome.Problem.Detail);

    /// <summary>Answers the directory's own refusals and outages as problems the dialog can show.</summary>
    private async Task<IActionResult> FromDirectoryAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (DirectoryQueryException exception)
        {
            logger.LogInformation("Directory request refused: {Reason}", exception.Message);
            return ProblemResult(StatusCodes.Status400BadRequest, "The directory refused the search", exception.Message);
        }
        catch (DirectoryUnavailableException exception)
        {
            logger.LogError(exception, "The directory is not reachable: {Reason}", exception.Message);
            return ProblemResult(StatusCodes.Status503ServiceUnavailable, "The directory is not reachable",
                $"The directory is not reachable, so nobody can be looked up right now. {exception.Message}");
        }
    }

    private ObjectResult ProblemResult(int status, string title, string detail)
    {
        var problem = new ProblemDetailsBuilder().WithTitle(title).WithDetail(detail).Build();
        problem.Status = status;
        return StatusCode(status, problem);
    }
}
