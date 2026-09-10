using System.Collections.Immutable;
using System.Xml.Linq;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Tasks;

/// <summary>Task Scheduler operations using the current Windows identity on an explicit local or remote computer.</summary>
public sealed class WindowsTaskSchedulerClient
{
    private readonly string? _machineName;

    /// <summary>Creates a client. Null selects the local computer; native bindings currently require Windows x64.</summary>
    public WindowsTaskSchedulerClient(string? machineName = null)
    {
        Automation.RequireX64();
        if (machineName is not null) NativeError.Text(machineName, nameof(machineName));
        _machineName = machineName;
    }

    /// <summary>Reads a task including its complete XML. Null means absent.</summary>
    public Task<ScheduledTaskSnapshot?> FindAsync(string path, CancellationToken cancellationToken = default)
    {
        path = PathValue(path);
        return Execute(service =>
        {
            using var folder = GetFolder(service, "\\");
            using var task = FindTask(folder, path);
            return task is null ? null : Snapshot(task);
        }, cancellationToken);
    }

    /// <summary>Enumerates tasks directly inside a folder, including hidden tasks.</summary>
    public Task<ImmutableArray<ScheduledTaskSnapshot>> EnumerateAsync(string folderPath = "\\", CancellationToken cancellationToken = default)
    {
        folderPath = PathValue(folderPath);
        return Execute(service => EnumerateTasks(service, folderPath), cancellationToken);
    }

    /// <summary>Enumerates immediate child folder paths.</summary>
    public Task<ImmutableArray<string>> EnumerateFoldersAsync(string folderPath = "\\", CancellationToken cancellationToken = default)
    {
        folderPath = PathValue(folderPath);
        return Execute(service => EnumerateFolders(service, folderPath), cancellationToken);
    }

