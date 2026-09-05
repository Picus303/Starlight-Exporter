using System.Text.Json;

namespace StarlightExporter.Snapshot;

public static class SnapshotSecurityGuard
{
    private static readonly string[] ForbiddenPropertyFragments = [
        "authorization",
        "cookie",
        "credential",
        "password",
        "passwd",
        "privatekey",
        "secret",
        "sessionkey",
        "token"
    ];

    public static void EnsureNoSensitiveProperties(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions {
            CommentHandling = JsonCommentHandling.Disallow
        });

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            string propertyName = reader.GetString() ?? string.Empty;
            string normalized = string.Concat(propertyName
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant));
            if (ForbiddenPropertyFragments.Any(normalized.Contains))
            {
                throw new InvalidDataException(
                    $"Sensitive property '{propertyName}' is forbidden in a snapshot artifact.");
            }
        }
    }
}
