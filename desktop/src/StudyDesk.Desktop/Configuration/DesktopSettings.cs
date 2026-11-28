using System.IO;
using System.Text;

namespace StudyDesk.Desktop.Configuration;

/// <summary>Loads explicitly scoped desktop configuration from the user's private .env file.</summary>
public sealed class DesktopSettings
{
    private readonly string environmentFile;
    /// <summary>Creates validated settings after reading private configuration.</summary>
    private DesktopSettings(string dataDirectory, string tokenFile, string connectionFile, bool allowInsecureLoopback, string language, string environmentFile)
    {
        DataDirectory = dataDirectory;
        TokenFile = tokenFile;
        ConnectionFile = connectionFile;
        AllowInsecureLoopback = allowInsecureLoopback;
        Language = language;
        this.environmentFile = environmentFile;
    }

    /// <summary>Gets the absolute private location of notes, recovery data and browser files.</summary>
    public string DataDirectory { get; }

    /// <summary>Gets the file containing a Windows-user-protected association token.</summary>
    public string TokenFile { get; }

    /// <summary>Gets the file containing non-secret server association metadata.</summary>
    public string ConnectionFile { get; }

    /// <summary>Gets whether explicit loopback-only HTTP development connections are allowed.</summary>
    public bool AllowInsecureLoopback { get; }

    /// <summary>Gets the persisted preference: auto, it or en.</summary>
    public string Language { get; private set; }

    /// <summary>Resolves the automatic preference without changing the operating system's culture.</summary>
    public string EffectiveLanguage => Language == "auto"
        ? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en"
        : Language;

    /// <summary>Atomically updates only the local language entry while preserving every other private setting.</summary>
    public void SaveLanguagePreference(string language)
    {
        ValidateLanguage(language);
        PrivateDirectoryPolicy.ValidateExistingFile(environmentFile);
        string[] lines = File.ReadAllLines(environmentFile);
        bool found = false;
        for (int index = 0; index < lines.Length; index++)
        {
            int separator = lines[index].IndexOf('=');
            if (separator > 0 && lines[index][..separator].Trim() == "STUDYDESK_DESKTOP_LANGUAGE")
            {
                lines[index] = "STUDYDESK_DESKTOP_LANGUAGE=" + language;
                found = true;
            }
        }

        if (!found)
        {
            lines = [.. lines, "STUDYDESK_DESKTOP_LANGUAGE=" + language];
        }

        string temporary = environmentFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temporary, environmentFile, true);
            Language = language;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>Rejects unsupported preferences before any setting or interface is changed.</summary>
    private static void ValidateLanguage(string language)
    {
        if (language is not ("auto" or "it" or "en"))
        {
            throw new ArgumentException("The desktop language must be auto, it or en.", nameof(language));
        }
    }

    /// <summary>Creates a private first-run .env file and applies process environment overrides.</summary>
    public static DesktopSettings Load()
    {
        string privateDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudyDesk");
        string envPath = Environment.GetEnvironmentVariable("STUDYDESK_DESKTOP_ENV_FILE") ?? Path.Combine(privateDirectory, ".env");
        if (!Path.IsPathFullyQualified(envPath))
        {
            throw new InvalidOperationException("The desktop environment file path must be absolute.");
        }

        privateDirectory = Path.GetDirectoryName(envPath) ?? throw new InvalidOperationException("An environment directory is required.");
        PrivateDirectoryPolicy.Ensure(privateDirectory);
        if (!File.Exists(envPath))
        {
            string text = "# StudyDesk private desktop configuration. Do not publish this file.\n"
                + "STUDYDESK_DESKTOP_DATA_DIR=" + Path.Combine(privateDirectory, "workspace") + "\n"
                + "STUDYDESK_DESKTOP_TOKEN_FILE=" + Path.Combine(privateDirectory, "device-token.bin") + "\n"
                + "STUDYDESK_DESKTOP_CONNECTION_FILE=" + Path.Combine(privateDirectory, "connection.json") + "\n"
                + "STUDYDESK_DESKTOP_ALLOW_INSECURE_LOOPBACK=false\n"
                + "STUDYDESK_DESKTOP_LANGUAGE=auto\n";
            using FileStream stream = new(envPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using StreamWriter writer = new(stream, new UTF8Encoding(false));
            writer.Write(text);
        }

        PrivateDirectoryPolicy.ValidateExistingFile(envPath);
        Dictionary<string, string> fileValues = ReadEnvironmentFile(envPath);
        string data = ReadSetting("STUDYDESK_DESKTOP_DATA_DIR", fileValues);
        string token = ReadSetting("STUDYDESK_DESKTOP_TOKEN_FILE", fileValues);
        string connection = ReadSetting("STUDYDESK_DESKTOP_CONNECTION_FILE", fileValues);
        string language = Environment.GetEnvironmentVariable("STUDYDESK_DESKTOP_LANGUAGE")
            ?? fileValues.GetValueOrDefault("STUDYDESK_DESKTOP_LANGUAGE", "auto");
        ValidateLanguage(language);
        bool insecure = bool.TryParse(ReadSetting("STUDYDESK_DESKTOP_ALLOW_INSECURE_LOOPBACK", fileValues), out bool allowed) && allowed;
        foreach (string path in new[] { data, token, connection })
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidOperationException("Desktop paths must be absolute.");
            }
        }

        PrivateDirectoryPolicy.Ensure(data);
        PrivateDirectoryPolicy.Ensure(Path.GetDirectoryName(token) ?? throw new InvalidOperationException("A token directory is required."));
        PrivateDirectoryPolicy.Ensure(Path.GetDirectoryName(connection) ?? throw new InvalidOperationException("A connection directory is required."));
        foreach (string existing in new[] { token, connection, Path.Combine(data, "workspace.sqlite3"), Path.Combine(data, "workspace.sqlite3-wal"), Path.Combine(data, "workspace.sqlite3-shm"), Path.Combine(data, "editor-startup.json") })
        {
            PrivateDirectoryPolicy.ValidateExistingFile(existing);
        }

        return new DesktopSettings(Path.GetFullPath(data), Path.GetFullPath(token), Path.GetFullPath(connection), insecure, language, Path.GetFullPath(envPath));
    }

    /// <summary>Parses literal key/value settings without executing interpolation or shell syntax.</summary>
    private static Dictionary<string, string> ReadEnvironmentFile(string path)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new InvalidOperationException("The desktop environment file contains an invalid setting.");
            }

            string key = line[..separator].Trim();
            if (!key.StartsWith("STUDYDESK_DESKTOP_", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The desktop environment file contains an unsupported setting.");
            }

            string value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            if (!values.TryAdd(key, value))
            {
                throw new InvalidOperationException("The desktop environment file contains a duplicate setting.");
            }
        }

        return values;
    }

    /// <summary>Reads an environment override or the corresponding required private file entry.</summary>
    private static string ReadSetting(string name, Dictionary<string, string> values)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) && !values.TryGetValue(name, out value))
        {
            throw new InvalidOperationException("A required desktop environment setting is missing.");
        }

        return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidOperationException("A required desktop environment setting is empty.");
    }
}
