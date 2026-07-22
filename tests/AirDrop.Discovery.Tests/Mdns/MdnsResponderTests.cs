using System.Net;
using AirDrop.Core.Protocol;
using AirDrop.Discovery.Dns;
using AirDrop.Discovery.Mdns;
using Xunit;

namespace AirDrop.Discovery.Tests.Mdns;

public class MdnsResponderTests
{
    private const string InstanceId = "a1b2c3d4e5f6";
    private const string InstanceName = $"{InstanceId}._airdrop._tcp.local";
    private const string HostName = "MiPC.local";

    private static AirDropServiceRegistration CreateRegistration() =>
        new(
            InstanceId,
            "MiPC",
            [IPAddress.Parse("192.168.1.50"), IPAddress.Parse("fe80::1")]);

    private static (MdnsResponder Responder, FakeMdnsTransport Transport) CreateResponder()
    {
        var transport = new FakeMdnsTransport();

        // Sin espera entre anuncios: los tests comprueban que se emiten los tres, no el ritmo.
        return (
            new MdnsResponder(transport, CreateRegistration(), announcementInterval: TimeSpan.Zero),
            transport);
    }

    [Fact]
    public async Task Start_AnnouncesTheServiceRepeatedly()
    {
        var (responder, transport) = CreateResponder();
        await using var _ = responder;

        await responder.StartAsync();

        // El multicast no es fiable: un anuncio único puede perderse y dejarnos invisibles.
        Assert.True(transport.Sent.Count >= 3, $"Solo se enviaron {transport.Sent.Count} anuncios.");
        Assert.All(transport.Sent, p => Assert.True(p.WasMulticast));

        var announcement = transport.Sent[0].Decode();
        Assert.True(announcement.Header.IsResponse);
        Assert.True(announcement.Header.IsAuthoritative);
        Assert.Contains(announcement.Answers, r => r is PtrRecord { Target: InstanceName });
        Assert.Contains(announcement.Answers, r => r is SrvRecord { Port: 8770 });
        Assert.Contains(announcement.Answers, r => r is TxtRecord);
        Assert.Contains(announcement.Answers, r => r is AddressRecord);
    }

    [Fact]
    public async Task PtrQuery_IsAnsweredWithPointerServiceAndAddressRecords()
    {
        var (responder, transport) = CreateResponder();
        await using var _ = responder;
        await responder.StartAsync();
        transport.Clear();

        transport.Receive(DnsMessage.CreateQuery(
            new DnsQuestion(MdnsConstants.AirDropServiceType, DnsRecordType.Ptr)));

        var response = Assert.Single(transport.Sent).Decode();

        // Sin SRV y TXT en la misma respuesta, el consultante necesitaría dos rondas más
        // para saber a qué puerto conectarse.
        Assert.Contains(response.Answers, r => r is PtrRecord);
        Assert.Contains(response.Answers, r => r is SrvRecord);
        Assert.Contains(response.Answers, r => r is TxtRecord);
        // Y las direcciones van como additionals, para que resuelva el host sin preguntar otra vez.
        Assert.Contains(response.Additionals, r => r is AddressRecord);
    }

    [Fact]
    public async Task UnicastQuestionIsAnsweredDirectlyToTheSender()
    {
        var (responder, transport) = CreateResponder();
        await using var _ = responder;
        await responder.StartAsync();
        transport.Clear();

        var source = new IPEndPoint(IPAddress.Parse("192.168.1.77"), 5353);
        transport.Receive(
            DnsMessage.CreateQuery(new DnsQuestion(
                MdnsConstants.AirDropServiceType, DnsRecordType.Ptr, WantsUnicastResponse: true)),
            source);

        var sent = Assert.Single(transport.Sent);
        Assert.False(sent.WasMulticast);
        Assert.Equal(source, sent.Destination);
    }

    [Fact]
    public async Task MulticastQuestionIsAnsweredByMulticast()
    {
        var (responder, transport) = CreateResponder();
        await using var _ = responder;
        await responder.StartAsync();
        transport.Clear();

        transport.Receive(DnsMessage.CreateQuery(
            new DnsQuestion(MdnsConstants.AirDropServiceType, DnsRecordType.Ptr)));

        Assert.True(Assert.Single(transport.Sent).WasMulticast);
    }

    [Theory]
    [InlineData(DnsRecordType.Srv)]
    [InlineData(DnsRecordType.Txt)]
    [InlineData(DnsRecordType.Any)]
    public async Task InstanceQueriesAreAnswered(DnsRecordType type)
    {
        var (responder, transport) = CreateResponder();
        await using var _ = responder;
        await responder.StartAsync();
        transport.Clear();

        transport.Receive(DnsMessage.CreateQuery(new DnsQuestion(InstanceName, type)));

        var response = Assert.Single(transport.Sent).Decode();
        Assert.NotEmpty(response.Answers);
    }

