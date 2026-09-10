using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.GroupPolicy;
using Windows.Win32.System.Variant;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.GroupPolicy;

/// <summary>GPMC operations for an explicit domain and controller, using the caller's Windows identity.</summary>
/// <remarks>Requires GPMC/RSAT. Long native operations run on an owned apartment; cancellation after native execution starts does not imply rollback.</remarks>
public sealed partial class WindowsGroupPolicyClient
{
    private readonly string _domainName, _controller;

    /// <summary>Creates a client with no implicit domain-controller selection.</summary>
    public WindowsGroupPolicyClient(string domainName, string domainController)
    {
        Automation.RequireX64();
        NativeError.Text(domainName, nameof(domainName));
        NativeError.Text(domainController, nameof(domainController));
        _domainName = domainName;
        _controller = domainController;
    }

    /// <summary>Enumerates all GPOs; display names are not assumed to be unique.</summary>
    public Task<ImmutableArray<GroupPolicySnapshot>> EnumerateAsync(CancellationToken cancellationToken = default) =>
        Execute((gpm, domain) =>
        {
            using var criteria = GpmRead.GetObject(gpm, GpmGetObject.CreateSearchCriteria, "Create GPO search criteria");
            using var collection = SearchGpos(domain, criteria.Pointer, "Search GPOs");
            var result = ImmutableArray.CreateBuilder<GroupPolicySnapshot>();
            var count = GpmRead.GetInt32(collection, GpmGetInt32.GPOCollectionCount, "Read GPO count");
            for (var i = 1; i <= count; i++) { using var gpo = Item(collection, GpmCollection.Gpos, i); result.Add(Snapshot(gpo)); }
            return result.ToImmutable();
        }, cancellationToken);

    /// <summary>Finds a GPO by GUID; null means absent.</summary>
    public Task<GroupPolicySnapshot?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Execute((_, domain) =>
        {
            using var gpo = Find(domain, id);
            return gpo is null ? null : Snapshot(gpo);
        }, cancellationToken);

    /// <summary>Creates a new GPO and assigns its display name. Naming failure may leave a newly created GPO; no automatic deletion occurs.</summary>
    public Task<GroupPolicySnapshot> CreateAsync(string displayName, CancellationToken cancellationToken = default)
    {
        NativeError.Text(displayName, nameof(displayName));
        return Execute((_, domain) =>
        {
            using var gpo = GpmRead.GetObject(domain, GpmGetObject.DomainCreateGPO, "Create GPO");
            SetDisplayName(gpo, displayName, "Set GPO display name");
            return Snapshot(gpo);
        }, cancellationToken);
    }

    /// <summary>Renames a GPO without changing its identity or policy settings.</summary>
    public Task RenameAsync(Guid id, string displayName, CancellationToken cancellationToken = default)
    {
        NativeError.Text(displayName, nameof(displayName));
        return Execute((_, domain) =>
        {
            using var gpo = Required(domain, id);
            SetDisplayName(gpo, displayName, "Set GPO display name");
            return true;
        }, cancellationToken);
    }

    /// <summary>Deletes a GPO through GPMC. Returns false only if absent.</summary>
    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Execute((_, domain) =>
        {
            using var gpo = Find(domain, id);
            if (gpo is null) return false;
            DeleteObject(gpo, false, "Delete GPO");
            return true;
        }, cancellationToken);

    /// <summary>Enables/disables selected policy halves; null preserves a half's current state.</summary>
    public Task SetEnabledAsync(Guid id, bool? userEnabled = null, bool? computerEnabled = null, CancellationToken cancellationToken = default) =>
        Execute((_, domain) =>
        {
            using var gpo = Required(domain, id);
            if (userEnabled is { } user) GpmRead.SetBoolean(gpo, GpmSetBoolean.GPOSetUserEnabled, user, "Set GPO user state");
            if (computerEnabled is { } computer) GpmRead.SetBoolean(gpo, GpmSetBoolean.GPOSetComputerEnabled, computer, "Set GPO computer state");
            return true;
        }, cancellationToken);

