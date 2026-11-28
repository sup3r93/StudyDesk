using CefSharp;
using CefSharp.Handler;

namespace StudyDesk.Desktop.Editor;

/// <summary>Registers the local HTTPS resource factory before browser contexts begin loading.</summary>
public sealed class LocalEditorBrowserProcessHandler : BrowserProcessHandler
{
    private readonly string editorDirectory;
    private readonly Func<IRequestContext, ISchemeHandlerFactory, bool> registrar;

    /// <summary>Gets the native registration gate shared by this process's editor controls.</summary>
    public static EditorResourceRegistration Registration { get; } = new();

    /// <summary>Captures the installed bundle directory for context initialization.</summary>
    public LocalEditorBrowserProcessHandler(string editorDirectory)
        : this(editorDirectory, RegisterNativeFactory)
    {
    }

    /// <summary>Allows the native runtime test assembly to exercise a false result inside the real CEF callback.</summary>
    internal LocalEditorBrowserProcessHandler(string editorDirectory, Func<IRequestContext, ISchemeHandlerFactory, bool> registrar)
    {
        this.editorDirectory = editorDirectory;
        this.registrar = registrar ?? throw new ArgumentNullException(nameof(registrar));
    }

    /// <summary>Preserves the actual native registration call as the only production-default registrar.</summary>
    private static bool RegisterNativeFactory(IRequestContext context, ISchemeHandlerFactory factory)
    {
        return context.RegisterSchemeHandlerFactory("https", LocalEditorPolicy.Host, factory);
    }

    /// <summary>Registers the factory only after Chromium's global request context exists.</summary>
    protected override void OnContextInitialized()
    {
        try
        {
            using IRequestContext context = Cef.GetGlobalRequestContext();
            bool registered = registrar(context, new LocalEditorSchemeFactory(editorDirectory));
            Registration.Complete(registered);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            Registration.Complete(false);
        }
    }
}
