using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Auth.Commands.Login;

public record LoginCommand(LoginRequest Request) : IRequest<Result<AuthResponse>>;
