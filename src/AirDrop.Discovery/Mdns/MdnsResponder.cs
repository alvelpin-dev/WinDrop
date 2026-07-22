using System.Net;
using AirDrop.Discovery.Dns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AirDrop.Discovery.Mdns;

/// <summary>
/// Anuncia el servicio AirDrop de esta máquina por mDNS y responde a las consultas ajenas.
/// </summary>
/// <remarks>
/// <para>
/// Implementa un responder propio en lugar de apoyarse en Bonjour de Apple: Bonjour es una
/// dependencia propietaria, no siempre está instalado, y cuando lo está (por iTunes) compite por
/// el puerto 5353.
/// </para>
/// <para>
/// Al arrancar se emiten anuncios gratuitos, y al parar, despedidas con TTL cero para que los
/// demás nos retiren de sus cachés de inmediato.
/// </para>
/// </remarks>
public sealed class MdnsResponder : IAsyncDisposable
{
    /// <summary>
    /// Número de anuncios gratuitos al arrancar.
    /// </summary>
    /// <remarks>
    /// Se repite porque el multicast no es fiable: un solo paquete puede perderse sin más y
    /// dejarnos invisibles hasta que alguien pregunte.
    /// </remarks>
    private const int AnnouncementCount = 3;

    /// <summary>Separación entre anuncios recomendada por el RFC 6762.</summary>
    public static readonly TimeSpan DefaultAnnouncementInterval = TimeSpan.FromSeconds(1);

    private readonly IMdnsTransport _transport;
    private readonly AirDropServiceRegistration _registration;
    private readonly ILogger<MdnsResponder> _logger;
    private readonly TimeSpan _announcementInterval;

    private bool _started;

    /// <param name="announcementInterval">
    /// Separación entre los anuncios de arranque. Se puede reducir a cero en los tests para que no
    /// esperen de verdad; en producción debe mantenerse el valor por defecto.
    /// </param>
    public MdnsResponder(
        IMdnsTransport transport,
        AirDropServiceRegistration registration,
        ILogger<MdnsResponder>? logger = null,
        TimeSpan? announcementInterval = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _logger = logger ?? NullLogger<MdnsResponder>.Instance;
        _announcementInterval = announcementInterval ?? DefaultAnnouncementInterval;
    }

