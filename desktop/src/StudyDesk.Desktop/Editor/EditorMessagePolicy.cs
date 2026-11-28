using System.Text;

namespace StudyDesk.Desktop.Editor;

/// <summary>Bounds native bridge envelopes before they can allocate event arguments or enter the dispatcher queue.</summary>
public static class EditorMessagePolicy
{
    /// <summary>Gets the maximum UTF-16 length accepted before checking the encoded payload size.</summary>
    public const int MaximumMessageCharacters = 2 * 1024 * 1024;

    /// <summary>Gets the maximum UTF-8 envelope size, including native session metadata.</summary>
    public const int MaximumMessageBytes = 2 * 1024 * 1024;

    /// <summary>Rejects missing, empty and oversized envelopes without parsing or copying their content.</summary>
    public static bool IsAllowed(string? message)
    {
        return message is { Length: > 0 and <= MaximumMessageCharacters }
            && Encoding.UTF8.GetByteCount(message) <= MaximumMessageBytes;
    }
}
