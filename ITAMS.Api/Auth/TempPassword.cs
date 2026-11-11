using System.Security.Cryptography;

namespace ITAMS.Api.Auth;

public static class TempPassword
{
    // No look-alike characters (0/O, 1/l/I) so it can be read out or typed without mistakes.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>16 characters from a cryptographic RNG (about 93 bits).</summary>
    public static string Generate(int length = 16) => new(RandomNumberGenerator.GetItems<char>(Alphabet, length));
}
