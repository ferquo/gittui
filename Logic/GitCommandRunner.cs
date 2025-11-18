using System.Diagnostics;
using gittui.Abstractions;
using gittui.Models;

namespace gittui.Logic;

internal sealed class GitCommandRunner : IGitCommandRunner
{
    public GitCommandResult Run(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in args)
        {
            psi.ArgumentList.Add(argument);
        }

        var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Unable to start git process.");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new GitCommandResult(process.ExitCode, stdout, stderr);
    }
}
