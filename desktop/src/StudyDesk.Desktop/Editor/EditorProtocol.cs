using System.Text.Json;

namespace StudyDesk.Desktop.Editor;

/// <summary>Defines the idempotent, origin-gated ready acknowledgement used by the local editor.</summary>
public static class EditorProtocol
{
    /// <summary>Creates a content-free readiness probe that can be repeated after a missed startup event.</summary>
    public static string CreateHandshakeScript(string challenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge);
        string argument = JsonSerializer.Serialize(challenge);
        return "(function(){const api=typeof window.studyDesk?.connect==='function'&&typeof window.studyDesk?.setLanguage==='function';const bridge=typeof window.CefSharp?.PostMessage==='function';if(api){window.studyDesk.connect("
            + argument + ");}return JSON.stringify({editorApiReady:api,postMessageAvailable:bridge});})()";
    }

    /// <summary>Accepts only a ready acknowledgement for the latest native-generated channel.</summary>
    public static bool IsReadyAcknowledgement(JsonElement message, string challenge)
    {
        return message.ValueKind == JsonValueKind.Object && message.TryGetProperty("type", out JsonElement type)
            && type.ValueKind == JsonValueKind.String && type.GetString() == "editorReady" && IsMatchingChannel(message, challenge);
    }

    /// <summary>Rejects previous frames, old startup attempts and unrelated renderer sessions.</summary>
    public static bool IsMatchingChannel(JsonElement message, string challenge)
    {
        return message.ValueKind == JsonValueKind.Object && message.TryGetProperty("channel", out JsonElement channel)
            && channel.ValueKind == JsonValueKind.String && string.Equals(channel.GetString(), challenge, StringComparison.Ordinal);
    }
}
