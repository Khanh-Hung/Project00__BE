using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Features.Lorebook.Commands.CreateLorebookEntry;
using Application.Features.Lorebook.Commands.DeleteLorebookEntry;
using Application.Features.Lorebook.Queries.GetCharacterLorebook;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Presentation.Http.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/[controller]")]
public sealed class LorebookController : ControllerBase
{
    private readonly ISender _sender;

    public LorebookController(ISender sender)
    {
        _sender = sender;
    }

    /// <summary>
    /// Gets all active lorebook and world knowledge entries for a character (or universal entries)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetLorebookEntries([FromQuery] Guid? characterId, CancellationToken ct)
    {
        var result = await _sender.Send(new GetCharacterLorebookQuery(characterId), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Creates a new lorebook entry for world knowledge and dynamic rule injection
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateLorebookEntry([FromBody] CreateLorebookEntryRequest request, CancellationToken ct)
    {
        var result = await _sender.Send(new CreateLorebookEntryCommand(request), ct);
        return result.ToActionResult();
    }

    /// <summary>
    /// Deletes a lorebook entry
    /// </summary>
    [HttpDelete("{entryId:guid}")]
    public async Task<IActionResult> DeleteLorebookEntry(Guid entryId, CancellationToken ct)
    {
        var result = await _sender.Send(new DeleteLorebookEntryCommand(entryId), ct);
        return result.ToActionResult();
    }
}
