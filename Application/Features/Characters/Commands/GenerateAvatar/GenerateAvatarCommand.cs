using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Characters.Commands.GenerateAvatar;

public sealed record GenerateAvatarCommand(GenerateAvatarRequest Request) : IRequest<Result<GenerateAvatarResponse>>;
