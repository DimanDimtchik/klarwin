namespace KlarWin.Services;

public sealed class SoftwareInstallProgress
{
    public int Step { get; init; }
    public int Total { get; init; }
    public string Headline { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool IsIndeterminate { get; init; } = true;
}
