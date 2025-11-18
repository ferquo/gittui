namespace gittui.Models;

internal sealed class BranchInfo
{
    public BranchInfo(string name, bool isCurrent)
    {
        Name = name;
        IsCurrent = isCurrent;
    }

    public string Name { get; }
    public bool IsCurrent { get; }
}
