using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Features.UserProfile.Commands.UpdateUserProfile;
using Application.Features.UserProfile.Queries.GetUserProfile;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.Http.Controllers;

[ApiController]
[Route("api/v1/user-profile")]
public sealed class UserProfileController : ControllerBase
{
    private readonly ISender _sender;

    public UserProfileController(ISender sender)
    {
        _sender = sender;
    }

    [AllowAnonymous]
    [HttpGet("{userId:guid}")]
    public async Task<IActionResult> GetUserProfile(Guid userId, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new GetUserProfileQuery(userId), cancellationToken);
        return result.ToActionResult();
    }

    [AllowAnonymous]
    [HttpPut("{userId:guid}")]
    public async Task<IActionResult> UpdateUserProfile(Guid userId, [FromBody] UpdateUserProfileRequest request, CancellationToken cancellationToken)
    {
        var result = await _sender.Send(new UpdateUserProfileCommand(userId, request), cancellationToken);
        return result.ToActionResult();
    }
}