    /// <summary>Backs up a selected GPO to an explicit directory. Completion includes GPMC overall status.</summary>
    public Task<GroupPolicyOperationResult<GroupPolicyBackup>> BackupAsync(Guid id, string directory, string comment = "", CancellationToken cancellationToken = default)
    {
        AbsolutePath(directory);
        NativeError.Text(comment, nameof(comment), true);
        return Execute((_, domain) => Backup(Required(domain, id), directory, comment), cancellationToken);
    }

    /// <summary>Imports policy settings into an existing GPO, replacing its settings while retaining its identity and links.</summary>
    public Task<GroupPolicyOperationResult<GroupPolicySnapshot>> ImportAsync(Guid destinationId, string backupDirectory, Guid backupId,
        GroupPolicyImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        AbsolutePath(backupDirectory);
        options ??= new();
        if (options.MigrationTablePath is { } table) AbsolutePath(table);
        if (options.RequireMigrationTableMappings && options.MigrationTablePath is null) throw new ArgumentException("A migration table is required.", nameof(options));
        return Execute((gpm, domain) => Import(gpm, domain, destinationId, backupDirectory, backupId, options), cancellationToken);
    }

    /// <summary>Restores an explicitly selected backup through GPMC, preserving its original GPO identity.</summary>
    public Task<GroupPolicyOperationResult<GroupPolicySnapshot>> RestoreAsync(string backupDirectory, Guid backupId, CancellationToken cancellationToken = default)
    {
        AbsolutePath(backupDirectory);
        return Execute((gpm, domain) => Restore(gpm, domain, backupDirectory, backupId), cancellationToken);
    }

    /// <summary>Generates XML or HTML with native operation status.</summary>
    public Task<GroupPolicyOperationResult<string>> GenerateReportAsync(Guid id, GroupPolicyReportFormat format, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        return Execute((_, domain) => Report(Required(domain, id), format), cancellationToken);
    }

    private Task<T> Execute<T>(Func<ComObject, ComObject, T> action, CancellationToken cancellationToken) =>
        ComApartment.RunAsync(() =>
        {
            using var gpm = ComObject.Create(new("f5694708-88fe-4b35-babf-e56162d5fbc8"), new("f5fae809-3bd6-4da9-a65e-17665b41d763"));
            using var domain = Domain(gpm);
            cancellationToken.ThrowIfCancellationRequested();
            return action(gpm, domain);
        }, cancellationToken);

