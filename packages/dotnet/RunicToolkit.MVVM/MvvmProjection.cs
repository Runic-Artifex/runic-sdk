using System.Buffers;
using System.Text.Json;

namespace RunicToolkit.MVVM;

/// <summary>The closed set of member projections in a version 1 snapshot.</summary>
public enum MvvmProjectionMemberKind
{
    /// <summary>A scalar or structured property value.</summary>
    Property,

    /// <summary>A projected collection.</summary>
    Collection,

    /// <summary>A command's availability and execution state.</summary>
    Command,

    /// <summary>A member's validation errors.</summary>
    Validation,
}

/// <summary>One immutable member in an authoritative version 1 snapshot.</summary>
public abstract record MvvmProjectionMember
{
    private protected MvvmProjectionMember(MvvmProjectionMemberKind kind, int memberId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memberId);
        Kind = kind;
        MemberId = memberId;
    }

    /// <summary>Gets the closed projection discriminator.</summary>
    public MvvmProjectionMemberKind Kind { get; }

    /// <summary>Gets the stable generated member identifier.</summary>
    public int MemberId { get; }
}

/// <summary>A property member in an authoritative snapshot.</summary>
public sealed record MvvmProjectionProperty : MvvmProjectionMember
{
    /// <summary>Creates a property projection.</summary>
    public MvvmProjectionProperty(int memberId, JsonElement value)
        : base(MvvmProjectionMemberKind.Property, memberId)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("A projected property value must be valid JSON.", nameof(value));
        }

        Value = value.Clone();
    }

    /// <summary>Gets the detached property value.</summary>
    public JsonElement Value { get; }
}

/// <summary>A collection member in an authoritative snapshot.</summary>
public sealed record MvvmProjectionCollectionMember : MvvmProjectionMember
{
    /// <summary>Creates a collection projection.</summary>
    public MvvmProjectionCollectionMember(int memberId, IReadOnlyList<JsonElement> items)
        : base(MvvmProjectionMemberKind.Collection, memberId)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > MvvmLimits.MaximumCollectionItems)
        {
            throw new ArgumentException("A projected collection exceeds the protocol item ceiling.", nameof(items));
        }

        if (items.Any(static item => item.ValueKind == JsonValueKind.Undefined))
        {
            throw new ArgumentException("Projected collection items must be valid JSON.", nameof(items));
        }

        Items = Array.AsReadOnly(items.Select(static item => item.Clone()).ToArray());
    }

    /// <summary>Gets the detached collection items.</summary>
    public IReadOnlyList<JsonElement> Items { get; }
}

/// <summary>A command member in an authoritative snapshot.</summary>
public sealed record MvvmProjectionCommand : MvvmProjectionMember
{
    /// <summary>Creates a command projection.</summary>
    public MvvmProjectionCommand(int memberId, bool canExecute, bool isExecuting)
        : base(MvvmProjectionMemberKind.Command, memberId)
    {
        CanExecute = canExecute;
        IsExecuting = isExecuting;
    }

    /// <summary>Gets whether the command is currently available.</summary>
    public bool CanExecute { get; }

    /// <summary>Gets whether the command is currently executing.</summary>
    public bool IsExecuting { get; }
}

/// <summary>A validation member in an authoritative snapshot.</summary>
public sealed record MvvmProjectionValidation : MvvmProjectionMember
{
    /// <summary>Creates a validation projection.</summary>
    public MvvmProjectionValidation(int memberId, IReadOnlyList<string> errors)
        : base(MvvmProjectionMemberKind.Validation, memberId)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count > 32)
        {
            throw new ArgumentException("A validation projection cannot contain more than 32 errors.", nameof(errors));
        }

        Errors = Array.AsReadOnly(errors.Select(MvvmFault.SanitizeProtocolMessage).ToArray());
    }

    /// <summary>Gets the safe, bounded validation errors.</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>Builds a deterministic, authoritative version 1 member snapshot.</summary>
/// <remarks>
/// Members are emitted by ascending member identifier and then by closed member kind. A duplicate
/// <c>(kind, member)</c> pair is rejected. The session runtime owns and adds the revision.
/// </remarks>
public sealed class MvvmProjectionSnapshotBuilder
{
    private readonly Dictionary<(MvvmProjectionMemberKind Kind, int MemberId), MvvmProjectionMember> _members = [];
    private readonly MvvmBindingVocabulary? _vocabulary;

    /// <summary>Creates a snapshot builder with optional principal-kind validation.</summary>
    /// <param name="vocabulary">
    /// The complete generated vocabulary, or <see langword="null"/> when validation is performed
    /// elsewhere. Supplying it also requires every principal member to be present at build time.
    /// </param>
    public MvvmProjectionSnapshotBuilder(MvvmBindingVocabulary? vocabulary = null)
    {
        _vocabulary = vocabulary;
    }

    /// <summary>Gets the number of projected members.</summary>
    public int Count => _members.Count;

