using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Services;

public sealed partial class WindowsServiceClient
{
    /// <summary>Creates a new own-process service. Supplemental configuration is applied afterward and is not transactional.</summary>
    /// <remarks>If a later step fails, the created service remains. Inspect its state before deciding how to recover. Password is never retained.</remarks>
    public unsafe void Create(ServiceSpecification specification, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(specification);
        NativeError.Text(specification.Name, nameof(specification.Name));
        NativeError.Text(specification.BinaryCommandLine, nameof(specification.BinaryCommandLine));
        var update = new ServiceUpdate
        {
            DisplayName = specification.DisplayName ?? specification.Name,
            BinaryCommandLine = specification.BinaryCommandLine,
            AccountName = specification.AccountName,
            StartMode = specification.StartMode,
            Dependencies = specification.Dependencies,
            Description = specification.Description,
            DelayedAutomaticStart = specification.DelayedAutomaticStart,
            FailurePolicy = specification.FailurePolicy
        };
        Validate(update, password);
        if (specification.StartMode is ServiceStartMode.Boot or ServiceStartMode.System)
            throw new ArgumentException("Boot and system start are driver-only modes.", nameof(specification));
        using var manager = Manager(2);
        var dependencies = MultiString(specification.Dependencies);
        fixed (char* deps = dependencies)
        {
            var pointer = ServiceNative.CreateService(manager, specification.Name, specification.DisplayName ?? specification.Name,
                2 | (specification.FailurePolicy is null ? 0u : 0x10u), 0x10, (uint)specification.StartMode, 1,
                specification.BinaryCommandLine, null, 0, deps, specification.AccountName, password);
            if (pointer == 0) throw NativeError.Win32("Create service", Marshal.GetLastPInvokeError());
            using var service = new ServiceHandle(pointer);
            ApplySupplemental(service, update);
        }
    }

    /// <summary>Updates only supplied fields. Password changes require an explicit AccountName. Native changes are not transactional.</summary>
    public unsafe void Update(string name, ServiceUpdate update, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        Validate(update, password);
        using var service = Open(name, 2 | 1 | (update.FailurePolicy is null ? 0u : 0x10u))!;
        if (update.DelayedAutomaticStart is true && update.StartMode is null)
        {
            var config = ReadConfig(service, 0);
            fixed (byte* data = config)
                if (((ServiceNative.Config*)data)->Start != 2)
                    throw new ArgumentException("Delayed start requires automatic start.", nameof(update));
        }
        var dependencies = update.Dependencies is { } values ? MultiString(values) : null;
        fixed (char* deps = dependencies)
            NativeError.CheckWin32(ServiceNative.ChangeServiceConfig(service, uint.MaxValue,
                update.StartMode is { } mode ? (uint)mode : uint.MaxValue, uint.MaxValue,
                update.BinaryCommandLine, null, 0, deps, update.AccountName, password, update.DisplayName), "Update service configuration");
        ApplySupplemental(service, update);
    }

    private static string MultiString(ImmutableArray<string> values) =>
        string.Join('\0', values) + "\0\0";

    private static void Validate(ServiceUpdate update, string? password)
    {
        if (update.BinaryCommandLine is { } binary) NativeError.Text(binary, nameof(update.BinaryCommandLine));
        if (update.DisplayName is { } display) NativeError.Text(display, nameof(update.DisplayName));
        if (update.AccountName is { } account) NativeError.Text(account, nameof(update.AccountName));
        if (update.Description is { } description) NativeError.Text(description, nameof(update.Description), true);
        if (password is not null)
        {
            NativeError.Text(password, nameof(password), true);
            if (update.AccountName is null) throw new ArgumentException("A password requires an explicit service account.", nameof(password));
        }
        if (update.StartMode is { } mode && !Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(update));
        if (update.DelayedAutomaticStart is true && update.StartMode is { } start && start != ServiceStartMode.Automatic)
            throw new ArgumentException("Delayed start requires automatic start.", nameof(update));
        if (update.Dependencies is { } dependencies)
        {
            if (dependencies.IsDefault) throw new ArgumentException("Dependencies must be initialized.", nameof(update));
            foreach (var dependency in dependencies) NativeError.Text(dependency, nameof(update.Dependencies));
        }
        if (update.FailurePolicy is { } policy)
        {
            if (policy.Actions.IsDefault) throw new ArgumentException("Failure actions must be initialized.", nameof(update));
            NativeError.Text(policy.Command, nameof(policy.Command), true);
            NativeError.Text(policy.RebootMessage, nameof(policy.RebootMessage), true);
            if (policy.ResetPeriod is { } reset && (reset < TimeSpan.Zero || reset.TotalSeconds >= uint.MaxValue))
                throw new ArgumentOutOfRangeException(nameof(update));
            foreach (var action in policy.Actions)
            {
                ArgumentNullException.ThrowIfNull(action);
                if (!Enum.IsDefined(action.Kind) || action.Delay < TimeSpan.Zero || action.Delay.TotalMilliseconds > uint.MaxValue)
                    throw new ArgumentOutOfRangeException(nameof(update));
            }
        }
    }

    private static unsafe void ApplySupplemental(ServiceHandle service, ServiceUpdate update)
    {
        if (update.Description is { } description)
            fixed (char* text = description)
            {
                var pointer = text;
                NativeError.CheckWin32(ServiceNative.ChangeServiceConfig2(service, 1, &pointer), "Update service description");
            }
        if (update.DelayedAutomaticStart is { } delayed)
        {
            var value = delayed ? 1 : 0;
            NativeError.CheckWin32(ServiceNative.ChangeServiceConfig2(service, 3, &value), "Update delayed service start");
        }
        if (update.FailurePolicy is { } policy)
        {
            var actions = policy.Actions.Select(action => new ServiceNative.FailureAction
            {
                Kind = (uint)action.Kind, Delay = (uint)action.Delay.TotalMilliseconds
            }).ToArray();
            // A non-null pointer with zero count explicitly clears recovery actions.
            var storage = actions.Length == 0 ? new ServiceNative.FailureAction[1] : actions;
            fixed (ServiceNative.FailureAction* nativeActions = storage)
            fixed (char* command = policy.Command)
            fixed (char* message = policy.RebootMessage)
            {
                var failure = new ServiceNative.FailureActions
                {
                    Reset = policy.ResetPeriod is { } reset ? (uint)reset.TotalSeconds : uint.MaxValue,
                    Command = command, RebootMessage = message, Count = (uint)actions.Length, Actions = nativeActions
                };
                NativeError.CheckWin32(ServiceNative.ChangeServiceConfig2(service, 2, &failure), "Update service recovery actions");
            }
            var value = policy.OnNonCrashFailures ? 1 : 0;
            NativeError.CheckWin32(ServiceNative.ChangeServiceConfig2(service, 4, &value), "Update service failure detection");
        }
    }
}
