using System.Text;
using System.Text.Json;

namespace RunicToolkit.MVVM;

/// <summary>Strictly validates generated snapshot and patch projections against a closed vocabulary.</summary>
public static class MvvmProjectionValidator
{
    /// <summary>Validates a canonical authoritative snapshot.</summary>
    /// <exception cref="InvalidOperationException">The projection is incomplete, duplicated, unknown, or of the wrong kind.</exception>
    public static void ValidateSnapshot(MvvmSnapshot snapshot, MvvmBindingVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (!IsValidSnapshot(snapshot.State, vocabulary))
        {
            throw new InvalidOperationException("The adapter produced an invalid snapshot projection.");
        }
    }

    /// <summary>Validates an ordered patch transaction against registered principal kinds.</summary>
    /// <exception cref="InvalidOperationException">A change is unknown, malformed, or of the wrong kind.</exception>
    public static void ValidatePatches(IReadOnlyList<MvvmPatch> patches, MvvmBindingVocabulary vocabulary)
    {
        ArgumentNullException.ThrowIfNull(patches);
        ArgumentNullException.ThrowIfNull(vocabulary);
        if (!AreValidPatches(patches, vocabulary))
        {
            throw new InvalidOperationException("The adapter produced an invalid patch projection.");
        }
    }

    private static bool IsValidSnapshot(JsonElement state, MvvmBindingVocabulary vocabulary)
    {
        if (state.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonElement members = default;
        int rootPropertyCount = 0;
        foreach (JsonProperty property in state.EnumerateObject())
        {
            rootPropertyCount++;
            if (property.Name != "members" || members.ValueKind != JsonValueKind.Undefined)
            {
                return false;
            }

            members = property.Value;
        }

        if (rootPropertyCount != 1 || members.ValueKind != JsonValueKind.Array ||
            members.GetArrayLength() > MvvmLimits.MaximumSnapshotMembers)
        {
            return false;
        }

        var principals = new HashSet<int>();
        var entries = new HashSet<(MvvmProjectionMemberKind Kind, int MemberId)>();
        int priorMemberId = 0;
        MvvmProjectionMemberKind priorKind = MvvmProjectionMemberKind.Property;
        bool hasPrior = false;
        foreach (JsonElement element in members.EnumerateArray())
        {
            if (!TryValidateSnapshotMember(element, vocabulary, out MvvmProjectionMemberKind kind, out int memberId) ||
                !entries.Add((kind, memberId)) ||
                (hasPrior && (memberId < priorMemberId || (memberId == priorMemberId && kind <= priorKind))))
            {
                return false;
            }

            if (kind is not MvvmProjectionMemberKind.Validation && !principals.Add(memberId))
            {
                return false;
            }

            priorMemberId = memberId;
            priorKind = kind;
            hasPrior = true;
        }

        return principals.Count == vocabulary.Members.Count &&
            vocabulary.Members.All(member => principals.Contains(member.MemberId));
    }

    private static bool TryValidateSnapshotMember(
        JsonElement element,
        MvvmBindingVocabulary vocabulary,
        out MvvmProjectionMemberKind kind,
        out int memberId)
    {
        kind = default;
        memberId = 0;
        if (element.ValueKind != JsonValueKind.Object ||
            !TryGetUniqueProperty(element, "type", out JsonElement type) || type.ValueKind != JsonValueKind.String ||
            !TryGetUniqueProperty(element, "member", out JsonElement member) ||
            !member.TryGetInt32(out memberId) || memberId <= 0 ||
            !vocabulary.TryGetMember(memberId, out MvvmBindingMember? binding))
        {
            return false;
        }

        string? typeName = type.GetString();
        kind = typeName switch
        {
            "property" => MvvmProjectionMemberKind.Property,
            "collection" => MvvmProjectionMemberKind.Collection,
            "command" => MvvmProjectionMemberKind.Command,
            "validation" => MvvmProjectionMemberKind.Validation,
            _ => (MvvmProjectionMemberKind)(-1),
        };

        return kind switch
        {
            MvvmProjectionMemberKind.Property =>
                binding.Kind == MvvmBindingMemberKind.Property &&
                HasExactProperties(element, "type", "member", "value"),
            MvvmProjectionMemberKind.Collection =>
                binding.Kind == MvvmBindingMemberKind.Collection &&
                HasExactProperties(element, "type", "member", "items") &&
                TryGetUniqueProperty(element, "items", out JsonElement items) &&
                items.ValueKind == JsonValueKind.Array &&
                items.GetArrayLength() <= MvvmLimits.MaximumCollectionItems,
            MvvmProjectionMemberKind.Command =>
                binding.Kind == MvvmBindingMemberKind.Command &&
                HasExactProperties(element, "type", "member", "canExecute", "isExecuting") &&
                IsBooleanProperty(element, "canExecute") && IsBooleanProperty(element, "isExecuting"),
            MvvmProjectionMemberKind.Validation =>
                binding.Kind is MvvmBindingMemberKind.Property or MvvmBindingMemberKind.Collection &&
                HasExactProperties(element, "type", "member", "errors") &&
                TryGetUniqueProperty(element, "errors", out JsonElement errors) && AreValidErrors(errors),
            _ => false,
        };
    }

    private static bool AreValidPatches(IReadOnlyList<MvvmPatch> patches, MvvmBindingVocabulary vocabulary)
    {
        if (patches.Count > MvvmLimits.MaximumPatchOperations)
        {
            return false;
        }

        int replacementItems = 0;
        foreach (MvvmPatch patch in patches)
        {
            if (patch is null ||
                !vocabulary.TryGetMember(patch.MemberId, out MvvmBindingMember? binding))
            {
                return false;
            }

            switch (patch)
            {
                case MvvmPropertyPatch when binding.Kind != MvvmBindingMemberKind.Property:
                case MvvmCollectionPatch when binding.Kind != MvvmBindingMemberKind.Collection:
                case MvvmCollectionMovePatch when binding.Kind != MvvmBindingMemberKind.Collection:
                case MvvmCommandPatch when binding.Kind != MvvmBindingMemberKind.Command:
                case MvvmValidationPatch when binding.Kind is not MvvmBindingMemberKind.Property and not MvvmBindingMemberKind.Collection:
                    return false;
                case MvvmCollectionPatch { Operation: not MvvmCollectionOperation.Reset, Items.Count: 0 }:
                    return false;
                case MvvmCollectionPatch
                {
                    Operation: MvvmCollectionOperation.Insert or MvvmCollectionOperation.Replace or MvvmCollectionOperation.Reset,
                } collection:
                    if (replacementItems > MvvmLimits.MaximumCollectionItems - collection.Items.Count)
                    {
                        return false;
                    }

                    replacementItems += collection.Items.Count;
                    break;
                case MvvmValidationPatch validation when validation.Errors.Any(static error => !IsValidError(error)):
                    return false;
                case MvvmPropertyPatch or MvvmCollectionMovePatch or MvvmCommandPatch or MvvmValidationPatch:
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return false;
            }
        }

        return names.Count == expected.Length && expected.All(names.Contains);
    }

    private static bool TryGetUniqueProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name != name)
            {
                continue;
            }

            if (value.ValueKind != JsonValueKind.Undefined)
            {
                return false;
            }

            value = property.Value;
        }

        return value.ValueKind != JsonValueKind.Undefined;
    }

    private static bool IsBooleanProperty(JsonElement element, string name) =>
        TryGetUniqueProperty(element, name, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool AreValidErrors(JsonElement errors)
    {
        if (errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() > 32)
        {
            return false;
        }

        foreach (JsonElement error in errors.EnumerateArray())
        {
            if (error.ValueKind != JsonValueKind.String || !IsValidError(error.GetString()))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidError(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        error.AsSpan().SequenceEqual(error.Trim().AsSpan()) &&
        !error.Any(char.IsControl) &&
        Encoding.UTF8.GetByteCount(error) <= 256;
}
