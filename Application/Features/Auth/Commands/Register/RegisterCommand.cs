using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Auth.Commands.Register;

public record RegisterCommand(RegisterRequest Request) : IRequest<Result<AuthResponse>>;
