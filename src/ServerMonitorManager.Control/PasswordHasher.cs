using System.Security.Cryptography;

namespace ServerMonitorManager.Control;

public static class PasswordHasher
{
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;
    private static readonly byte[] DummySalt = new byte[SaltSize];

    public static string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            Algorithm,
            KeySize);

        return $"$pbkdf2-sha256$i={Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string? storedHash)
    {
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(storedHash))
        {
            PerformDummyVerification(password);
            return false;
        }

        var parts = storedHash.Split('$');
        // Expected format: "" "$" "pbkdf2-sha256" "$" "i=600000" "$" "salt" "$" "hash"
        // parts = ["", "pbkdf2-sha256", "i=600000", "<salt>", "<hash>"]
        if (parts.Length != 5 || !string.Equals(parts[1], "pbkdf2-sha256", StringComparison.OrdinalIgnoreCase))
        {
            PerformDummyVerification(password);
            return false;
        }

        var iterPart = parts[2];
        if (!iterPart.StartsWith("i=", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(iterPart.AsSpan(2), out var iterations)
            || iterations < 100_000)
        {
            PerformDummyVerification(password);
            return false;
        }

        byte[] salt;
        byte[] expectedHash;
        try
        {
            salt = Convert.FromBase64String(parts[3]);
            expectedHash = Convert.FromBase64String(parts[4]);
        }
        catch (FormatException)
        {
            PerformDummyVerification(password);
            return false;
        }

        var actualHash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            Algorithm,
            expectedHash.Length);

        return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
    }

    public static void PerformDummyVerification(string? password)
    {
        // Executes standard PBKDF2 iterations to ensure uniform response timing
        var pwd = string.IsNullOrEmpty(password) ? "dummy-password" : password;
        _ = Rfc2898DeriveBytes.Pbkdf2(
            pwd,
            DummySalt,
            Iterations,
            Algorithm,
            KeySize);
    }
}
