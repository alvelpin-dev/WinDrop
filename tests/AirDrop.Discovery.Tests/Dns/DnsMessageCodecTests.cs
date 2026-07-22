using System.Net;
using AirDrop.Discovery.Dns;
using Xunit;

namespace AirDrop.Discovery.Tests.Dns;

public class DnsMessageCodecTests
{
    private const string ServiceType = "_airdrop._tcp.local";
    private const string InstanceName = "MiPC._airdrop._tcp.local";
    private const string HostName = "MiPC.local";

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(120);

    [Fact]
    public void Query_SurvivesRoundTrip()
    {
        var query = DnsMessage.CreateQuery(new DnsQuestion(ServiceType, DnsRecordType.Ptr));

        var result = DnsMessageReader.Read(DnsMessageWriter.Write(query));

        Assert.False(result.Header.IsResponse);
        var question = Assert.Single(result.Questions);
        Assert.Equal(ServiceType, question.Name);
        Assert.Equal(DnsRecordType.Ptr, question.Type);
        Assert.False(question.WantsUnicastResponse);
    }

    [Fact]
    public void Query_PreservesUnicastResponseBit()
    {
        // El bit QU se envía en la primera consulta tras arrancar, para no inundar la red.
        var query = DnsMessage.CreateQuery(
            new DnsQuestion(ServiceType, DnsRecordType.Ptr, WantsUnicastResponse: true));

        var result = DnsMessageReader.Read(DnsMessageWriter.Write(query));

        Assert.True(result.Questions[0].WantsUnicastResponse);
    }

