using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;

namespace Touchline.Api;

/// <summary>
/// Reads KEY=VALUE pairs from the nearest <c>.env</c> file into configuration, so
/// <c>ConnectionStrings__Touchline</c> becomes <c>ConnectionStrings:Touchline</c>.
/// Local development only: hosting sets real environment variables, and those always win.
/// </summary>
public static class DotEnv
{
    /// <summary>
    /// Adds the nearest <c>.env</c> (searching up from the current directory) as a configuration
    /// source just below environment variables: it overrides appsettings and user-secrets, and
    /// real environment variables and command-line arguments override it.
    /// </summary>
    public static void AddDotEnv(this IConfigurationManager configuration, string? startDirectory = null)
    {
        var path = Find(startDirectory ?? Directory.GetCurrentDirectory());
        if (path is null)
        {
            return;
        }

        var source = new MemoryConfigurationSource { InitialData = Parse(File.ReadAllLines(path)) };
        var envIndex = configuration.Sources
            .Select((s, i) => (s, i))
            .Where(x => x.s is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
            .Select(x => (int?)x.i)
            .FirstOrDefault();

        if (envIndex is { } i)
            configuration.Sources.Insert(i, source);
        else
            configuration.Sources.Add(source);
    }

    public static Dictionary<string, string?> Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith("export "))
                line = line["export ".Length..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim().Replace("__", ConfigurationPath.KeyDelimiter);
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            values[key] = value;
        }
        return values;
    }

    private static string? Find(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, ".env");
            if (File.Exists(path))
                return path;
        }
        return null;
    }
}