    /// <summary>Creates a folder with inherited security; an existing folder is a conflict.</summary>
    public Task CreateFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        folderPath = PathValue(folderPath);
        return Execute(service => { CreateFolder(service, folderPath); return true; }, cancellationToken);
    }

    /// <summary>Deletes an empty folder. Returns false only if absent.</summary>
    public Task<bool> DeleteFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        folderPath = PathValue(folderPath);
        if (folderPath == "\\") throw new ArgumentException("The root folder cannot be deleted.", nameof(folderPath));
        return Execute(service =>
        {
            using var root = GetFolder(service, "\\");
            return Delete(root, 12, folderPath, "Delete task folder");
        }, cancellationToken);
    }

    /// <summary>Creates a task from a typed definition. Passwords are used only during registration.</summary>
    public Task<ScheduledTaskSnapshot> CreateAsync(string path, ScheduledTaskSpecification specification,
        string? password = null, CancellationToken cancellationToken = default) =>
        RegisterXmlAsync(path, TaskXml.Create(specification), false, password, cancellationToken);

    /// <summary>Registers complete native XML. replaceExisting opts into create-or-update, without deleting the existing task.</summary>
    public Task<ScheduledTaskSnapshot> RegisterXmlAsync(string path, string xml, bool replaceExisting = false,
        string? password = null, CancellationToken cancellationToken = default)
    {
        path = PathValue(path);
        var document = TaskXml.Parse(xml);
        ValidatePassword(password);
        return Execute(service => Register(service, path, document, replaceExisting ? 6 : 2, password), cancellationToken);
    }

    /// <summary>Updates selected XML subtrees, preserving all unmodified native settings. Missing tasks fail.</summary>
    public Task<ScheduledTaskSnapshot> UpdateAsync(string path, ScheduledTaskUpdate update,
        string? password = null, CancellationToken cancellationToken = default)
    {
        path = PathValue(path);
        ArgumentNullException.ThrowIfNull(update);
        ValidatePassword(password);
        return Execute(service =>
        {
            using var root = GetFolder(service, "\\");
            using var task = FindTask(root, path) ?? throw NativeError.Win32("Update scheduled task", 2);
            var xml = Automation.GetString(task, 20, "Read task XML");
            return Register(service, path, TaskXml.Parse(TaskXml.Update(xml, update)), 4 | 16, password);
        }, cancellationToken);
    }

    /// <summary>Deletes a registered task. Returns false only if absent.</summary>
    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        path = PathValue(path);
        return Execute(service =>
        {
            using var root = GetFolder(service, "\\");
            return Delete(root, 15, path, "Delete scheduled task");
        }, cancellationToken);
    }

    /// <summary>Changes enabled state without rewriting the task definition.</summary>
    public Task SetEnabledAsync(string path, bool enabled, CancellationToken cancellationToken = default)
    {
        path = PathValue(path);
        return Execute(service =>
        {
            using var root = GetFolder(service, "\\");
            using var task = FindTask(root, path) ?? throw NativeError.Win32("Enable scheduled task", 2);
            Automation.SetBoolean(task, 11, enabled, "Enable scheduled task");
            return true;
        }, cancellationToken);
    }

    /// <summary>Requests execution and returns its instance identity and current state, not an execution-success claim.</summary>
    public Task<RunningTaskSnapshot> RunAsync(string path, CancellationToken cancellationToken = default)
    {
        path = PathValue(path);
        return Execute(service => Run(service, path), cancellationToken);
    }

    /// <summary>Requests that all running instances of a registered task stop.</summary>
    public Task StopAsync(string path, CancellationToken cancellationToken = default)
    {
        path = PathValue(path);
        return Execute(service => { Stop(service, path); return true; }, cancellationToken);
    }


    /// <summary>Validates typed task XML through Windows without registering a task or changing machine state.</summary>
    public Task ValidateAsync(ScheduledTaskSpecification specification, CancellationToken cancellationToken = default)
    {
        var document = TaskXml.Parse(TaskXml.Create(specification));
        return Execute(service =>
        {
            ValidateXml(service, document.ToString(SaveOptions.DisableFormatting));
            var principal = ReadPrincipal(service, document);
            if (principal.LogonType != specification.Principal.LogonType)
                throw NativeError.Win32("Validate task principal roundtrip", 13);
            return true;
        }, cancellationToken);
    }

    /// <summary>Validates native task XML without registration. Account credentials and runtime permissions are not validated.</summary>
    public Task ValidateXmlAsync(string xml, CancellationToken cancellationToken = default)
    {
        var document = TaskXml.Parse(xml);
        return Execute(service => { ValidateXml(service, document.ToString(SaveOptions.DisableFormatting)); return true; }, cancellationToken);
    }

    private static unsafe void ValidateXml(ComObject service, string xml)
    {
        using var root = GetFolder(service, "\\");
        using var text = new BString(xml);
        nint result = 0;
        var status = ((delegate* unmanaged[Stdcall]<nint, nint, nint, int, Variant, Variant, int, Variant, nint*, int>)root.Slot(16))(
            root.Pointer, 0, text.Pointer, 1, default, default, 0, default, &result);
        try { NativeError.Check(status, "Validate scheduled task XML"); }
        finally { if (result != 0) ComObject.Own(result).Dispose(); }
    }

    private Task<T> Execute<T>(Func<ComObject, T> action, CancellationToken cancellationToken) =>
        ComApartment.RunAsync(() =>
        {
            using var service = Connect();
            return action(service);
        }, cancellationToken);

    private unsafe ComObject Connect()
    {
        var service = ComObject.Create(new("0f87369f-a4e5-4cfc-bd3e-73e6154572dd"), new("2faba4c7-4da9-4013-9697-20cc3fd40f85"));
        try
        {
            using var server = _machineName is null ? null : new BString(_machineName);
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, Variant, Variant, Variant, Variant, int>)service.Slot(10))(
                service.Pointer, server is null ? default : Variant.String(server.Pointer), default, default, default), "Connect Task Scheduler");
            return service;
        }
        catch { service.Dispose(); throw; }
    }

    private static string PathValue(string path)
    {
        NativeError.Text(path, nameof(path));
        if (!path.StartsWith('\\') || path.Contains('/') || path.Split('\\').Any(part => part is "." or ".."))
            throw new ArgumentException("Use an absolute Task Scheduler path beginning with a backslash.", nameof(path));
        return path;
    }

    private static void ValidatePassword(string? password)
    {
        if (password is not null) NativeError.Text(password, nameof(password), true);
    }

    private static unsafe ComObject GetFolder(ComObject service, string path)
    {
        using var name = new BString(path);
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)service.Slot(7))(service.Pointer, name.Pointer, &result), "Open task folder");
        return ComObject.Own(result);
    }

    private static unsafe ComObject? FindTask(ComObject folder, string path)
    {
        using var name = new BString(path);
        nint result = 0;
        var status = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)folder.Slot(13))(folder.Pointer, name.Pointer, &result);
        if (IsMissing(status)) return null;
        NativeError.Check(status, "Find scheduled task");
        return ComObject.Own(result);
    }

    private static bool IsMissing(int status) => status is unchecked((int)0x80070002) or unchecked((int)0x80070003);

    private static unsafe ComObject Collection(ComObject folder, int slot, int flags, string operation)
    {
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)folder.Slot(slot))(folder.Pointer, flags, &result), operation);
        return ComObject.Own(result);
    }

    private static unsafe ComObject Item(ComObject collection, int index)
    {
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, Variant, nint*, int>)collection.Slot(8))(collection.Pointer, Variant.Int32(index), &result), "Read scheduler collection item");
        return ComObject.Own(result);
    }

    private static ImmutableArray<ScheduledTaskSnapshot> EnumerateTasks(ComObject service, string path)
    {
        using var folder = GetFolder(service, path);
        using var collection = Collection(folder, 14, 1, "Enumerate scheduled tasks");
        var count = Automation.GetInt32(collection, 7, "Read task count");
        var result = ImmutableArray.CreateBuilder<ScheduledTaskSnapshot>();
        for (var i = 1; i <= count; i++) { using var task = Item(collection, i); result.Add(Snapshot(task)); }
        return result.ToImmutable();
    }

    private static ImmutableArray<string> EnumerateFolders(ComObject service, string path)
    {
        using var folder = GetFolder(service, path);
        using var collection = Collection(folder, 10, 0, "Enumerate task folders");
        var count = Automation.GetInt32(collection, 7, "Read task folder count");
        var result = ImmutableArray.CreateBuilder<string>();
        for (var i = 1; i <= count; i++) { using var item = Item(collection, i); result.Add(Automation.GetString(item, 8, "Read task folder path")); }
        return result.ToImmutable();
    }

    private static unsafe void CreateFolder(ComObject service, string path)
    {
        using var root = GetFolder(service, "\\");
        using var name = new BString(path);
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, Variant, nint*, int>)root.Slot(11))(root.Pointer, name.Pointer, default, &result), "Create task folder");
        using var folder = ComObject.Own(result);
    }

    private static unsafe bool Delete(ComObject folder, int slot, string path, string operation)
    {
        using var name = new BString(path);
        var result = ((delegate* unmanaged[Stdcall]<nint, nint, int, int>)folder.Slot(slot))(folder.Pointer, name.Pointer, 0);
        if (IsMissing(result)) return false;
        NativeError.Check(result, operation);
        return true;
    }

    private static unsafe TaskPrincipal ReadPrincipal(ComObject service, XDocument document)
    {
        nint pointer = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)service.Slot(9))(service.Pointer, 0, &pointer), "Create task definition");
        using var definition = ComObject.Own(pointer);
        using var xml = new BString(document.ToString(SaveOptions.DisableFormatting));
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)definition.Slot(20))(definition.Pointer, xml.Pointer), "Read native task definition");
        using var principal = Automation.GetObject(definition, 15, "Read native task principal");
        var mode = (TaskLogonType)Automation.GetInt32(principal, 13, "Read native task logon type");
        if (!Enum.IsDefined(mode)) throw NativeError.Win32("Read task logon type", 13);
        var identity = Automation.GetString(principal, mode == TaskLogonType.Group ? 15 : 11, "Read task identity");
        NativeError.Text(identity, nameof(document));
        return new(identity, mode, Automation.GetInt32(principal, 17, "Read task run level") == 1);
    }


    private static unsafe ScheduledTaskSnapshot Register(ComObject service, string path, XDocument document, int flags, string? password)
    {
        var principal = ReadPrincipal(service, document);
        var identity = principal.Identity;
        var mode = principal.LogonType;

        if (mode is (TaskLogonType.Password or TaskLogonType.InteractiveTokenOrPassword) && password is null) throw new ArgumentException("Password logon requires a registration password.", nameof(password));
        if (mode is not (TaskLogonType.Password or TaskLogonType.InteractiveTokenOrPassword) && password is not null) throw new ArgumentException("A password is only valid for password logon.", nameof(password));
        using var root = GetFolder(service, "\\");
        using var name = new BString(path);
        using var xml = new BString(document.ToString(SaveOptions.DisableFormatting));
        using var user = new BString(identity);
        using var secret = password is null ? null : new BString(password);
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, int, Variant, Variant, int, Variant, nint*, int>)root.Slot(16))(
            root.Pointer, name.Pointer, xml.Pointer, flags, Variant.String(user.Pointer),
            secret is null ? default : Variant.String(secret.Pointer), (int)mode, default, &result), "Register scheduled task");
        using var task = ComObject.Own(result);
        return Snapshot(task);
    }

    private static ScheduledTaskSnapshot Snapshot(ComObject task) => new(
        Automation.GetString(task, 7, "Read task name"), Automation.GetString(task, 8, "Read task path"),
        (ScheduledTaskState)Automation.GetInt32(task, 9, "Read task state"), Automation.GetBoolean(task, 10, "Read task enabled state"),
        Date(task, 15), Automation.GetInt32(task, 16, "Read task result"), Date(task, 18),
        Automation.GetString(task, 20, "Read task XML"));

    private static unsafe DateTime? Date(ComObject task, int slot)
    {
        double value;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, double*, int>)task.Slot(slot))(task.Pointer, &value), "Read task timestamp");
        return value == 0 ? null : NativeError.Date(value);
    }

    private static unsafe RunningTaskSnapshot Run(ComObject service, string path)
    {
        using var root = GetFolder(service, "\\");
        using var task = FindTask(root, path) ?? throw NativeError.Win32("Run scheduled task", 2);
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, Variant, nint*, int>)task.Slot(12))(task.Pointer, default, &result), "Run scheduled task");
        using var running = ComObject.Own(result);
        return new(Automation.GetString(running, 8, "Read task instance"), Automation.GetString(running, 9, "Read running task path"),
            (ScheduledTaskState)Automation.GetInt32(running, 10, "Read running task state"));
    }

    private static unsafe void Stop(ComObject service, string path)
    {
        using var root = GetFolder(service, "\\");
        using var task = FindTask(root, path) ?? throw NativeError.Win32("Stop scheduled task", 2);
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, int>)task.Slot(23))(task.Pointer, 0), "Stop scheduled task");
    }
}
