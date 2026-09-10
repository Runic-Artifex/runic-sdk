using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.GroupPolicy;
using Runic.Platform.Administration.Windows.Internal;
namespace Runic.Platform.Administration.Windows.GroupPolicy;
internal enum GpmGetString { GPOID, GPODisplayName, GPODomainName, GPOPath, WMIFilterPath, StatusMessageMessage, StatusMessageExtensionName, StatusMessageSettingsName, StatusMessageObjectPath, BackupID, BackupGPOID, BackupGPODomain, BackupGPODisplayName, BackupComment, BackupBackupDir, TrusteeTrusteeSid, TrusteeTrusteeName, TrusteeTrusteeDomain, GPOLinkGPOID, GPOLinkGPODomain }
internal enum GpmGetInt32 { GPOCollectionCount, StatusMsgCollectionCount, GPOLinksCollectionCount, SecurityInfoCount, PermissionPermission, GPOLinkSOMLinkOrder }
internal enum GpmGetBoolean { GPOIsUserEnabled, GPOIsComputerEnabled, SOMGPOInheritanceBlocked, PermissionInherited, PermissionInheritable, PermissionDenied, GPOLinkEnabled, GPOLinkEnforced }
internal enum GpmSetBoolean { GPOSetUserEnabled, GPOSetComputerEnabled, GPOLinkEnabled, GPOLinkEnforced, SOMGPOInheritanceBlocked }
internal enum GpmGetObject { CreateSearchCriteria, DomainCreateGPO, ResultStatus, SOMGetGPOLinks, GPOGetSecurityInfo, PermissionTrustee }
internal static unsafe class GpmRead
{
    internal static string GetString(ComObject source, GpmGetString field, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        BSTR value = default;
        try
        {
        var status = field switch
        {
            GpmGetString.GPOID => ((IGPMGPO*)source.Pointer)->get_ID((BSTR*)&value),
            GpmGetString.GPODisplayName => ((IGPMGPO*)source.Pointer)->get_DisplayName((BSTR*)&value),
            GpmGetString.GPODomainName => ((IGPMGPO*)source.Pointer)->get_DomainName((BSTR*)&value),
            GpmGetString.GPOPath => ((IGPMGPO*)source.Pointer)->get_Path((BSTR*)&value),
            GpmGetString.WMIFilterPath => ((IGPMWMIFilter*)source.Pointer)->get_Path((BSTR*)&value),
            GpmGetString.StatusMessageMessage => ((IGPMStatusMessage*)source.Pointer)->get_Message((BSTR*)&value),
            GpmGetString.StatusMessageExtensionName => ((IGPMStatusMessage*)source.Pointer)->get_ExtensionName((BSTR*)&value),
            GpmGetString.StatusMessageSettingsName => ((IGPMStatusMessage*)source.Pointer)->get_SettingsName((BSTR*)&value),
            GpmGetString.StatusMessageObjectPath => ((IGPMStatusMessage*)source.Pointer)->get_ObjectPath((BSTR*)&value),
            GpmGetString.BackupID => ((IGPMBackup*)source.Pointer)->get_ID((BSTR*)&value),
            GpmGetString.BackupGPOID => ((IGPMBackup*)source.Pointer)->get_GPOID((BSTR*)&value),
            GpmGetString.BackupGPODomain => ((IGPMBackup*)source.Pointer)->get_GPODomain((BSTR*)&value),
            GpmGetString.BackupGPODisplayName => ((IGPMBackup*)source.Pointer)->get_GPODisplayName((BSTR*)&value),
            GpmGetString.BackupComment => ((IGPMBackup*)source.Pointer)->get_Comment((BSTR*)&value),
            GpmGetString.BackupBackupDir => ((IGPMBackup*)source.Pointer)->get_BackupDir((BSTR*)&value),
            GpmGetString.TrusteeTrusteeSid => ((IGPMTrustee*)source.Pointer)->get_TrusteeSid((BSTR*)&value),
            GpmGetString.TrusteeTrusteeName => ((IGPMTrustee*)source.Pointer)->get_TrusteeName((BSTR*)&value),
            GpmGetString.TrusteeTrusteeDomain => ((IGPMTrustee*)source.Pointer)->get_TrusteeDomain((BSTR*)&value),
            GpmGetString.GPOLinkGPOID => ((IGPMGPOLink*)source.Pointer)->get_GPOID((BSTR*)&value),
            GpmGetString.GPOLinkGPODomain => ((IGPMGPOLink*)source.Pointer)->get_GPODomain((BSTR*)&value),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        NativeError.Check(status.Value, operation);
        return value.Value == null ? "" : Marshal.PtrToStringBSTR((nint)value.Value);
        }
        finally { if (value.Value != null) Marshal.FreeBSTR((nint)value.Value); }
    }
    internal static int GetInt32(ComObject source, GpmGetInt32 field, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        int value = 0;
        var status = field switch
        {
            GpmGetInt32.GPOCollectionCount => ((IGPMGPOCollection*)source.Pointer)->get_Count((int*)&value),
            GpmGetInt32.StatusMsgCollectionCount => ((IGPMStatusMsgCollection*)source.Pointer)->get_Count((int*)&value),
            GpmGetInt32.GPOLinksCollectionCount => ((IGPMGPOLinksCollection*)source.Pointer)->get_Count((int*)&value),
            GpmGetInt32.SecurityInfoCount => ((IGPMSecurityInfo*)source.Pointer)->get_Count((int*)&value),
            GpmGetInt32.PermissionPermission => ((IGPMPermission*)source.Pointer)->get_Permission((GPMPermissionType*)&value),
            GpmGetInt32.GPOLinkSOMLinkOrder => ((IGPMGPOLink*)source.Pointer)->get_SOMLinkOrder((int*)&value),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        NativeError.Check(status.Value, operation);
        return value;
    }
    internal static bool GetBoolean(ComObject source, GpmGetBoolean field, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        VARIANT_BOOL value = default;
        var status = field switch
        {
            GpmGetBoolean.GPOIsUserEnabled => ((IGPMGPO*)source.Pointer)->IsUserEnabled((VARIANT_BOOL*)&value),
            GpmGetBoolean.GPOIsComputerEnabled => ((IGPMGPO*)source.Pointer)->IsComputerEnabled((VARIANT_BOOL*)&value),
            GpmGetBoolean.SOMGPOInheritanceBlocked => ((IGPMSOM*)source.Pointer)->get_GPOInheritanceBlocked((VARIANT_BOOL*)&value),
            GpmGetBoolean.PermissionInherited => ((IGPMPermission*)source.Pointer)->get_Inherited((VARIANT_BOOL*)&value),
            GpmGetBoolean.PermissionInheritable => ((IGPMPermission*)source.Pointer)->get_Inheritable((VARIANT_BOOL*)&value),
            GpmGetBoolean.PermissionDenied => ((IGPMPermission*)source.Pointer)->get_Denied((VARIANT_BOOL*)&value),
            GpmGetBoolean.GPOLinkEnabled => ((IGPMGPOLink*)source.Pointer)->get_Enabled((VARIANT_BOOL*)&value),
            GpmGetBoolean.GPOLinkEnforced => ((IGPMGPOLink*)source.Pointer)->get_Enforced((VARIANT_BOOL*)&value),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        NativeError.Check(status.Value, operation);
        return value.Value != 0;
    }
    internal static void SetBoolean(ComObject source, GpmSetBoolean field, bool enabled, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        VARIANT_BOOL value = new(enabled ? (short)-1 : (short)0);
        var status = field switch
        {
            GpmSetBoolean.GPOSetUserEnabled => ((IGPMGPO*)source.Pointer)->SetUserEnabled(value),
            GpmSetBoolean.GPOSetComputerEnabled => ((IGPMGPO*)source.Pointer)->SetComputerEnabled(value),
            GpmSetBoolean.GPOLinkEnabled => ((IGPMGPOLink*)source.Pointer)->put_Enabled(value),
            GpmSetBoolean.GPOLinkEnforced => ((IGPMGPOLink*)source.Pointer)->put_Enforced(value),
            GpmSetBoolean.SOMGPOInheritanceBlocked => ((IGPMSOM*)source.Pointer)->put_GPOInheritanceBlocked(value),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        NativeError.Check(status.Value, operation);
    }
    internal static ComObject GetObject(ComObject source, GpmGetObject field, string operation)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        nint value = 0;
        var status = field switch
        {
            GpmGetObject.CreateSearchCriteria => ((IGPM*)source.Pointer)->CreateSearchCriteria((IGPMSearchCriteria**)&value),
            GpmGetObject.DomainCreateGPO => ((IGPMDomain*)source.Pointer)->CreateGPO((IGPMGPO**)&value),
            GpmGetObject.ResultStatus => ((IGPMResult*)source.Pointer)->get_Status((IGPMStatusMsgCollection**)&value),
            GpmGetObject.SOMGetGPOLinks => ((IGPMSOM*)source.Pointer)->GetGPOLinks((IGPMGPOLinksCollection**)&value),
            GpmGetObject.GPOGetSecurityInfo => ((IGPMGPO*)source.Pointer)->GetSecurityInfo((IGPMSecurityInfo**)&value),
            GpmGetObject.PermissionTrustee => ((IGPMPermission*)source.Pointer)->get_Trustee((IGPMTrustee**)&value),
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        return ComObject.FromResult(status.Value, value, operation);
    }
}
