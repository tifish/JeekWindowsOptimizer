using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace JeekWindowsOptimizer;

/// <summary>Fixed cache allowlists, with explicit user environment/configuration overrides.</summary>
internal sealed class DeveloperCachePaths
{
    internal static readonly string[] Kinds = ["Pip", "Npm", "Npx", "Yarn", "Gradle", "GradleDistributions",
        "Maven", "Cargo", "GoBuild", "VsComponentModel", "VsCode", "PackageCache"];
    internal string User { get; }
    internal string Local { get; }
    internal string Roaming { get; }
    internal string Shared { get; }
    private readonly Func<string, string?> _environment;

    internal DeveloperCachePaths() : this(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Environment.GetEnvironmentVariable) { }

    internal DeveloperCachePaths(string user, string local, string roaming, string shared, Func<string, string?> environment)
    { User = user; Local = local; Roaming = roaming; Shared = shared; _environment = environment; }

    private string Override(string variable, string fallback)
        => _environment(variable) is { Length: > 0 } value ? Absolute(value) : fallback;

    private string Absolute(string value)
    {
        value = value.Trim().Trim('"');
        value = value.Replace("${user.home}", User, StringComparison.Ordinal);
        value = Regex.Replace(value, @"\$\{(?:env\.)?([^}]+)\}|%([^%]+)%", match =>
        {
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            return _environment(name) ?? throw new IOException("Unresolved cache path variable: " + name);
        });
        if (!Path.IsPathFullyQualified(value)) throw new IOException("Cache path must be absolute.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    private string[] NpmRoots()
    {
        if (_environment("npm_config_cache") is { Length: > 0 } env) return [Absolute(env)];
        var config = Override("NPM_CONFIG_USERCONFIG", Path.Join(User, ".npmrc"));
        if (File.Exists(config))
        {
            // Read only the cache setting. Never expose registry credentials from .npmrc.
            var match = File.ReadLines(config).Select(line => Regex.Match(line, @"^\s*cache\s*=\s*(.*?)\s*$", RegexOptions.IgnoreCase))
                .LastOrDefault(m => m.Success);
            if (match is not null) return [Absolute(match.Groups[1].Value)];
        }
        return [Path.Join(Local, "npm-cache"), Path.Join(Roaming, "npm-cache")];
    }

    private string MavenRepository()
    {
        var settings = Path.Join(User, ".m2", "settings.xml");
        if (File.Exists(settings))
        {
            using var reader = XmlReader.Create(settings, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var document = XDocument.Load(reader);
            var value = document.Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "localRepository")?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return Absolute(value);
        }
        return Path.Join(User, ".m2", "repository");
    }

    internal IReadOnlyList<string> Resolve(string kind) => kind switch
    {
        "Pip" => [Override("PIP_CACHE_DIR", Path.Join(Local, "pip", "Cache"))],
        "Npm" => NpmRoots().SelectMany(root => new[] { Path.Join(root, "_cacache"), Path.Join(root, "_logs") }).ToArray(),
        "Npx" => NpmRoots().Select(root => Path.Join(root, "_npx")).ToArray(),
        "Yarn" => [Override("YARN_CACHE_FOLDER", Path.Join(Local, "Yarn", "Cache"))],
        "Gradle" => [Path.Join(Override("GRADLE_USER_HOME", Path.Join(User, ".gradle")), "caches")],
        "GradleDistributions" => [Path.Join(Override("GRADLE_USER_HOME", Path.Join(User, ".gradle")), "wrapper", "dists")],
        "Maven" => [MavenRepository()],
        "Cargo" => [Path.Join(Override("CARGO_HOME", Path.Join(User, ".cargo")), "registry", "cache")],
        "GoBuild" => _environment("GOCACHE") == "off" ? [] : [Override("GOCACHE", Path.Join(Local, "go-build"))],
        "VsComponentModel" => VisualStudioCaches(),
        "VsCode" => new[] { "Code", "Code - Insiders" }.SelectMany(product =>
            new[] { "Cache", "Code Cache", "CachedData", "GPUCache" }.Select(cache => Path.Join(Roaming, product, cache))).ToArray(),
        "PackageCache" => [Path.Join(Shared, "Package Cache")],
        _ => throw new ArgumentException("Unknown developer cache", nameof(kind)),
    };

    private string[] VisualStudioCaches()
    {
        var root = Path.Join(Local, "Microsoft", "VisualStudio");
        if (!FileSystemCleaner.IsPlainDirectoryPath(root)) throw new IOException("Visual Studio cache root contains a link.");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root).Where(path => Regex.IsMatch(Path.GetFileName(path), @"^\d+\.\d+(?:_[A-Za-z0-9]+)?(?:Exp)?$"))
            .Select(path => Path.Join(path, "ComponentModelCache")).ToArray();
    }

    internal void Validate(string path, CancellationToken token)
    {
        ValidateRoot(path);
        // Direct directory deletion requires rejecting links throughout the tree.
        NuGetCache.ValidatePath(path, token);
    }

    internal void ValidateRoot(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new IOException("A drive root is not a cache directory.");
        foreach (var protectedTree in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(protectedTree)) continue;
            var tree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(protectedTree));
            if (full.Equals(tree, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(tree + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Cache path is inside an installation or system directory.");
        }
        foreach (var root in new[] { User, Local, Roaming, Shared, Path.Join(User, ".m2"), Path.Join(User, ".cargo"), Path.Join(User, ".gradle") })
            if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Cache path overlaps a protected directory.");
        foreach (var marker in new[] { ".git", ".hg", "package.json", "pyproject.toml", "Cargo.toml", "pom.xml", "build.gradle", "settings.gradle" })
            if (File.Exists(Path.Join(full, marker)) || Directory.Exists(Path.Join(full, marker)))
                throw new IOException("Cache path contains a project root.");
        if (!FileSystemCleaner.IsPlainDirectoryPath(full)) throw new IOException("Cache root contains a link.");
    }
}
