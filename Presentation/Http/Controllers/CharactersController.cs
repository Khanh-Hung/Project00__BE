using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Features.Characters.Commands.CreateCharacter;
using Application.Features.Characters.Queries.GetCharacterById;
using Application.Features.Characters.Queries.GetPublicCharacters;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.Http.Controllers;

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
}
