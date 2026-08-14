using Domain.Common;

namespace Domain.Entities;

public class User : BaseEntity
{
    public string Username { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public int CreditsBalance { get; private set; } = 100;

    private User() { } // EF Core

    public User(string username, string email, int initialCredits = 100)
    {
        Username = username;
        Email = email;
        CreditsBalance = initialCredits;
    }

    public void DeductCredits(int amount)
    {
        if (amount <= 0) return;
        if (CreditsBalance < amount)
        {
            throw new InvalidOperationException("Not enough credits.");
        }
        CreditsBalance -= amount;
        Touch();
    }

    public void AddCredits(int amount)
    {
        if (amount <= 0) return;
        CreditsBalance += amount;
        Touch();
    }
}
