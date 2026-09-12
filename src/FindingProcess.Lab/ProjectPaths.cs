namespace FindingProcess.Lab;

internal static class ProjectPaths
{
    internal static string Root { get; } = FindRoot();
    internal static string Artifacts => Path.Combine(Root, "artifacts");

    internal static bool IsWithin(string path, string directory) => Path.GetFullPath(path)
        .StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string FindRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                    File.Exists(Path.Combine(directory.FullName, "src", "FindingProcess.Lab", "FindingProcess.Lab.csproj")))
                    return directory.FullName;
        throw new DirectoryNotFoundException("Finding Process repository was not found.");
    }
}