    /// <summary>Empieza a anunciar y a responder consultas.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _transport.PacketReceived += OnPacketReceived;
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Anunciando {Instance} en {Host}:{Port} con flags {Flags} y {AddressCount} direcciones",
            _registration.InstanceName,
            _registration.HostName,
            _registration.Port,
            (int)_registration.Flags,
            _registration.Addresses.Count);

        await AnnounceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Emite los anuncios gratuitos que declaran nuestra presencia.</summary>
    public async Task AnnounceAsync(CancellationToken cancellationToken = default)
    {
        var announcement = DnsMessage.CreateResponse(_registration.CreateAllRecords());
        var payload = DnsMessageWriter.Write(announcement);

        for (var i = 0; i < AnnouncementCount; i++)
        {
            if (i > 0 && _announcementInterval > TimeSpan.Zero)
            {
                await Task.Delay(_announcementInterval, cancellationToken).ConfigureAwait(false);
            }

            await _transport.SendAsync(payload, destination: null, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug("Anuncio {Number}/{Total} enviado", i + 1, AnnouncementCount);
        }
    }

    /// <summary>Emite las despedidas con TTL cero.</summary>
    public async Task GoodbyeAsync(CancellationToken cancellationToken = default)
    {
        var goodbye = DnsMessage.CreateResponse(_registration.CreateGoodbyeRecords());

        _logger.LogInformation("Retirando el anuncio de {Instance}", _registration.InstanceName);

        await _transport.SendAsync(DnsMessageWriter.Write(goodbye), destination: null, cancellationToken)
            .ConfigureAwait(false);
    }

    private void OnPacketReceived(MdnsPacket packet)
    {
        DnsMessage message;
        try
        {
            message = DnsMessageReader.Read(packet.Payload);
        }
        catch (DnsFormatException ex)
        {
            // La red local está llena de tráfico mDNS de todo tipo. Un paquete que no sabemos
            // interpretar es ruido, no un error: se registra en debug y se sigue.
            _logger.LogDebug(ex, "Paquete mDNS ilegible de {Source}", packet.Source);
            return;
        }

        if (message.Header.IsResponse)
        {
            return;   // las respuestas ajenas las procesa el browser, no el responder
        }

        var answers = BuildAnswers(message.Questions, out var wantsUnicast);
        if (answers.Count == 0)
        {
            return;
        }

        _logger.LogDebug(
            "Respondiendo a {Source} con {Count} registros ({Mode})",
            packet.Source,
            answers.Count,
            wantsUnicast ? "unicast" : "multicast");

        // Las direcciones van como additionals: así el consultante resuelve el host sin tener que
        // preguntar otra vez, que es lo que hace que el descubrimiento se sienta instantáneo.
        var response = DnsMessage.CreateResponse(answers, _registration.CreateAddressRecords());
        var destination = wantsUnicast ? packet.Source : null;

        _ = SendResponseAsync(DnsMessageWriter.Write(response), destination);
    }

    private async Task SendResponseAsync(byte[] payload, IPEndPoint? destination)
    {
        try
        {
            await _transport.SendAsync(payload, destination).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Un fallo al responder a una consulta no debe tumbar el responder: la siguiente
            // consulta volverá a intentarlo.
            _logger.LogWarning(ex, "No se pudo enviar la respuesta mDNS");
        }
    }

    /// <summary>Determina qué registros contestan a las preguntas recibidas.</summary>
    private List<DnsResourceRecord> BuildAnswers(
        IReadOnlyList<DnsQuestion> questions,
        out bool wantsUnicast)
    {
        var answers = new List<DnsResourceRecord>();
        wantsUnicast = false;

        foreach (var question in questions)
        {
            var matched = MatchQuestion(question);
            if (matched.Count == 0)
            {
                continue;
            }

            answers.AddRange(matched);

            // Basta con que una pregunta relevante pida unicast para contestar por unicast.
            wantsUnicast |= question.WantsUnicastResponse;
        }

        // Una consulta puede coincidir por varias vías (p. ej. ANY sobre la instancia): se
        // eliminan los duplicados para no inflar la respuesta.
        return [.. answers.Distinct()];
    }

    private List<DnsResourceRecord> MatchQuestion(DnsQuestion question)
    {
        var records = new List<DnsResourceRecord>();

        if (NameMatches(question.Name, MdnsConstants.AirDropServiceType)
            && question.Type is DnsRecordType.Ptr or DnsRecordType.Any)
        {
            // Una consulta PTR se contesta con el puntero y, de paso, con SRV y TXT: sin ellos el
            // consultante necesitaría dos rondas más para saber a qué puerto conectarse.
            records.AddRange(_registration.CreatePointerRecords());
            records.AddRange(_registration.CreateInstanceRecords());
        }
        else if (NameMatches(question.Name, _registration.InstanceName))
        {
            records.AddRange(question.Type switch
            {
                DnsRecordType.Srv => _registration.CreateInstanceRecords().OfType<SrvRecord>(),
                DnsRecordType.Txt => _registration.CreateInstanceRecords().OfType<TxtRecord>(),
                DnsRecordType.Any => _registration.CreateInstanceRecords(),
                _ => [],
            });
        }
        else if (NameMatches(question.Name, _registration.HostName))
        {
            records.AddRange(_registration.CreateAddressRecords()
                .Where(r => question.Type is DnsRecordType.Any || r.Type == question.Type));
        }

        return records;
    }

    /// <summary>
    /// Compara nombres DNS sin distinguir mayúsculas y tolerando el punto final.
    /// </summary>
    /// <remarks>
    /// Los nombres DNS son insensibles a mayúsculas por especificación, y hay implementaciones que
    /// incluyen el punto raíz final. Comparar con igualdad de cadenas haría que el descubrimiento
    /// fallase con esos peers sin ninguna pista del motivo.
    /// </remarks>
    private static bool NameMatches(string left, string right) =>
        string.Equals(left.TrimEnd('.'), right.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        if (_started)
        {
            _transport.PacketReceived -= OnPacketReceived;

            try
            {
                await GoodbyeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Cerrar no debe fallar: como mucho, los demás tardarán el TTL en olvidarnos.
                _logger.LogDebug(ex, "No se pudo enviar la despedida mDNS");
            }

            _started = false;
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
