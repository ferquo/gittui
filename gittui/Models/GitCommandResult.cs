namespace gittui.Models;

internal readonly record struct GitCommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}
