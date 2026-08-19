using FluentValidation;

namespace Application.Features.Chat.Commands.SendChatMessage;

public sealed class SendChatMessageCommandValidator : AbstractValidator<SendChatMessageCommand>
{
    public SendChatMessageCommandValidator()
    {
        RuleFor(x => x.Request.SessionId)
            .NotEmpty().WithMessage("Session ID is required.");

        RuleFor(x => x.Request.Content)
            .NotEmpty().WithMessage("Message content is required.")
            .MaximumLength(4000).WithMessage("Message content cannot exceed 4000 characters.");
    }
}
