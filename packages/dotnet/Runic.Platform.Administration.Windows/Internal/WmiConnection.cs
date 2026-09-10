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
        Automation.RequireX64();
        if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        _timeoutMilliseconds = checked((int)timeout.TotalMilliseconds);
        using var locator = ComObject.Create(new("4590f811-1d3a-11d0-891f-00aa004b2e24"), new("dc12a687-737f-11cf-884d-00aa004b2e24"));
        using var target = new BString(@"\\" + server + "\\" + scope);
        using var user = credential is null ? null : new BString(
            string.IsNullOrEmpty(credential.Domain) ? credential.UserName : credential.Domain + "\\" + credential.UserName);
        using var password = credential is null ? null : new BString(credential.Password);
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, nint, int, nint, nint, nint*, int>)locator.Slot(3))(
            locator.Pointer, target.Pointer, user?.Pointer ?? 0, password?.Pointer ?? 0, 0, 0x80, 0, 0, &result), "Connect Windows management");
        _services = ComObject.Own(result);
        try
        {
            _identity = credential is null ? null : new WmiIdentity(credential);
            Secure(_services);
        }
        catch { _services.Dispose(); _identity?.Dispose(); throw; }
    }

    private void Secure(ComObject proxy)
    {
        var identity = _identity?.Value ?? default;
        NativeError.Check(CoSetProxyBlanket(proxy.Pointer, uint.MaxValue, uint.MaxValue, -1, 6, 3,
            _identity is null ? null : &identity, 0), "Secure Windows management proxy");
    }

    internal ImmutableArray<ImmutableDictionary<string, object?>> Query(string query, string[] properties, CancellationToken cancellationToken)
    {
        using var language = new BString("WQL");
        using var text = new BString(query);
        nint result = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, int, nint, nint*, int>)_services.Slot(20))(
            _services.Pointer, language.Pointer, text.Pointer, 0x30, 0, &result), "Query Windows management");
        using var iterator = ComObject.Own(result);
        Secure(iterator);
        var rows = ImmutableArray.CreateBuilder<ImmutableDictionary<string, object?>>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nint value = 0;
            uint count = 0;
            var status = ((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, uint*, int>)iterator.Slot(4))(
                iterator.Pointer, _timeoutMilliseconds, 1, &value, &count);
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
        NativeError.Text(objectPath, nameof(objectPath));
        using var path = new BString(objectPath);
        nint pointer = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint, nint*, nint, int>)_services.Slot(6))(
            _services.Pointer, path.Pointer, 0, 0, &pointer, 0), "Read Windows management object");
        using var value = ComObject.Own(pointer);
        var row = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties) row.Add(property, Get(value, property));
        return row.ToImmutable();
    }

    internal void Delete(string objectPath)
    {
        NativeError.Text(objectPath, nameof(objectPath));
        using var path = new BString(objectPath);
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint, nint, int>)_services.Slot(16))(
            _services.Pointer, path.Pointer, 0, 0, 0), "Delete Windows management instance");
    }

    internal void Invoke(string objectPath, string methodName, IReadOnlyDictionary<string, object> arguments)
    {
        NativeError.Text(objectPath, nameof(objectPath));
        using var path = new BString(objectPath);
        using var method = new BString(methodName);
        nint objectPointer = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint, nint*, nint, int>)_services.Slot(6))(
            _services.Pointer, path.Pointer, 0, 0, &objectPointer, 0), "Read Windows management method owner");
        using var owner = ComObject.Own(objectPointer);
        var className = Get(owner, "__CLASS") as string ?? throw NativeError.Win32("Read WMI method class", 13);
        using var classPath = new BString(className);
        nint classPointer = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint, nint*, nint, int>)_services.Slot(6))(
            _services.Pointer, classPath.Pointer, 0, 0, &classPointer, 0), "Read Windows management method class");
        using var methodClass = ComObject.Own(classPointer);
        nint inputDefinition = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, nint, int>)methodClass.Slot(19))(
            methodClass.Pointer, method.Pointer, 0, &inputDefinition, 0), "Read Windows management method definition");
        using var definition = inputDefinition == 0 ? null : ComObject.Own(inputDefinition);
        nint inputPointer = 0;
        if (definition is not null)
            NativeError.Check(((delegate* unmanaged[Stdcall]<nint, int, nint*, int>)definition.Slot(15))(
                definition.Pointer, 0, &inputPointer), "Create Windows management method input");
        using var input = inputPointer == 0 ? null : ComObject.Own(inputPointer);
        if (input is null && arguments.Count != 0) throw new ArgumentException("This native method has no input parameters.", nameof(arguments));
        if (input is not null) foreach (var argument in arguments) Put(input, argument.Key, argument.Value);
        nint outputPointer = 0;
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, int, nint, nint, nint*, nint, int>)_services.Slot(24))(
            _services.Pointer, path.Pointer, method.Pointer, 0, 0, input?.Pointer ?? 0, &outputPointer, 0), "Invoke Windows management method");
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
        using var property = new BString(name);
        Variant result = default;
        try
        {
            var status = ((delegate* unmanaged[Stdcall]<nint, nint, int, Variant*, nint, nint, int>)value.Slot(4))(
                value.Pointer, property.Pointer, 0, &result, 0, 0);
            if (allowMissing && status == unchecked((int)0x80041002)) return null;
            NativeError.Check(status, "Read Windows management property");
            return result.Type switch
            {
                0 or 1 => null,
                8 => result.Pointer == 0 ? "" : Marshal.PtrToStringBSTR(result.Pointer),
                3 => result.Integer,
                19 => unchecked((uint)result.Integer),
                2 => unchecked((short)result.Integer),
                18 => unchecked((ushort)result.Integer),
                11 => unchecked((short)result.Integer) != 0,
                0x2008 or 0x200c => AutomationArrays.ReadStrings(result),
                _ => throw NativeError.Win32("Read supported Windows management property type", 13)
            };
        }
        finally { _ = AutomationArrays.VariantClear(&result); }
    }

    private static void Put(ComObject input, string name, object value)
    {
        using var property = new BString(name);
        using var text = value is string stringValue ? new BString(stringValue) : null;
        var native = value switch
        {
            string => Variant.String(text!.Pointer),
            uint integer => Variant.Int32(unchecked((int)integer)),
            int integer => Variant.Int32(integer),
            bool boolean => new Variant { Type = 11, Integer = boolean ? -1 : 0 },
            _ => throw new ArgumentException("Unsupported Windows management method input.", nameof(value))
        };
        NativeError.Check(((delegate* unmanaged[Stdcall]<nint, nint, int, Variant*, int, int>)input.Slot(5))(
            input.Pointer, property.Pointer, 0, &native, 0), "Write Windows management method input");
    }

    public void Dispose() { _services.Dispose(); _identity?.Dispose(); }

    [StructLayout(LayoutKind.Sequential)]
    private struct Identity
    {
        internal nint User; internal uint UserLength;
        internal nint Domain; internal uint DomainLength;
        internal nint Password; internal uint PasswordLength;
        internal uint Flags;
    }
    private sealed class WmiIdentity : IDisposable
    {
        private readonly BString _user, _domain, _password;
        internal Identity Value { get; }
        internal WmiIdentity(NetworkCredential credential)
        {
            _user = new(credential.UserName); _domain = new(credential.Domain); _password = new(credential.Password);
            Value = new() { User = _user.Pointer, UserLength = (uint)credential.UserName.Length,
                Domain = _domain.Pointer, DomainLength = (uint)credential.Domain.Length,
                Password = _password.Pointer, PasswordLength = (uint)credential.Password.Length, Flags = 2 };
        }
        public void Dispose() { _password.Dispose(); _domain.Dispose(); _user.Dispose(); }
    }
    [LibraryImport("ole32.dll")]
    private static partial int CoSetProxyBlanket(nint proxy, uint authentication, uint authorization, nint principal,
        uint authenticationLevel, uint impersonationLevel, Identity* identity, uint capabilities);
}
