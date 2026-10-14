using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using PathForge.Core.Grbl;

namespace PathForge.App.Services;

/// <summary>
/// Runs esptool as a program of its own and hands its output over line by line. esptool is GPL software: PathForge only starts
/// it, it never links to it.
/// </summary>
public sealed class EsptoolProcess : IEsptoolProcess
{
    /// <summary>
    /// How long the output is awaited after esptool has ended. A helper process that has inherited the pipes and outlives
    /// esptool must not hold the operation up: the lines that arrive later are of no interest.
    /// </summary>
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(2);

    public async Task<int> RunAsync(EsptoolTool tool, IReadOnlyList<string> arguments, Action<string> onLine, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(tool.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in tool.PrefixArguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // esptool is a Python program. Its output must not wait in a buffer until the end (the progress bar would stand still
        // and then jump), a Windows pipe uses the ANSI code page where the bars of esptool 5 have characters that it does not
        // hold, and escape codes of colours would end up in the log.
        start.Environment["PYTHONUNBUFFERED"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["PYTHONUTF8"] = "1";
        start.Environment["NO_COLOR"] = "1";
        start.Environment["TERM"] = "dumb";

        // The output of the two streams arrives on different threads; the listener sees one line at a time.
        var gate = new object();

        void Forward(string? line)
        {
            if (line is not null)
            {
                lock (gate)
                {
                    onLine(line);
                }
            }
        }

        // The end of a stream is announced by a line that is null.
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outputDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                outputDone.TrySetResult();
            }
            else
            {
                Forward(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                errorDone.TrySetResult();
            }
            else
            {
                Forward(e.Data);
            }
        };
        process.Exited += (_, _) => exited.TrySetResult();
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            throw new EsptoolNotStartedException(tool.Describe(), ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // The standalone build of esptool starts a second process that does the work: both have to go.
        using var registration = token.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // It has ended on its own in the meantime.
            }
        });

        // Process.WaitForExit would also wait until every holder of the pipes lets go of them; the end of esptool is enough here.
        await exited.Task;
        await Task.WhenAny(Task.WhenAll(outputDone.Task, errorDone.Task), Task.Delay(OutputGrace));
        token.ThrowIfCancellationRequested();
        return process.ExitCode;
    }
}