    private unsafe ComObject Domain(ComObject gpm)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var name = new BString(_domainName);
        using var controller = new BString(_controller);
        nint result = 0;
        return ComObject.FromResult(((IGPM*)gpm.Pointer)->GetDomain(name.Native, controller.Native, 0, (IGPMDomain**)&result).Value, result, "Open GPMC domain");
    }

    private static ComObject Required(ComObject domain, Guid id) => Find(domain, id) ?? throw NativeError.Win32("Find GPO", 2);
    private static ComObject? Find(ComObject domain, Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A GPO GUID is required.", nameof(id));
        try { return StringObject(domain, GpmLookup.Gpo, id.ToString("B"), "Find GPO"); }
        catch (WindowsAdministrationException error) when (error.NativeErrorCode is unchecked((int)0x80072030) or unchecked((int)0x80070002)) { return null; }
    }

    private enum GpmLookup { Gpo, BackupDirectory, Backup, MigrationTable, Som, WmiFilter }
    private enum GpmCollection { Gpos, Messages, Links, Permissions }
    private enum GpmDate { Created, Modified, Backup }

    private static unsafe ComObject StringObject(ComObject source, GpmLookup lookup, string argument, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var text = new BString(argument);
        nint value = 0;
        var status = lookup switch
        {
            GpmLookup.Gpo => ((IGPMDomain*)source.Pointer)->GetGPO(text.Native, (IGPMGPO**)&value),
            GpmLookup.BackupDirectory => ((IGPM*)source.Pointer)->GetBackupDir(text.Native, (IGPMBackupDir**)&value),
            GpmLookup.Backup => ((IGPMBackupDir*)source.Pointer)->GetBackup(text.Native, (IGPMBackup**)&value),
            GpmLookup.MigrationTable => ((IGPM*)source.Pointer)->GetMigrationTable(text.Native, (IGPMMigrationTable**)&value),
            GpmLookup.Som => ((IGPMDomain*)source.Pointer)->GetSOM(text.Native, (IGPMSOM**)&value),
            GpmLookup.WmiFilter => ((IGPMDomain*)source.Pointer)->GetWMIFilter(text.Native, (IGPMWMIFilter**)&value),
            _ => throw new ArgumentOutOfRangeException(nameof(lookup))
        };
        return ComObject.FromResult(status.Value, value, operation);
    }

    private static unsafe ComObject SearchGpos(ComObject domain, nint criteria, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        IGPMGPOCollection* result = null;
        var status = ((IGPMDomain*)domain.Pointer)->SearchGPOs((IGPMSearchCriteria*)criteria, &result);
        return ComObject.FromResult(status.Value, (nint)result, operation);
    }

    private static unsafe ComObject Item(ComObject collection, GpmCollection kind, int index)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        VARIANT value = default;
        try
        {
            var status = kind switch
            {
                GpmCollection.Gpos => ((IGPMGPOCollection*)collection.Pointer)->get_Item(index, &value),
                GpmCollection.Messages => ((IGPMStatusMsgCollection*)collection.Pointer)->get_Item(index, &value),
                GpmCollection.Links => ((IGPMGPOLinksCollection*)collection.Pointer)->get_Item(index, &value),
                GpmCollection.Permissions => ((IGPMSecurityInfo*)collection.Pointer)->get_Item(index, &value),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            NativeError.Check(status.Value, "Read GPMC collection item");
            var iid = kind switch
            {
                GpmCollection.Gpos => IGPMGPO.IID_Guid,
                GpmCollection.Messages => IGPMStatusMessage.IID_Guid,
                GpmCollection.Links => IGPMGPOLink.IID_Guid,
                GpmCollection.Permissions => IGPMPermission.IID_Guid,
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            };
            return VariantObject(value, iid);
        }
        finally { _ = PInvoke.VariantClear(&value); }
    }

    private static unsafe ComObject VariantObject(VARIANT value, Guid iid)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        if (value.vt is not (VARENUM.VT_DISPATCH or VARENUM.VT_UNKNOWN) || value.punkVal == null)
            throw NativeError.Win32("Read GPMC result object", 13);
        void* result = null;
        var status = value.punkVal->QueryInterface(&iid, &result);
        return ComObject.FromResult(status.Value, (nint)result, "Read typed GPMC result object");
    }

    private static unsafe void DeleteObject(ComObject value, bool link, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        NativeError.Check((link ? ((IGPMGPOLink*)value.Pointer)->Delete() : ((IGPMGPO*)value.Pointer)->Delete()).Value, operation);
    }

    private static unsafe void SetDisplayName(ComObject value, string text, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var argument = new BString(text);
        NativeError.Check(((IGPMGPO*)value.Pointer)->put_DisplayName(argument.Native).Value, operation);
    }

    private static unsafe DateTime Date(ComObject value, GpmDate field)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        double date;
        var status = field switch
        {
            GpmDate.Created => ((IGPMGPO*)value.Pointer)->get_CreationTime(&date),
            GpmDate.Modified => ((IGPMGPO*)value.Pointer)->get_ModificationTime(&date),
            GpmDate.Backup => ((IGPMBackup*)value.Pointer)->get_Timestamp(&date),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        NativeError.Check(status.Value, "Read GPMC timestamp");
        return NativeError.Date(date);
    }

    private static GroupPolicySnapshot Snapshot(ComObject gpo) => new(
        NativeError.ParseGuid(GpmRead.GetString(gpo, GpmGetString.GPOID, "Read GPO ID")), GpmRead.GetString(gpo, GpmGetString.GPODisplayName, "Read GPO name"),
        GpmRead.GetString(gpo, GpmGetString.GPODomainName, "Read GPO domain"), GpmRead.GetString(gpo, GpmGetString.GPOPath, "Read GPO directory path"),
        Date(gpo, GpmDate.Created), Date(gpo, GpmDate.Modified), GpmRead.GetBoolean(gpo, GpmGetBoolean.GPOIsUserEnabled, "Read GPO user state"),
        GpmRead.GetBoolean(gpo, GpmGetBoolean.GPOIsComputerEnabled, "Read GPO computer state"), WmiFilterPath(gpo));

    private static unsafe string? WmiFilterPath(ComObject gpo)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        nint result = 0;
        NativeError.Check(((IGPMGPO*)gpo.Pointer)->GetWMIFilter((IGPMWMIFilter**)&result).Value, "Read GPO WMI filter");
        if (result == 0) return null;
        using var filter = ComObject.Own(result);
        return GpmRead.GetString(filter, GpmGetString.WMIFilterPath, "Read WMI filter path");
    }

    private static void AbsolutePath(string path)
    {
        NativeError.Text(path, nameof(path));
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute filesystem path is required.", nameof(path));
    }

    private static unsafe ImmutableArray<GroupPolicyStatusMessage> CheckResult(ComObject result, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var messages = GpmRead.GetObject(result, GpmGetObject.ResultStatus, "Read GPMC status messages");
        var count = GpmRead.GetInt32(messages, GpmGetInt32.StatusMsgCollectionCount, "Read GPMC status count");
        var values = ImmutableArray.CreateBuilder<GroupPolicyStatusMessage>();
        for (var i = 1; i <= count; i++)
        {
            using var message = Item(messages, GpmCollection.Messages, i);
            var error = ((IGPMStatusMessage*)message.Pointer)->ErrorCode().Value;
            var code = ((IGPMStatusMessage*)message.Pointer)->OperationCode().Value;
            values.Add(new(error, code, GpmRead.GetString(message, GpmGetString.StatusMessageMessage, "Read GPMC message"),
                GpmRead.GetString(message, GpmGetString.StatusMessageExtensionName, "Read GPMC extension"), GpmRead.GetString(message, GpmGetString.StatusMessageSettingsName, "Read GPMC setting"),
                GpmRead.GetString(message, GpmGetString.StatusMessageObjectPath, "Read GPMC object path")));
        }
        var status = ((IGPMResult*)result.Pointer)->OverallStatus().Value;
        if (status < 0) throw new GroupPolicyOperationException(operation, status, values.ToImmutable());
        return values.ToImmutable();
    }

    private static unsafe ComObject ResultObject(ComObject result, Guid iid)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        VARIANT value = default;
        try
        {
            NativeError.Check(((IGPMResult*)result.Pointer)->get_Result(&value).Value, "Read GPMC operation result");
            return VariantObject(value, iid);
        }
        finally { _ = PInvoke.VariantClear(&value); }
    }

    private static unsafe GroupPolicyOperationResult<GroupPolicyBackup> Backup(ComObject gpo, string directory, string comment)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using (gpo)
        using (var target = new BString(directory))
        using (var annotation = new BString(comment))
        {
            nint resultPointer = 0;
            using var result = ComObject.FromResult(((IGPMGPO*)gpo.Pointer)->Backup(target.Native, annotation.Native, null, null, (IGPMResult**)&resultPointer).Value, resultPointer, "Back up GPO");
            var messages = CheckResult(result, "Back up GPO");
            using var backup = ResultObject(result, IGPMBackup.IID_Guid);
            return new(new(NativeError.ParseGuid(GpmRead.GetString(backup, GpmGetString.BackupID, "Read backup ID")),
                NativeError.ParseGuid(GpmRead.GetString(backup, GpmGetString.BackupGPOID, "Read backed-up GPO ID")), GpmRead.GetString(backup, GpmGetString.BackupGPODomain, "Read backup domain"),
                GpmRead.GetString(backup, GpmGetString.BackupGPODisplayName, "Read backup name"), Date(backup, GpmDate.Backup), GpmRead.GetString(backup, GpmGetString.BackupComment, "Read backup comment"),
                GpmRead.GetString(backup, GpmGetString.BackupBackupDir, "Read backup directory")), messages);
        }
    }

    private static ComObject OpenBackup(ComObject gpm, string directory, Guid backupId)
    {
        if (backupId == Guid.Empty) throw new ArgumentException("A backup GUID is required.", nameof(backupId));
        using var backupDirectory = StringObject(gpm, GpmLookup.BackupDirectory, directory, "Open GPO backup directory");
        return StringObject(backupDirectory, GpmLookup.Backup, backupId.ToString("B"), "Open GPO backup");
    }

    private static unsafe GroupPolicyOperationResult<GroupPolicySnapshot> Import(ComObject gpm, ComObject domain, Guid id, string directory,
        Guid backupId, GroupPolicyImportOptions options)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var gpo = Required(domain, id);
        using var backup = OpenBackup(gpm, directory, backupId);
        using var table = options.MigrationTablePath is null ? null : StringObject(gpm, GpmLookup.MigrationTable, options.MigrationTablePath, "Open migration table");
        VARIANT migration = table is null ? default : new() { vt = VARENUM.VT_DISPATCH, pdispVal = (IDispatch*)table.Pointer };
        nint resultPointer = 0;
        using var result = ComObject.FromResult(((IGPMGPO*)gpo.Pointer)->Import(options.RequireMigrationTableMappings ? 1 : 0, (IGPMBackup*)backup.Pointer, table is null ? null : &migration, null, null, (IGPMResult**)&resultPointer).Value, resultPointer, "Import GPO");
        var messages = CheckResult(result, "Import GPO");
        using var updated = ResultObject(result, IGPMGPO.IID_Guid);
        return new(Snapshot(updated), messages);
    }

    private static unsafe GroupPolicyOperationResult<GroupPolicySnapshot> Restore(ComObject gpm, ComObject domain, string directory, Guid backupId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var backup = OpenBackup(gpm, directory, backupId);
        nint resultPointer = 0;
        using var result = ComObject.FromResult(((IGPMDomain*)domain.Pointer)->RestoreGPO((IGPMBackup*)backup.Pointer, 0, null, null, (IGPMResult**)&resultPointer).Value, resultPointer, "Restore GPO");
        var messages = CheckResult(result, "Restore GPO");
        using var restored = ResultObject(result, IGPMGPO.IID_Guid);
        return new(Snapshot(restored), messages);
    }

    private static unsafe GroupPolicyOperationResult<string> Report(ComObject gpo, GroupPolicyReportFormat format)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using (gpo)
        {
            nint pointer = 0;
            using var result = ComObject.FromResult(((IGPMGPO*)gpo.Pointer)->GenerateReport((GPMReportType)format, null, null, (IGPMResult**)&pointer).Value, pointer, "Generate GPO report");
            var messages = CheckResult(result, "Generate GPO report");
            VARIANT value = default;
            try
            {
                NativeError.Check(((IGPMResult*)result.Pointer)->get_Result(&value).Value, "Read GPO report");
                if (value.vt != VARENUM.VT_BSTR) throw NativeError.Win32("Read GPO report text", 13);
                return new(value.bstrVal.Value == null ? "" : Marshal.PtrToStringBSTR((nint)value.bstrVal.Value), messages);
            }
            finally { _ = PInvoke.VariantClear(&value); }
        }
    }
}
