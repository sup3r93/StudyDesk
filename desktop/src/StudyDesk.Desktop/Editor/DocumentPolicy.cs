using System.Text;
using System.Text.Json;

namespace StudyDesk.Desktop.Editor;

/// <summary>Bounds native document payloads and permits only inert, bounded embedded raster images.</summary>
public static class DocumentPolicy
{
    /// <summary>Rejects oversized documents and unsupported image sources without changing saved content.</summary>
    public static void Validate(string contentJson)
    {
        ArgumentNullException.ThrowIfNull(contentJson);
        if (Encoding.UTF8.GetByteCount(contentJson) > 1024 * 1024)
        {
            throw new InvalidOperationException("Split this note into smaller documents before saving.");
        }

        using JsonDocument document = JsonDocument.Parse(contentJson, new JsonDocumentOptions { MaxDepth = 64 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type) || type.GetString() != "doc")
        {
            throw new InvalidOperationException("The document format is not supported by this editor.");
        }

        ValidateNode(root);
    }

    /// <summary>Validates a PNG or JPEG data URL before the renderer can load it as a subresource.</summary>
    public static bool IsAllowedImage(string source)
    {
        if (source.Length > 350000)
        {
            return false;
        }

        bool png = source.StartsWith("data:image/png;base64,", StringComparison.Ordinal);
        bool jpeg = source.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal);
        int separator = source.IndexOf(',');
        if ((!png && !jpeg) || separator < 0)
        {
            return false;
        }

        byte[] decoded = new byte[262144];
        if (!Convert.TryFromBase64String(source[(separator + 1)..], decoded, out int count) || count < 8)
        {
            return false;
        }

        return png ? decoded.AsSpan(0, count).StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            : decoded.AsSpan(0, count).StartsWith(new byte[] { 255, 216, 255 });
    }

    /// <summary>Inspects nested rich-text nodes, including images loaded from synchronization or export files.</summary>
    private static void ValidateNode(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("The rich-text document contains an invalid node.");
        }

        if (node.TryGetProperty("type", out JsonElement kind) && kind.GetString() == "image")
        {
            if (!node.TryGetProperty("attrs", out JsonElement attributes) || attributes.ValueKind != JsonValueKind.Object
                || !attributes.TryGetProperty("src", out JsonElement source) || source.ValueKind != JsonValueKind.String
                || !IsAllowedImage(source.GetString()!))
            {
                throw new InvalidOperationException("This note contains an unsupported or oversized image. Its saved content is preserved.");
            }
        }

        if (node.TryGetProperty("content", out JsonElement children))
        {
            if (children.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("The rich-text document contains invalid child nodes.");
            }

            foreach (JsonElement child in children.EnumerateArray())
            {
                ValidateNode(child);
            }
        }
    }
}
