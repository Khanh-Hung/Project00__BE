using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Features.Chat.Commands.CreateChatSession;
using Application.Features.Chat.Commands.DeleteChatSession;
using Application.Features.Chat.Commands.SendChatMessage;
using Application.Features.Chat.Queries.GetChatSession;
using Application.Features.Chat.Queries.GetUserChatSessions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.Http.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class ChatController : ControllerBase
{
    private readonly ISender _sender;

    public ChatController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>
    /// Gets all recent chat sessions
    /// </summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(CancellationToken ct)
    {
        var result = await _sender.Send(new GetUserChatSessionsQuery(), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Creates a new chat session with an AI character
    /// </summary>
    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest request, CancellationToken ct)
    {
        var result = await _sender.Send(new CreateChatSessionCommand(request), ct);
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
    /// Sends roleplay message and receives AI response
    /// </summary>
    [HttpPost("messages")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request, CancellationToken ct)
    {
        var result = await _sender.Send(new SendChatMessageCommand(request), ct);
        return result.ToActionResult();
    }
}
