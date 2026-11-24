namespace Guardian.Cli;

internal sealed class InputPathException(string message) : Exception(message);

internal static class InputPath
{
    internal static string ResolveUnderRoot(string path, string root)
    {
        var fullRoot = GetLocalPath(root);
        var fullPath = GetLocalPath(path);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InputPathException("File is outside the input root.");
        }

        var attributes = File.GetAttributes(fullPath);
        for (var directory = Path.GetDirectoryName(fullPath); directory is not null;
             directory = Path.GetDirectoryName(directory))
        {
            attributes |= File.GetAttributes(directory) & FileAttributes.ReparsePoint;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InputPathException("Input paths must not contain symbolic links or reparse points.");
        }
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0)
        {
            throw new InputPathException("Provide a regular input file.");
        }

        return fullPath;
    }

    private static string GetLocalPath(string path)
    {
        if (path.StartsWith("//", StringComparison.Ordinal) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || (!Path.IsPathFullyQualified(path) && path.Contains(':')))
        {
            throw new InputPathException("Provide a local input path.");
        }

        var fullPath = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows()
            && (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.AsSpan(2).Contains(':')
                || new DriveInfo(Path.GetPathRoot(fullPath)!).DriveType == DriveType.Network))
        {
            throw new InputPathException("Provide a local input path.");
        }

        return fullPath;
    }
}
