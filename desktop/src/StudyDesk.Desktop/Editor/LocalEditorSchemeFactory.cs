using System.IO;
using CefSharp;

namespace StudyDesk.Desktop.Editor;

/// <summary>Serves only bundled editor files from a fixed installation directory.</summary>
public sealed class LocalEditorSchemeFactory : ISchemeHandlerFactory
{
    private readonly string root;

    /// <summary>Creates a local-resource handler constrained to the installed editor directory.</summary>
    public LocalEditorSchemeFactory(string editorDirectory)
    {
        root = Path.GetFullPath(editorDirectory) + Path.DirectorySeparatorChar;
    }

    /// <summary>Resolves a trusted resource with canonical containment checks and restrictive headers.</summary>
    public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
    {
        if (!LocalEditorPolicy.IsTrusted(request.Url) || request.Method != "GET")
        {
            return ResourceHandler.ForErrorMessage("Resource unavailable.", System.Net.HttpStatusCode.Forbidden);
        }

        Uri uri = new(request.Url);
        string relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
        if (string.IsNullOrEmpty(relative))
        {
            relative = "index.html";
        }

        string resolved;
        try
        {
            resolved = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (ArgumentException)
        {
            return ResourceHandler.ForErrorMessage("Resource unavailable.", System.Net.HttpStatusCode.Forbidden);
        }

        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
        {
            return ResourceHandler.ForErrorMessage("Resource unavailable.", System.Net.HttpStatusCode.NotFound);
        }

        ResourceHandler handler = new(ResourceHandler.GetMimeType(Path.GetExtension(resolved)), File.OpenRead(resolved), autoDisposeStream: true);
        handler.Headers.Remove("Access-Control-Allow-Origin");
        handler.Headers["Cache-Control"] = "no-store";
        handler.Headers["X-Content-Type-Options"] = "nosniff";
        handler.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
        return handler;
    }
}
