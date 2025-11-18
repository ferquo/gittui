using gittui.Abstractions;
using gittui.Logic;
using gittui.Models;
using Moq;

namespace gittui.gittui.tests;

public class GitFacadeTests
{
    private readonly Mock<IGitCommandRunner> _mockRunner;
    private readonly GitFacade _facade;
    private const string RepoPath = "/tmp/testrepo";

    public GitFacadeTests()
    {
        _mockRunner = new Mock<IGitCommandRunner>();
        _facade = new GitFacade(RepoPath, _mockRunner.Object);
    }

    [Fact]
    public void LoadSnapshot_ParsesStatusAndBranchesCorrectly()
    {
        // Arrange
        var statusOutput = """
                           ## main...origin/main [ahead 1, behind 2]
                           M  file1.txt
                           ?? file2.txt

                           """;
        _mockRunner.Setup(r => r.Run(RepoPath, "status", "--short", "--branch"))
            .Returns(new GitCommandResult(0, statusOutput, ""));

        var branchesOutput = """
                             main
                             feature/test

                             """;
        _mockRunner.Setup(r => r.Run(RepoPath, "for-each-ref", "--format=%(refname:short)", "refs/heads"))
            .Returns(new GitCommandResult(0, branchesOutput, ""));

        // Act
        var snapshot = _facade.LoadSnapshot();

        // Assert
        Assert.Equal("main", snapshot.CurrentBranch);
        Assert.Equal(1, snapshot.AheadBy);
        Assert.Equal(2, snapshot.BehindBy);
        
        Assert.Single(snapshot.StagedChanges); // None in the output above actually, wait. 'M ' means staged? No, 'M ' means staged modification.
        // 'M ' -> Staged: M, Worktree: space.
        // '??' -> Untracked.

        // Let's re-verify the status output format handling in GitFacade.
        // line[0] is staged, line[1] is worktree.
        // "M  file1.txt" -> Staged='M', Worktree=' '
        // "?? file2.txt" -> Unstaged='?', Worktree='?' (handled specially)

        Assert.Single(snapshot.StagedChanges);
        Assert.Equal("file1.txt", snapshot.StagedChanges[0].Path);
        Assert.Equal('M', snapshot.StagedChanges[0].StagedCode);

        Assert.Single(snapshot.UnstagedChanges);
        Assert.Equal("file2.txt", snapshot.UnstagedChanges[0].Path);
        
        Assert.Equal(2, snapshot.Branches.Count);
        Assert.Contains(snapshot.Branches, b => b is { Name: "main", IsCurrent: true });
        Assert.Contains(snapshot.Branches, b => b is { Name: "feature/test", IsCurrent: false });
    }

    [Fact]
    public void LoadSnapshot_ThrowsException_WhenStatusFails()
    {
        // Arrange
        _mockRunner.Setup(r => r.Run(RepoPath, "status", "--short", "--branch"))
            .Returns(new GitCommandResult(1, "", "fatal: not a git repository"));

        // Act & Assert
        var ex = Assert.Throws<GitCommandException>(() => _facade.LoadSnapshot());
        Assert.Contains("Unable to read git status", ex.Message);
    }
}
