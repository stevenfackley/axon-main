namespace Axon.Infrastructure.Configuration;

/// <summary>
/// Minimal, dependency-free <c>.env</c> file parser/loader.
///
/// .NET does not read <c>.env</c> files natively; this fills that gap for local
/// development and self-contained desktop deployments where vendor OAuth credentials
/// live in a gitignored <c>.env</c> rather than a secrets manager.
///
/// Supported syntax:
///   • <c>KEY=VALUE</c> pairs (value may contain '=' — only the first '=' splits)
///   • blank lines and <c># comment</c> lines are ignored
///   • surrounding single or double quotes are stripped from the value
///   • an optional leading <c>export </c> prefix is stripped (shell-style files)
///   • CRLF and LF line endings both work
///
/// AOT-safe: pure string processing, no reflection, no JSON.
/// </summary>
public static class DotEnvFile
{
    /// <summary>Parses <c>.env</c> file <paramref name="content"/> into a key/value map.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue; // no '=', or empty key → not a pair

            var key = line[..eq].Trim();
            if (key.StartsWith("export ", StringComparison.Ordinal))
                key = key["export ".Length..].Trim();
            if (key.Length == 0) continue;

            var value = line[(eq + 1)..].Trim();
            value = StripMatchingQuotes(value);

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// Reads <paramref name="path"/> if it exists and applies each pair to the process
    /// environment via <see cref="Environment.SetEnvironmentVariable(string, string?)"/>.
    /// Existing environment variables are NOT overwritten (real env wins over the file).
    /// No-op when the file is absent.
    /// </summary>
    public static void Load(string path)
    {
        if (!File.Exists(path)) return;

        foreach (var (key, value) in Parse(File.ReadAllText(path)))
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static string StripMatchingQuotes(string value)
    {
        if (value.Length >= 2 &&
            (value[0] == '"' || value[0] == '\'') &&
            value[^1] == value[0])
        {
            return value[1..^1];
        }
        return value;
    }
}
