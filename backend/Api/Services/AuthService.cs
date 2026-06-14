using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace Api.Services;

public interface IAuthService
{
    Task<User?> RegisterAsync(string email, string password);
    Task<User?> LoginAsync(string email, string password);
    Task<bool> ValidatePasswordAsync(string password);
    Task<bool> ResetPasswordAsync(string email, string newPassword);
}

public class AuthService(AppDbContext context) : IAuthService
{
    /// <summary>User-facing description of the password policy enforced below.</summary>
    public const string PasswordPolicyMessage =
        "Password must be at least 8 characters and include a letter, a number, and a special character.";

    public async Task<User?> RegisterAsync(string email, string password)
    {
        if (!await ValidatePasswordAsync(password))
            return null;

        var existingUser = await context.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (existingUser != null)
            return null;

        var passwordHash = HashPassword(password);
        var user = new User
        {
            Email = email,
            PasswordHash = passwordHash
        };

        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user;
    }

    public async Task<User?> LoginAsync(string email, string password)
    {
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
            return null;

        if (!VerifyPassword(password, user.PasswordHash))
            return null;

        return user;
    }

    public async Task<bool> ResetPasswordAsync(string email, string newPassword)
    {
        if (!await ValidatePasswordAsync(newPassword))
            return false;

        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == email);
        if (user == null)
            return false;

        user.PasswordHash = HashPassword(newPassword);
        await context.SaveChangesAsync();
        return true;
    }

    public Task<bool> ValidatePasswordAsync(string password) =>
        Task.FromResult(IsPasswordValid(password));

    /// <summary>
    /// Password policy: at least 8 characters and contains a letter, a digit, and a
    /// special (non-alphanumeric) character. Kept in sync with <see cref="PasswordPolicyMessage"/>
    /// and the live checklist on the registration form.
    /// </summary>
    public static bool IsPasswordValid(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
            return false;

        return password.Any(char.IsLetter)
            && password.Any(char.IsDigit)
            && password.Any(c => !char.IsLetterOrDigit(c));
    }

    private static string HashPassword(string password)
    {
        var salt = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
        }

        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            10000,
            HashAlgorithmName.SHA256,
            20);

        var hashWithSalt = new byte[36];
        Array.Copy(salt, 0, hashWithSalt, 0, 16);
        Array.Copy(hash, 0, hashWithSalt, 16, 20);

        return Convert.ToBase64String(hashWithSalt);
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        var hashBytes = Convert.FromBase64String(storedHash);
        var salt = new byte[16];
        Array.Copy(hashBytes, 0, salt, 0, 16);

        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            10000,
            HashAlgorithmName.SHA256,
            20);

        for (int i = 0; i < 20; i++)
        {
            if (hashBytes[i + 16] != hash[i])
                return false;
        }

        return true;
    }
}
