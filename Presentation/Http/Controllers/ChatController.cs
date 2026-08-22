using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Features.Chat.Commands.CreateChatSession;
using Application.Features.Chat.Commands.DeleteChatSession;
using Application.Features.Chat.Commands.ImagineScene;
using Application.Features.Chat.Commands.RollbackChatMessage;
using Application.Features.Chat.Commands.SendChatMessage;
using Application.Features.Chat.Queries.GetCharacterMemories;
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
        var userId = GetCurrentUserId();
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
    /// Generates contextual roleplay suggestions based on the conversation history
    /// </summary>
    [HttpGet("sessions/{sessionId:guid}/suggestions")]
    public async Task<IActionResult> GetSuggestions(Guid sessionId, CancellationToken ct)
    {
        var result = await _sender.Send(new Application.Features.Chat.Queries.GetRoleplaySuggestions.GetRoleplaySuggestionsQuery(sessionId), ct);
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

    /// <summary>
    /// Generates dynamic illustration image for a specific moment in chat
    /// </summary>
    [HttpPost("imagine-scene")]
    public async Task<IActionResult> ImagineScene([FromBody] GenerateSceneImageRequest request, CancellationToken ct)
    {
        var result = await _sender.Send(new ImagineSceneCommand(request), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Triggers character to browse user profile and proactively send an opening DM message
    /// </summary>
    [HttpPost("proactive-reachout")]
    public async Task<IActionResult> ProactiveReachout([FromBody] ProactiveReachoutRequest request, CancellationToken ct)
    {
        var result = await _sender.Send(new Application.Features.Chat.Commands.GenerateProactiveReachout.GenerateProactiveReachoutCommand(request), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Gets all long-term memories remembered by this character about the current user
    /// </summary>
    [HttpGet("memories/{characterId:guid}")]
    public async Task<IActionResult> GetCharacterMemories(Guid characterId, [FromQuery] int limit = 30, CancellationToken ct = default)
    {
        var userId = GetCurrentUserId();
        var result = await _sender.Send(new Application.Features.Chat.Queries.GetCharacterMemories.GetCharacterMemoriesQuery(characterId, userId, limit), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Gets the dynamic relationship state, affection score, mood, and unlocked events for a character with the current user
    /// </summary>
    [HttpGet("relationship/{characterId:guid}")]
    public async Task<IActionResult> GetCharacterRelationship(Guid characterId, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        var result = await _sender.Send(new Application.Features.Chat.Queries.GetCharacterRelationship.GetCharacterRelationshipQuery(characterId, userId), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Sends roleplay message and streams AI response tokens in real-time via Server-Sent Events (SSE)
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/stream")]
    public async Task StreamMessage(
        Guid sessionId,
        [FromBody] SendMessageRequest request,
        [FromServices] Application.Interfaces.ICharacterRuntime characterRuntime,
        CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (!userId.HasValue || userId.Value == Guid.Empty)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        Response.ContentType = "text/event-stream";
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        var turnRequest = new Application.Interfaces.CharacterTurnRequest(
            UserId: userId.Value,
            CharacterId: Guid.Empty,
            SessionId: sessionId,
            UserMessage: request.Content,
            TurnId: request.TurnId ?? Guid.NewGuid()
        );

        await foreach (var streamEvent in characterRuntime.ProcessTurnStreamAsync(turnRequest, ct))
        {
            var sseText = streamEvent.ToSseString();
            await Response.WriteAsync(sseText, ct);
            await Response.Body.FlushAsync(ct);
        }
    }
}
