using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

// An ILC mstat file (version 2) is an ECMA-335 assembly. The bodies of its methods Types, Methods and Blobs are IL
// streams: each record is `ldtoken <type|method>` or `ldstr <blob name>`, followed by `ldc.i4` operands, the first of
// which is the node's size. Version 2 adds a node id, which this reader ignores.
//   dotnet run --project tests/scaling/aot-size/MstatReader -- <file.mstat>
if (args is not [var path])
{
    Console.Error.WriteLine("Usage: MstatReader <file.mstat>");
    return 2;
}

using var pe = new PEReader(File.OpenRead(path));
var md = pe.GetMetadataReader();
var names = new NameProvider(md);
using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = false };
foreach (var typeHandle in md.TypeDefinitions)
foreach (var methodHandle in md.GetTypeDefinition(typeHandle).GetMethods())
{
    var method = md.GetMethodDefinition(methodHandle);
    var kind = md.GetString(method.Name);
    if (kind is not ("Types" or "Methods" or "Blobs")) continue;
    var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
    var items = new List<(char Op, int Value)>();
    while (il.RemainingBytes > 0)
    {
        var op = il.ReadByte();
        switch (op)
        {
            case 0xD0: items.Add(('T', il.ReadInt32())); break;              // ldtoken
            case 0x72: items.Add(('S', il.ReadInt32())); break;              // ldstr
            case 0x20: items.Add(('I', il.ReadInt32())); break;              // ldc.i4
            case 0x1F: items.Add(('I', il.ReadSByte())); break;              // ldc.i4.s
            case >= 0x16 and <= 0x1E: items.Add(('I', op - 0x16)); break;    // ldc.i4.0 .. ldc.i4.8
            case 0x15: items.Add(('I', -1)); break;                          // ldc.i4.m1
            case 0x26 or 0x2A or 0x00: break;                                // pop, ret, nop
            default: throw new InvalidOperationException($"Unexpected IL {op:X2} in {kind} of {path}.");
        }
    }
    for (var i = 0; i < items.Count; i++)
    {
        if (items[i].Op == 'I') continue;
        if (i + 1 >= items.Count || items[i + 1].Op != 'I')
            throw new InvalidOperationException($"A record of {kind} in {path} has no size.");
        var size = items[i + 1].Value;
        var label = items[i].Op == 'S'
            ? md.GetUserString(MetadataTokens.UserStringHandle(items[i].Value & 0xFFFFFF))
            : names.Token(items[i].Value);
        output.Write(kind);
        output.Write('\t');
        output.Write(size);
        output.Write('\t');
        output.WriteLine(label);
    }
}
output.Flush();
return 0;

// Names types and methods the way ILC's mstat references them, with generic arguments in angle brackets.
sealed partial class NameProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>
{
    public string Token(int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        return handle.Kind switch
        {
            HandleKind.MethodDefinition => Method((MethodDefinitionHandle)handle),
            HandleKind.MemberReference => Member((MemberReferenceHandle)handle),
            HandleKind.MethodSpecification => Specification((MethodSpecificationHandle)handle),
            _ => Type(handle),
        };
    }

    private string Type(EntityHandle handle) => handle.Kind switch
    {
        HandleKind.TypeDefinition => GetTypeFromDefinition(md, (TypeDefinitionHandle)handle, 0),
        HandleKind.TypeReference => GetTypeFromReference(md, (TypeReferenceHandle)handle, 0),
        HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null),
        _ => handle.Kind.ToString(),
    };

    private string Method(MethodDefinitionHandle handle)
    {
        var method = md.GetMethodDefinition(handle);
        return Type(method.GetDeclaringType()) + "." + md.GetString(method.Name);
    }

    private string Member(MemberReferenceHandle handle)
    {
        var member = md.GetMemberReference(handle);
        return Type(member.Parent) + "." + md.GetString(member.Name);
    }

    private string Specification(MethodSpecificationHandle handle)
    {
        var specification = md.GetMethodSpecification(handle);
        var target = specification.Method.Kind == HandleKind.MethodDefinition
            ? Method((MethodDefinitionHandle)specification.Method)
            : Member((MemberReferenceHandle)specification.Method);
        return target + "<" + string.Join(", ", specification.DecodeSignature(this, null)) + ">";
    }

    [GeneratedRegex("`\\d+")]
    private static partial Regex Arity();

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
        Arity().Replace(genericType, "") + "<" + string.Join(", ", typeArguments) + ">";
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPinnedType(string elementType) => elementType;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeDefinition(handle);
        if (type.GetDeclaringType() is { IsNil: false } declaring)
            return GetTypeFromDefinition(reader, declaring, rawTypeKind) + "+" + reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        return ns.Length > 0 ? ns + "." + reader.GetString(type.Name) : reader.GetString(type.Name);
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeReference(handle);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            return GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, rawTypeKind) + "+" + reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        return ns.Length > 0 ? ns + "." + reader.GetString(type.Name) : reader.GetString(type.Name);
    }

    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
