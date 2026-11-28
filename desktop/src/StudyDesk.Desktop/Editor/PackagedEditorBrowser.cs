using CefSharp;
using CefSharp.Internals;
using CefSharp.Wpf;

namespace StudyDesk.Desktop.Editor;

/// <summary>Captures actual CEF sender metadata before asynchronous public-event dispatch releases the frame.</summary>
public sealed class PackagedEditorBrowser : ChromiumWebBrowser, IWebBrowserInternal
{
    private long rejectedMessages;

    /// <summary>Gets a content-free count of envelopes rejected before any subscriber was invoked.</summary>
    public long RejectedMessages => Interlocked.Read(ref rejectedMessages);

    /// <summary>Notifies the host using immutable data captured while the originating frame is still valid.</summary>
    public event EventHandler<PackagedEditorMessageEventArgs>? PackagedMessageReceived;

    /// <summary>Replaces only the IPC dispatch boundary; all other browser behavior remains CefSharp's implementation.</summary>
    void IWebBrowserInternal.SetJavascriptMessageReceived(JavascriptMessageReceivedEventArgs args)
    {
        // CefSharp's public event dispatches through Task.Run; retaining args.Frame crosses its native lifetime.
        try
        {
            if (args.Frame.IsDisposed)
            {
                return;
            }

            object value = args.Message;
            if (value is not string message || !EditorMessagePolicy.IsAllowed(message))
            {
                Interlocked.Increment(ref rejectedMessages);
                return;
            }

            bool mainFrame = args.Frame.IsMain;
            bool trustedOrigin = LocalEditorPolicy.IsTrusted(args.Frame.Url);
            PackagedMessageReceived?.Invoke(this, new PackagedEditorMessageEventArgs(message, mainFrame, trustedOrigin));
        }
        catch (ObjectDisposedException)
        {
            // A closing browser has no active sender; fail closed instead of extending the frame's lifetime.
        }
    }
}

/// <summary>Contains no CEF wrappers, native handles or credential-bearing state.</summary>
public sealed class PackagedEditorMessageEventArgs : EventArgs
{
    /// <summary>Captures verified sender facts before their native frame reference can expire.</summary>
    internal PackagedEditorMessageEventArgs(string message, bool isMainFrame, bool isTrustedOrigin)
    {
        Message = message;
        IsMainFrame = isMainFrame;
        IsTrustedOrigin = isTrustedOrigin;
    }

    /// <summary>Gets the renderer's bounded structured message for native validation.</summary>
    public string Message { get; }

    /// <summary>Gets whether CEF identified the actual sender as the main frame.</summary>
    public bool IsMainFrame { get; }

    /// <summary>Gets whether the actual CEF sender URL matched the packaged editor origin.</summary>
    public bool IsTrustedOrigin { get; }
}
