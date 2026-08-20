using FluentValidation;

namespace Application.Features.Characters.Commands.CreateCharacter;

public sealed class CreateCharacterCommandValidator : AbstractValidator<CreateCharacterCommand>
{
    public CreateCharacterCommandValidator()
    {
        RuleFor(x => x.Request.Name)
            .NotEmpty().WithMessage("Character name is required.")
            .MaximumLength(100).WithMessage("Character name cannot exceed 100 characters.");

        RuleFor(x => x.Request.Title)
            .NotEmpty().WithMessage("Character title is required.")
            .MaximumLength(200).WithMessage("Character title cannot exceed 200 characters.");

        RuleFor(x => x.Request.PersonalityPrompt)
            .NotEmpty().WithMessage("Personality prompt is required.");

        RuleFor(x => x.Request.Greeting)
            .MaximumLength(1000).WithMessage("Greeting message cannot exceed 1000 characters.")
            .When(x => !string.IsNullOrEmpty(x.Request.Greeting));

        RuleFor(x => x.Request.Category)
            .NotEmpty().WithMessage("Character category is required.");

        RuleFor(x => x.Request.DefaultAffectionScore)
            .InclusiveBetween(-100, 100).WithMessage("Initial affection score must be between -100 and 100.");

        RuleFor(x => x.Request.DefaultMood)
            .MaximumLength(100).WithMessage("Initial mood cannot exceed 100 characters.");
    }
}