    /// <summary>Adds one projected member.</summary>
    public MvvmProjectionSnapshotBuilder Add(MvvmProjectionMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        ValidateMember(member);
        if (_members.Count >= MvvmLimits.MaximumSnapshotMembers)
        {
            throw new InvalidOperationException("The snapshot member ceiling was reached.");
        }

        if (!_members.TryAdd((member.Kind, member.MemberId), member))
        {
            throw new ArgumentException("The snapshot already contains this member kind and identifier.", nameof(member));
        }

        return this;
    }

    /// <summary>Adds a property projection.</summary>
    public MvvmProjectionSnapshotBuilder AddProperty(int memberId, JsonElement value) =>
        Add(new MvvmProjectionProperty(memberId, value));

    /// <summary>Adds a collection projection.</summary>
    public MvvmProjectionSnapshotBuilder AddCollection(int memberId, IReadOnlyList<JsonElement> items) =>
        Add(new MvvmProjectionCollectionMember(memberId, items));

    /// <summary>Adds a command projection.</summary>
    public MvvmProjectionSnapshotBuilder AddCommand(int memberId, bool canExecute, bool isExecuting) =>
        Add(new MvvmProjectionCommand(memberId, canExecute, isExecuting));

    /// <summary>Adds a validation projection.</summary>
    public MvvmProjectionSnapshotBuilder AddValidation(int memberId, IReadOnlyList<string> errors) =>
        Add(new MvvmProjectionValidation(memberId, errors));

    /// <summary>Builds a detached snapshot whose root contains the canonical <c>members</c> array.</summary>
    public MvvmSnapshot Build()
    {
        if (_vocabulary is not null)
        {
            foreach (MvvmBindingMember member in _vocabulary.Members)
            {
                MvvmProjectionMemberKind kind = member.Kind switch
                {
                    MvvmBindingMemberKind.Property => MvvmProjectionMemberKind.Property,
                    MvvmBindingMemberKind.Collection => MvvmProjectionMemberKind.Collection,
                    MvvmBindingMemberKind.Command => MvvmProjectionMemberKind.Command,
                    _ => throw new InvalidOperationException("The binding member kind is not defined by protocol version 1."),
                };
                if (!_members.ContainsKey((kind, member.MemberId)))
                {
                    throw new InvalidOperationException("The snapshot does not contain every principal member in the vocabulary.");
                }
            }
        }

        MvvmProjectionMember[] members = _members.Values
            .OrderBy(static member => member.MemberId)
            .ThenBy(static member => member.Kind)
            .ToArray();

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("members");
            writer.WriteStartArray();
            foreach (MvvmProjectionMember member in members)
            {
                WriteMember(writer, member);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return new MvvmSnapshot(document.RootElement);
    }

    private void ValidateMember(MvvmProjectionMember member)
    {
        if (_vocabulary is null)
        {
            return;
        }

        if (!_vocabulary.TryGetMember(member.MemberId, out MvvmBindingMember? binding))
        {
            throw new ArgumentException("The projection member is absent from the binding vocabulary.", nameof(member));
        }

        bool matches = member.Kind switch
        {
            MvvmProjectionMemberKind.Property => binding.Kind == MvvmBindingMemberKind.Property,
            MvvmProjectionMemberKind.Collection => binding.Kind == MvvmBindingMemberKind.Collection,
            MvvmProjectionMemberKind.Command => binding.Kind == MvvmBindingMemberKind.Command,
            MvvmProjectionMemberKind.Validation =>
                binding.Kind is MvvmBindingMemberKind.Property or MvvmBindingMemberKind.Collection,
            _ => false,
        };
        if (!matches)
        {
            throw new ArgumentException("The projection does not match the member's registered principal kind.", nameof(member));
        }
    }

    private static void WriteMember(Utf8JsonWriter writer, MvvmProjectionMember member)
    {
        writer.WriteStartObject();
        switch (member)
        {
            case MvvmProjectionProperty property:
                writer.WriteString("type", "property");
                writer.WriteNumber("member", property.MemberId);
                writer.WritePropertyName("value");
                property.Value.WriteTo(writer);
                break;
            case MvvmProjectionCollectionMember collection:
                writer.WriteString("type", "collection");
                writer.WriteNumber("member", collection.MemberId);
                writer.WritePropertyName("items");
                writer.WriteStartArray();
                foreach (JsonElement item in collection.Items)
                {
                    item.WriteTo(writer);
                }

                writer.WriteEndArray();
                break;
            case MvvmProjectionCommand command:
                writer.WriteString("type", "command");
                writer.WriteNumber("member", command.MemberId);
                writer.WriteBoolean("canExecute", command.CanExecute);
                writer.WriteBoolean("isExecuting", command.IsExecuting);
                break;
            case MvvmProjectionValidation validation:
                writer.WriteString("type", "validation");
                writer.WriteNumber("member", validation.MemberId);
                writer.WritePropertyName("errors");
                writer.WriteStartArray();
                foreach (string error in validation.Errors)
                {
                    writer.WriteStringValue(error);
                }

                writer.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException("The projection member kind is not defined by protocol version 1.");
        }

        writer.WriteEndObject();
    }
}

/// <summary>Accumulates one ordered, atomic version 1 patch transaction.</summary>
public sealed class MvvmProjectionPatchBuilder
{
    private readonly List<MvvmPatch> _patches = [];
    private readonly MvvmBindingVocabulary? _vocabulary;
    private int _collectionItems;

