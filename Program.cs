using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Terminal.Gui;

Application.Init();

try
{
    Application.Run(new DashboardWindow());
}
finally
{
    Application.Shutdown();
}

internal sealed class DashboardWindow : Window
{
    private readonly List<WorkItem> _allItems = new();
    private readonly List<WorkItem> _visibleItems = new();
    private readonly ObservableCollection<string> _listDisplay = new();

    private readonly ListView _workList;
    private readonly TextView _detailView;
    private readonly Label _metaLabel;
    private readonly Label _statusLabel;
    private readonly TextField _titleInput;
    private readonly TextView _notesInput;
    private readonly CheckBox _filterCheckBox;

    private bool _showActiveOnly;

    public DashboardWindow()
    {
        Title = "gitTUI dashboard";
        Width = Dim.Fill();
        Height = Dim.Fill();

        var instructions = new Label
        {
            Text = "↑/↓ navigate · Enter toggles · Ctrl+N capture · Ctrl+Q quits",
            X = 1,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1
        };
        Add(instructions);

        _statusLabel = new Label
        {
            Text = string.Empty,
            X = 1,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill()
        };
        Add(_statusLabel);

        var leftPanel = new FrameView
        {
            Title = "Work Items",
            X = 0,
            Y = Pos.Bottom(instructions) + 1,
            Width = Dim.Percent(35),
            Height = Dim.Fill(4)
        };
        Add(leftPanel);

        var rightPanel = new FrameView
        {
            Title = "Details",
            X = Pos.Right(leftPanel) + 1,
            Y = leftPanel.Y,
            Width = Dim.Fill(),
            Height = Dim.Percent(55)
        };
        Add(rightPanel);

        var composePanel = new FrameView
        {
            Title = "Scratchpad",
            X = Pos.Right(leftPanel) + 1,
            Y = Pos.Bottom(rightPanel) + 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(4)
        };
        Add(composePanel);

        _workList = new ListView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(2)
        };
        _workList.SetSource(_listDisplay);
        _workList.SelectedItemChanged += (_, args) => UpdateDetailPane(args.Item);
        _workList.OpenSelectedItem += (_, __) => ToggleSelectedItem();
        leftPanel.Add(_workList);

        _filterCheckBox = new CheckBox
        {
            Text = "Show only active items",
            X = 0,
            Y = Pos.Bottom(_workList) + 1
        };
        _filterCheckBox.CheckedStateChanged += (_, __) =>
        {
            _showActiveOnly = _filterCheckBox.CheckedState == CheckState.Checked;
            RefreshWorkItems();
        };
        leftPanel.Add(_filterCheckBox);

        _metaLabel = new Label
        {
            Text = string.Empty,
            X = 0,
            Y = 0,
            Width = Dim.Fill()
        };
        rightPanel.Add(_metaLabel);

        _detailView = new TextView
        {
            ReadOnly = true,
            WordWrap = true,
            X = 0,
            Y = Pos.Bottom(_metaLabel) + 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(2)
        };
        rightPanel.Add(_detailView);

        var toggleButton = new Button
        {
            Text = "Toggle completion",
            X = 0,
            Y = Pos.Bottom(_detailView) + 1
        };
        toggleButton.Accepting += (_, args) =>
        {
            ToggleSelectedItem();
            args.Cancel = true;
        };
        rightPanel.Add(toggleButton);

        var duplicateButton = new Button
        {
            Text = "Clone item",
            X = Pos.Right(toggleButton) + 2,
            Y = toggleButton.Y
        };
        duplicateButton.Accepting += (_, args) =>
        {
            DuplicateSelectedItem();
            args.Cancel = true;
        };
        rightPanel.Add(duplicateButton);

        var titleLabel = new Label
        {
            Text = "Title",
            X = 0,
            Y = 0
        };
        composePanel.Add(titleLabel);

        _titleInput = new TextField
        {
            X = 0,
            Y = Pos.Bottom(titleLabel),
            Width = Dim.Fill()
        };
        composePanel.Add(_titleInput);

        var notesLabel = new Label
        {
            Text = "Notes / commands",
            X = 0,
            Y = Pos.Bottom(_titleInput) + 1
        };
        composePanel.Add(notesLabel);

        _notesInput = new TextView
        {
            X = 0,
            Y = Pos.Bottom(notesLabel),
            Width = Dim.Fill(),
            Height = Dim.Fill(3)
        };
        composePanel.Add(_notesInput);

        var addButton = new Button
        {
            Text = "Capture item",
            X = 0,
            Y = Pos.Bottom(_notesInput)
        };
        addButton.Accepting += (_, args) =>
        {
            AddNewItem();
            args.Cancel = true;
        };
        composePanel.Add(addButton);

        KeyDown += (_, key) =>
        {
            if (key == Key.Enter.WithCtrl)
            {
                AddNewItem();
                key.Handled = true;
            }
        };

