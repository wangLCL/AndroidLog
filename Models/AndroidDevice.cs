namespace AndroidLogViewer.Models;

public sealed record AndroidDevice(string Serial, string State, string DisplayName)
{
    public override string ToString()
    {
        return DisplayName;
    }
}
