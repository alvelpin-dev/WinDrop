namespace AirDrop.Core.Protocol;

/// <summary>
/// Capacidades anunciadas en la clave <c>flags</c> del registro TXT de <c>_airdrop._tcp</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ NIVEL DE CONFIANZA: [PROBABLE]. La correspondencia entre bits y capacidades procede de
/// fuentes secundarias coincidentes, no de una especificación de Apple. Los bits marcados como
/// desconocidos se han observado activos en macOS pero no se ha determinado su significado.
/// Ver docs/01-RESEARCH-airdrop-protocol.md §5.
/// </para>
/// <para>
/// Regla de oro al anunciar: declarar de menos degrada la transferencia a un camino más simple,
/// declarar de más la rompe. El emisor confía en estos flags para decidir qué formato enviarnos,
/// así que solo se anuncia lo que está realmente implementado.
/// </para>
/// </remarks>
[Flags]
public enum AirDropFlags
{
    None = 0,

    /// <summary>Acepta compartir URLs además de ficheros.</summary>
    SupportsUrl = 1 << 0,

    /// <summary>
    /// Acepta DVZip, el formato comprimido propietario de Apple.
    /// <b>Nunca se debe anunciar:</b> es un formato cerrado y no documentado que no sabemos
    /// decodificar. Anunciarlo haría que el emisor nos enviara datos ilegibles.
    /// </summary>
    SupportsDvZip = 1 << 1,

    /// <summary>Acepta subidas encadenadas sobre la misma conexión.</summary>
    SupportsPipelining = 1 << 2,

    /// <summary>Acepta varios tipos de fichero distintos en una misma transferencia.</summary>
    SupportsMixedTypes = 1 << 3,

    /// <summary>Significado no determinado. Observado activo en macOS.</summary>
    Unknown4 = 1 << 4,

    /// <summary>Significado no determinado. Observado activo en macOS.</summary>
    Unknown5 = 1 << 5,

    /// <summary>Relacionado con el subsistema Iris de Apple. Significado exacto no determinado.</summary>
    SupportsIris = 1 << 6,

    /// <summary>Implementa el endpoint <c>/Discover</c>.</summary>
    SupportsDiscover = 1 << 7,

    /// <summary>Significado no determinado. Observado activo en macOS.</summary>
    Unknown8 = 1 << 8,

    /// <summary>Acepta asset bundles.</summary>
    SupportsAssetBundle = 1 << 9,

    /// <summary>
    /// Permite <c>/Upload</c> sin un <c>/Ask</c> previo.
    /// <b>Nunca se debe anunciar:</b> equivaldría a aceptar ficheros sin consentimiento del usuario.
    /// </summary>
    SupportsUnattendedPush = 1 << 10,

    /// <summary>
    /// Lo que anuncia esta implementación: descubrimiento, tipos mezclados y URLs.
    /// </summary>
    /// <remarks>
    /// Deliberadamente excluye <see cref="SupportsDvZip"/> (formato propietario que no podemos
    /// leer) y <see cref="SupportsUnattendedPush"/> (saltaría el consentimiento del usuario).
    /// El valor típico de macOS es 1019 (0x3FB), que sí incluye DVZip.
    /// </remarks>
    Supported = SupportsUrl | SupportsMixedTypes | SupportsDiscover,
}
