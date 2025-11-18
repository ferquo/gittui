namespace gittui.Models;

internal enum DiffLineType
{
    Context,
    Addition,
    Deletion,
    Header,
    Info
}

internal readonly record struct DiffDisplayLine(string Text, DiffLineType Kind);
