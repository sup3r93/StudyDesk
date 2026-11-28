using System.Text.Json;
using StudyDesk.Desktop.Editor;
using Xunit;

namespace StudyDesk.Desktop.Ui.Tests;

/// <summary>Requires acknowledgement of the current native startup attempt instead of a one-shot ready signal.</summary>
public sealed class EditorProtocolTests
{
    /// <summary>Rejects missed, stale and unrelated startup replies while accepting a repeated current challenge.</summary>
    [Fact]
    public void ReadyAcknowledgementRequiresTheCurrentChallenge()
    {
        string current = Guid.NewGuid().ToString("N");
        string old = Guid.NewGuid().ToString("N");
        using JsonDocument valid = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "editorReady", channel = current }));
        using JsonDocument stale = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "editorReady", channel = old }));
        using JsonDocument wrongKind = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "documentChanged", channel = current }));
        using JsonDocument missing = JsonDocument.Parse("{\"type\":\"editorReady\"}");
        Assert.True(EditorProtocol.IsReadyAcknowledgement(valid.RootElement, current));
        Assert.True(EditorProtocol.IsReadyAcknowledgement(valid.RootElement, current));
        Assert.False(EditorProtocol.IsReadyAcknowledgement(stale.RootElement, current));
        Assert.False(EditorProtocol.IsReadyAcknowledgement(wrongKind.RootElement, current));
        Assert.False(EditorProtocol.IsReadyAcknowledgement(missing.RootElement, current));
    }

    /// <summary>The native probe asks the editor to acknowledge delivery and never assumes visual readiness is enough.</summary>
    [Fact]
    public void HandshakeProbeRequestsDeliveryAcknowledgement()
    {
        string challenge = Guid.NewGuid().ToString("N");
        string script = EditorProtocol.CreateHandshakeScript(challenge);
        Assert.Contains("window.studyDesk.connect(", script, StringComparison.Ordinal);
        Assert.Contains("window.CefSharp?.PostMessage", script, StringComparison.Ordinal);
        Assert.Contains(JsonSerializer.Serialize(challenge), script, StringComparison.Ordinal);
        Assert.DoesNotContain("setReadOnly(false)", script, StringComparison.Ordinal);
    }
}
