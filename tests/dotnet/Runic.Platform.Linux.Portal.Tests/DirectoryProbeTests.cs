using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Runic.Platform.Runtime;
using TUnit.Core;

// A1: the portal directory picker learns whether the FileChooser portal (version 3) is on the bus when it is created,
// so platform.directories.open reports a missing portal before the first pick instead of after it.
internal sealed class DirectoryProbeTests
{
    [Test, DisplayName("a missing or old FileChooser portal makes directory selection unavailable before a pick")]
    public async Task MissingPortalAsync()
    {
        foreach (var transport in new[] { new ProbedTransport { Missing = true }, new ProbedTransport { Version = 2 } })
        {
            var picker = new PortalFilePicker(new Owner(), transport);
            var backend = new NativePickerBackend(new Owner(), picker);
            Check(picker.IsDirectorySelectionAvailable && backend.SupportsDirectorySelection, "an unprobed portal is assumed present");
            await picker.ProbeDirectorySelectionAsync();
            Check(!picker.IsDirectorySelectionAvailable && !backend.SupportsDirectorySelection && !backend.SupportsMultipleDirectorySelection,
                "the probe did not report the missing portal");
            // A pick checks again and reports the portal missing without a request.
            var result = await backend.OpenDirectoryAsync(new());
            Check(result is PickerResult<IDirectoryLease>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable } && transport.Requests == 0,
                $"a pick with a missing portal returned {result} after {transport.Requests} requests");
        }
    }

    [Test, DisplayName("a portal that appears later is found again by the next pick")]
    public async Task PortalAppearsAsync()
    {
        var transport = new ProbedTransport { Missing = true, Response = new(1, []) };
        var picker = new PortalFilePicker(new Owner(), transport);
        var backend = new NativePickerBackend(new Owner(), picker);
        await picker.ProbeDirectorySelectionAsync();
        Check(!backend.SupportsDirectorySelection, "the probe found a portal that is missing");
        transport.Missing = false;
        var result = await backend.OpenDirectoryAsync(new());
        Check(result is PickerResult<IDirectoryLease>.Dismissed && transport.Requests == 1 && backend.SupportsDirectorySelection,
            $"the pick did not reach the portal that appeared: {result}");
    }

    [Test, DisplayName("a pick that finds no portal makes directory selection unavailable")]
    public async Task PickFindsNoPortalAsync()
    {
        var transport = new ProbedTransport { Unanswered = true };
        var picker = new PortalFilePicker(new Owner(), transport);
        await picker.ProbeDirectorySelectionAsync();
        Check(picker.IsDirectorySelectionAvailable, "the probe reported a present portal missing");
        var result = await new NativePickerBackend(new Owner(), picker).OpenDirectoryAsync(new());
        Check(result is PickerResult<IDirectoryLease>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
            && !picker.IsDirectorySelectionAvailable, $"a pick without an answering portal returned {result}");
    }

    private sealed class ProbedTransport : IPortalTransport
    {
        public bool Missing { get; set; }
        public uint Version { get; init; } = 3;
        public bool Unanswered { get; init; }
        public PortalResponse Response { get; init; } = new(1, []);
        public int Requests { get; private set; }

        public ValueTask<uint> GetFileChooserVersionAsync(CancellationToken cancellationToken) =>
            Missing ? ValueTask.FromException<uint>(new NativeBackendUnavailableException()) : ValueTask.FromResult(Version);

        public ValueTask<PortalResponse> RequestAsync(string parent, string method, string argument, CancellationToken cancellationToken)
        {
            Requests++;
            return Unanswered ? ValueTask.FromException<PortalResponse>(new NativeBackendUnavailableException()) : ValueTask.FromResult(Response);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
