using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Terminal.Gui;
using gittui.Logic;
using gittui.Models;
using GuiColor = Terminal.Gui.Color;
using GuiAttribute = Terminal.Gui.Attribute;

namespace gittui.UI;

internal sealed class GitDashboardWindow : Window
{
    private readonly GitFacade _git;
    private Label _repoLabel = null!;
    private Label _divergenceLabel = null!;
    private ComboBox _branchSelector = null!;
    private Button _pullButton = null!;
    private Button _pushButton = null!;
    private ListView _stagedList = null!;
    private ListView _unstagedList = null!;
    private ListView _diffList = null!;
    private Label _diffCaption = null!;
    private Label _refreshLabel = null!;
    private Button _stageButton = null!;
    private Button _unstageButton = null!;
    private readonly StatusBar _statusBar;
    private readonly Label _statusMessage;
    private readonly ObservableCollection<string> _stagedDisplay = new();
    private readonly ObservableCollection<string> _unstagedDisplay = new();
    private readonly List<string> _diffDisplay = new();
    private ObservableCollection<string> _diffDisplaySource = new();
    private readonly List<GitFileChange> _stagedChanges = new();
    private readonly List<GitFileChange> _unstagedChanges = new();
    private readonly List<DiffDisplayLine> _diffLines = new();
    private readonly ObservableCollection<string> _branchNames = new();
    private const string CreateBranchOptionLabel = "<Create new branch>";
    private readonly HorizontalSplitView _splitView;
    private readonly GuiAttribute _additionAttribute = new(GuiColor.Black, GuiColor.BrightGreen);
    private readonly GuiAttribute _deletionAttribute = new(GuiColor.White, GuiColor.BrightRed);
    private readonly GuiAttribute _headerAttribute = new(GuiColor.BrightBlue, GuiColor.Black);
    private readonly GuiAttribute _infoAttribute = new(GuiColor.BrightYellow, GuiColor.Black);

    private RepoSnapshot? _snapshot;
    private bool _suppressBranchEvent;
    private bool _autoRefreshEnabled = true;
    private bool _isBusy;
    private bool _pendingRefresh;
    private object? _autoRefreshToken;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;
    private SelectionContext _selectionContext = SelectionContext.None;
    private string? _lastSelectedPath;
    private bool _isUpdatingSelection;

    public GitDashboardWindow(string repositoryPath)
    {
        _git = new GitFacade(repositoryPath);

        Title = "gitTUI";
        Width = Dim.Fill();
        Height = Dim.Fill();

        var topBar = BuildTopBar();
        Add(topBar);

        var changesPane = BuildChangesPane();
        var diffPane = BuildDiffPane();
        _splitView = new HorizontalSplitView(changesPane, diffPane)
        {
            X = 0,
            Y = Pos.Bottom(topBar),
            Width = Dim.Fill(),
            Height = Dim.Fill(2)
        };
        Add(_splitView);

        _statusMessage = new Label
        {
            X = 1,
            Y = Pos.AnchorEnd(2),
            Width = Dim.Fill(),
            Text = "Ready."
        };
        Add(_statusMessage);

        _statusBar = BuildStatusBar();
        Add(_statusBar);

        _autoRefreshToken = Application.AddTimeout(TimeSpan.FromSeconds(30), () =>
        {
            if (!_autoRefreshEnabled)
            {
                return true;
            }

            RefreshSnapshot(triggeredByTimer: true);
            return true;
        });

        RefreshSnapshot();
    }

    private View BuildTopBar()
    {
        var topBar = new FrameView
        {
            Title = "Repository",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 4,
            CanFocus = false
        };

        _repoLabel = new Label
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(),
            Text = "Loading repository..."
        };
        topBar.Add(_repoLabel);