    [Fact]
    public void Response_SurvivesRoundTripWithAllRecordTypes()
    {
        // La forma de una respuesta completa de AirDrop: PTR + SRV + TXT + direcciones.
        var response = DnsMessage.CreateResponse(
            [
                new PtrRecord(ServiceType, InstanceName, Ttl),
                new SrvRecord(InstanceName, HostName, 8770, Ttl),
                TxtRecord.FromPairs(
                    InstanceName,
                    new Dictionary<string, string> { ["flags"] = "137" },
                    Ttl),
            ],
            [
                new AddressRecord(HostName, IPAddress.Parse("192.168.1.50"), Ttl),
                new AddressRecord(HostName, IPAddress.Parse("fe80::1"), Ttl),
            ]);

        var result = DnsMessageReader.Read(DnsMessageWriter.Write(response));

        Assert.True(result.Header.IsResponse);
        Assert.True(result.Header.IsAuthoritative);
        Assert.Equal(3, result.Answers.Count);
        Assert.Equal(2, result.Additionals.Count);

        var ptr = Assert.IsType<PtrRecord>(result.Answers[0]);
        Assert.Equal(InstanceName, ptr.Target);

        var srv = Assert.IsType<SrvRecord>(result.Answers[1]);
        Assert.Equal(HostName, srv.Target);
        Assert.Equal(8770, srv.Port);

        var txt = Assert.IsType<TxtRecord>(result.Answers[2]);
        Assert.Equal("137", txt.ToPairs()["flags"]);

        var a = Assert.IsType<AddressRecord>(result.Additionals[0]);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), a.Address);
        Assert.Equal(DnsRecordType.A, a.Type);

        var aaaa = Assert.IsType<AddressRecord>(result.Additionals[1]);
        Assert.Equal(DnsRecordType.Aaaa, aaaa.Type);
    }

    [Fact]
    public void Writer_CompressesRepeatedNameSuffixes()
    {
        // Sin compresión, una respuesta de AirDrop puede pasarse del MTU y fragmentarse, que en
        // multicast es una causa clásica de descubrimientos intermitentes.
        var response = DnsMessage.CreateResponse(
            [
                new PtrRecord(ServiceType, InstanceName, Ttl),
                new SrvRecord(InstanceName, HostName, 8770, Ttl),
                TxtRecord.FromPairs(InstanceName, new Dictionary<string, string>(), Ttl),
            ]);

        var encoded = DnsMessageWriter.Write(response);

        // "_airdrop" aparece una sola vez pese a estar en los tres registros.
        Assert.Equal(1, CountOccurrences(encoded, "_airdrop"u8));
        // Y el mensaje sigue siendo legible pese a la compresión.
        Assert.Equal(3, DnsMessageReader.Read(encoded).Answers.Count);
    }

    [Fact]
    public void Reader_ParsesHandBuiltPacket()
    {
        // Paquete construido byte a byte según el RFC 1035, para validar el lector de forma
        // independiente de nuestro propio escritor.
        var packet = new List<byte>
        {
            0x00, 0x00,             // ID
            0x84, 0x00,             // flags: QR=1, AA=1
            0x00, 0x00,             // QDCOUNT
            0x00, 0x01,             // ANCOUNT
            0x00, 0x00,             // NSCOUNT
            0x00, 0x00,             // ARCOUNT
        };

        // Nombre: _airdrop._tcp.local
        packet.AddRange([8, .. "_airdrop"u8]);
        packet.AddRange([4, .. "_tcp"u8]);
        packet.AddRange([5, .. "local"u8]);
        packet.Add(0);

        packet.AddRange([0x00, 0x0C]);                    // TYPE = PTR
        packet.AddRange([0x00, 0x01]);                    // CLASS = IN
        packet.AddRange([0x00, 0x00, 0x00, 0x78]);        // TTL = 120

        // RDATA: "MiPC" + puntero de compresión al nombre del offset 12.
        List<byte> rdata = [4, .. "MiPC"u8, 0xC0, 0x0C];
        packet.AddRange([0x00, (byte)rdata.Count]);
        packet.AddRange(rdata);

        var message = DnsMessageReader.Read([.. packet]);

        Assert.True(message.Header.IsResponse);
        var ptr = Assert.IsType<PtrRecord>(Assert.Single(message.Answers));
        Assert.Equal(ServiceType, ptr.Name);
        // El puntero debe haberse resuelto hasta el nombre completo.
        Assert.Equal(InstanceName, ptr.Target);
        Assert.Equal(TimeSpan.FromSeconds(120), ptr.Ttl);
    }

    [Fact]
    public void Reader_RejectsCompressionPointerLoops()
    {
        // Un puntero que se apunta a sí mismo colgaría un parser ingenuo. Llega por multicast
        // desde cualquier equipo de la red, sin autenticación: no puede tumbarnos.
        var packet = new List<byte>
        {
            0x00, 0x00, 0x84, 0x00,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            0xC0, 0x0C,   // en el offset 12, un puntero al offset 12
        };

        Assert.Throws<DnsFormatException>(() => DnsMessageReader.Read([.. packet]));
    }

    [Fact]
    public void Reader_RejectsPointerBeyondTheMessage()
    {
        var packet = new List<byte>
        {
            0x00, 0x00, 0x84, 0x00,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
            0xC0, 0xFF,   // offset 255, fuera de un mensaje de 14 bytes
        };

        Assert.Throws<DnsFormatException>(() => DnsMessageReader.Read([.. packet]));
    }

    [Fact]
    public void Reader_RejectsTruncatedMessages()
    {
        Assert.Throws<DnsFormatException>(() => DnsMessageReader.Read([0x00, 0x00, 0x84]));
    }

    [Fact]
    public void Reader_RejectsHeaderClaimingMoreRecordsThanPresent()
    {
        // ANCOUNT dice 5 pero no viene ninguno.
        byte[] packet = [0x00, 0x00, 0x84, 0x00, 0x00, 0x00, 0x00, 0x05, 0x00, 0x00, 0x00, 0x00];

        Assert.Throws<DnsFormatException>(() => DnsMessageReader.Read(packet));
    }

    [Fact]
    public void Reader_PreservesUnknownRecordTypesInsteadOfFailing()
    {
        // Un tipo que no manejamos no debe invalidar el mensaje entero: la red local está llena
        // de servicios que anuncian registros que no nos interesan.
        var message = DnsMessage.CreateResponse(
            [new UnknownRecord(HostName, DnsRecordType.Nsec, [0x01, 0x02, 0x03], Ttl)]);

        var result = DnsMessageReader.Read(DnsMessageWriter.Write(message));

        var unknown = Assert.IsType<UnknownRecord>(Assert.Single(result.Answers));
        Assert.Equal(DnsRecordType.Nsec, unknown.Type);
        Assert.Equal<byte[]>([0x01, 0x02, 0x03], unknown.Data);
    }

    [Fact]
    public void CacheFlushBit_SurvivesRoundTrip()
    {
        // Es lo que permite anunciar un cambio de nombre o de IP sin esperar al TTL.
        var message = DnsMessage.CreateResponse(
            [
                new SrvRecord(InstanceName, HostName, 8770, Ttl, CacheFlush: true),
                new PtrRecord(ServiceType, InstanceName, Ttl, CacheFlush: false),
            ]);

        var result = DnsMessageReader.Read(DnsMessageWriter.Write(message));

        Assert.True(result.Answers[0].CacheFlush);
        Assert.False(result.Answers[1].CacheFlush);
    }

    [Fact]
    public void GoodbyeRecordWithZeroTtlSurvivesRoundTrip()
    {
        // TTL 0 es como se anuncia la retirada de un servicio al cerrar la aplicación.
        var message = DnsMessage.CreateResponse(
            [new PtrRecord(ServiceType, InstanceName, TimeSpan.Zero)]);

        var result = DnsMessageReader.Read(DnsMessageWriter.Write(message));

        Assert.Equal(TimeSpan.Zero, result.Answers[0].Ttl);
    }

    [Fact]
    public void TxtRecord_ParsesKeyValuePairs()
    {
        var txt = new TxtRecord(InstanceName, ["flags=137", "sinvalor", "vacio="], Ttl);

        var pairs = txt.ToPairs();

        Assert.Equal("137", pairs["flags"]);
        // Una cadena sin '=' es una clave con valor vacío, según DNS-SD.
        Assert.Equal(string.Empty, pairs["sinvalor"]);
        Assert.Equal(string.Empty, pairs["vacio"]);
    }

    [Fact]
    public void TxtRecord_EmptyStillProducesValidRdata()
    {
        // RDATA de longitud cero es inválido: debe emitirse al menos un byte nulo.
        var message = DnsMessage.CreateResponse([new TxtRecord(InstanceName, [], Ttl)]);

        var result = DnsMessageReader.Read(DnsMessageWriter.Write(message));

        Assert.IsType<TxtRecord>(Assert.Single(result.Answers));
    }

    [Fact]
    public void Writer_RejectsOversizedLabels()
    {
        var tooLong = new string('a', 64);
        var message = DnsMessage.CreateResponse([new PtrRecord($"{tooLong}.local", "x.local", Ttl)]);

        Assert.Throws<DnsFormatException>(() => DnsMessageWriter.Write(message));
    }

    [Fact]
    public void Writer_DerivesCountsFromActualLists()
    {
        // Un header con contadores incoherentes no debe poder salir al cable.
        var inconsistent = new DnsMessage(
            new DnsHeader(0, IsResponse: true, IsAuthoritative: true,
                QuestionCount: 9, AnswerCount: 9, AuthorityCount: 9, AdditionalCount: 9),
            [],
            [new PtrRecord(ServiceType, InstanceName, Ttl)],
            [],
            []);

        var result = DnsMessageReader.Read(DnsMessageWriter.Write(inconsistent));

        Assert.Equal(0, result.Header.QuestionCount);
        Assert.Equal(1, result.Header.AnswerCount);
        Assert.Single(result.Answers);
    }

    private static int CountOccurrences(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        var count = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
            {
                count++;
            }
        }

        return count;
    }
}
