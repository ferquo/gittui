using Terminal.Gui;
using gittui.Logic;
using gittui.UI;

Application.Init();

try
{
    var repoPath = GitRepositoryLocator.FindRepository(Environment.CurrentDirectory);
    if (repoPath is null)
    {
        MessageBox.ErrorQuery("No Git repository", "This directory (or its parents) does not contain a Git repository.", "Ok");
        return;
    }

    Application.Run(new GitDashboardWindow(repoPath));
}
finally
{
    Application.Shutdown();
}
