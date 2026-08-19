using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Features.Characters.Commands.CreateCharacter;
using Application.Features.Characters.Commands.DeleteCharacter;
using Application.Features.Characters.Commands.UpdateCharacter;
using Application.Features.Characters.Queries.GetCharacterById;
using Application.Features.Characters.Queries.GetPublicCharacters;
using Application.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.Http.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class CharactersController : ControllerBase
{
    private readonly ISender _sender;
    private readonly ILLMService _llmService;

    public CharactersController(ISender sender, ILLMService llmService)
    {
        _sender = sender;
        _llmService = llmService;
    }

    /// <summary>
    /// Generates character profile and backstory using AI
    /// </summary>
    [HttpPost("generate-ai")]
    public async Task<IActionResult> GenerateCharacterAi([FromBody] GenerateCharacterAiRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Idea))
        {
            return BadRequest(Result<GeneratedCharacterDto>.Failure(StatusCodes.Status400BadRequest, "Character idea cannot be empty."));
        }

        try
        {
            var generated = await _llmService.GenerateCharacterProfileAsync(request.Idea, request.Category, ct);
            return Result<GeneratedCharacterDto>.Success(generated).ToActionResult();
        }
        catch (Exception ex)
        {
            return Result<GeneratedCharacterDto>.Failure(StatusCodes.Status500InternalServerError, ex.Message).ToActionResult();
        }
    }

    /// <summary>
    /// Generates random creative character ideas using AI
    /// </summary>
    [HttpGet("generate-ideas")]
    public async Task<IActionResult> GenerateRandomIdeas([FromQuery] int count = 4, CancellationToken ct = default)
    {
        try
        {
            var ideas = await _llmService.GenerateRandomIdeasAsync(count, ct);
            return Result<List<string>>.Success(ideas).ToActionResult();
        }
        catch (Exception ex)
        {
            return Result<List<string>>.Failure(StatusCodes.Status500InternalServerError, ex.Message).ToActionResult();
        }
    }

    /// <summary>
    /// Gets public AI characters
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetCharacters([FromQuery] string? category, CancellationToken ct)
    {
        var result = await _sender.Send(new GetPublicCharactersQuery(category), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Gets AI character details by ID
    /// </summary>
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
