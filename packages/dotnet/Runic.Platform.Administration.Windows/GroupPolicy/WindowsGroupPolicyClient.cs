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
            using var criteria = Automation.GetObject(gpm, 12, "Create GPO search criteria");
            using var collection = ObjectArgument(domain, 11, criteria.Pointer, "Search GPOs");
            var result = ImmutableArray.CreateBuilder<GroupPolicySnapshot>();
            var count = Automation.GetInt32(collection, 7, "Read GPO count");
            for (var i = 1; i <= count; i++) { using var gpo = Item(collection, i); result.Add(Snapshot(gpo)); }
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
            using var gpo = Automation.GetObject(domain, 9, "Create GPO");
            SetText(gpo, 8, displayName, "Set GPO display name");
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
            SetText(gpo, 8, displayName, "Set GPO display name");
            return true;
        }, cancellationToken);
    }

    /// <summary>Deletes a GPO through GPMC. Returns false only if absent.</summary>
    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        Execute((_, domain) =>
        {
            using var gpo = Find(domain, id);
            if (gpo is null) return false;
            Call(gpo, 26, "Delete GPO");
            return true;
        }, cancellationToken);

    /// <summary>Enables/disables selected policy halves; null preserves a half's current state.</summary>
    public Task SetEnabledAsync(Guid id, bool? userEnabled = null, bool? computerEnabled = null, CancellationToken cancellationToken = default) =>
        Execute((_, domain) =>
        {
            using var gpo = Required(domain, id);
            if (userEnabled is { } user) Automation.SetBoolean(gpo, 20, user, "Set GPO user state");
            if (computerEnabled is { } computer) Automation.SetBoolean(gpo, 21, computer, "Set GPO computer state");
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
        using var name = new BString(_domainName);
        using var controller = new BString(_controller);
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, int, nint*, int>)gpm.Slot(7))(
            gpm.Pointer, name.Pointer, controller.Pointer, 0, &result), "Open GPMC domain");
        return ComObject.Own(result);
    }

    private static ComObject Required(ComObject domain, Guid id) => Find(domain, id) ?? throw NativeError.Win32("Find GPO", 2);
    private static ComObject? Find(ComObject domain, Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("A GPO GUID is required.", nameof(id));
        try { return StringObject(domain, 10, id.ToString("B"), "Find GPO"); }
        catch (WindowsAdministrationException error) when (error.NativeErrorCode is unchecked((int)0x80072030) or unchecked((int)0x80070002)) { return null; }
    }

    private static unsafe ComObject StringObject(ComObject value, int slot, string argument, string operation)
    {
        using var text = new BString(argument);
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)value.Slot(slot))(value.Pointer, text.Pointer, &result), operation);
        return ComObject.Own(result);
    }

    private static unsafe ComObject ObjectArgument(ComObject value, int slot, nint argument, string operation)
    {
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)value.Slot(slot))(value.Pointer, argument, &result), operation);
        return ComObject.Own(result);
    }

    private static unsafe ComObject Item(ComObject collection, int index)
    {
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)collection.Slot(8))(collection.Pointer, index, &result), "Read GPMC collection item");
        return ComObject.Own(result);
    }

    private static unsafe void Call(ComObject value, int slot, string operation) =>
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int>)value.Slot(slot))(value.Pointer), operation);

    private static unsafe void SetText(ComObject value, int slot, string text, string operation)
    {
        using var argument = new BString(text);
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)value.Slot(slot))(value.Pointer, argument.Pointer), operation);
    }

    private static unsafe DateTime Date(ComObject value, int slot)
    {
        double date;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, double*, int>)value.Slot(slot))(value.Pointer, &date), "Read GPMC timestamp");
        return NativeError.Date(date);
    }

    private static GroupPolicySnapshot Snapshot(ComObject gpo) => new(
        NativeError.ParseGuid(Automation.GetString(gpo, 10, "Read GPO ID")), Automation.GetString(gpo, 7, "Read GPO name"),
        Automation.GetString(gpo, 11, "Read GPO domain"), Automation.GetString(gpo, 9, "Read GPO directory path"),
        Date(gpo, 12), Date(gpo, 13), Automation.GetBoolean(gpo, 22, "Read GPO user state"),
        Automation.GetBoolean(gpo, 23, "Read GPO computer state"), WmiFilterPath(gpo));

    private static unsafe string? WmiFilterPath(ComObject gpo)
    {
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)gpo.Slot(18))(gpo.Pointer, &result), "Read GPO WMI filter");
        if (result == 0) return null;
        using var filter = ComObject.Own(result);
        return Automation.GetString(filter, 7, "Read WMI filter path");
    }

    private static void AbsolutePath(string path)
    {
        NativeError.Text(path, nameof(path));
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute filesystem path is required.", nameof(path));
    }

    private static unsafe ImmutableArray<GroupPolicyStatusMessage> CheckResult(ComObject result, string operation)
    {
        using var messages = Automation.GetObject(result, 7, "Read GPMC status messages");
        var count = Automation.GetInt32(messages, 7, "Read GPMC status count");
        var values = ImmutableArray.CreateBuilder<GroupPolicyStatusMessage>();
        for (var i = 1; i <= count; i++)
        {
            using var message = Item(messages, i);
            var error = ((delegate* unmanaged[Stdcall]<nint, int>)message.Slot(8))(message.Pointer);
            var code = ((delegate* unmanaged[Stdcall]<nint, int>)message.Slot(11))(message.Pointer);
            values.Add(new(error, code, Automation.GetString(message, 12, "Read GPMC message"),
                Automation.GetString(message, 9, "Read GPMC extension"), Automation.GetString(message, 10, "Read GPMC setting"),
                Automation.GetString(message, 7, "Read GPMC object path")));
        }
        var status = ((delegate* unmanaged[Stdcall]<nint, int>)result.Slot(9))(result.Pointer);
        if (status < 0) throw new GroupPolicyOperationException(operation, status, values.ToImmutable());
        return values.ToImmutable();
    }

    private static unsafe ComObject ResultObject(ComObject result)
    {
        Variant value = default;
        try
        {
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, Variant*, int>)result.Slot(8))(result.Pointer, &value), "Read GPMC operation result");
            if (value.Type is not (9 or 13) || value.Pointer == 0) throw NativeError.Win32("Read GPMC result object", 13);
            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)value.Pointer)[1])(value.Pointer);
            return ComObject.Own(value.Pointer);
        }
        finally { _ = AutomationArrays.VariantClear(&value); }
    }

    private static unsafe GroupPolicyOperationResult<GroupPolicyBackup> Backup(ComObject gpo, string directory, string comment)
    {
        using (gpo)
        using (var target = new BString(directory))
        using (var annotation = new BString(comment))
        {
            nint resultPointer = 0;
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, nint*, int>)gpo.Slot(27))(
                gpo.Pointer, target.Pointer, annotation.Pointer, 0, 0, &resultPointer), "Back up GPO");
            using var result = ComObject.Own(resultPointer);
            var messages = CheckResult(result, "Back up GPO");
            using var backup = ResultObject(result);
            return new(new(NativeError.ParseGuid(Automation.GetString(backup, 7, "Read backup ID")),
                NativeError.ParseGuid(Automation.GetString(backup, 8, "Read backed-up GPO ID")), Automation.GetString(backup, 9, "Read backup domain"),
                Automation.GetString(backup, 10, "Read backup name"), Date(backup, 11), Automation.GetString(backup, 12, "Read backup comment"),
                Automation.GetString(backup, 13, "Read backup directory")), messages);
        }
    }

    private static ComObject OpenBackup(ComObject gpm, string directory, Guid backupId)
    {
        if (backupId == Guid.Empty) throw new ArgumentException("A backup GUID is required.", nameof(backupId));
        using var backupDirectory = StringObject(gpm, 8, directory, "Open GPO backup directory");
        return StringObject(backupDirectory, 8, backupId.ToString("B"), "Open GPO backup");
    }

    private static unsafe GroupPolicyOperationResult<GroupPolicySnapshot> Import(ComObject gpm, ComObject domain, Guid id, string directory,
        Guid backupId, GroupPolicyImportOptions options)
    {
        using var gpo = Required(domain, id);
        using var backup = OpenBackup(gpm, directory, backupId);
        using var table = options.MigrationTablePath is null ? null : StringObject(gpm, 16, options.MigrationTablePath, "Open migration table");
        Variant migration = table is null ? default : new() { Type = 9, Pointer = table.Pointer };
        nint resultPointer = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, nint, Variant*, nint, nint, nint*, int>)gpo.Slot(28))(
            gpo.Pointer, options.RequireMigrationTableMappings ? 1 : 0, backup.Pointer, table is null ? null : &migration, 0, 0, &resultPointer), "Import GPO");
        using var result = ComObject.Own(resultPointer);
        var messages = CheckResult(result, "Import GPO");
        using var updated = ResultObject(result);
        return new(Snapshot(updated), messages);
    }

    private static unsafe GroupPolicyOperationResult<GroupPolicySnapshot> Restore(ComObject gpm, ComObject domain, string directory, Guid backupId)
    {
        using var backup = OpenBackup(gpm, directory, backupId);
        nint resultPointer = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint, nint, nint*, int>)domain.Slot(12))(
            domain.Pointer, backup.Pointer, 0, 0, 0, &resultPointer), "Restore GPO");
        using var result = ComObject.Own(resultPointer);
        var messages = CheckResult(result, "Restore GPO");
        using var restored = ResultObject(result);
        return new(Snapshot(restored), messages);
    }

    private static unsafe GroupPolicyOperationResult<string> Report(ComObject gpo, GroupPolicyReportFormat format)
    {
        using (gpo)
        {
            nint pointer = 0;
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, nint, nint, nint*, int>)gpo.Slot(29))(
                gpo.Pointer, (int)format, 0, 0, &pointer), "Generate GPO report");
            using var result = ComObject.Own(pointer);
            var messages = CheckResult(result, "Generate GPO report");
            Variant value = default;
            try
            {
                NativeError.Check(((delegate* unmanaged[Stdcall]<nint, Variant*, int>)result.Slot(8))(result.Pointer, &value), "Read GPO report");
                if (value.Type != 8) throw NativeError.Win32("Read GPO report text", 13);
                return new(value.Pointer == 0 ? "" : Marshal.PtrToStringBSTR(value.Pointer), messages);
            }
            finally { _ = AutomationArrays.VariantClear(&value); }
        }
    }
}
