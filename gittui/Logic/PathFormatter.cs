using System.IO;

namespace gittui.Logic;

public static class PathFormatter
{
    public static string FormatGitStatusPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(path);
        var directory = Path.GetDirectoryName(path);

        if (string.IsNullOrEmpty(directory))
        {
            return fileName;
        }

        // Format: filename (.../path/to/parent)
        // We use forward slashes for consistency in display, or keep system separator?
        // Git paths usually use forward slashes. Let's stick to what input gives or normalize.
        // The input path from git status usually has forward slashes.
        
        return $"{fileName} (.../{directory})";
    }
}
