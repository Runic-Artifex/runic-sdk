using TUnit.Core;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// S1: window tokens, session credentials, client cookies and launch codes come from getrandom on Linux, so the
// process does not load OpenSSL for them; elsewhere from RandomNumberGenerator.
public sealed class OsRandomTests
{
    [Test]
    public async Task FillsEveryByteAcrossSeveralRequests()
    {
        // More than one getrandom request (256 bytes each).
        var first = OsRandom.GetBytes(1000);
        var second = OsRandom.GetBytes(1000);
        Assert.Equal(1000, first.Length);
        Assert.False(first.AsSpan().SequenceEqual(second));
        // Every byte is written: a 64-byte run of zeros has probability 2^-512.
        for (var offset = 0; offset + 64 <= first.Length; offset += 64)
        {
            Assert.True(first.AsSpan(offset, 64).ContainsAnyExcept((byte)0));
        }
        Span<byte> empty = [];
        OsRandom.Fill(empty);
        await Task.CompletedTask;
    }
}
