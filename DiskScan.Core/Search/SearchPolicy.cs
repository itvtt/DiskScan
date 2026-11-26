namespace DiskScan.Core;

/// <summary>Central eligibility policy for search and search-result actions.</summary>
public static class SearchPolicy
{
    // FILE_ATTRIBUTE_RECALL_ON_OPEN, FILE_ATTRIBUTE_UNPINNED, and
    // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS. The latter two values are not
    // available under every target framework as named FileAttributes values.
    private const FileAttributes CloudOrRecallAttributes =
        (FileAttributes)0x00040000 |
        (FileAttributes)0x00100000 |
        (FileAttributes)0x00400000;

    private const FileAttributes ExcludedAttributes =
        FileAttributes.System |
        FileAttributes.ReparsePoint |
        FileAttributes.Offline |
        CloudOrRecallAttributes;

    private const FileAttributes UnreadableAttributes =
        FileAttributes.ReparsePoint |
        FileAttributes.Offline |
        CloudOrRecallAttributes;

    private static readonly HashSet<string> ProtectedRootDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "Recovery", "$WinREAgent",
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData",
        "PerfLogs", "Config.Msi", "MSOCache", "Documents and Settings", "EFI", "Boot", "ESD",
        "$WINDOWS.~BT", "$Windows.~WS"
    };

    private static readonly HashSet<string> ProtectedRootFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp",
        "bootmgr", "BOOTNXT", "ntldr", "ntdetect.com", "bootstat.dat"
    };

    // Volume-root entries that are never useful in a name search (recycle bin
    // internals, restore points, setup leftovers). NTFS metadata files start with $.
    private static readonly HashSet<string> HiddenRootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Config.Msi",
        "$WINDOWS.~BT", "$Windows.~WS", "Recovery", "Documents and Settings"
    };

    // Folders whose contents are usually build output, caches or tool data. They
    // stay searchable but are ranked after the user's own files.
    private static readonly HashSet<string> NoisyDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".svn", ".hg", ".vs", ".idea", "__pycache__",
        "packages", ".nuget", ".cache", "AppData", "Temp", "tmp", "$Recycle.Bin",
        "bower_components", "site-packages", ".venv"
    };

    // Downloaded libraries and version-control internals: found by name, but their
    // contents are not read unless the search starts inside one (as VS Code excludes
    // node_modules and .git from search by default).
    private static readonly HashSet<string> DependencyDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bower_components", ".git", ".svn", ".hg", "__pycache__", "site-packages", ".venv"
    };

    private static readonly string[] KnownOperatingSystemRoots = GetKnownOperatingSystemRoots();

    /// <summary>
    /// True when the item's contents must not be read: system files, protected
    /// operating-system folders, links and cloud/offline placeholders (or anything below them).
    /// </summary>
    public static bool IsExcluded(ScanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        // FullPath covers protected directory ancestry without walking and
        // normalizing every parent path for every file.
        if (IsProtectedPath(node.FullPath))
            return true;

        // Scanner trees are shallow. The high depth bound avoids allocating a
        // per-file visited set while still failing closed on a corrupt cycle.
        const int maximumParentDepth = 4096;
        ScanNode? current = node;
        for (int depth = 0; current is not null && depth < maximumParentDepth; depth++, current = current.Parent)
        {
            // Windows marks every volume root System/Hidden; that describes the
            // mount itself, not the files stored on the volume.
            if (!IsVolumeRoot(current) && (current.Attributes & ExcludedAttributes) != 0)
                return true;
        }

        return current is not null;
    }

    /// <summary>
    /// True when the item is left out of name search: system-attribute items (or
    /// anything below one) and recycle-bin/restore-point internals at a volume root.
    /// Operating-system folders such as Program Files stay findable by name.
    /// </summary>
    public static bool IsHiddenFromNameSearch(ScanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return (new EligibilityCache().Get(node) & Eligibility.HiddenFromNames) != 0;
    }

    /// <summary>True for Windows, Program Files, ProgramData and similar volume-root system folders.</summary>
    public static bool IsOperatingSystemPath(string? path) => IsProtectedPath(path);

    [Flags]
    internal enum Eligibility : byte
    {
        None = 0,
        HiddenFromNames = 1,
        ContentExcluded = 2,
        OperatingSystem = 4,
        Noisy = 8,
        Dependency = 16
    }

    /// <summary>
    /// Computes eligibility for many nodes of one tree, evaluating each directory
    /// once. Not thread-safe; fill it on one thread and read the results anywhere.
    /// </summary>
    internal sealed class EligibilityCache
    {
        private const int MaximumDepth = 4096;
        private readonly Dictionary<ScanNode, Eligibility> _directories = new(ReferenceEqualityComparer.Instance);

        public Eligibility Get(ScanNode node) => Get(node, 0);

        private Eligibility Get(ScanNode node, int depth)
        {
            if (node.IsDirectory && _directories.TryGetValue(node, out Eligibility cached))
                return cached;

            Eligibility result;
            if (depth > MaximumDepth)
            {
                // A corrupt parent cycle fails closed.
                result = Eligibility.HiddenFromNames | Eligibility.ContentExcluded;
            }
            else if (node.Parent is null)
            {
                result = FromFullPath(node);
            }
            else
            {
                Eligibility parent = Get(node.Parent, depth + 1);
                result = parent | FromAttributes(node);
                if (IsVolumeRoot(node.Parent))
                    result |= FromRootLevelName(node);
                else if (node.IsDirectory && IsKnownOperatingSystemRoot(node.FullPath))
                    result |= Eligibility.ContentExcluded | Eligibility.OperatingSystem;
                if (node.IsDirectory && NoisyDirectoryNames.Contains(node.Name))
                    result |= Eligibility.Noisy;
                if (node.IsDirectory && DependencyDirectoryNames.Contains(node.Name))
                    result |= Eligibility.Dependency;
            }

            if (node.IsDirectory)
                _directories[node] = result;
            return result;
        }

        private static Eligibility FromAttributes(ScanNode node)
        {
            if (IsVolumeRoot(node))
                return Eligibility.None;
            Eligibility result = Eligibility.None;
            if ((node.Attributes & FileAttributes.System) != 0)
                result |= Eligibility.HiddenFromNames | Eligibility.ContentExcluded;
            if ((node.Attributes & UnreadableAttributes) != 0)
                result |= Eligibility.ContentExcluded;
            return result;
        }

        private static Eligibility FromRootLevelName(ScanNode node)
        {
            Eligibility result = Eligibility.None;
            string name = node.Name;
            if (node.IsDirectory ? ProtectedRootDirectoryNames.Contains(name) : ProtectedRootFileNames.Contains(name))
                result |= Eligibility.ContentExcluded | Eligibility.OperatingSystem;
            if (HiddenRootNames.Contains(name) || name.StartsWith('$'))
                result |= Eligibility.HiddenFromNames | Eligibility.ContentExcluded;
            return result;
        }

        private static Eligibility FromFullPath(ScanNode node)
        {
            Eligibility result = FromAttributes(node);
            string path = node.FullPath;
            if (IsProtectedPath(path))
                result |= Eligibility.ContentExcluded | Eligibility.OperatingSystem;

            string root;
            try { root = Path.GetPathRoot(path) ?? string.Empty; }
            catch { root = string.Empty; }
            if (root.Length == 0 || path.Length <= root.Length)
                return result;

            string[] segments = path[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 0 && (HiddenRootNames.Contains(segments[0]) || segments[0].StartsWith('$')))
                result |= Eligibility.HiddenFromNames | Eligibility.ContentExcluded;
            int directorySegments = node.IsDirectory ? segments.Length : segments.Length - 1;
            for (int index = 0; index < directorySegments; index++)
            {
                if (NoisyDirectoryNames.Contains(segments[index]))
                    result |= Eligibility.Noisy;
                if (DependencyDirectoryNames.Contains(segments[index]))
                    result |= Eligibility.Dependency;
            }
            return result;
        }
    }

    private static bool IsVolumeRoot(ScanNode node)
    {
        string path = node.FullPath;
        if (string.IsNullOrEmpty(path))
            return false;
        // Fast path: drive roots are "C:\" or "C:"; only UNC paths need the full check.
        if (path.Length > 3 && !path.StartsWith(@"\\", StringComparison.Ordinal))
            return false;
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) &&
                   string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsKnownOperatingSystemRoot(string path)
    {
        foreach (string root in KnownOperatingSystemRoots)
        {
            if (path.Length == root.Length && path.Equals(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsProtectedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string normalized = NormalizePath(path);
        foreach (string protectedRoot in KnownOperatingSystemRoots)
        {
            if (IsSameOrDescendant(normalized, protectedRoot))
                return true;
        }

        string root;
        try
        {
            root = Path.GetPathRoot(normalized) ?? string.Empty;
        }
        catch
        {
            return false;
        }

        if (root.Length == 0 || normalized.Length <= root.Length)
            return false;

        string relative = normalized[root.Length..]
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Length == 0)
            return false;

        int separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        string firstSegment = separator < 0 ? relative : relative[..separator];

        // These directory names are special only immediately below a volume/share
        // root. A user folder such as Documents\Windows remains searchable.
        if (ProtectedRootDirectoryNames.Contains(firstSegment))
            return true;

        return separator < 0 && ProtectedRootFileNames.Contains(firstSegment);
    }

    private static string[] GetKnownOperatingSystemRoots()
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData
        ];

        return folders
            .Select(Environment.GetFolderPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path.Length <= root.Length)
            return false;

        char boundary = path[root.Length];
        return boundary == Path.DirectorySeparatorChar || boundary == Path.AltDirectorySeparatorChar;
    }
}
