using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Features.Characters.Commands.CreateCharacter;
using Application.Features.Characters.Commands.DeleteCharacter;
using Application.Features.Characters.Commands.GenerateAvatar;
using Application.Features.Characters.Commands.GenerateCharacterAI;
using Application.Features.Characters.Commands.UpdateCharacter;
using Application.Features.Characters.Queries.GenerateRandomIdeas;
using Application.Features.Characters.Queries.GetCharacterById;
using Application.Features.Characters.Queries.GetPublicCharacters;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.Http.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public sealed class CharactersController : ControllerBase
{
    private readonly ISender _sender;

    public CharactersController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>
    /// Generates character profile and backstory using AI
    /// </summary>
    [HttpPost("generate-ai")]
    public async Task<IActionResult> GenerateCharacterAI([FromBody] GenerateCharacterAIRequest request, CancellationToken ct)
    {
        var result = await _sender.Send(new GenerateCharacterAICommand(request), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Generates random creative character ideas using AI
    /// </summary>
    [AllowAnonymous]
    [HttpGet("generate-ideas")]
    public async Task<IActionResult> GenerateRandomIdeas([FromQuery] int count = 3, CancellationToken ct = default)
    {
        var result = await _sender.Send(new GenerateRandomIdeasQuery(count), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Gets public AI characters
    /// </summary>
    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> GetCharacters([FromQuery] string? category, CancellationToken ct)
    {
        var result = await _sender.Send(new GetPublicCharactersQuery(category), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Gets AI character details by ID
    /// </summary>
    [AllowAnonymous]
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetCharacterById(Guid id, CancellationToken ct)
    {
        var result = await _sender.Send(new GetCharacterByIdQuery(id), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Creates a new AI character
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateCharacter([FromBody] CreateCharacterRequest request, CancellationToken ct)
    {
        var result = await _sender.Send(new CreateCharacterCommand(request), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Updates an existing AI character
    /// </summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateCharacter(Guid id, [FromBody] UpdateCharacterRequest request, CancellationToken ct)
    {
        var result = await _sender.Send(new UpdateCharacterCommand(id, request), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Generates a stunning anime avatar image using AI
    /// </summary>
    [HttpPost("generate-avatar")]
    public async Task<IActionResult> GenerateAvatar([FromBody] GenerateAvatarRequest request, CancellationToken ct)
    {
        var result = await _sender.Send(new Application.Features.Characters.Commands.GenerateAvatar.GenerateAvatarCommand(request), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Deletes an AI character
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteCharacter(Guid id, CancellationToken ct)
    {
        var result = await _sender.Send(new DeleteCharacterCommand(id), ct);
        return result.ToActionResult();
    }
}
