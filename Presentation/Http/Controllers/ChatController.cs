using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Features.Chat.Commands.CreateChatSession;
using Application.Features.Chat.Commands.DeleteChatSession;
using Application.Features.Chat.Commands.RollbackChatMessage;
using Application.Features.Chat.Commands.SendChatMessage;
using Application.Features.Chat.Queries.GetChatSession;
using Application.Features.Chat.Queries.GetUserChatSessions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace Presentation.Http.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public sealed class ChatController : ControllerBase
{
    private readonly ISender _sender;

    public ChatController(ISender sender)
    {
        _sender = sender;
    }

    private Guid? GetCurrentUserId()
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        if (!string.IsNullOrEmpty(userIdClaim) && Guid.TryParse(userIdClaim, out var uid))
        {
            return uid;
        }
        return null;
    }

    /// <summary>
    /// Gets all recent chat sessions for the current authenticated user (or guest sessions)
    /// </summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        var result = await _sender.Send(new GetUserChatSessionsQuery(userId), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Creates a new chat session with an AI character attached to the current user
    /// </summary>
    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest request, CancellationToken ct)
    {
        var userId = GetCurrentUserId() ?? request.UserId;
        var requestWithUser = request with { UserId = userId };
        var result = await _sender.Send(new CreateChatSessionCommand(requestWithUser), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Gets chat session details and message history
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}")]
    public async Task<IActionResult> GetSession(Guid sessionId, CancellationToken ct)
    {
        var result = await _sender.Send(new GetChatSessionQuery(sessionId), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Deletes a chat session
    /// </summary>
    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> DeleteSession(Guid sessionId, CancellationToken ct)
    {
        var result = await _sender.Send(new DeleteChatSessionCommand(sessionId), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Rollbacks conversation to a specific past message, deleting all messages after it
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/rollback/{messageId:guid}")]
    public async Task<IActionResult> RollbackSession(Guid sessionId, Guid messageId, CancellationToken ct)
    {
        var result = await _sender.Send(new RollbackChatMessageCommand(sessionId, messageId), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Sends roleplay message and receives AI response
    /// </summary>
    [HttpPost("messages")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request, CancellationToken ct)
    {
        var result = await _sender.Send(new SendChatMessageCommand(request), ct);
        return result.ToActionResult();
    }
}
