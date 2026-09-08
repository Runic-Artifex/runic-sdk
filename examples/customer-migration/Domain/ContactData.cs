using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomerMigration.Domain;

public static class ContactCodec
{
    public static ContactData Parse(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) > 4096) throw new InvalidDataException();
        var contact = JsonSerializer.Deserialize(text, ContactJson.Default.ContactData);
        if (contact is null || contact.Name is null || contact.Email is null || contact.Company is null ||
            contact.Name.Length > 1000 || contact.Email.Length > 1000 || contact.Company.Length > 1000) throw new InvalidDataException();
        return contact;
    }
    public static string Serialize(ContactData contact)
    {
        var text = JsonSerializer.Serialize(contact, ContactJson.Default.ContactData);
        if (Encoding.UTF8.GetByteCount(text) > 4096) throw new InvalidDataException();
        return text;
    }
}
public sealed record ContactData(string Name, string Email, string Company);
[JsonSerializable(typeof(ContactData))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ContactJson : JsonSerializerContext;
