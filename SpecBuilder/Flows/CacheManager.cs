using System.Text;
using System.Text.Json;

namespace SpecBuilder.Flows;

/// <summary>
/// Incremental per-hash caching for pipeline flows.
/// Cache key = SHA256 hash of source files (Steps 1-2) or AST (Step 3-4)
/// Invalidation = automatic on hash change, safe fallback on errors
/// </summary>
internal sealed class CacheManager
{
    private readonly string _cacheRoot;

    public CacheManager(string workspaceRoot)
    {
        _cacheRoot = Path.Combine(workspaceRoot, "generated", "pipeline-cache");
        Directory.CreateDirectory(_cacheRoot);
    }

    /// <summary>
    /// Hash all source files in workspace (for Steps 1-2 cache key)
    /// </summary>
    public string ComputeSourceFilesHash(string workspaceRoot)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var sourceFiles = Directory.GetFiles(workspaceRoot, "*.*", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\") && !f.Contains("\\.") && !f.Contains("\\generated\\"))
            .OrderBy(f => f)
            .ToList();

        var hashInput = new StringBuilder();
        foreach (var file in sourceFiles)
        {
            try
            {
                var content = File.ReadAllBytes(file);
                hashInput.Append(file).Append(':').Append(Convert.ToHexString(sha256.ComputeHash(content))).Append('|');
            }
            catch { }
        }

        var combined = sha256.ComputeHash(Encoding.UTF8.GetBytes(hashInput.ToString()));
        return Convert.ToHexString(combined);
    }

    /// <summary>
    /// Hash AST content (for Step 3-4 cache key)
    /// </summary>
    public string ComputeAstHash(string astJson)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(astJson));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Get cache directory for step + hash
    /// </summary>
    public string GetCachePath(int step, string hash) => Path.GetFullPath(Path.Combine(_cacheRoot, $"step{step}", hash));

    /// <summary>
    /// Load cached file content
    /// </summary>
    public string? LoadCachedFile(int step, string hash, string filename)
    {
        var path = Path.Combine(GetCachePath(step, hash), filename);
        if (!File.Exists(path)) return null;
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    /// <summary>
    /// Save file to cache
    /// </summary>
    public void SaveCachedFile(int step, string hash, string filename, string content)
    {
        try
        {
            var dir = GetCachePath(step, hash);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, filename), content);
            File.WriteAllText(Path.Combine(dir, ".version"), GetVersionMarker());
        }
        catch { }
    }

    /// <summary>
    /// Load cached JSON object
    /// </summary>
    public T? LoadCachedJson<T>(int step, string hash, string filename)
    {
        var json = LoadCachedFile(step, hash, filename);
        if (json is null) return default;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch { return default; }
    }

    /// <summary>
    /// Save object as cached JSON
    /// </summary>
    public void SaveCachedJson<T>(int step, string hash, string filename, T obj)
    {
        try
        {
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false });
            SaveCachedFile(step, hash, filename, json);
        }
        catch { }
    }

    /// <summary>
    /// Invalidate cache if version changed
    /// </summary>
    public void InvalidateIfVersionMismatch(int step, string hash)
    {
        var cachePath = GetCachePath(step, hash);
        if (!Directory.Exists(cachePath)) return;

        var versionFile = Path.Combine(cachePath, ".version");
        if (!File.Exists(versionFile))
        {
            Directory.Delete(cachePath, recursive: true);
            return;
        }

        try
        {
            var cached = File.ReadAllText(versionFile).Trim();
            var current = GetVersionMarker();
            if (cached != current)
            {
                Directory.Delete(cachePath, recursive: true);
            }
        }
        catch { }
    }

    private static string GetVersionMarker()
    {
        var version = typeof(CacheManager).Assembly.GetName().Version;
        return $"{version?.Major}.{version?.Minor}.{version?.Build}";
    }
}
