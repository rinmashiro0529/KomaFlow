using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MangaCli;

/// <summary>
/// JSON options for the NDJSON event stream: snake_case property names, enum values rendered as
/// snake_case strings, relaxed escaping so CJK and path characters stay readable.
/// </summary>
public static class CliJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    /// <summary>Camel/Pascal enum or identifier → snake_case ("DrainThenStop" → "drain_then_stop").</summary>
    public static string ToSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (char.IsUpper(ch) && i > 0)
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }
}
