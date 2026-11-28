using System.IO;
using System.Net;
using CefSharp;
using CefSharp.Handler;

namespace StudyDesk.Desktop.Editor;

/// <summary>Prevents local editor requests from falling back to network loading, even after registration failures.</summary>
public sealed class LocalEditorRequestHandler : RequestHandler
{
    private readonly LocalEditorSchemeFactory factory = new(Path.Combine(AppContext.BaseDirectory, "editor"));

    /// <summary>Allows editor navigation only after native registration and only at the packaged origin.</summary>
    protected override bool OnBeforeBrowse(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, bool userGesture, bool isRedirect)
    {
        return !LocalEditorBrowserProcessHandler.Registration.IsRegistered || !LocalEditorPolicy.IsTrusted(request.Url);
    }

    /// <summary>Disables Chromium's default resource handling except for validated inert inline image data.</summary>
    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
    {
        disableDefaultHandling = !DocumentPolicy.IsAllowedImage(request.Url);
        return new LocalResourceRequestHandler(factory);
    }

    /// <summary>Provides local handlers or explicit errors instead of permitting a DNS/network fallback.</summary>
    private sealed class LocalResourceRequestHandler : ResourceRequestHandler
    {
        private readonly LocalEditorSchemeFactory factory;

        /// <summary>Captures the fixed installed editor directory for one request.</summary>
        public LocalResourceRequestHandler(LocalEditorSchemeFactory factory)
        {
            this.factory = factory;
        }

        /// <summary>Rejects unregistered, external and unsupported resources before any ordinary network loader.</summary>
        protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, IRequestCallback callback)
        {
            if (LocalEditorPolicy.IsTrusted(request.Url))
            {
                return LocalEditorBrowserProcessHandler.Registration.IsRegistered ? CefReturnValue.Continue : CefReturnValue.Cancel;
            }

            return DocumentPolicy.IsAllowedImage(request.Url) ? CefReturnValue.Continue : CefReturnValue.Cancel;
        }

        /// <summary>Always supplies an installed-file/error handler for local HTTPS; only inline raster data uses built-in handling.</summary>
        protected override IResourceHandler GetResourceHandler(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request)
        {
            if (LocalEditorPolicy.IsTrusted(request.Url) && LocalEditorBrowserProcessHandler.Registration.IsRegistered)
            {
                return factory.Create(browser, frame, "https", request);
            }

            if (DocumentPolicy.IsAllowedImage(request.Url))
            {
                return null!;
            }

            return ResourceHandler.ForErrorMessage("Resource unavailable.", HttpStatusCode.Forbidden);
        }
    }
}
