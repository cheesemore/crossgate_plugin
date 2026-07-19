using Mono.Cecil;

namespace CrossgateMod.Patcher;

internal sealed class HotfixAssemblyResolver : DefaultAssemblyResolver
{
    /// <summary>hotfix 元数据程序集名 → hotfixdata 内实际 .dll.bytes 文件名。</summary>
    private static readonly Dictionary<string, string> HotfixDataAssemblyAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["UnityEngine.CoreModule"] = "unityengine.dll.bytes",
            ["UnityEngine"] = "unityengine.dll.bytes",
        };

    private readonly string _hotfixDataDir;
    private readonly string[] _stubDirs;
    private readonly ReaderParameters _readerParameters;
    /// <summary>读取 hotfixdata / stub 依赖时不挂自定义 Resolver，避免 Unity 元数据互引用栈溢出。</summary>
    private readonly ReaderParameters _dependencyReaderParameters;
    private readonly Dictionary<string, AssemblyDefinition> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _resolving = new(StringComparer.OrdinalIgnoreCase);

    public HotfixAssemblyResolver(string hotfixDataDir)
    {
        _hotfixDataDir = hotfixDataDir;
        _stubDirs = Program.ResolveRefStubDirsPublic()
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _readerParameters = new ReaderParameters
        {
            AssemblyResolver = this,
            InMemory = true,
        };
        _dependencyReaderParameters = new ReaderParameters
        {
            InMemory = true,
            ReadingMode = ReadingMode.Deferred,
        };
        AddSearchDirectory(hotfixDataDir);
        foreach (var stubDir in _stubDirs)
        {
            AddSearchDirectory(stubDir);
        }
    }

    public override AssemblyDefinition Resolve(AssemblyNameReference name)
    {
        if (_cache.TryGetValue(name.Name, out var cached))
        {
            return cached;
        }

        if (!_resolving.Add(name.Name))
        {
            throw new AssemblyResolutionException(name);
        }

        try
        {
            AssemblyDefinition? resolved = null;
            if (name.Name.StartsWith("UnityEngine", StringComparison.Ordinal)
                || name.Name.StartsWith("Unity.", StringComparison.Ordinal))
            {
                resolved = TryReadFromStubDirs(name.Name);
            }

            resolved ??= TryReadFromHotfixData(name.Name)
                ?? TryReadAlias(name.Name)
                ?? TryReadFromStubDirs(name.Name);

            resolved ??= base.Resolve(name);
            _cache[name.Name] = resolved;
            return resolved;
        }
        finally
        {
            _resolving.Remove(name.Name);
        }
    }

    private AssemblyDefinition? TryReadAlias(string assemblyName)
    {
        if (!HotfixDataAssemblyAliases.TryGetValue(assemblyName, out var aliasFile))
        {
            return null;
        }

        return TryReadBytesFile(aliasFile);
    }

    private AssemblyDefinition? TryReadFromStubDirs(string assemblyName)
    {
        foreach (var stubDir in _stubDirs)
        {
            var dllPath = Path.Combine(stubDir, assemblyName + ".dll");
            if (!File.Exists(dllPath))
            {
                continue;
            }

            try
            {
                return AssemblyDefinition.ReadAssembly(dllPath, _dependencyReaderParameters);
            }
            catch (Exception ex) when (ex is AssemblyResolutionException or BadImageFormatException)
            {
                continue;
            }
        }

        return null;
    }

    private AssemblyDefinition? TryReadFromHotfixData(string assemblyName)
    {
        var bytesPath = Path.Combine(_hotfixDataDir, assemblyName + ".dll.bytes");
        if (File.Exists(bytesPath))
        {
            return ReadDependencyAssembly(bytesPath);
        }

        var dllPath = Path.Combine(_hotfixDataDir, assemblyName + ".dll");
        if (File.Exists(dllPath))
        {
            return ReadDependencyAssembly(dllPath);
        }

        return null;
    }

    private AssemblyDefinition? TryReadBytesFile(string fileName)
    {
        var path = Path.Combine(_hotfixDataDir, fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        return ReadDependencyAssembly(path);
    }

    private AssemblyDefinition? ReadDependencyAssembly(string path)
    {
        try
        {
            return AssemblyDefinition.ReadAssembly(path, _dependencyReaderParameters);
        }
        catch (Exception ex) when (ex is AssemblyResolutionException or BadImageFormatException)
        {
            return null;
        }
    }
}