    /// <summary>Creates a patch builder with optional principal-kind validation.</summary>
    public MvvmProjectionPatchBuilder(MvvmBindingVocabulary? vocabulary = null)
    {
        _vocabulary = vocabulary;
    }

    /// <summary>Gets the number of ordered changes.</summary>
    public int Count => _patches.Count;

    /// <summary>Adds a change without coalescing or reordering it.</summary>
    public MvvmProjectionPatchBuilder Add(MvvmPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ValidatePatch(patch);
        if (patch is MvvmCollectionPatch { Operation: not MvvmCollectionOperation.Reset, Items.Count: 0 })
        {
            throw new ArgumentException("Insert, remove, and replace changes require at least one item.", nameof(patch));
        }

        if (_patches.Count >= MvvmLimits.MaximumPatchOperations)
        {
            throw new InvalidOperationException("The patch operation ceiling was reached.");
        }

        int addedItems = patch is MvvmCollectionPatch
        {
            Operation: MvvmCollectionOperation.Insert or
                MvvmCollectionOperation.Replace or
                MvvmCollectionOperation.Reset,
        } collection
            ? collection.Items.Count
            : 0;
        if (_collectionItems > MvvmLimits.MaximumCollectionItems - addedItems)
        {
            throw new InvalidOperationException("The patch collection-item ceiling was reached.");
        }

        _patches.Add(patch);
        _collectionItems += addedItems;
        return this;
    }

    /// <summary>Adds a property replacement.</summary>
    public MvvmProjectionPatchBuilder Property(int memberId, JsonElement value) =>
        Add(new MvvmPropertyPatch(memberId, value));

    /// <summary>Adds an indexed collection range change.</summary>
    public MvvmProjectionPatchBuilder Collection(
        int memberId,
        MvvmCollectionOperation operation,
        int index,
        IReadOnlyList<JsonElement> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (operation is not MvvmCollectionOperation.Reset && items.Count == 0)
        {
            throw new ArgumentException("Insert, remove, and replace changes require at least one item.", nameof(items));
        }

        return Add(new MvvmCollectionPatch(memberId, operation, index, items));
    }

    /// <summary>Adds a contiguous collection move.</summary>
    public MvvmProjectionPatchBuilder CollectionMove(int memberId, int from, int to, int count) =>
        Add(new MvvmCollectionMovePatch(memberId, from, to, count));

    /// <summary>Adds a command state replacement.</summary>
    public MvvmProjectionPatchBuilder Command(int memberId, bool canExecute, bool isExecuting) =>
        Add(new MvvmCommandPatch(memberId, canExecute, isExecuting));

    /// <summary>Adds a validation error-set replacement.</summary>
    public MvvmProjectionPatchBuilder Validation(int memberId, IReadOnlyList<string> errors) =>
        Add(new MvvmValidationPatch(memberId, errors));

    /// <summary>Builds an immutable copy of the ordered transaction.</summary>
    public IReadOnlyList<MvvmPatch> Build() => Array.AsReadOnly(_patches.ToArray());

    /// <summary>Builds a successful committed result from the transaction.</summary>
    public MvvmBindingResult Success(JsonElement? payload = null) =>
        MvvmBindingResult.Success(payload, Build());

    /// <summary>Builds a failed committed result from the transaction.</summary>
    public MvvmBindingResult CommittedFailure(MvvmFault fault) =>
        MvvmBindingResult.CommittedFailure(fault, Build());

    private void ValidatePatch(MvvmPatch patch)
    {
        if (_vocabulary is null)
        {
            return;
        }

        if (!_vocabulary.TryGetMember(patch.MemberId, out MvvmBindingMember? binding))
        {
            throw new ArgumentException("The patch member is absent from the binding vocabulary.", nameof(patch));
        }

        bool matches = patch switch
        {
            MvvmPropertyPatch => binding.Kind == MvvmBindingMemberKind.Property,
            MvvmCollectionPatch or MvvmCollectionMovePatch => binding.Kind == MvvmBindingMemberKind.Collection,
            MvvmCommandPatch => binding.Kind == MvvmBindingMemberKind.Command,
            MvvmValidationPatch => binding.Kind is MvvmBindingMemberKind.Property or MvvmBindingMemberKind.Collection,
            _ => false,
        };
        if (!matches)
        {
            throw new ArgumentException("The patch does not match the member's registered principal kind.", nameof(patch));
        }
    }
}
