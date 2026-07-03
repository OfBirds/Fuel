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

    /// <summary>
    /// Issues a single-use reset token for the local login, or returns null if no
    /// eligible account exists (unknown email, or an OIDC-only account with no local
    /// password — those reset via CrimsonRaven). Callers must not reveal which case
    /// occurred. The returned value is the RAW token to place in the emailed link;
    /// only its hash is stored.
    /// </summary>
    Task<string?> CreatePasswordResetTokenAsync(string email);

    /// <summary>Redeems a raw reset token and sets a new password. False if the token is unknown, expired, or already used.</summary>
    Task<bool> ResetPasswordWithTokenAsync(string rawToken, string newPassword);
}

public class AuthService(AppDbContext context) : IAuthService
{
    // PBKDF2-SHA256 work factor. 600k iterations follows current OWASP guidance;
    // stored hashes are self-describing (see HashPassword) so this can be raised again
    // later without breaking existing passwords.
    private const int Pbkdf2Iterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    // Reset tokens are short-lived; a link that leaks (forwarded mail, shoulder-surf)
    // stops working quickly.
    private static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromHours(1);

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

        // OIDC-only accounts have no local password — they must sign in via CrimsonRaven.
        if (string.IsNullOrEmpty(user.PasswordHash) || !VerifyPassword(password, user.PasswordHash))
            return null;

        return user;
    }

    public async Task<string?> CreatePasswordResetTokenAsync(string email)
    {
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == email);

        // Only local accounts can reset a local password. Unknown email or an OIDC-only
        // account (no local hash) → no token, so this flow can never mint a local password
        // on an SSO account and bypass CrimsonRaven.
        if (user == null || string.IsNullOrEmpty(user.PasswordHash))
            return null;

        // Invalidate any outstanding tokens so only the newest link works.
        var outstanding = context.PasswordResetTokens
            .Where(t => t.UserId == user.Id && t.ConsumedAt == null);
        context.PasswordResetTokens.RemoveRange(outstanding);

        var rawToken = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        context.PasswordResetTokens.Add(new PasswordResetToken
        {
            UserId = user.Id,
            TokenHash = HashToken(rawToken),
            ExpiresAt = DateTime.UtcNow.Add(ResetTokenLifetime),
        });
        await context.SaveChangesAsync();

        return rawToken;
    }

    public async Task<bool> ResetPasswordWithTokenAsync(string rawToken, string newPassword)
    {
        if (!await ValidatePasswordAsync(newPassword) || string.IsNullOrWhiteSpace(rawToken))
            return false;

        var tokenHash = HashToken(rawToken);
        var now = DateTime.UtcNow;
        var token = await context.PasswordResetTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.ConsumedAt == null && t.ExpiresAt > now);

        if (token == null)
            return false;

        token.User.PasswordHash = HashPassword(newPassword);
        token.ConsumedAt = now;
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

    // Stored as a self-describing string: "pbkdf2$<iterations>$<salt_b64>$<hash_b64>".
    // Encoding the iteration count lets us raise the work factor later while still
    // verifying older hashes (see VerifyPassword's legacy branch).
    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);

        return $"pbkdf2${Pbkdf2Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string storedHash)
    {
        // Current format: pbkdf2$<iterations>$<salt>$<hash>.
        if (storedHash.StartsWith("pbkdf2$", StringComparison.Ordinal))
        {
            var parts = storedHash.Split('$');
            if (parts.Length != 4 || !int.TryParse(parts[1], out var iterations))
                return false;

            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }

        // Legacy format: base64 of [16-byte salt][20-byte hash], PBKDF2-SHA256 @ 10k.
        // Kept so passwords set before the work-factor bump still verify.
        try
        {
            var hashBytes = Convert.FromBase64String(storedHash);
            if (hashBytes.Length != 36)
                return false;

            var salt = hashBytes[..16];
            var expected = hashBytes[16..];
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, 10000, HashAlgorithmName.SHA256, 20);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string HashToken(string rawToken) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
