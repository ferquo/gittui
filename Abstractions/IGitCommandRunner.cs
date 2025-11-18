using gittui.Models;

namespace gittui.Abstractions;

internal interface IGitCommandRunner
{
    GitCommandResult Run(string workingDirectory, params string[] args);
}
