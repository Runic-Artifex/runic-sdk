using System.Collections.Immutable;
using System.Runtime.Versioning;
using static Runic.Platform.Windows.WindowsNotificationInterop;

namespace Runic.Platform.Windows;

// The activation of a process installed from an MSIX package, through Windows.ApplicationModel.AppInstance's
// GetActivatedEventArgs in raw WinRT ABI calls (as WindowsNotificationInterop does). A protocol activation carries its
// URI, a file activation every file (several in one process with MultiSelectModel="Player"); a launch or a startup task
// carries only the arguments.
[SupportedOSPlatform("windows10.0.17134")]
internal static unsafe class WindowsActivation
{
    internal const int Launch = 0, File = 3, Protocol = 4;
    private const string AppInstanceStatics = "9d11e77f-9ea6-47af-a6ec-46784c5ba254", ActivatedEventArgs = "cf651713-cd08-4fd8-b697-a281b6544e2e",
        ProtocolArgs = "6095f4dd-b7c0-46ab-81fe-d90f36d00d24", FileArgs = "bb2afc33-93b1-42ed-8b26-236dd9c78496",
        StorageItem = "4207a996-ca2f-42f7-bde8-8b10457a7f30";
    private const int MaximumItems = 1024;

    // The activation's kind and items, or null when the process has none to read.
    internal static (int Kind, ImmutableArray<string> Items)? Read()
    {
        // WinRT on a thread of the multithreaded apartment, so the caller's thread keeps the apartment it chooses.
        return Task.Run(ReadOnWorker).GetAwaiter().GetResult();
    }

    private static (int Kind, ImmutableArray<string> Items)? ReadOnWorker()
    {
        nint statics = 0, args = 0, typed = 0, value = 0;
        try
        {
            InitializeRuntime();
            statics = Factory("Windows.ApplicationModel.AppInstance", AppInstanceStatics);
            args = Get(statics, 7); // GetActivatedEventArgs
            if (args == 0) return null;
            nint activated = Query(args, ActivatedEventArgs);
            int kind;
            try { kind = (int)Get(activated, 6); } // get_Kind
            finally { Release(activated); }
            var items = ImmutableArray.CreateBuilder<string>();
            if (kind == Protocol)
            {
                typed = Query(args, ProtocolArgs);
                value = Get(typed, 6); // get_Uri: Windows.Foundation.Uri
                nint text = Get(value, 6); // IUriRuntimeClass.get_AbsoluteUri
                try { items.Add(ReadString(text)); }
                finally { DeleteString(text); }
            }
            else if (kind == File)
            {
                typed = Query(args, FileArgs);
                value = Get(typed, 6); // get_Files: IVectorView<IStorageItem>
                uint count = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(value, 7))(value, &count)); // get_Size
                for (uint index = 0; index < count && items.Count < MaximumItems; index++)
                {
                    nint item = 0, storage = 0, path = 0;
                    try
                    {
                        Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(value, 6))(value, index, &item)); // GetAt
                        storage = Query(item, StorageItem);
                        path = Get(storage, 12); // IStorageItem.get_Path
                        string text = ReadString(path);
                        if (text.Length > 0) items.Add(text);
                    }
                    finally { if (path != 0) DeleteString(path); Release(storage); Release(item); }
                }
            }
            return (kind, items.ToImmutable());
        }
        // A failed WinRT call (Marshal.ThrowExceptionForHR) means there is no activation to read: the arguments stay.
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException or InvalidCastException or EntryPointNotFoundException
            or DllNotFoundException or UnauthorizedAccessException or ArgumentException or NotImplementedException or InvalidOperationException or IOException)
        {
            return null;
        }
        finally { Release(value); Release(typed); Release(args); Release(statics); }
    }

    // The launch with the activation's items as arguments: those the process's arguments do not already carry are
    // appended in the activation's order.
    internal static ApplicationLaunch Merge(IEnumerable<string> arguments, (int Kind, ImmutableArray<string> Items)? activation)
    {
        var list = arguments.ToList();
        if (activation is { Kind: Protocol or File, Items: var items })
            foreach (string item in items)
                if (!list.Contains(item, StringComparer.OrdinalIgnoreCase)) list.Add(item);
        return ApplicationLaunch.FromProcess(list);
    }
}
