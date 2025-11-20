using gittui.Logic;
using Xunit;

namespace gittui.tests;

public class PathFormatterTests
{
    [Fact]
    public void FormatGitStatusPath_ReturnsEmpty_WhenPathIsEmpty()
    {
        Assert.Equal(string.Empty, PathFormatter.FormatGitStatusPath(""));
    }

    [Fact]
    public void FormatGitStatusPath_ReturnsFilename_WhenPathHasNoDirectory()
    {
        Assert.Equal("file.txt", PathFormatter.FormatGitStatusPath("file.txt"));
    }

    [Fact]
    public void FormatGitStatusPath_ReturnsFormattedString_WhenPathHasDirectory()
    {
        var input = "path/to/file.txt";
        // Path.GetDirectoryName might return backslashes on Windows, but we want to verify the structure.
        // The implementation uses Path.GetDirectoryName which uses OS separator.
        // Let's check what the implementation does. It just interpolates.
        
        var expected = $"file.txt (.../{Path.GetDirectoryName(input)})";
        Assert.Equal(expected, PathFormatter.FormatGitStatusPath(input));
    }

    [Fact]
    public void FormatGitStatusPath_HandlesLongPaths()
    {
        var input = "very/long/path/structure/that/goes/deep/file.cs";
        var expected = $"file.cs (.../{Path.GetDirectoryName(input)})";
        Assert.Equal(expected, PathFormatter.FormatGitStatusPath(input));
    }
}
