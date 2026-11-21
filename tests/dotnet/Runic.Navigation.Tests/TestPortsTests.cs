using System.Net;
using System.Net.Sockets;
using TUnit.Core;

namespace Runic.Navigation.Tests;

// The shared port lease (tests/support/TestPorts.cs), checked here once for every project that compiles it.
internal sealed class TestPortsTests
{
    [Test]
    public void ALeaseIsHeldUntilReleasedAndThenBinds()
    {
        using var lease = TestPorts.Lease();
        Require(lease.IsHeld && !Binds(lease.Port), $"Port {lease.Port} was not held by its lease.");
        Require(lease.Release() == lease.Port && !lease.IsHeld && Binds(lease.Port), $"Port {lease.Port} did not bind after its release.");
        lease.Release();
    }

    [Test]
    public void LeasesComeFromOutsideTheEphemeralRangeAndDoNotRepeatWhileHeld()
    {
        var (low, high) = TestPorts.Candidates();
        var leases = Enumerable.Range(0, 20).Select(_ => TestPorts.Lease()).ToList();
        try
        {
            Require(leases.All(lease => lease.Port >= low && lease.Port <= high), $"A port outside {low}-{high} was leased.");
            Require(leases.Select(lease => lease.Port).Distinct().Count() == leases.Count, "A held port was leased twice.");
            if (OperatingSystem.IsLinux())
            {
                var fields = File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range").Split((char[])['\t', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries);
                int first = int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture), last = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
                Require(leases.All(lease => lease.Port < first || lease.Port > last), $"A port inside the ephemeral range {first}-{last} was leased.");
            }
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    [Test]
    public void TheCandidatesAreTheLargerSpanBesideTheEphemeralRange()
    {
        Require(TestPorts.Candidates((32_768, 60_999)) == (10_000, 32_767), "Linux's default range did not leave the ports below it.");
        Require(TestPorts.Candidates((49_152, 65_535)) == (10_000, 49_151), "The IANA range did not leave the ports below it.");
        Require(TestPorts.Candidates((1_024, 40_000)) == (40_001, 65_535), "A range that starts low did not leave the ports above it.");
        try
        {
            TestPorts.Candidates((1_024, 65_000));
            throw new InvalidOperationException("A range that leaves no ports was accepted.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("leaves no ports", StringComparison.Ordinal)) { }
    }

    [Test]
    public void APortThatASocketUsesIsReportedOnLinux()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Require(TestPorts.SocketsUse(port) == OperatingSystem.IsLinux(), $"A listener on {port} was not seen in the socket tables.");
    }

    private static bool Binds(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