        _divergenceLabel = new Label
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill(),
            Text = "↑0 ↓0"
        };
        topBar.Add(_divergenceLabel);

        var branchLabel = new Label
        {
            Text = "Branch:",
            X = 1,
            Y = 2
        };
        topBar.Add(branchLabel);

        _branchSelector = new ComboBox
        {
            X = Pos.Right(branchLabel) + 1,
            Y = branchLabel.Y,
            Width = 30,
            Height = 1
        };
        _branchSelector.SetSource<string>(_branchNames);
        _branchSelector.SelectedItemChanged += (_, args) =>
        {
            if (_snapshot is null)
            {
                return;
            }

            if (_suppressBranchEvent)
            {
                return;
            }

            var selectedName = args.Value?.ToString();
            if (string.IsNullOrEmpty(selectedName))
            {
                return;
            }

            if (string.Equals(selectedName, CreateBranchOptionLabel, StringComparison.Ordinal))
            {
                ReselectCurrentBranch();
                OpenCreateBranchDialog();
                return;
            }

            if (!string.Equals(selectedName, _snapshot.CurrentBranch, StringComparison.Ordinal))
            {
                CheckoutBranch(selectedName);
            }
        };
        topBar.Add(_branchSelector);

        _pullButton = new Button
        {
            Text = "Pull",
            X = Pos.Right(_branchSelector) + 2,
            Y = branchLabel.Y,
            Width = 10
        };
        _pullButton.Accepting += (_, args) =>
        {
            args.Cancel = true;
            PullChanges();
        };
        topBar.Add(_pullButton);

        _pushButton = new Button
        {
            Text = "Push",
            X = Pos.Right(_pullButton) + 2,
            Y = branchLabel.Y,
            Width = 10
        };
        _pushButton.Accepting += (_, args) =>
        {
            args.Cancel = true;
            PushChanges();
        };
        topBar.Add(_pushButton);

        _refreshLabel = new Label
        {
            X = Pos.Right(_pushButton) + 2,
            Y = branchLabel.Y,
            Width = Dim.Fill(),
            Text = "Auto-refresh: on (30s)"
        };
        topBar.Add(_refreshLabel);

        return topBar;
    }

    private View BuildChangesPane()
    {
        var container = new View
        {
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        var stagedFrame = new FrameView
        {
            Title = "Staged changes",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Percent(50)
        };
        container.Add(stagedFrame);

        _stagedList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            AllowsMarking = true,
            AllowsMultipleSelection = true
        };
        _stagedList.SetSource<string>(_stagedDisplay);
        _stagedList.SelectedItemChanged += (_, __) =>
        {
            if (_isUpdatingSelection) return;

            try
            {
                _isUpdatingSelection = true;
                if (_stagedList.SelectedItem != -1)
                {
                    if (_unstagedList.Source?.Count > 0 && _unstagedList.SelectedItem != -1)
                    {
                        try
                        {
                            _unstagedList.SelectedItem = -1;
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // Ignore internal Terminal.Gui error
                        }
                    }
                }
                UpdateDiffPane();
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        };
        stagedFrame.Add(_stagedList);

        _unstageButton = new Button
        {
            Text = "Unstage selection",
            X = 0,
            Y = Pos.Bottom(_stagedList),
            Width = Dim.Fill()
        };
        _unstageButton.Accepting += (_, args) =>
        {
            args.Cancel = true;
            UnstageSelection();
        };
        stagedFrame.Add(_unstageButton);

        var unstagedFrame = new FrameView
        {
            Title = "Unstaged changes",
            X = 0,
            Y = Pos.Bottom(stagedFrame) + 1,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        container.Add(unstagedFrame);

        _unstagedList = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            AllowsMarking = true,
            AllowsMultipleSelection = true
        };
        _unstagedList.SetSource<string>(_unstagedDisplay);
        _unstagedList.SelectedItemChanged += (_, __) =>
        {
            if (_isUpdatingSelection) return;

            try
            {
                _isUpdatingSelection = true;
                if (_unstagedList.SelectedItem != -1)
                {
                    if (_stagedList.Source?.Count > 0 && _stagedList.SelectedItem != -1)
                    {
                        try
                        {
                            _stagedList.SelectedItem = -1;
                        }
                        catch (ArgumentOutOfRangeException)
                        {
                            // Ignore internal Terminal.Gui error
                        }
                    }
                }
                UpdateDiffPane();
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        };
        unstagedFrame.Add(_unstagedList);

        _stageButton = new Button
        {
            Text = "Stage selection",
            X = 0,
            Y = Pos.Bottom(_unstagedList),
            Width = Dim.Fill()
        };
        _stageButton.Accepting += (_, args) =>
        {
            args.Cancel = true;
            StageSelection();
        };
        unstagedFrame.Add(_stageButton);

        return container;
    }

    private View BuildDiffPane()
    {
        var frame = new FrameView
        {
            Title = "Diff",
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        _diffCaption = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Text = "Select a file to preview its diff."
        };
        frame.Add(_diffCaption);

        _diffList = new ListView
        {
            X = 0,
            Y = Pos.Bottom(_diffCaption) + 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false,
            AllowsMultipleSelection = false
        };
        _diffList.RowRender += DiffListOnRowRender;
        _diffList.HorizontalScrollBar.AutoShow = true;
        _diffList.VerticalScrollBar.AutoShow = true;
        _diffList.SetSource<string>(_diffDisplaySource);
        frame.Add(_diffList);

        return frame;
    }

    private StatusBar BuildStatusBar()
    {
        var items = new[]
        {
            new Shortcut(Key.Q.WithCtrl, "Quit", () => Application.RequestStop()),
            new Shortcut(Key.R.WithCtrl, "Refresh", () => RefreshSnapshot()),
            new Shortcut(Key.S.WithCtrl, "Stage", StageSelection),
            new Shortcut(Key.U.WithCtrl, "Unstage", UnstageSelection),
            new Shortcut(Key.Enter.WithCtrl, "Commit", OpenCommitDialog),
            new Shortcut(Key.L.WithCtrl, "Pull", PullChanges),
            new Shortcut(Key.P.WithCtrl, "Push", PushChanges),
            new Shortcut(Key.T.WithCtrl, "Toggle timer", ToggleAutoRefresh)
        };
        return new StatusBar(items);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_autoRefreshToken is not null)
            {
                Application.RemoveTimeout(_autoRefreshToken);
                _autoRefreshToken = null;
            }
        }

        base.Dispose(disposing);
    }



    private void ToggleAutoRefresh()
    {
        _autoRefreshEnabled = !_autoRefreshEnabled;
        UpdateRefreshLabel();
    }

    private void RefreshSnapshot(bool triggeredByTimer = false)
    {
        if (_isBusy && triggeredByTimer)
        {
            _pendingRefresh = true;
            return;
        }

        RunGitOperation("Refreshing repository status", () =>
        {
            var snapshot = _git.LoadSnapshot();
            ApplySnapshot(snapshot);
            UpdateStatusMessage("Repository snapshot updated.");
        }, refreshAfter: false, warnIfBusy: !triggeredByTimer);
    }

    private void ApplySnapshot(RepoSnapshot snapshot)
    {
        _snapshot = snapshot;
        _stagedChanges.Clear();
        _stagedChanges.AddRange(snapshot.StagedChanges);
        _unstagedChanges.Clear();
        _unstagedChanges.AddRange(snapshot.UnstagedChanges);

        _stagedDisplay.Clear();
        foreach (var change in _stagedChanges)
        {
            _stagedDisplay.Add(RenderChange(change));
        }
        _stagedList.SetSource<string>(_stagedDisplay);

        _unstagedDisplay.Clear();
        foreach (var change in _unstagedChanges)
        {
            _unstagedDisplay.Add(RenderChange(change));
        }
        _unstagedList.SetSource<string>(_unstagedDisplay);

        _repoLabel.Text = $"{snapshot.RepositoryName} · {snapshot.RepositoryPath}";
        _divergenceLabel.Text = $"↑{snapshot.AheadBy} ↓{snapshot.BehindBy}";

        _branchNames.Clear();
        foreach (var branch in snapshot.Branches)
        {
            _branchNames.Add(branch.Name);
        }
        _branchNames.Add(CreateBranchOptionLabel);
        _branchSelector.SetSource<string>(_branchNames);
        SetBranchSelection(snapshot.CurrentBranch);

        UpdateListViewMarks(_stagedList, _stagedChanges.Count);
        UpdateListViewMarks(_unstagedList, _unstagedChanges.Count);

        RestoreSelection();

        _lastRefresh = DateTimeOffset.Now;
        UpdateRefreshLabel();
        UpdateStatusMessage("Ready.");
        UpdateDiffPane();
    }

    private static string RenderChange(GitFileChange change)
    {
        var staged = change.StagedCode == '\0' ? ' ' : change.StagedCode;
        var worktree = change.WorkTreeCode == '\0' ? ' ' : change.WorkTreeCode;
        var formattedPath = PathFormatter.FormatGitStatusPath(change.Path);
        return $"[{staged}{worktree}] {formattedPath}";
    }

    private void UpdateListViewMarks(ListView listView, int count)
    {
        if (listView.Source is null)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            listView.Source.SetMark(i, false);
        }
    }

    private void UpdateDiffPane()
    {
        if (_snapshot is null)
        {
            return;
        }

        var (selection, scope) = GetActiveSelection();
        if (selection is null)
        {
            _diffCaption.Text = "Select a file to preview its diff.";
            ShowDiffMessage("No file selected.");
            return;
        }

        var target = selection.Value;
        _diffCaption.Text = $"{target.Path}";
        _lastSelectedPath = target.Path;

        try
        {
            var isUntracked = target.WorkTreeCode == '?';
            var diff = _git.GetDiff(target.Path, scope, isUntracked);
            RenderDiff(diff);
        }
        catch (Exception ex)
        {
            ShowDiffMessage(ex.Message);
        }
    }

    private void RestoreSelection()
    {
        if (string.IsNullOrEmpty(_lastSelectedPath))
        {
            return;
        }

        var stagedIndex = _stagedChanges.FindIndex(change =>
            string.Equals(change.Path, _lastSelectedPath, StringComparison.Ordinal));
        if (stagedIndex >= 0)
        {
            _selectionContext = SelectionContext.Staged;
            _stagedList.SelectedItem = stagedIndex;
            return;
        }

        var unstagedIndex = _unstagedChanges.FindIndex(change =>
            string.Equals(change.Path, _lastSelectedPath, StringComparison.Ordinal));
        if (unstagedIndex >= 0)
        {
            _selectionContext = SelectionContext.Unstaged;
            _unstagedList.SelectedItem = unstagedIndex;
            return;
        }

        _selectionContext = SelectionContext.None;
        _lastSelectedPath = null;
    }

    private void ShowDiffMessage(string message)
    {
        _diffLines.Clear();
        _diffDisplay.Clear();
        _diffDisplay.Add(message);
        _diffLines.Add(new DiffDisplayLine(message, DiffLineType.Info));
        ApplyDiffDisplay();
    }

    private static readonly Regex HunkHeaderRegex = new(@"@@\s*-(?<old>\d+)(?:,(?<oldCount>\d+))?\s+\+(?<new>\d+)(?:,(?<newCount>\d+))?\s*@@", RegexOptions.Compiled);

    private void RenderDiff(string diffText)
    {
        _diffLines.Clear();
        _diffDisplay.Clear();

        if (string.IsNullOrWhiteSpace(diffText))
        {
            ShowDiffMessage("No diff available.");
            return;
        }

        var sanitized = diffText.Replace("\r", string.Empty);
        var segments = sanitized.Split('\n');
        var currentOld = 0;
        var currentNew = 0;
        var insideHunk = false;

        foreach (var raw in segments)
        {
            var line = raw ?? string.Empty;

            if (line.StartsWith("@@"))
            {
                var match = HunkHeaderRegex.Match(line);
                if (match.Success)
                {
                    currentOld = int.Parse(match.Groups["old"].Value);
                    currentNew = int.Parse(match.Groups["new"].Value);
                    insideHunk = true;
                }

                AddDiffLine(FormatHeaderLine(line), DiffLineType.Header);
                continue;
            }

            if (line.StartsWith("diff --git", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("---", StringComparison.Ordinal)
                || line.StartsWith("+++", StringComparison.Ordinal))
            {
                insideHunk = false;
                continue;
            }

            if (!insideHunk)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                AddDiffLine(FormatHeaderLine(line), DiffLineType.Header);
                continue;
            }

            if (line.StartsWith("+") && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                var content = line.Length > 1 ? line[1..] : string.Empty;
                AddDiffLine(FormatContentLine(null, currentNew, '+', content), DiffLineType.Addition);
                currentNew++;
                continue;
            }

            if (line.StartsWith("-") && !line.StartsWith("---", StringComparison.Ordinal))
            {
                var content = line.Length > 1 ? line[1..] : string.Empty;
                AddDiffLine(FormatContentLine(currentOld, null, '-', content), DiffLineType.Deletion);
                currentOld++;
                continue;
            }

            if (line.StartsWith("\\"))
            {
                AddDiffLine(FormatHeaderLine(line), DiffLineType.Info);
                continue;
            }

            var ctxContent = line.Length > 0 ? line[1..] : string.Empty;
            AddDiffLine(FormatContentLine(currentOld, currentNew, ' ', ctxContent), DiffLineType.Context);
            currentOld++;
            currentNew++;
        }

        if (_diffDisplay.Count == 0)
        {
            ShowDiffMessage("No diff available.");
            return;
        }

        ApplyDiffDisplay();
    }

    private void AddDiffLine(string text, DiffLineType kind)
    {
        _diffLines.Add(new DiffDisplayLine(text, kind));
        _diffDisplay.Add(text);
    }

    private static string FormatHeaderLine(string text)
    {
        return $"{FormatLineNumber(null)} {FormatLineNumber(null)} │{text}";
    }

    private static string FormatContentLine(int? oldNumber, int? newNumber, char indicator, string content)
    {
        var prefix = indicator switch
        {
            '+' => "+ ",
            '-' => "- ",
            _ => "  "
        };
        return $"{FormatLineNumber(oldNumber)} {FormatLineNumber(newNumber)} │{prefix}{content}";
    }

    private static string FormatLineNumber(int? number)
    {
        return number.HasValue ? number.Value.ToString().PadLeft(4) : "    ";
    }

    private void ApplyDiffDisplay()
    {
        _diffDisplaySource = new ObservableCollection<string>(_diffDisplay);
        _diffList.SetSource<string>(_diffDisplaySource);
        _diffList.SetNeedsDraw();
    }

    private void DiffListOnRowRender(object? sender, ListViewRowEventArgs e)
    {
        if (e.Row < 0 || e.Row >= _diffLines.Count)
        {
            return;
        }

        var kind = _diffLines[e.Row].Kind;
        e.RowAttribute = kind switch
        {
            DiffLineType.Addition => _additionAttribute,
            DiffLineType.Deletion => _deletionAttribute,
            DiffLineType.Header => _headerAttribute,
            DiffLineType.Info => _infoAttribute,
            _ => null
        };
    }

    private void StageSelection()
    {
        var paths = GetSelectedPaths(_unstagedList, _unstagedChanges);
        if (paths.Count == 0)
        {
            MessageBox.Query("Nothing selected", "Highlight or mark one or more files to stage.", "Ok");
            return;
        }

        RunGitOperation($"Staging {paths.Count} file(s)", () => _git.Stage(paths), refreshAfter: true);
    }

    private void UnstageSelection()
    {
        var paths = GetSelectedPaths(_stagedList, _stagedChanges);
        if (paths.Count == 0)
        {
            MessageBox.Query("Nothing selected", "Highlight or mark one or more files to unstage.", "Ok");
            return;
        }

        RunGitOperation($"Unstaging {paths.Count} file(s)", () => _git.Unstage(paths), refreshAfter: true);
    }

    private void OpenCreateBranchDialog()
    {
        string? pendingError = null;

        var dialog = new Dialog
        {
            Title = "Create new branch"
        };

        var nameLabel = new Label
        {
            Text = "Branch name:",
            X = 0,
            Y = 0,
            Width = Dim.Fill()
        };
        dialog.Add(nameLabel);

        var nameInput = new TextField
        {
            X = 0,
            Y = Pos.Bottom(nameLabel) + 1,
            Width = 40
        };
        dialog.Add(nameInput);

        var createButton = new Button
        {
            Text = "Create",
            X = 0,
            Y = Pos.Bottom(nameInput) + 1
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            X = Pos.Right(createButton) + 2,
            Y = createButton.Y
        };

        createButton.Accepting += (_, args) =>
        {
            var branchName = nameInput.Text.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(branchName))
            {
                pendingError = "Branch name is required.";
                args.Cancel = true;
                return;
            }

            if (branchName.Any(char.IsWhiteSpace))
            {
                pendingError = "Branch names cannot contain whitespace.";
                args.Cancel = true;
                return;
            }

            args.Cancel = true;
            RunGitOperation($"Creating branch {branchName}", () => _git.CreateBranch(branchName), refreshAfter: true);
            Application.RequestStop();
        };

        cancelButton.Accepting += (_, args) =>
        {
            args.Cancel = true;
            Application.RequestStop();
        };

        dialog.Add(createButton, cancelButton);
        Application.Run(dialog);

        if (!string.IsNullOrEmpty(pendingError))
        {
            MessageBox.ErrorQuery("Branch creation blocked", pendingError, "Ok");
        }
    }

    private void PullChanges()
    {
        RunGitOperation("Pulling latest changes", () => _git.Pull(), refreshAfter: true);
    }

    private void PushChanges()
    {
        RunGitOperation("Pushing changes", () => _git.Push(), refreshAfter: true);
    }

    private void CheckoutBranch(string branchName)
    {
        RunGitOperation($"Switching to {branchName}", () => _git.Checkout(branchName), refreshAfter: true);
    }

    private void ReselectCurrentBranch()
    {
        if (_snapshot is null)
        {
            return;
        }

        SetBranchSelection(_snapshot.CurrentBranch);
    }

    private void SetBranchSelection(string? branchName)
    {
        var index = -1;
        if (!string.IsNullOrEmpty(branchName))
        {
            index = FindBranchIndex(branchName);
        }

        _suppressBranchEvent = true;
        _branchSelector.SelectedItem = index;
        _suppressBranchEvent = false;
    }

    private int FindBranchIndex(string branchName)
    {
        for (var i = 0; i < _branchNames.Count; i++)
        {
            if (string.Equals(_branchNames[i], branchName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private void OpenCommitDialog()
    {
        if (_stagedChanges.Count == 0)
        {
            MessageBox.Query("Nothing staged", "Stage changes before attempting to commit.", "Ok");
            return;
        }

        var dialog = new Dialog
        {
            Title = "Commit staged changes"
        };

        var infoLabel = new Label
        {
            Text = $"{_stagedChanges.Count} file(s) staged.",
            X = 0,
            Y = 0,
            Width = Dim.Fill()
        };
        dialog.Add(infoLabel);

        var messageInput = new TextView
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = 6,
            WordWrap = true
        };
        dialog.Add(messageInput);

        string? pendingError = null;

        var commitButton = new Button
        {
            Text = "Commit",
            X = 0,
            Y = Pos.Bottom(messageInput) + 1
        };

        var cancelButton = new Button
        {
            Text = "Cancel",
            X = Pos.Right(commitButton) + 2,
            Y = commitButton.Y
        };

        commitButton.Accepting += (_, args) =>
        {
            var message = messageInput.Text.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                pendingError = "Commit message is required.";
                args.Cancel = true;
                return;
            }

            args.Cancel = true;
            RunGitOperation("Committing changes", () => _git.Commit(message), refreshAfter: true);
            Application.RequestStop();
        };

        cancelButton.Accepting += (_, args) =>
        {
            args.Cancel = true;
            Application.RequestStop();
        };

        dialog.Add(commitButton, cancelButton);
        Application.Run(dialog);

        if (!string.IsNullOrEmpty(pendingError))
        {
            MessageBox.ErrorQuery("Commit blocked", pendingError, "Ok");
        }
    }

    private void RunGitOperation(string description, Action operation, bool refreshAfter, bool warnIfBusy = true)
    {
        if (_isBusy)
        {
            if (warnIfBusy)
            {
                MessageBox.Query("Busy", "Please wait for the current operation to complete.", "Ok");
            }

            _pendingRefresh = _pendingRefresh || refreshAfter;
            return;
        }

        var shouldRunDeferredRefresh = false;

        try
        {
            _isBusy = true;
            SetInteractionEnabled(false);
            UpdateStatusMessage(description);

            operation();

            if (refreshAfter)
            {
                var snapshot = _git.LoadSnapshot();
                ApplySnapshot(snapshot);
                _pendingRefresh = false;
            }
        }
        catch (GitCommandException ex)
        {
            MessageBox.ErrorQuery("Git error", ex.Message, "Ok");
        }
        catch (Exception ex)
        {
            MessageBox.ErrorQuery("Unexpected error", ex.Message, "Ok");
        }
        finally
        {
            _isBusy = false;
            SetInteractionEnabled(true);
            UpdateStatusMessage("Ready.");

            if (_pendingRefresh)
            {
                shouldRunDeferredRefresh = true;
                _pendingRefresh = false;
            }
        }

        if (shouldRunDeferredRefresh)
        {
            RefreshSnapshot();
        }
    }

    private void UpdateStatusMessage(string message)
    {
        _statusMessage.Text = message;
    }

    private void SetInteractionEnabled(bool enabled)
    {
        _pullButton.Enabled = enabled;
        _pushButton.Enabled = enabled;
        _stageButton.Enabled = enabled;
        _unstageButton.Enabled = enabled;
        _branchSelector.Enabled = enabled;
    }

    private void UpdateRefreshLabel()
    {
        var state = _autoRefreshEnabled ? "on" : "off";
        var timestamp = _lastRefresh == DateTimeOffset.MinValue
            ? "never"
            : _lastRefresh.LocalDateTime.ToString("HH:mm:ss");
        _refreshLabel.Text = $"Auto-refresh: {state} (30s) · Last: {timestamp}";
    }

    private (GitFileChange? selection, DiffScope scope) GetActiveSelection()
    {
        // 1. Priority: Focused list with valid selection
        if (_stagedList.HasFocus && IsValidSelection(_stagedList, _stagedChanges))
        {
            _selectionContext = SelectionContext.Staged;
            return (_stagedChanges[_stagedList.SelectedItem], DiffScope.Staged);
        }

        if (_unstagedList.HasFocus && IsValidSelection(_unstagedList, _unstagedChanges))
        {
            _selectionContext = SelectionContext.Unstaged;
            return (_unstagedChanges[_unstagedList.SelectedItem], DiffScope.WorkingTree);
        }

        // 2. Priority: Last active context with valid selection
        if (_selectionContext == SelectionContext.Staged && IsValidSelection(_stagedList, _stagedChanges))
        {
            return (_stagedChanges[_stagedList.SelectedItem], DiffScope.Staged);
        }

        if (_selectionContext == SelectionContext.Unstaged && IsValidSelection(_unstagedList, _unstagedChanges))
        {
            return (_unstagedChanges[_unstagedList.SelectedItem], DiffScope.WorkingTree);
        }

        // 3. Priority: Any list with valid selection (Staged first)
        if (IsValidSelection(_stagedList, _stagedChanges))
        {
            _selectionContext = SelectionContext.Staged;
            return (_stagedChanges[_stagedList.SelectedItem], DiffScope.Staged);
        }

        if (IsValidSelection(_unstagedList, _unstagedChanges))
        {
            _selectionContext = SelectionContext.Unstaged;
            return (_unstagedChanges[_unstagedList.SelectedItem], DiffScope.WorkingTree);
        }

        return (null, DiffScope.WorkingTree);
    }

    private static bool IsValidSelection(ListView list, List<GitFileChange> backing)
    {
        return list.SelectedItem >= 0 && list.SelectedItem < backing.Count;
    }

    private List<GitFileChange> GetSelectedChanges(ListView listView, List<GitFileChange> backing)
    {
        var indexes = new HashSet<int>();
        if (listView.Source is null)
        {
            return new List<GitFileChange>();
        }

        for (var i = 0; i < backing.Count; i++)
        {
            if (listView.Source.IsMarked(i))
            {
                indexes.Add(i);
            }
        }

        if (indexes.Count == 0 && listView.SelectedItem >= 0 && listView.SelectedItem < backing.Count)
        {
            indexes.Add(listView.SelectedItem);
        }

        return indexes.Select(index => backing[index]).ToList();
    }

    private List<string> GetSelectedPaths(ListView list, List<GitFileChange> backing)
    {
        var changes = GetSelectedChanges(list, backing);
        return changes.Select(change => change.Path).Distinct().ToList();
    }

    private enum SelectionContext
    {
        None,
        Staged,
        Unstaged
    }
}
