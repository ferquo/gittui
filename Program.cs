using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Terminal.Gui;
using GuiColor = Terminal.Gui.Color;
using GuiAttribute = Terminal.Gui.Attribute;

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
            if (!string.IsNullOrEmpty(selectedName) && !string.Equals(selectedName, _snapshot.CurrentBranch, StringComparison.Ordinal))
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
        _stagedList.SelectedItemChanged += (_, __) => UpdateDiffPane();
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
        _unstagedList.SelectedItemChanged += (_, __) => UpdateDiffPane();
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
        if (disposing && _autoRefreshToken is not null)
        {
            Application.RemoveTimeout(_autoRefreshToken);
            _autoRefreshToken = null;
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

        var match = _branchNames
            .Select((name, index) => (name, index))
            .FirstOrDefault(pair => string.Equals(pair.name, snapshot.CurrentBranch, StringComparison.Ordinal));
        _suppressBranchEvent = true;
        _branchSelector.SelectedItem = match == default ? -1 : match.index;
        _suppressBranchEvent = false;

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
        return $"[{staged}{worktree}] {change.Path}";
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
        if (selection.Count == 0)
        {
            _diffCaption.Text = "Select a file to preview its diff.";
            ShowDiffMessage("No file selected.");
            return;
        }

        if (selection.Count > 1)
        {
            _diffCaption.Text = "Multiple files selected. Choose a single file to preview the diff.";
            ShowDiffMessage("Multiple files selected.");
            return;
        }

        var target = selection[0];
        _diffCaption.Text = $"{target.Path}";
        _lastSelectedPath = target.Path;

        try
        {
            var diff = _git.GetDiff(target.Path, scope);
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

    private (List<GitFileChange> selection, DiffScope scope) GetActiveSelection()
    {
        var stagedSelection = GetSelectedChanges(_stagedList, _stagedChanges);
        var unstagedSelection = GetSelectedChanges(_unstagedList, _unstagedChanges);

        if (_stagedList.HasFocus && stagedSelection.Count > 0)
        {
            _selectionContext = SelectionContext.Staged;
            return (stagedSelection, DiffScope.Staged);
        }

        if (_unstagedList.HasFocus && unstagedSelection.Count > 0)
        {
            _selectionContext = SelectionContext.Unstaged;
            return (unstagedSelection, DiffScope.WorkingTree);
        }

        if (_selectionContext == SelectionContext.Staged && stagedSelection.Count > 0)
        {
            return (stagedSelection, DiffScope.Staged);
        }

        if (_selectionContext == SelectionContext.Unstaged && unstagedSelection.Count > 0)
        {
            return (unstagedSelection, DiffScope.WorkingTree);
        }

        if (stagedSelection.Count > 0)
        {
            _selectionContext = SelectionContext.Staged;
            return (stagedSelection, DiffScope.Staged);
        }

        if (unstagedSelection.Count > 0)
        {
            _selectionContext = SelectionContext.Unstaged;
            return (unstagedSelection, DiffScope.WorkingTree);
        }

        return (new List<GitFileChange>(), DiffScope.WorkingTree);
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

    private enum DiffLineType
    {
        Context,
        Addition,
        Deletion,
        Header,
        Info
    }

    private readonly record struct DiffDisplayLine(string Text, DiffLineType Kind);
}

internal sealed class HorizontalSplitView : View
{
    private readonly View _left;
    private readonly View _right;
    private float _ratio = 0.38f;
    private int _currentLeftWidth;
    private bool _isDragging;

    public HorizontalSplitView(View left, View right)
    {
        _left = left;
        _right = right;

        WantMousePositionReports = true;
        Add(_left, _right);

        Initialized += (_, __) => ApplyFrames();
        SubviewsLaidOut += (_, __) => ApplyFrames();
    }

    protected override bool OnMouseEvent(MouseEventArgs mouseEvent)
    {
        if (mouseEvent.Flags.HasFlag(MouseFlags.Button1Pressed))
        {
            if (Math.Abs(mouseEvent.Position.X - _currentLeftWidth) <= 1)
            {
                _isDragging = true;
                Application.GrabMouse(this);
                UpdateRatio(mouseEvent.Position.X);
                return true;
            }
        }

        if (_isDragging && mouseEvent.Flags.HasFlag(MouseFlags.ReportMousePosition))
        {
            UpdateRatio(mouseEvent.Position.X);
            return true;
        }

        if (_isDragging && mouseEvent.Flags.HasFlag(MouseFlags.Button1Released))
        {
            _isDragging = false;
            Application.UngrabMouse();
            return true;
        }

        return base.OnMouseEvent(mouseEvent);
    }

    private void UpdateRatio(int mouseX)
    {
        var width = Frame.Width;
        if (width <= 0)
        {
            return;
        }

        var clamped = Math.Clamp(mouseX, 10, Math.Max(11, width - 10));
        _ratio = Math.Clamp((float)clamped / width, 0.2f, 0.8f);
        ApplyFrames();
    }

    private void ApplyFrames()
    {
        var totalWidth = Frame.Width;
        var totalHeight = Frame.Height;
        if (totalWidth <= 0)
        {
            return;
        }

        var leftWidth = Math.Max(10, (int)(totalWidth * _ratio));
        var rightWidth = Math.Max(10, totalWidth - leftWidth - 1);

        _currentLeftWidth = leftWidth;
        _left.Frame = new System.Drawing.Rectangle(0, 0, leftWidth, totalHeight);
        _right.Frame = new System.Drawing.Rectangle(leftWidth + 1, 0, rightWidth, totalHeight);
    }
}

internal sealed class GitFacade
{
    private readonly string _repositoryPath;

    public GitFacade(string repositoryPath)
    {
        _repositoryPath = repositoryPath;
    }

    public RepoSnapshot LoadSnapshot()
    {
        var rootName = Path.GetFileName(_repositoryPath.TrimEnd(Path.DirectorySeparatorChar));
        var statusResult = RunGit("status", "--short", "--branch");
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

    public string GetDiff(string path, DiffScope scope)
    {
        GitCommandResult result;
        if (scope == DiffScope.Staged)
        {
            result = RunGit("diff", "--cached", "--", path);
        }
        else
        {
            result = RunGit("diff", "--", path);
        }

        if (!result.Success)
        {
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
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repositoryPath,
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

internal readonly record struct GitFileChange(string Path, char StagedCode, char WorkTreeCode);

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

internal enum DiffScope
{
    Staged,
    WorkingTree
}

internal sealed class GitRepositoryLocator
{
    public static string? FindRepository(string startPath)
    {
        var directory = new DirectoryInfo(startPath);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

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

internal readonly record struct GitCommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}
