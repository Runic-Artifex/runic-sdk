using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

// An ILC mstat file (version 2) is an ECMA-335 assembly. The bodies of its record methods (Types, Methods, Blobs,
// FrozenObjects, RvaFields and others) are IL streams: each record is `ldtoken <type|method|field>` or
// `ldstr <blob name>`, followed by `ldc.i4` operands, the first of which is the node's size. Version 2 adds a node id,
// which this reader ignores. It prints kind<TAB>size<TAB>owner<TAB>name, where owner is the assembly that defines the
// record's type, the declaring type of its method or field, or the generic type definition of an instantiation, so a
// List<Runic type> belongs to System.Private.CoreLib. A blob has no owner.
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
    if (method.RelativeVirtualAddress == 0) continue;
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
        int size;
        string label, owner;
        if (kind == "FrozenObjects")
        {
            // `ldtoken <object type>`, size, id, then `ldtoken <owning type>` or `ldc.i4.0` (none, as for a string
            // literal). A preinitialized static's object belongs to the assembly of the type that owns the static.
            if (i + 3 >= items.Count || items[i].Op != 'T' || items[i + 1].Op != 'I' || items[i + 2].Op != 'I'
                || items[i + 3] is not ({ Op: 'T' } or { Op: 'I', Value: 0 }))
                throw new InvalidOperationException($"A record of {kind} in {path} is not type, size, id, owner.");
            var objectType = items[i].Value;
            var owningType = items[i + 3] is { Op: 'T', Value: var token } ? token : (int?)null;
            size = items[i + 1].Value;
            label = names.Token(objectType) + (owningType is int type ? " in " + names.Token(type) : "");
            owner = names.Owner(owningType ?? objectType);
            i += 3;
        }
        else
        {
            if (items[i].Op == 'I') continue;
            if (i + 1 >= items.Count || items[i + 1].Op != 'I')
                throw new InvalidOperationException($"A record of {kind} in {path} has no size.");
            size = items[i + 1].Value;
            (label, owner) = items[i].Op == 'S'
                ? (md.GetUserString(MetadataTokens.UserStringHandle(items[i].Value & 0xFFFFFF)), "")
                : (names.Token(items[i].Value), names.Owner(items[i].Value));
        }
        output.Write(kind);
        output.Write('\t');
        output.Write(size);
        output.Write('\t');
        output.Write(owner);
        output.Write('\t');
        output.WriteLine(label.Replace('\t', ' ').Replace('\n', ' '));
    }
}
output.Flush();
return 0;

// Names types and methods the way ILC's mstat references them, with generic arguments in angle brackets.
sealed partial class NameProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>
{
    // The assembly that owns a record's token; "?" when the mstat file does not say.
    public string Owner(int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        return handle.Kind switch
        {
            HandleKind.MemberReference => OwnerOf(md.GetMemberReference((MemberReferenceHandle)handle).Parent),
            HandleKind.MethodSpecification => md.GetMethodSpecification((MethodSpecificationHandle)handle).Method is { Kind: HandleKind.MemberReference } target
                ? OwnerOf(md.GetMemberReference((MemberReferenceHandle)target).Parent)
                : "?",
            HandleKind.TypeReference or HandleKind.TypeSpecification => OwnerOf(handle),
            _ => "?",
        };
    }

    private string OwnerOf(EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                var type = md.GetTypeReference((TypeReferenceHandle)handle);
                return type.ResolutionScope.Kind switch
                {
                    HandleKind.AssemblyReference => md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name),
                    HandleKind.TypeReference => OwnerOf(type.ResolutionScope),
                    HandleKind.ModuleReference => md.GetString(md.GetModuleReference((ModuleReferenceHandle)type.ResolutionScope).Name),
                    _ => "?",
                };
            case HandleKind.TypeSpecification:
                var signature = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                return OwnerOfSignature(ref signature);
            default:
                return "?";
        }
    }

    // A generic instantiation belongs to its definition, an array, pointer or byref to its element type, and a
    // primitive to System.Private.CoreLib.
    private string OwnerOfSignature(ref BlobReader signature)
    {
        switch ((SignatureTypeCode)signature.ReadByte())
        {
            case SignatureTypeCode.GenericTypeInstance:
                signature.ReadByte();
                return OwnerOf(signature.ReadTypeHandle());
            case SignatureTypeCode.SZArray or SignatureTypeCode.Array or SignatureTypeCode.Pointer or SignatureTypeCode.ByReference:
                return OwnerOfSignature(ref signature);
            case (SignatureTypeCode)0x11 or (SignatureTypeCode)0x12: // valuetype, class
                return OwnerOf(signature.ReadTypeHandle());
            default:
                return "System.Private.CoreLib";
        }
    }

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
