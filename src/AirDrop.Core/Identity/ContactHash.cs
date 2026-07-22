using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AirDrop.Core.Identity;

/// <summary>
/// Hashes de identificadores de contacto (emails y teléfonos) como los usa AirDrop.
/// </summary>
/// <remarks>
/// <para>
/// AirDrop no transmite emails ni teléfonos en claro: envía SHA-256 truncados. Aparecen en dos
/// sitios con longitudes distintas:
/// </para>
/// <list type="bullet">
///   <item>En el advertisement BLE: <b>2 bytes</b> por identificador (ver docs/01 §3.1).</item>
///   <item>En el <c>SenderRecordData</c> del plist: el hash completo, dentro de un blob firmado.</item>
/// </list>
/// <para>
/// ⚠️ <b>Un hash truncado a 2 bytes no es una prueba de identidad.</b> Solo hay 65 536 valores
/// posibles, y el trabajo publicado sobre AirCollect demuestra que estos hashes son reversibles
/// por fuerza bruta para números de teléfono. Sirven para filtrar rápido a quién mostrar, nunca
/// para autenticar. La identidad real se valida con certificados, algo que esta implementación no
/// puede hacer de forma completa (ver docs/01 §6.5).
/// </para>
/// </remarks>
public static class ContactHash
{
    /// <summary>Bytes que se conservan del hash en el advertisement BLE.</summary>
    public const int BleTruncatedLength = 2;

    /// <summary>Calcula el SHA-256 completo de un identificador de contacto ya normalizado.</summary>
    public static byte[] Compute(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        return SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(identifier)));
    }

    /// <summary>Calcula el hash truncado a los primeros bytes, como en el advertisement BLE.</summary>
    public static byte[] ComputeTruncated(string identifier, int length = BleTruncatedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, SHA256.HashSizeInBytes);

        return Compute(identifier)[..length];
    }

    /// <summary>
    /// Normaliza un identificador antes de hashearlo.
    /// </summary>
    /// <remarks>
    /// Sin normalización, <c>Usuario@Ejemplo.com</c> y <c>usuario@ejemplo.com</c> producirían
    /// hashes distintos y el emparejamiento fallaría. Los teléfonos se reducen a sus dígitos:
    /// los espacios, guiones y paréntesis son puramente de presentación.
    /// </remarks>
    public static string Normalize(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);

        var trimmed = identifier.Trim();

        if (LooksLikePhoneNumber(trimmed))
        {
            var digits = new StringBuilder(trimmed.Length);
            foreach (var c in trimmed)
            {
                if (char.IsAsciiDigit(c))
                {
                    digits.Append(c);
                }
            }

            return digits.ToString();
        }

        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// Distingue un teléfono de un email. Un identificador con arroba es siempre un email; el
    /// resto se considera teléfono si solo contiene dígitos y separadores de formato.
    /// </summary>
    private static bool LooksLikePhoneNumber(string identifier)
    {
        if (identifier.Contains('@'))
        {
            return false;
        }

        var hasDigit = false;
        foreach (var c in identifier)
        {
            if (char.IsAsciiDigit(c))
            {
                hasDigit = true;
            }
            else if (c is not ('+' or '-' or ' ' or '(' or ')' or '.'))
            {
                return false;
            }
        }

        return hasDigit;
    }

    /// <summary>Compara dos hashes en tiempo constante, para no filtrar información por temporización.</summary>
    public static bool Matches(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

    /// <summary>Formatea un hash en hexadecimal, para los logs de diagnóstico.</summary>
    public static string ToHex(ReadOnlySpan<byte> hash) =>
        Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
}