    [Theory]
    [InlineData(DnsRecordType.A)]
    [InlineData(DnsRecordType.Aaaa)]
    public async Task HostQueriesReturnOnlyTheMatchingAddressFamily(DnsRecordType type)
    {
        var (responder, transport) = CreateResponder();
        await using var _ = responder;
        await responder.StartAsync();
        transport.Clear();

        transport.Receive(DnsMessage.CreateQuery(new DnsQuestion(HostName, type)));

        var response = Assert.Single(transport.Sent).Decode();
        Assert.NotEmpty(response.Answers);
        Assert.All(response.Answers, r => Assert.Equal(type, r.Type));
    }

    [Fact]
    public async Task NameMatchingIgnoresCaseAndTrailingDot()
    {
        // Los nombres DNS son insensibles a mayúsculas por especificación, y hay implementaciones
        // que incluyen el punto raíz final. Fallar aquí rompería el descubrimiento con esos peers
        // sin dar ninguna pista del motivo.
        var (responder, transport) = CreateResponder();
        await using var _ = responder;
        await responder.StartAsync();
        transport.Clear();

        transport.Receive(DnsMessage.CreateQuery(
            new DnsQuestion("_AIRDROP._TCP.LOCAL.", DnsRecordType.Ptr)));

        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task UnrelatedQueriesAreIgnored()
    {
        // La red local está llena de tráfico mDNS de impresoras, Chromecasts y NAS.
        var (responder, transport) = CreateResponder();
        await using var _ = responder;
        await responder.StartAsync();
        transport.Clear();

        transport.Receive(DnsMessage.CreateQuery(
            new DnsQuestion("_googlecast._tcp.local", DnsRecordType.Ptr)));
        transport.Receive(DnsMessage.CreateQuery(
            new DnsQuestion("_ipp._tcp.local", DnsRecordType.Any)));
        transport.Receive(DnsMessage.CreateQuery(
            new DnsQuestion("OtroPC.local", DnsRecordType.A)));

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task ResponsesFromOtherDevicesAreIgnored()
    {
        // Responder a una respuesta provocaría una tormenta de multicast.
        var (responder, transport) = CreateResponder();
        await using var _ = responder;
        await responder.StartAsync();
        transport.Clear();

        transport.Receive(DnsMessage.CreateResponse(
            [new PtrRecord(MdnsConstants.AirDropServiceType, "otro._airdrop._tcp.local",
                TimeSpan.FromSeconds(120))]));

        Assert.Empty(transport.Sent);
    }

    [Fact]
    public async Task MalformedPacketsAreIgnoredWithoutCrashing()
    {
        // Llegan por multicast desde cualquier equipo de la red, sin autenticación.
        var (responder, transport) = CreateResponder();
        await using var _ = responder;
        await responder.StartAsync();
        transport.Clear();

        transport.ReceiveRaw([0xFF, 0xFF, 0xFF]);
        transport.ReceiveRaw([]);
        transport.ReceiveRaw([0x00, 0x00, 0x84, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00]);

        Assert.Empty(transport.Sent);

        // Y el responder sigue funcionando después.
        transport.Receive(DnsMessage.CreateQuery(
            new DnsQuestion(MdnsConstants.AirDropServiceType, DnsRecordType.Ptr)));
        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task DuplicateAnswersAreNotRepeated()
    {
        var (responder, transport) = CreateResponder();
        await using var _ = responder;
        await responder.StartAsync();
        transport.Clear();

        // Dos preguntas que coinciden por vías distintas con los mismos registros.
        transport.Receive(DnsMessage.CreateQuery(
            new DnsQuestion(MdnsConstants.AirDropServiceType, DnsRecordType.Ptr),
            new DnsQuestion(InstanceName, DnsRecordType.Any)));

        var response = Assert.Single(transport.Sent).Decode();
        Assert.Equal(response.Answers.Count, response.Answers.Distinct().Count());
    }

    [Fact]
    public async Task Goodbye_SendsAllRecordsWithZeroTtl()
    {
        var (responder, transport) = CreateResponder();
        await using var _ = responder;
        await responder.StartAsync();
        transport.Clear();

        await responder.GoodbyeAsync();

        var goodbye = Assert.Single(transport.Sent).Decode();
        Assert.NotEmpty(goodbye.Answers);
        // TTL cero es lo que retira el servicio de las cachés ajenas al momento.
        Assert.All(goodbye.Answers, r => Assert.Equal(TimeSpan.Zero, r.Ttl));
    }

    [Fact]
    public async Task Dispose_SendsGoodbyeAndDisposesTheTransport()
    {
        var transport = new FakeMdnsTransport();
        var responder = new MdnsResponder(
            transport, CreateRegistration(), announcementInterval: TimeSpan.Zero);
        await responder.StartAsync();
        transport.Clear();

        await responder.DisposeAsync();

        var goodbye = Assert.Single(transport.Sent).Decode();
        Assert.All(goodbye.Answers, r => Assert.Equal(TimeSpan.Zero, r.Ttl));
        Assert.True(transport.IsDisposed);
    }

    [Fact]
    public async Task SendFailuresDoNotBringDownTheResponder()
    {
        var (responder, transport) = CreateResponder();
        await using var _ = responder;
        await responder.StartAsync();
        transport.Clear();
        transport.SendFailure = new IOException("interfaz caída");

        // No debe propagar: la siguiente consulta volverá a intentarlo.
        transport.Receive(DnsMessage.CreateQuery(
            new DnsQuestion(MdnsConstants.AirDropServiceType, DnsRecordType.Ptr)));

        transport.SendFailure = null;
        transport.Receive(DnsMessage.CreateQuery(
            new DnsQuestion(MdnsConstants.AirDropServiceType, DnsRecordType.Ptr)));

        Assert.Single(transport.Sent);
    }

    [Fact]
    public async Task StartIsIdempotent()
    {
        var (responder, transport) = CreateResponder();
        await using var _ = responder;

        await responder.StartAsync();
        var afterFirst = transport.Sent.Count;
        await responder.StartAsync();

        Assert.Equal(afterFirst, transport.Sent.Count);
    }
}

public class AirDropServiceRegistrationTests
{
    [Fact]
    public void InstanceName_CombinesIdWithServiceType()
    {
        var registration = new AirDropServiceRegistration("abc123", "MiPC", []);

        Assert.Equal("abc123._airdrop._tcp.local", registration.InstanceName);
    }

    [Fact]
    public void HostName_GetsTheLocalSuffixWhenMissing()
    {
        Assert.Equal("MiPC.local", new AirDropServiceRegistration("a", "MiPC", []).HostName);
        Assert.Equal("MiPC.local", new AirDropServiceRegistration("a", "MiPC.local", []).HostName);
    }

    [Fact]
    public void GenerateInstanceId_IsRandomAndHexadecimal()
    {
        // Un identificador estable permitiría rastrear el equipo entre redes distintas.
        var first = AirDropServiceRegistration.GenerateInstanceId();
        var second = AirDropServiceRegistration.GenerateInstanceId();

        Assert.NotEqual(first, second);
        Assert.Equal(12, first.Length);
        Assert.All(first, c => Assert.True(Uri.IsHexDigit(c), $"'{c}' no es hexadecimal."));
    }

    [Fact]
    public void TxtRecord_AnnouncesTheConfiguredFlags()
    {
        var registration = new AirDropServiceRegistration(
            "abc123", "MiPC", [], AirDropFlags.Supported);

        var txt = Assert.IsType<TxtRecord>(
            registration.CreateInstanceRecords().First(r => r is TxtRecord));

        Assert.Equal(
            ((int)AirDropFlags.Supported).ToString(),
            txt.ToPairs()["flags"]);
    }

    [Fact]
    public void AddressRecords_UseTheRightTypePerFamily()
    {
        var registration = new AirDropServiceRegistration(
            "abc123",
            "MiPC",
            [IPAddress.Parse("192.168.1.50"), IPAddress.Parse("fe80::1")]);

        var records = registration.CreateAddressRecords();

        Assert.Equal(2, records.Count);
        Assert.Equal(DnsRecordType.A, records[0].Type);
        Assert.Equal(DnsRecordType.Aaaa, records[1].Type);
    }

    [Fact]
    public void HostRecordsUseShorterTtlThanServiceRecords()
    {
        // Los registros ligados al host cambian cuando el equipo cambia de IP; un TTL largo
        // dejaría a los demás conectando a una dirección muerta.
        var registration = new AirDropServiceRegistration(
            "abc123", "MiPC", [IPAddress.Loopback]);

        var srv = registration.CreateInstanceRecords().First(r => r is SrvRecord);
        var ptr = registration.CreatePointerRecords()[0];

        Assert.True(srv.Ttl < ptr.Ttl);
    }

    [Fact]
    public void Constructor_RejectsInvalidArguments()
    {
        Assert.Throws<ArgumentException>(() => new AirDropServiceRegistration("", "MiPC", []));
        Assert.Throws<ArgumentException>(() => new AirDropServiceRegistration("abc", "  ", []));
        Assert.Throws<ArgumentNullException>(() => new AirDropServiceRegistration("abc", "MiPC", null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AirDropServiceRegistration("abc", "MiPC", [], port: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AirDropServiceRegistration("abc", "MiPC", [], port: 70_000));
    }
}
