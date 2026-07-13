using AndroidLogViewer.Models;
using System.Diagnostics;

namespace AndroidLogViewer.Services;

public sealed class LogcatReader : IDisposable
{
    private readonly AdbService adbService;
    private Process? process;
    private CancellationTokenSource? cancellationTokenSource;

    public LogcatReader(AdbService adbService)
    {
        this.adbService = adbService;
    }

    public event EventHandler<LogEntry>? EntryReceived;

    public event EventHandler<string>? ErrorReceived;

    public bool IsRunning => process is { HasExited: false };

    public void Start(string serial)
    {
        Stop();

        cancellationTokenSource = new CancellationTokenSource();
        process = new Process
        {
            StartInfo = adbService.CreateLogcatStartInfo(serial),
            EnableRaisingEvents = true
        };

        process.Start();
        _ = ReadOutputAsync(process, cancellationTokenSource.Token);
        _ = ReadErrorAsync(process, cancellationTokenSource.Token);
    }

    public void Stop()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = null;

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may exit between the HasExited check and Kill.
        }
        finally
        {
            process.Dispose();
            process = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private async Task ReadOutputAsync(Process activeProcess, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await activeProcess.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                EntryReceived?.Invoke(this, new LogEntry(line));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorReceived?.Invoke(this, ex.Message);
        }
    }

    private async Task ReadErrorAsync(Process activeProcess, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await activeProcess.StandardError.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    ErrorReceived?.Invoke(this, line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorReceived?.Invoke(this, ex.Message);
        }
    }
}
