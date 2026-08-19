using Application.Abstractions.Responses;
using MediatR;

namespace Application.Features.Chat.Queries.GetRoleplaySuggestions;

public sealed record GetRoleplaySuggestionsQuery(Guid SessionId) : IRequest<Result<List<string>>>;
