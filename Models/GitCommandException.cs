namespace gittui.Models;

internal sealed class GitCommandException : Exception
{
    public GitCommandException(string message, GitCommandResult result)
        : base(BuildMessage(message, result))
    {
        Result = result;
    }

    public GitCommandResult Result { get; }

    private static string BuildMessage(string message, GitCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Stderr))
        {
            return message;
        }

        return $"{message}{Environment.NewLine}{result.Stderr.Trim()}";
    }
}
