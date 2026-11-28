namespace StudyDesk.Server.Core.Configuration;

/// <summary>Loads a deliberately selected local environment file without evaluation or interpolation.</summary>
public static class EnvironmentFile
{
    /// <summary>Loads unset process variables from a bounded, explicitly selected file.</summary>
    /// <param name="path">Absolute path supplied by the local server operator.</param>
    /// <exception cref="ArgumentException">The path is not absolute.</exception>
    /// <exception cref="FormatException">A line is not a supported assignment.</exception>
    public static void Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The environment-file path must be absolute.", nameof(path));
        }

        if (new FileInfo(path).Length > 65536)
        {
            throw new FormatException("The environment file exceeds the supported size.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (string original in File.ReadAllLines(path))
        {
            string line = original.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 1)
            {
                throw new FormatException("The environment file contains an invalid assignment.");
            }

            string name = line[..separator].Trim();
            if (name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_')
                || !(name.StartsWith("STUDYDESK_", StringComparison.Ordinal)
                    || name.StartsWith("ASPNETCORE_", StringComparison.Ordinal))
                || !names.Add(name))
            {
                throw new FormatException("The environment file contains an unsupported or duplicate variable name.");
            }

            string value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\''))
            {
                value = value[1..^1];
            }

            if (value.Contains('\0'))
            {
                throw new FormatException("Environment values cannot contain null characters.");
            }

            if (Environment.GetEnvironmentVariable(name) is null)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
