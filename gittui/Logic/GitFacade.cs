using gittui.Abstractions;
using gittui.Models;

namespace gittui.Logic;

internal sealed class GitFacade
{
    private readonly string _repositoryPath;
    private readonly IGitCommandRunner _commandRunner;

    public GitFacade(string repositoryPath, IGitCommandRunner commandRunner)
    {
        _repositoryPath = repositoryPath;
        _commandRunner = commandRunner;
    }

    // Convenience constructor for production use
    public GitFacade(string repositoryPath) : this(repositoryPath, new GitCommandRunner())
    {
    }

    public RepoSnapshot LoadSnapshot()
    {
        var rootName = Path.GetFileName(_repositoryPath.TrimEnd(Path.DirectorySeparatorChar));
        var statusResult = RunGit("status", "--short", "--branch", "-uall");
        if (!statusResult.Success)
        {
            throw new GitCommandException("Unable to read git status.", statusResult);
        }

        var branchLine = statusResult.Stdout.Split('\n').FirstOrDefault(line => line.StartsWith("##"));
        var (currentBranch, ahead, behind) = ParseBranchLine(branchLine);

        var stagedChanges = new List<GitFileChange>();
        var unstagedChanges = new List<GitFileChange>();

        foreach (var rawLine in statusResult.Stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("##"))
            {
                continue;
            }

            if (line.StartsWith("?? "))
            {
                var path = line[3..].Trim();
                unstagedChanges.Add(new GitFileChange(path, '?', '?'));
                continue;
            }

            if (line.Length < 3)
            {
                continue;
            }

            var stagedCode = line[0];
            var worktreeCode = line[1];
            var pathSegment = line.Length > 3 ? line.Substring(3).Trim() : string.Empty;

            if (!string.IsNullOrEmpty(pathSegment))
            {
                if (stagedCode != ' ')
                {
                    stagedChanges.Add(new GitFileChange(pathSegment, stagedCode, worktreeCode));
                }

                if (worktreeCode != ' ')
                {
                    unstagedChanges.Add(new GitFileChange(pathSegment, stagedCode, worktreeCode));
                }
            }
        }

        var branchesResult = RunGit("for-each-ref", "--format=%(refname:short)", "refs/heads");
        if (!branchesResult.Success)
        {
            throw new GitCommandException("Unable to list local branches.", branchesResult);
        }

        var branches = branchesResult.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(name => new BranchInfo(name.Trim(), string.Equals(name.Trim(), currentBranch, StringComparison.Ordinal)))
            .ToList();

        return new RepoSnapshot(_repositoryPath, rootName, currentBranch, branches, stagedChanges, unstagedChanges, ahead, behind);
    }

    public void Stage(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
        {
            return;
        }

        var arguments = new List<string> { "add", "--" };
        arguments.AddRange(pathList);
        var result = RunGit(arguments.ToArray());
        if (!result.Success)
        {
            throw new GitCommandException("Failed to stage files.", result);
        }
    }

    public void Unstage(IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        if (pathList.Count == 0)
        {
            return;
        }

        var arguments = new List<string> { "restore", "--staged", "--" };
        arguments.AddRange(pathList);
        var result = RunGit(arguments.ToArray());
        if (!result.Success)
        {
            throw new GitCommandException("Failed to unstage files.", result);
        }
    }

    public void Checkout(string branch)
    {
        var result = RunGit("checkout", branch);
        if (!result.Success)
        {
            throw new GitCommandException($"Failed to checkout {branch}.", result);
        }
    }

    public void CreateBranch(string branchName)
    {
        var result = RunGit("checkout", "-b", branchName);
        if (!result.Success)
        {
            throw new GitCommandException($"Failed to create branch {branchName}.", result);
        }
    }

    public void Commit(string message)
    {
        var result = RunGit("commit", "-m", message);
        if (!result.Success)
        {
            throw new GitCommandException("Git commit failed.", result);
        }
    }

    public void Pull()
    {
        var result = RunGit("pull");
        if (!result.Success)
        {
            throw new GitCommandException("Git pull failed.", result);
        }
    }

    public void Push()
    {
        var result = RunGit("push");
        if (!result.Success)
        {
            throw new GitCommandException("Git push failed.", result);
        }
    }

    public string GetDiff(string path, DiffScope scope, bool isUntracked = false)
    {
        GitCommandResult result;
        if (isUntracked)
        {
            // Use --no-index to diff against /dev/null (empty) to show the full file content as added
            result = RunGit("diff", "--no-index", "--", "/dev/null", path);
        }
        else if (scope == DiffScope.Staged)
        {
            result = RunGit("diff", "--cached", "--", path);
        }
        else
        {
            result = RunGit("diff", "--", path);
        }

        // git diff --no-index returns 1 if there are differences, which is expected here.
        // But RunGit might throw if exit code is not 0? Let's check RunGit implementation.
        // RunGit returns GitCommandResult, checking Success property.
        // Standard git diff also returns 1 if diffs are found.
        // We need to ensure RunGit doesn't treat exit code 1 as failure for diff commands if we want to be safe,
        // OR we just handle it here.
        // However, looking at existing code:
        // if (!result.Success) { throw ... }
        // This implies RunGit considers exit code 1 as failure?
        // Let's check GitCommandRunner or how RunGit is implemented.
        // Wait, standard `git diff` returns 1 if differences are found.
        // If existing code works, then `RunGit` or `_commandRunner` must be handling this, or `git diff` behaves differently than I recall in this context?
        // Actually `git diff` *does* return 1 if differences are found.
        // Let's assume the existing infrastructure handles it or I should check `GitCommandRunner`.
        // But for now, let's stick to the pattern.
        
        // Correction: `git diff` without --exit-code (default) returns 0 even if there are diffs.
        // `git diff --no-index` DOES return 1 if there are differences.
        // So we might need to handle that.
        
        if (!result.Success && !isUntracked) 
        {
             throw new GitCommandException("Failed to calculate diff.", result);
        }
        else if (isUntracked && result.ExitCode != 0 && result.ExitCode != 1)
        {
             // For no-index, 0 = no diff, 1 = diff found. Anything else is error.
             throw new GitCommandException("Failed to calculate diff.", result);
        }

        if (string.IsNullOrWhiteSpace(result.Stdout))
        {
            return "No diff available.";
        }

        return result.Stdout;
    }

    private GitCommandResult RunGit(params string[] args)
    {
        return _commandRunner.Run(_repositoryPath, args);
    }

    private static (string Branch, int Ahead, int Behind) ParseBranchLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return ("HEAD", 0, 0);
        }

        var branchSegment = line[2..];
        var ahead = 0;
        var behind = 0;
        var branchName = branchSegment;

        var divergenceStart = branchSegment.IndexOf('[');
        if (divergenceStart >= 0)
        {
            branchName = branchSegment[..divergenceStart];
            var divergence = branchSegment[(divergenceStart + 1)..].TrimEnd(']');
            var parts = divergence.Split(',', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                if (part.Contains("ahead", StringComparison.OrdinalIgnoreCase))
                {
                    var amount = new string(part.Where(char.IsDigit).ToArray());
                    _ = int.TryParse(amount, out ahead);
                }

                if (part.Contains("behind", StringComparison.OrdinalIgnoreCase))
                {
                    var amount = new string(part.Where(char.IsDigit).ToArray());
                    _ = int.TryParse(amount, out behind);
                }
            }
        }

        var branchOnly = branchName.Split(new[] { "..." }, StringSplitOptions.None)[0];
        return (branchOnly.Trim(), ahead, behind);
    }
}
