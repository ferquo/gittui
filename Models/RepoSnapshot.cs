namespace gittui.Models;

internal sealed class RepoSnapshot
{
    public RepoSnapshot(
        string repositoryPath,
        string repositoryName,
        string currentBranch,
        IReadOnlyList<BranchInfo> branches,
        IReadOnlyList<GitFileChange> stagedChanges,
        IReadOnlyList<GitFileChange> unstagedChanges,
        int aheadBy,
        int behindBy)
    {
        RepositoryPath = repositoryPath;
        RepositoryName = repositoryName;
        CurrentBranch = currentBranch;
        Branches = branches;
        StagedChanges = stagedChanges;
        UnstagedChanges = unstagedChanges;
        AheadBy = aheadBy;
        BehindBy = behindBy;
    }

    public string RepositoryPath { get; }
    public string RepositoryName { get; }
    public string CurrentBranch { get; }
    public IReadOnlyList<BranchInfo> Branches { get; }
    public IReadOnlyList<GitFileChange> StagedChanges { get; }
    public IReadOnlyList<GitFileChange> UnstagedChanges { get; }
    public int AheadBy { get; }
    public int BehindBy { get; }
}
