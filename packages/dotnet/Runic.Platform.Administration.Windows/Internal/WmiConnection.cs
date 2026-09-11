using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Wmi;
using Windows.Win32.System.Variant;
using Runic.Platform.Administration.Windows.Internal.Backends;
using System.Collections.Immutable;
using System.Net;
using System.Runtime.InteropServices;

namespace Runic.Platform.Administration.Windows.Internal;

/// <summary>Internal, operation-scoped WMI binding. All use is confined to an owned COM apartment.</summary>
internal sealed unsafe partial class WmiConnection : IDisposable
{
    private readonly ComObject _services;
    private readonly WmiIdentity? _identity;
    private readonly int _timeoutMilliseconds;

    internal WmiConnection(string server, string scope, NetworkCredential? credential, TimeSpan timeout)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        Automation.RequireX64();
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeoutMilliseconds = checked((int)timeout.TotalMilliseconds);
        using var locator = ComObject.Create(new("4590f811-1d3a-11d0-891f-00aa004b2e24"), new("dc12a687-737f-11cf-884d-00aa004b2e24"));
        using var target = new BString(@"\\" + server + "\\" + scope);
        using var user = credential is null ? null : new BString(
            string.IsNullOrEmpty(credential.Domain) ? credential.UserName : credential.Domain + "\\" + credential.UserName);
        using var password = credential is null ? null : new BString(credential.Password);
        nint result = 0;
        _services = ComObject.FromResult(((IWbemLocator*)locator.Pointer)->ConnectServer(target.Native, user?.Native ?? default, password?.Native ?? default, default, 0x80, default, null, (IWbemServices**)&result).Value, result, "Connect Windows management");
        try
        {
            _identity = credential is null ? null : new WmiIdentity(credential);
            Secure(_services);
        }
        catch { _services.Dispose(); _identity?.Dispose(); throw; }
    }

    private void Secure(ComObject proxy)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        var identity = _identity?.Value ?? default;
        NativeError.Check(PInvoke.CoSetProxyBlanket((IUnknown*)proxy.Pointer, uint.MaxValue, uint.MaxValue, new PWSTR((char*)-1), RPC_C_AUTHN_LEVEL.RPC_C_AUTHN_LEVEL_PKT_PRIVACY, RPC_C_IMP_LEVEL.RPC_C_IMP_LEVEL_IMPERSONATE,
            _identity is null ? null : &identity, 0).Value, "Secure Windows management proxy");
    }

    internal ImmutableArray<ImmutableDictionary<string, object?>> Query(string query, string[] properties, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var language = new BString("WQL");
        using var text = new BString(query);
        nint result = 0;
        using var iterator = ComObject.FromResult(((IWbemServices*)_services.Pointer)->ExecQuery(language.Native, text.Native, (WBEM_GENERIC_FLAG_TYPE)0x30, null, (IEnumWbemClassObject**)&result).Value, result, "Query Windows management");
        Secure(iterator);
        var rows = ImmutableArray.CreateBuilder<ImmutableDictionary<string, object?>>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint value = 0;
            uint count = 0;
            var status = ((IEnumWbemClassObject*)iterator.Pointer)->Next(_timeoutMilliseconds, 1, (IWbemClassObject**)&value, &count).Value;
            NativeError.Check(status, "Read Windows management query");
            if (status == 0x40004) throw NativeError.Win32("Wait for Windows management query", 1460);
            if (count == 0)
            {
                if (status != 1) throw NativeError.Win32("Read complete Windows management query", 13);
                break;
            }
            using var item = ComObject.Own(value);
            var row = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in properties) row.Add(property, Get(item, property));
            rows.Add(row.ToImmutable());
        }
        return rows.ToImmutable();
    }


    internal ImmutableDictionary<string, object?> Read(string objectPath, string[] properties)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        NativeError.Text(objectPath, nameof(objectPath));
        using var path = new BString(objectPath);
        nint pointer = 0;
        using var value = ComObject.FromResult(((IWbemServices*)_services.Pointer)->GetObject(path.Native, 0, null, (IWbemClassObject**)&pointer, null).Value, pointer, "Read Windows management object");
        var row = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties) row.Add(property, Get(value, property));
        return row.ToImmutable();
    }

    internal void Delete(string objectPath)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        NativeError.Text(objectPath, nameof(objectPath));
        using var path = new BString(objectPath);
        NativeError.Check(((IWbemServices*)_services.Pointer)->DeleteInstance(path.Native, 0, null, null).Value, "Delete Windows management instance");
    }

    internal void Invoke(string objectPath, string methodName, IReadOnlyDictionary<string, object> arguments)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        NativeError.Text(objectPath, nameof(objectPath));
        using var path = new BString(objectPath);
        using var method = new BString(methodName);
        nint objectPointer = 0;
        using var owner = ComObject.FromResult(((IWbemServices*)_services.Pointer)->GetObject(path.Native, 0, null, (IWbemClassObject**)&objectPointer, null).Value, objectPointer, "Read Windows management method owner");
        var className = Get(owner, "__CLASS") as string ?? throw NativeError.Win32("Read WMI method class", 13);
        using var classPath = new BString(className);
        nint classPointer = 0;
        using var methodClass = ComObject.FromResult(((IWbemServices*)_services.Pointer)->GetObject(classPath.Native, 0, null, (IWbemClassObject**)&classPointer, null).Value, classPointer, "Read Windows management method class");
        nint inputDefinition = 0;
        NativeError.Check(((IWbemClassObject*)methodClass.Pointer)->GetMethod((char*)method.Pointer, 0, (IWbemClassObject**)&inputDefinition, null).Value, "Read Windows management method definition");
        using var definition = inputDefinition == 0 ? null : ComObject.Own(inputDefinition);
        nint inputPointer = 0;
        if (definition is not null)
            NativeError.Check(((IWbemClassObject*)definition.Pointer)->SpawnInstance(0, (IWbemClassObject**)&inputPointer).Value, "Create Windows management method input");
        using var input = inputPointer == 0 ? null : ComObject.Own(inputPointer);
        if (input is null && arguments.Count != 0) throw new ArgumentException("This native method has no input parameters.", nameof(arguments));
        if (input is not null) foreach (var argument in arguments) Put(input, argument.Key, argument.Value);
        nint outputPointer = 0;
        NativeError.Check(((IWbemServices*)_services.Pointer)->ExecMethod(path.Native, method.Native, 0, null, (IWbemClassObject*)(input?.Pointer ?? 0), (IWbemClassObject**)&outputPointer, null).Value, "Invoke Windows management method");
        if (outputPointer != 0)
        {
            using var output = ComObject.Own(outputPointer);
            var returnValue = Get(output, "ReturnValue", allowMissing: true);
            if (returnValue is uint code && code != 0) throw NativeError.Win32("Execute Windows management method", unchecked((int)code));
            if (returnValue is int signed && signed != 0) throw NativeError.Win32("Execute Windows management method", signed);
            if (returnValue is not (null or uint or int)) throw NativeError.Win32("Read Windows management method result", 13);
        }
    }

    private static object? Get(ComObject value, string name, bool allowMissing = false)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var property = new BString(name);
        VARIANT result = default;
        try
        {
            var status = ((IWbemClassObject*)value.Pointer)->Get((char*)property.Pointer, 0, &result, null, null).Value;
            if (allowMissing && status == unchecked((int)0x80041002)) return null;
            NativeError.Check(status, "Read Windows management property");
            return (ushort)result.vt switch
            {
                0 or 1 => null,
                8 => result.bstrVal.Value == null ? "" : Marshal.PtrToStringBSTR((nint)result.bstrVal.Value),
                3 => result.lVal,
                19 => unchecked((uint)result.lVal),
                2 => unchecked((short)result.lVal),
                18 => unchecked((ushort)result.lVal),
                11 => unchecked((short)result.lVal) != 0,
                0x2008 or 0x200c => GeneratedArrays.ReadStrings(result),
                _ => throw NativeError.Win32("Read supported Windows management property type", 13)
            };
        }
        finally { _ = PInvoke.VariantClear(&result); }
    }

    private static void Put(ComObject input, string name, object value)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        using var property = new BString(name);
        using var text = value is string stringValue ? new BString(stringValue) : null;
        var native = value switch
        {
            string => NativeVariant.String(text!.Pointer),
            uint integer => NativeVariant.Int32(unchecked((int)integer)),
            int integer => NativeVariant.Int32(integer),
            bool boolean => new VARIANT { vt = VARENUM.VT_BOOL, boolVal = new VARIANT_BOOL(boolean ? (short)-1 : (short)0) },
            _ => throw new ArgumentException("Unsupported Windows management method input.", nameof(value))
        };
        NativeError.Check(((IWbemClassObject*)input.Pointer)->Put((char*)property.Pointer, 0, &native, 0).Value, "Write Windows management method input");
    }

    public void Dispose() { _services.Dispose(); _identity?.Dispose(); }

    private sealed class WmiIdentity : IDisposable
    {
        private readonly BString _user, _domain, _password;
        internal COAUTHIDENTITY Value { get; }
        internal WmiIdentity(NetworkCredential credential)
        {
            _user = new(credential.UserName); _domain = new(credential.Domain); _password = new(credential.Password);
            Value = new() { User = (ushort*)_user.Pointer, UserLength = (uint)credential.UserName.Length,
                Domain = (ushort*)_domain.Pointer, DomainLength = (uint)credential.Domain.Length,
                Password = (ushort*)_password.Pointer, PasswordLength = (uint)credential.Password.Length, Flags = 2 };
        }
        public void Dispose() { _password.Dispose(); _domain.Dispose(); _user.Dispose(); }
    }
}
