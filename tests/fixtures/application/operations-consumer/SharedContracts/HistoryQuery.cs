using System.Text.Json.Serialization;

namespace SharedContracts;

// No Runic dependency: the presentation assembly chooses JsonIgnore compatibility.
public sealed record HistoryQuery(string Message, string Author, string? Hint)
{
    [JsonIgnore]
    public bool IsFiltered => Message.Length != 0 || Author.Length != 0;

    // A conditional serializer rule still belongs to the bridge contract.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Hint { get; init; } = Hint;
}
