using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AirDrop.Server.Security;

/// <summary>
/// Genera y persiste el certificado TLS del servidor AirDrop.
/// </summary>
/// <remarks>
/// <para>
/// Un certificado autofirmado es <b>suficiente y correcto</b> aquí, no un atajo. La investigación
/// del protocolo (docs/01 §6.1) confirma que el TLS de AirDrop usa certificados autofirmados sin
/// verificación de peer: aporta cifrado, no autenticación. La identidad se valida —cuando se
/// valida— en una capa superior, dentro del plist.
/// </para>
/// <para>
/// Consecuencia práctica muy favorable: <b>no hace falta ningún certificado firmado por Apple para
/// establecer la conexión TLS.</b> Lo que sí es imposible de replicar es el certificado de
/// identidad de Apple ID que exige el modo "solo contactos" (docs/01 §6.5).
/// </para>
/// </remarks>
public static class AirDropCertificate
{
    private const string SubjectName = "CN=AirDrop";

    /// <summary>Vigencia del certificado. Larga, porque su rotación no aporta seguridad aquí.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(3650);

    /// <summary>Genera un certificado autofirmado nuevo con su clave privada.</summary>
    public static X509Certificate2 CreateSelfSigned(TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            SubjectName,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")],   // autenticación de servidor TLS
                critical: false));

        // Se descuenta un margen en el inicio: un desfase de reloj de unos minutos entre los dos
        // equipos haría que el certificado se considerase "aún no válido".
        using var inMemory = request.CreateSelfSigned(
            now.AddHours(-1),
            now.Add(Lifetime));

        // Reimportar el certificado como PKCS#12 NO es redundante en Windows. Un certificado
        // recién creado en memoria lleva la clave privada en un "ephemeral key set" que SChannel
        // no puede usar, y el handshake TLS se corta sin mensaje útil: el cliente solo ve un EOF
        // inesperado. Exportar e importar deja la clave en una forma que SChannel sí acepta.
        return X509CertificateLoader.LoadPkcs12(
            inMemory.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Carga el certificado persistido, generándolo la primera vez.
    /// </summary>
    /// <remarks>
    /// Se persiste para que la identidad TLS del equipo no cambie en cada arranque. Se guarda como
    /// PKCS#12 sin contraseña: la clave privada no protege nada que el atacante no tenga ya si
    /// puede leer el fichero, y una contraseña embebida en el código solo daría una falsa sensación
    /// de seguridad. La protección real es la ACL del directorio del usuario.
    /// </remarks>
    public static X509Certificate2 LoadOrCreate(string path, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (File.Exists(path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(
                    path,
                    password: null,
                    X509KeyStorageFlags.Exportable);

                var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
                if (now < existing.NotAfter && now > existing.NotBefore)
                {
                    return existing;
                }

                existing.Dispose();
            }
            catch (CryptographicException)
            {
                // Fichero corrupto o de otra versión: se regenera en vez de dejar el servidor
                // sin arrancar por un certificado ilegible.
            }
        }

        var certificate = CreateSelfSigned(timeProvider);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pkcs12));
        return certificate;
    }
}
