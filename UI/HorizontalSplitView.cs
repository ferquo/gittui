using Terminal.Gui;

namespace gittui.UI;

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
