namespace AndroidLogViewer.Models;

public sealed record ApkInfo(
    string FilePath,
    string FileName,
    long FileSizeBytes,
    string PackageName,
    string ApplicationLabel,
    string VersionName,
    string VersionCode,
    IReadOnlyList<string> Permissions);
