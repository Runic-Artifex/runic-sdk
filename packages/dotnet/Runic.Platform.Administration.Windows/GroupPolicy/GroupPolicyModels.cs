using System.Collections.Immutable;
namespace Runic.Platform.Administration.Windows.GroupPolicy;

/// <summary>Native report format.</summary>
public enum GroupPolicyReportFormat
{
    /// <summary>XML report.</summary>
    Xml,
    /// <summary>HTML report.</summary>
    Html
}
/// <summary>A GPO identified by GUID, independently of its non-unique display name.</summary>
public sealed record GroupPolicySnapshot(Guid Id, string DisplayName, string DomainName, string DirectoryPath,
    DateTime CreationTime, DateTime ModificationTime, bool UserEnabled, bool ComputerEnabled, string? WmiFilterPath);
/// <summary>A GPMC operation message with its native status identity.</summary>
public sealed record GroupPolicyStatusMessage(int ErrorCode, int OperationCode, string Message, string ExtensionName, string SettingsName, string ObjectPath);
/// <summary>A completed GPMC operation and its status messages.</summary>
public sealed record GroupPolicyOperationResult<T>(T Value, ImmutableArray<GroupPolicyStatusMessage> Messages);
/// <summary>A selected filesystem backup, without backup-selection heuristics.</summary>
public sealed record GroupPolicyBackup(Guid BackupId, Guid GroupPolicyId, string DomainName, string DisplayName, DateTime Timestamp, string Comment, string Directory);
/// <summary>Import options for an explicitly selected backup and destination GPO.</summary>
public sealed record GroupPolicyImportOptions(string? MigrationTablePath = null, bool RequireMigrationTableMappings = false);
/// <summary>A direct GPO link. Order 1 is highest precedence.</summary>
public sealed record GroupPolicyLink(Guid GroupPolicyId, string DomainName, int Order, bool Enabled, bool Enforced);
/// <summary>GPMC permission levels supported for grants.</summary>
public enum GroupPolicyPermissionLevel
{
    /// <summary>Apply Group Policy.</summary>
    Apply = 0x10000,
    /// <summary>Read Group Policy.</summary>
    Read = 0x10100,
    /// <summary>Edit policy settings.</summary>
    Edit = 0x10101,
    /// <summary>Edit settings, modify security and delete.</summary>
    EditSecurityAndDelete = 0x10102
}
/// <summary>A native permission, including custom levels not exposed as grant presets.</summary>
public sealed record GroupPolicyPermissionEntry(string TrusteeSid, string TrusteeName, string TrusteeDomain,
    int NativePermission, bool Inherited, bool Inheritable, bool Denied);

/// <summary>A GPMC operation failure retaining all status messages returned by the component.</summary>
public sealed class GroupPolicyOperationException : WindowsAdministrationException
{
    /// <summary>Creates a failure retaining the overall HRESULT and detailed messages.</summary>
    public GroupPolicyOperationException(string operation, int hresult, ImmutableArray<GroupPolicyStatusMessage> messages)
        : base(operation, Internal.NativeError.HResult(operation, hresult).Category, NativeErrorDomain.HResult, hresult, operation + " failed in GPMC.")
        => Messages = messages;
    /// <summary>Native per-operation details.</summary>
    public ImmutableArray<GroupPolicyStatusMessage> Messages { get; }
}
