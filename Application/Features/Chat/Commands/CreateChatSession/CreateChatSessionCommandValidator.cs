using FluentValidation;

namespace Application.Features.Chat.Commands.CreateChatSession;

public sealed class CreateChatSessionCommandValidator : AbstractValidator<CreateChatSessionCommand>
{
    public CreateChatSessionCommandValidator()
    {
        RuleFor(x => x.Request.CharacterId)
            .NotEmpty().WithMessage("Character ID is required.");

        RuleFor(x => x.Request.Title)
            .MaximumLength(200).WithMessage("Chat session title cannot exceed 200 characters.");
    }
}
