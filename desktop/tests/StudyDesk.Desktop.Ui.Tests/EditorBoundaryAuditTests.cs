using System.Reflection;
using CefSharp;
using StudyDesk.Desktop.Editor;
using Xunit;

namespace StudyDesk.Desktop.Ui.Tests;

/// <summary>Checks the reviewed pre-subscriber envelope boundary and fail-closed resource loader.</summary>
public sealed class EditorBoundaryAuditTests
{
    /// <summary>Rejects excess characters and excess UTF-8 bytes before a subscriber can parse or queue an envelope.</summary>
    [Fact]
    public void EnvelopeBoundsCoverBothCharacterAndEncodedSize()
    {
        Assert.False(EditorMessagePolicy.IsAllowed(null));
        Assert.False(EditorMessagePolicy.IsAllowed(string.Empty));
        Assert.True(EditorMessagePolicy.IsAllowed(new string('x', EditorMessagePolicy.MaximumMessageBytes)));
        Assert.False(EditorMessagePolicy.IsAllowed(new string('x', EditorMessagePolicy.MaximumMessageCharacters + 1)));
        Assert.False(EditorMessagePolicy.IsAllowed(new string('é', (EditorMessagePolicy.MaximumMessageBytes / 2) + 1)));
    }

    /// <summary>A native rejection is terminal and cannot later become a successful registration.</summary>
    [Fact]
    public async Task RejectedFactoryRegistrationNeverBecomesReady()
    {
        EditorResourceRegistration registration = new();
        registration.Complete(false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.WaitAsync());
        Assert.False(registration.IsRegistered);
        registration.Complete(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registration.WaitAsync());
        Assert.False(registration.IsRegistered);
    }

    /// <summary>Readiness is not granted before the native registration call reports success.</summary>
    [Fact]
    public async Task FactoryReadinessWaitsForTheNativeResult()
    {
        EditorResourceRegistration registration = new();
        Task pending = registration.WaitAsync();
        Assert.False(pending.IsCompleted);
        Assert.False(registration.IsRegistered);
        registration.Complete(true);
        await pending;
        Assert.True(registration.IsRegistered);
    }

    /// <summary>Local HTTPS cannot use Chromium's default network loader, even without a registered global factory.</summary>
    [Theory]
    [InlineData("https://studydesk.local/index.html")]
    [InlineData("https://outside.example.invalid/file")]
    public void MissingRegistrationCannotFallBackToNetwork(string address)
    {
        IRequest request = DispatchProxy.Create<IRequest, RequestProxy>();
        ((RequestProxy)(object)request).Address = address;
        IRequestHandler handler = new LocalEditorRequestHandler();
        bool disableDefault = false;
        IResourceRequestHandler resources = handler.GetResourceRequestHandler(null!, null!, null!, request, true, false, string.Empty, ref disableDefault);
        Assert.True(disableDefault);
        Assert.Equal(CefReturnValue.Cancel, resources.OnBeforeResourceLoad(null!, null!, null!, request, null!));
        using IResourceHandler error = resources.GetResourceHandler(null!, null!, null!, request);
        Assert.NotNull(error);
    }

    /// <summary>Supplies only the request fields read by the real handler; no network or native browser is started.</summary>
    public class RequestProxy : DispatchProxy
    {
        /// <summary>Gets or sets the synthetic requested URL.</summary>
        public string Address { get; set; } = string.Empty;

        /// <summary>Returns only the managed URL/method needed by resource-policy checks.</summary>
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod?.Name switch
            {
                "get_Url" => Address,
                "get_Method" => "GET",
                _ => null,
            };
        }
    }
}
