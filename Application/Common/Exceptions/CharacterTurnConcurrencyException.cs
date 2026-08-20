namespace Application.Common.Exceptions;

public class CharacterTurnConcurrencyException : Exception
{
    public Guid TurnId { get; }
    public Guid CharacterId { get; }
    public Guid UserId { get; }

    public CharacterTurnConcurrencyException(Guid turnId, Guid characterId, Guid userId, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        TurnId = turnId;
        CharacterId = characterId;
        UserId = userId;
    }
}
