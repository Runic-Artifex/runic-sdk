using System.Collections.Immutable;
using Runic.Platform.Administration.Windows.Internal;
using Windows.Win32;
using Windows.Win32.System.GroupPolicy;
using Windows.Win32.System.Variant;

namespace Runic.Platform.Administration.Windows.GroupPolicy;

public sealed partial class WindowsGroupPolicyClient
{
    /// <summary>Enumerates every backup in an explicit GPMC backup directory, without selecting by name or recency.</summary>
    /// <remarks>Requires local GPMC but does not connect to the configured domain. No partial results are returned on failure or cancellation.</remarks>
    public static Task<ImmutableArray<GroupPolicyBackup>> EnumerateBackupsAsync(string directory, CancellationToken cancellationToken = default)
    {
        Automation.RequireX64();
        AbsolutePath(directory);
        return ComApartment.RunAsync(() => ReadBackups(directory, cancellationToken), cancellationToken);
    }

    private static unsafe ImmutableArray<GroupPolicyBackup> ReadBackups(string directory, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var gpm = ComObject.Create(new("f5694708-88fe-4b35-babf-e56162d5fbc8"), new("f5fae809-3bd6-4da9-a65e-17665b41d763"));
        using var backupDirectory = StringObject(gpm, GpmLookup.BackupDirectory, directory, "Open GPO backup directory");
        using var criteria = GpmRead.GetObject(gpm, GpmGetObject.CreateSearchCriteria, "Create backup search criteria");
        nint pointer = 0;
        using var collection = ComObject.FromResult(((IGPMBackupDir*)backupDirectory.Pointer)->SearchBackups(
            (IGPMSearchCriteria*)criteria.Pointer, (IGPMBackupCollection**)&pointer).Value, pointer, "Enumerate GPO backups");
        int count = 0;
        NativeError.Check(((IGPMBackupCollection*)collection.Pointer)->get_Count(&count).Value, "Read backup count");
        if (count < 0) throw NativeError.Win32("Read backup count", 13);
        var result = ImmutableArray.CreateBuilder<GroupPolicyBackup>();
        for (var index = 1; index <= count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VARIANT value = default;
            try
            {
                NativeError.Check(((IGPMBackupCollection*)collection.Pointer)->get_Item(index, &value).Value, "Read backup item");
                using var backup = VariantObject(value, IGPMBackup.IID_Guid);
                result.Add(BackupSnapshot(backup));
            }
            finally { _ = PInvoke.VariantClear(&value); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result.ToImmutable();
    }

    private static GroupPolicyBackup BackupSnapshot(ComObject backup) => new(
        NativeError.ParseGuid(GpmRead.GetString(backup, GpmGetString.BackupID, "Read backup ID")),
        NativeError.ParseGuid(GpmRead.GetString(backup, GpmGetString.BackupGPOID, "Read backed-up GPO ID")),
        GpmRead.GetString(backup, GpmGetString.BackupGPODomain, "Read backup domain"),
        GpmRead.GetString(backup, GpmGetString.BackupGPODisplayName, "Read backup name"),
        Date(backup, GpmDate.Backup), GpmRead.GetString(backup, GpmGetString.BackupComment, "Read backup comment"),
        GpmRead.GetString(backup, GpmGetString.BackupBackupDir, "Read backup directory"));
}
