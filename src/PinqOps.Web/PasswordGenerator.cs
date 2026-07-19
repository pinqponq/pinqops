using System.Security.Cryptography;

namespace PinqOps.Web;

/// <summary>
/// Generates credentials for catalog app installs. Alphanumeric only, so a
/// value is safe inside <c>NEO4J_AUTH=neo4j/x</c>, connection URLs and shell-
/// free <c>docker run -e</c> arguments alike.
/// </summary>
public static class PasswordGenerator
{
    private const string Alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int Length = 20;

    public static string Generate()
    {
        var chars = new char[Length];
        var bytes = RandomNumberGenerator.GetBytes(Length * 4);
        for (var i = 0; i < Length; i++)
        {
            chars[i] = Alphabet[(int)(BitConverter.ToUInt32(bytes, i * 4) % (uint)Alphabet.Length)];
        }

        return new string(chars);
    }
}
