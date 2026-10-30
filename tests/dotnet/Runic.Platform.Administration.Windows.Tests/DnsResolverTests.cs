using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Runic.Platform.Administration.Windows.Dns;
using TUnit.Core;
using TUnit.Core.Enums;

namespace Runic.Platform.Administration.Windows.Tests;

// sdk#728: WindowsDnsResolverClient asks the chosen server, not the configured resolvers. A DNS responder on
// 127.0.0.1:53 answers only these names, so the answers prove which server was asked.
[SupportedOSPlatform("windows")]
[RunOn(OS.Windows)]
internal sealed class DnsResolverTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [Test]
    public async Task ResolverQueriesTheChosenServerAsync()
    {
        UdpClient socket;
        try { socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 53)); }
        catch (SocketException error) { Skip.Test("UDP port 53 on the loopback address is in use: " + error.SocketErrorCode); return; }
        using var cancellation = new CancellationTokenSource();
        var responder = Task.Run(() => Respond(socket, cancellation.Token));
        try
        {
            var resolver = new WindowsDnsResolverClient(IPAddress.Loopback);
            var a = await resolver.QueryAsync("host.runic.test", DnsRecordType.A);
            Check(a.ResponseCode == DnsResponseCode.NoError && a.Answers.Length == 2
                && a.Answers[0] == new DnsAnswer("host.runic.test", DnsRecordType.A, TimeSpan.FromSeconds(300), new DnsRecordData.A(IPAddress.Parse("192.0.2.10")))
                && a.Answers[1].Data == new DnsRecordData.A(IPAddress.Parse("192.0.2.11")), "A: " + string.Join(", ", a.Answers));
            var alias = await resolver.QueryAsync("alias.runic.test", DnsRecordType.A);
            Check(alias.Answers.Length == 2 && alias.Answers[0].Data is DnsRecordData.CName { Target: "host.runic.test" }
                && alias.Answers[1].Name == "host.runic.test", "CNAME chain: " + string.Join(", ", alias.Answers));
            var ptr = await resolver.ReverseLookupAsync(IPAddress.Parse("192.0.2.10"));
            Check(ptr.Answers.SingleOrDefault()?.Data == new DnsRecordData.Ptr("host.runic.test"), "PTR: " + string.Join(", ", ptr.Answers));
            var v6 = await resolver.QueryAsync("host.runic.test", DnsRecordType.Aaaa);
            Check(v6.Answers.SingleOrDefault()?.Data == new DnsRecordData.Aaaa(IPAddress.Parse("2001:db8::10")), "AAAA: " + string.Join(", ", v6.Answers));
            var missing = await resolver.QueryAsync("missing.runic.test", DnsRecordType.A);
            Check(missing == new DnsResolution(DnsResponseCode.NameError, []), "NXDOMAIN: " + missing);
            var empty = await resolver.QueryAsync("host.runic.test", DnsRecordType.Mx);
            Check(empty.ResponseCode == DnsResponseCode.NoError && empty.Answers.IsEmpty, "An empty answer: " + empty);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { _ = await resolver.QueryAsync("host.runic.test", DnsRecordType.A, canceled.Token); throw new InvalidOperationException("A canceled query ran."); }
            catch (OperationCanceledException) { }
        }
        finally
        {
            await cancellation.CancelAsync();
            socket.Dispose();
            try { await responder; } catch (OperationCanceledException) { } catch (ObjectDisposedException) { } catch (SocketException) { }
        }
        Console.WriteLine("PASS DNS resolver queries against a chosen server: A, CNAME chain, PTR, AAAA, NXDOMAIN and an empty answer.");
    }

    [Test]
    public async Task UnansweredQueriesAreUnavailableAsync()
    {
        // TEST-NET-1 has no DNS server; Windows retries before it reports ERROR_TIMEOUT.
        try { _ = await new WindowsDnsResolverClient(IPAddress.Parse("192.0.2.1")).QueryAsync("host.runic.test", DnsRecordType.A); throw new InvalidOperationException("An unreachable server answered."); }
        catch (WindowsAdministrationException error)
        {
            Check(error.Category is AdministrationErrorCategory.Unavailable or AdministrationErrorCategory.NativeFailure,
                $"An unreachable server must fail, not {error.Category} ({error.NativeErrorCode}).");
            Console.WriteLine($"PASS An unreachable DNS server fails with {error.Category} ({error.NativeErrorCode}).");
        }
    }

    private static async Task Respond(UdpClient socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var request = await socket.ReceiveAsync(cancellationToken);
            var response = Answer(request.Buffer);
            if (response is not null) await socket.SendAsync(response, request.RemoteEndPoint, cancellationToken);
        }
    }

    // A minimal responder: one question, answers with compressed or full owner names, no additional records.
    private static byte[]? Answer(byte[] query)
    {
        if (query.Length < 17) return null;
        var offset = 12;
        var labels = new List<string>();
        while (query[offset] != 0)
        {
            labels.Add(Encoding.ASCII.GetString(query, offset + 1, query[offset]).ToLowerInvariant());
            offset += query[offset] + 1;
        }
        var name = string.Join('.', labels);
        var type = BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(offset + 1));
        var questionEnd = offset + 5;
        var answers = new List<byte[]>();
        var code = 0;
        switch (name, type)
        {
            case ("host.runic.test", 1):
                answers.Add(Record(null, 1, [192, 0, 2, 10]));
                answers.Add(Record(null, 1, [192, 0, 2, 11]));
                break;
            case ("host.runic.test", 28):
                answers.Add(Record(null, 28, IPAddress.Parse("2001:db8::10").GetAddressBytes()));
                break;
            case ("alias.runic.test", 1):
                answers.Add(Record(null, 5, Name("host.runic.test")));
                answers.Add(Record("host.runic.test", 1, [192, 0, 2, 10]));
                break;
            case ("10.2.0.192.in-addr.arpa", 12):
                answers.Add(Record(null, 12, Name("host.runic.test")));
                break;
            case ("host.runic.test", _) or ("alias.runic.test", _):
                break;
            default:
                code = 3;
                break;
        }
        var response = new List<byte>();
        response.AddRange(query.AsSpan(0, 2).ToArray());
        response.AddRange([0x85, (byte)(0x80 | code)]); // QR, AA, RD, RA and the response code
        response.AddRange([0, 1, 0, (byte)answers.Count, 0, 0, 0, 0]);
        response.AddRange(query.AsSpan(12, questionEnd - 12).ToArray());
        foreach (var answer in answers) response.AddRange(answer);
        return [.. response];
    }

    private static byte[] Record(string? owner, ushort type, byte[] data)
    {
        var record = new List<byte>();
        record.AddRange(owner is null ? [0xc0, 12] : Name(owner));
        record.AddRange([(byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0x01, 0x2c, (byte)(data.Length >> 8), (byte)data.Length]);
        record.AddRange(data);
        return [.. record];
    }

    private static byte[] Name(string name)
    {
        var bytes = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        bytes.Add(0);
        return [.. bytes];
    }
}
