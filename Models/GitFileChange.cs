namespace gittui.Models;

internal readonly record struct GitFileChange(string Path, char StagedCode, char WorkTreeCode);
