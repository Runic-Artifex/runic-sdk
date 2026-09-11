using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.TaskScheduler;
using Runic.Platform.Administration.Windows.Internal;
namespace Runic.Platform.Administration.Windows.Tasks;
internal enum TaskString { TaskName, TaskPath, TaskXml, FolderPath, InstanceGuid, RunningPath, GroupId, UserId }
internal enum TaskInteger { TaskCount, FolderCount, LogonType, RunLevel, TaskState, TaskResult, RunningState }
internal static unsafe class TaskRead
{
    internal static string String(ComObject source, TaskString field, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        BSTR value = default;
        try
        {
            var status = field switch
            {
                TaskString.TaskName => ((IRegisteredTask*)source.Pointer)->get_Name(&value),
                TaskString.TaskPath => ((IRegisteredTask*)source.Pointer)->get_Path(&value),
                TaskString.TaskXml => ((IRegisteredTask*)source.Pointer)->get_Xml(&value),
                TaskString.FolderPath => ((ITaskFolder*)source.Pointer)->get_Path(&value),
                TaskString.InstanceGuid => ((IRunningTask*)source.Pointer)->get_InstanceGuid(&value),
                TaskString.RunningPath => ((IRunningTask*)source.Pointer)->get_Path(&value),
                TaskString.GroupId => ((IPrincipal*)source.Pointer)->get_GroupId(&value),
                TaskString.UserId => ((IPrincipal*)source.Pointer)->get_UserId(&value),
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
            NativeError.Check(status.Value, operation);
            return value.Value == null ? "" : Marshal.PtrToStringBSTR((nint)value.Value);
        }
        finally { if (value.Value != null) Marshal.FreeBSTR((nint)value.Value); }
    }
    internal static int Int32(ComObject source, TaskInteger field, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        int value = 0;
        var status = field switch
        {
            TaskInteger.TaskCount => ((IRegisteredTaskCollection*)source.Pointer)->get_Count((int*)&value),
            TaskInteger.FolderCount => ((ITaskFolderCollection*)source.Pointer)->get_Count((int*)&value),
            TaskInteger.LogonType => ((IPrincipal*)source.Pointer)->get_LogonType((TASK_LOGON_TYPE*)&value),
            TaskInteger.RunLevel => ((IPrincipal*)source.Pointer)->get_RunLevel((TASK_RUNLEVEL_TYPE*)&value),
            TaskInteger.TaskState => ((IRegisteredTask*)source.Pointer)->get_State((TASK_STATE*)&value),
            TaskInteger.TaskResult => ((IRegisteredTask*)source.Pointer)->get_LastTaskResult((int*)&value),
            TaskInteger.RunningState => ((IRunningTask*)source.Pointer)->get_State((TASK_STATE*)&value),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        NativeError.Check(status.Value, operation);
        return value;
    }
    internal static bool Enabled(ComObject source, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        VARIANT_BOOL value;
        NativeError.Check(((IRegisteredTask*)source.Pointer)->get_Enabled(&value).Value, operation);
        return value.Value != 0;
    }
    internal static void SetEnabled(ComObject source, bool enabled, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        NativeError.Check(((IRegisteredTask*)source.Pointer)->put_Enabled(new VARIANT_BOOL(enabled ? (short)-1 : (short)0)).Value, operation);
    }
    internal static ComObject Principal(ComObject source, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        IPrincipal* value = null;
        var status = ((ITaskDefinition*)source.Pointer)->get_Principal(&value);
        return ComObject.FromResult(status.Value, (nint)value, operation);
    }
}
