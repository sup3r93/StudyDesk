namespace StudyDesk.Desktop.Editor;

/// <summary>Defines the single trusted origin allowed to use the native editor bridge.</summary>
public static class LocalEditorPolicy
{
    /// <summary>Gets the virtual host handled from packaged local files without a network request.</summary>
    public const string Host = "studydesk.local";

    /// <summary>Gets the installed editor entry point.</summary>
    public const string EntryPoint = "https://studydesk.local/index.html";

    /// <summary>Checks the exact scheme, host and port used for packaged application resources.</summary>
    public static bool IsTrusted(string? address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
            && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo);
    }
}
