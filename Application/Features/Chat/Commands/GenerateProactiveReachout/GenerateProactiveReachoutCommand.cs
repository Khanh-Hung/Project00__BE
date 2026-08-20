using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Chat.Commands.GenerateProactiveReachout;

public sealed record GenerateProactiveReachoutCommand(
    ProactiveReachoutRequest Request
) : IRequest<Result<ProactiveReachoutResponse>>;
