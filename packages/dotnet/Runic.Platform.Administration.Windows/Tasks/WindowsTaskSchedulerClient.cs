using Windows.Win32.System.TaskScheduler;
using Windows.Win32.System.Variant;
using Windows.Win32.Foundation;
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
            return Delete(root, false, folderPath, "Delete task folder");
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
            var xml = TaskRead.String(task, TaskString.TaskXml, "Read task XML");
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
            return Delete(root, true, path, "Delete scheduled task");
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
            TaskRead.SetEnabled(task, enabled, "Enable scheduled task");
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
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var root = GetFolder(service, "\\");
        using var text = new BString(xml);
        nint result = 0;
        var status = ((ITaskFolder*)root.Pointer)->RegisterTask(default, text.Native, 1, default, default, 0, default, (IRegisteredTask**)&result).Value;
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
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        var service = ComObject.Create(new("0f87369f-a4e5-4cfc-bd3e-73e6154572dd"), new("2faba4c7-4da9-4013-9697-20cc3fd40f85"));
        try
        {
            using var server = _machineName is null ? null : new BString(_machineName);
            NativeError.Check(((ITaskService*)service.Pointer)->Connect(server is null ? default : NativeVariant.String(server.Pointer), default, default, default).Value, "Connect Task Scheduler");
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
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var name = new BString(path);
        nint result = 0;
        return ComObject.FromResult(((ITaskService*)service.Pointer)->GetFolder(name.Native, (ITaskFolder**)&result).Value, result, "Open task folder");
    }

    private static unsafe ComObject? FindTask(ComObject folder, string path)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var name = new BString(path);
        nint result = 0;
        var status = ((ITaskFolder*)folder.Pointer)->GetTask(name.Native, (IRegisteredTask**)&result).Value;
        if (IsMissing(status)) return null;
        NativeError.Check(status, "Find scheduled task");
        return ComObject.Own(result);
    }

    private static bool IsMissing(int status) => status is unchecked((int)0x80070002) or unchecked((int)0x80070003);

    private static unsafe ComObject Collection(ComObject folder, bool tasks, int flags, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        nint result = 0;
        NativeError.Check((tasks ? ((ITaskFolder*)folder.Pointer)->GetTasks(flags, (IRegisteredTaskCollection**)&result) : ((ITaskFolder*)folder.Pointer)->GetFolders(flags, (ITaskFolderCollection**)&result)).Value, operation);
        return ComObject.Own(result);
    }

    private static unsafe ComObject Item(ComObject collection, int index, bool tasks)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        nint result = 0;
        return ComObject.FromResult((tasks ? ((IRegisteredTaskCollection*)collection.Pointer)->get_Item(NativeVariant.Int32(index), (IRegisteredTask**)&result) : ((ITaskFolderCollection*)collection.Pointer)->get_Item(NativeVariant.Int32(index), (ITaskFolder**)&result)).Value, result, "Read scheduler collection item");
    }

    private static ImmutableArray<ScheduledTaskSnapshot> EnumerateTasks(ComObject service, string path)
    {
        using var folder = GetFolder(service, path);
        using var collection = Collection(folder, true, 1, "Enumerate scheduled tasks");
        var count = TaskRead.Int32(collection, TaskInteger.TaskCount, "Read task count");
        var result = ImmutableArray.CreateBuilder<ScheduledTaskSnapshot>();
        for (var i = 1; i <= count; i++) { using var task = Item(collection, i, true); result.Add(Snapshot(task)); }
        return result.ToImmutable();
    }

    private static ImmutableArray<string> EnumerateFolders(ComObject service, string path)
    {
        using var folder = GetFolder(service, path);
        using var collection = Collection(folder, false, 0, "Enumerate task folders");
        var count = TaskRead.Int32(collection, TaskInteger.FolderCount, "Read task folder count");
        var result = ImmutableArray.CreateBuilder<string>();
        for (var i = 1; i <= count; i++) { using var item = Item(collection, i, false); result.Add(TaskRead.String(item, TaskString.FolderPath, "Read task folder path")); }
        return result.ToImmutable();
    }

    private static unsafe void CreateFolder(ComObject service, string path)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var root = GetFolder(service, "\\");
        using var name = new BString(path);
        nint result = 0;
        using var folder = ComObject.FromResult(((ITaskFolder*)root.Pointer)->CreateFolder(name.Native, default, (ITaskFolder**)&result).Value, result, "Create task folder");
    }

    private static unsafe bool Delete(ComObject folder, bool task, string path, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var name = new BString(path);
        var result = (task ? ((ITaskFolder*)folder.Pointer)->DeleteTask(name.Native, 0) : ((ITaskFolder*)folder.Pointer)->DeleteFolder(name.Native, 0)).Value;
        if (IsMissing(result)) return false;
        NativeError.Check(result, operation);
        return true;
    }

    private static unsafe TaskPrincipal ReadPrincipal(ComObject service, XDocument document)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        nint pointer = 0;
        using var definition = ComObject.FromResult(((ITaskService*)service.Pointer)->NewTask(0, (ITaskDefinition**)&pointer).Value, pointer, "Create task definition");
        using var xml = new BString(document.ToString(SaveOptions.DisableFormatting));
        NativeError.Check(((ITaskDefinition*)definition.Pointer)->put_XmlText(xml.Native).Value, "Read native task definition");
        using var principal = TaskRead.Principal(definition, "Read native task principal");
        var mode = (TaskLogonType)TaskRead.Int32(principal, TaskInteger.LogonType, "Read native task logon type");
        if (!Enum.IsDefined(mode)) throw NativeError.Win32("Read task logon type", 13);
        var identity = TaskRead.String(principal, mode == TaskLogonType.Group ? TaskString.GroupId : TaskString.UserId, "Read task identity");
        NativeError.Text(identity, nameof(document));
        return new(identity, mode, TaskRead.Int32(principal, TaskInteger.RunLevel, "Read task run level") == 1);
    }


    private static unsafe ScheduledTaskSnapshot Register(ComObject service, string path, XDocument document, int flags, string? password)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
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
        using var task = ComObject.FromResult(((ITaskFolder*)root.Pointer)->RegisterTask(name.Native, xml.Native, flags, NativeVariant.String(user.Pointer), secret is null ? default : NativeVariant.String(secret.Pointer), (TASK_LOGON_TYPE)mode, default, (IRegisteredTask**)&result).Value, result, "Register scheduled task");
        return Snapshot(task);
    }

    private static ScheduledTaskSnapshot Snapshot(ComObject task) => new(
        TaskRead.String(task, TaskString.TaskName, "Read task name"), TaskRead.String(task, TaskString.TaskPath, "Read task path"),
        (ScheduledTaskState)TaskRead.Int32(task, TaskInteger.TaskState, "Read task state"), TaskRead.Enabled(task, "Read task enabled state"),
        Date(task, false), TaskRead.Int32(task, TaskInteger.TaskResult, "Read task result"), Date(task, true),
        TaskRead.String(task, TaskString.TaskXml, "Read task XML"));

    private static unsafe DateTime? Date(ComObject task, bool next)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        double value;
        NativeError.Check((next ? ((IRegisteredTask*)task.Pointer)->get_NextRunTime(&value) : ((IRegisteredTask*)task.Pointer)->get_LastRunTime(&value)).Value, "Read task timestamp");
        return value == 0 ? null : NativeError.Date(value);
    }

    private static unsafe RunningTaskSnapshot Run(ComObject service, string path)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var root = GetFolder(service, "\\");
        using var task = FindTask(root, path) ?? throw NativeError.Win32("Run scheduled task", 2);
        nint result = 0;
        using var running = ComObject.FromResult(((IRegisteredTask*)task.Pointer)->Run(default, (IRunningTask**)&result).Value, result, "Run scheduled task");
        return new(TaskRead.String(running, TaskString.InstanceGuid, "Read task instance"), TaskRead.String(running, TaskString.RunningPath, "Read running task path"),
            (ScheduledTaskState)TaskRead.Int32(running, TaskInteger.RunningState, "Read running task state"));
    }

    private static unsafe void Stop(ComObject service, string path)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var root = GetFolder(service, "\\");
        using var task = FindTask(root, path) ?? throw NativeError.Win32("Stop scheduled task", 2);
        NativeError.Check(((IRegisteredTask*)task.Pointer)->Stop(0).Value, "Stop scheduled task");
    }
}