        Add(BuildStatusBar());
        SeedInitialData();
        RefreshWorkItems();
    }

    private StatusBar BuildStatusBar()
    {
        var shortcuts = new[]
        {
            new Shortcut(Key.Q.WithCtrl, "Quit", () => Application.RequestStop(), "Leave the dashboard"),
            new Shortcut(Key.Enter, "Toggle", ToggleSelectedItem, "Switch between done/active"),
            new Shortcut(Key.N.WithCtrl, "Capture", () => _titleInput.SetFocus(), "Jump to the capture form"),
            new Shortcut(Key.F.WithCtrl, "Filter", () =>
            {
                _filterCheckBox.CheckedState = _filterCheckBox.CheckedState == CheckState.Checked
                    ? CheckState.UnChecked
                    : CheckState.Checked;
                RefreshWorkItems();
            }, "Show only active items")
        };

        return new StatusBar(shortcuts);
    }

    private void SeedInitialData()
    {
        _allItems.Clear();
        _allItems.AddRange(new[]
        {
            new WorkItem("Scan repository", "Use `git status` to verify local changes and highlight suspicious files."),
            new WorkItem("Review untracked files", "Decide whether each temporary file belongs in source control."),
            new WorkItem("Run diagnostics", "Execute the scripted health checks located in `scripts/health-checks.sh`."),
            new WorkItem("Draft summary", "Summarize open work items so collaborators can read the terminal dashboard.")
        });
        _allItems[0].Completed = true;
    }

    private void RefreshWorkItems()
    {
        _visibleItems.Clear();
        foreach (var item in _allItems)
        {
            if (_showActiveOnly && item.Completed)
            {
                continue;
            }

            _visibleItems.Add(item);
        }

        _listDisplay.Clear();
        if (_visibleItems.Count == 0)
        {
            _workList.Enabled = false;
            _listDisplay.Add("No work items found");
            _workList.SelectedItem = 0;
            UpdateDetailPane(-1);
            UpdateStatusText();
            return;
        }

        _workList.Enabled = true;
        foreach (var item in _visibleItems)
        {
            _listDisplay.Add(FormatWorkItem(item));
        }

        if (_workList.SelectedItem < 0 || _workList.SelectedItem >= _visibleItems.Count)
        {
            _workList.SelectedItem = 0;
        }

        UpdateDetailPane(_workList.SelectedItem);
        UpdateStatusText();
    }

    private void UpdateDetailPane(int index)
    {
        if (index < 0 || index >= _visibleItems.Count)
        {
            _metaLabel.Text = "Select an item to see the details.";
            _detailView.Text = string.Empty;
            return;
        }

        var item = _visibleItems[index];
        _metaLabel.Text = $"{(item.Completed ? "[done]" : "[active]")} Created {item.CreatedAt.ToLocalTime():t}";
        _detailView.Text = item.Description;
    }

    private void UpdateStatusText()
    {
        var total = _allItems.Count;
        var done = _allItems.Count(item => item.Completed);
        _statusLabel.Text = $"Tracking {total} items · {done} done · {_visibleItems.Count} shown";
    }

    private void ToggleSelectedItem()
    {
        if (_visibleItems.Count == 0)
        {
            return;
        }

        var index = _workList.SelectedItem;
        if (index < 0 || index >= _visibleItems.Count)
        {
            return;
        }

        var item = _visibleItems[index];
        item.Completed = !item.Completed;
        RefreshWorkItems();
    }

    private void DuplicateSelectedItem()
    {
        if (_visibleItems.Count == 0)
        {
            return;
        }

        var index = _workList.SelectedItem;
        if (index < 0 || index >= _visibleItems.Count)
        {
            return;
        }

        var selected = _visibleItems[index];
        var duplicate = new WorkItem($"{selected.Title} (copy)", selected.Description)
        {
            Completed = selected.Completed
        };

        var insertIndex = _allItems.IndexOf(selected);
        _allItems.Insert(insertIndex + 1, duplicate);
        RefreshWorkItems();
        _workList.SelectedItem = Math.Min(insertIndex + 1, _visibleItems.Count - 1);
        UpdateDetailPane(_workList.SelectedItem);
    }

    private void AddNewItem()
    {
        var title = _titleInput.Text?.ToString().Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(title))
        {
            MessageBox.ErrorQuery("Missing title", "Give the work item a title first.", "Ok");
            _titleInput.SetFocus();
            return;
        }

        var description = _notesInput.Text?.ToString().Trim();
        if (string.IsNullOrWhiteSpace(description))
        {
            description = "To be detailed later.";
        }

        var workItem = new WorkItem(title, description);
        _allItems.Insert(0, workItem);
        _titleInput.Text = string.Empty;
        _notesInput.Text = string.Empty;

        RefreshWorkItems();
        _workList.SelectedItem = 0;
        UpdateDetailPane(0);
    }

    private static string FormatWorkItem(WorkItem item)
    {
        var statusGlyph = item.Completed ? "✓" : "•";
        return $"{statusGlyph} {item.Title}";
    }
}

internal sealed class WorkItem
{
    public WorkItem(string title, string description)
    {
        Title = title;
        Description = description;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string Title { get; set; }

    public string Description { get; set; }

    public bool Completed { get; set; }

    public DateTimeOffset CreatedAt { get; }
}
