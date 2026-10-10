using System;
using System.Text.Json;

namespace RuriLib.Blocks.Hotmail;

// Message references arrive either as a raw id or as a verbatim JSON row produced by
// List Messages; JSON rows are parsed for their "id" so configs can pipe the listing
// straight into detail, attachment, and delete blocks. Shared by every block that
// takes a message reference.
internal static class MessageRef
{
    public static string IdOf(string reference, string what)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            throw new ArgumentException($"The {what} reference is empty.");
        }

        var trimmed = reference.Trim();
        if (!trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                // Rest rows carry PascalCase "Id", Graph rows lowercase "id"; match either.
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrEmpty(property.Value.GetString()))
                    {
                        return property.Value.GetString();
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        throw new ArgumentException(
            $"The {what} reference is neither a raw id nor a JSON row carrying a string 'id': " +
            Truncate(trimmed));
    }

    private static string Truncate(string content)
        => content.Length <= 500 ? content : content[..500] + "...";
}
