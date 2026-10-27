using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StorageIt.Services;

public enum CommandTarget { Both, Files, Folders }

public enum CommandRunMode
{
    /// <summary>Start the program the way Explorer would (its own window, if it has one).</summary>
    Normal,
    /// <summary>Run without a window and show what the program printed when it finishes.</summary>
    HiddenWithOutput,
}

/// <summary>A user-defined action available from every context menu.</summary>
public sealed class CustomCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New command";
    public string Program { get; set; } = "";
    public string Arguments { get; set; } = "";
    [JsonConverter(typeof(JsonStringEnumConverter))] public CommandTarget AppliesTo { get; set; } = CommandTarget.Both;
    [JsonConverter(typeof(JsonStringEnumConverter))] public CommandRunMode Mode { get; set; } = CommandRunMode.Normal;
    /// <summary>With several items selected: run once per item (true) or once with {paths} (false).</summary>
    public bool PerItem { get; set; } = true;
    public bool Confirm { get; set; } = true;
    /// <summary>Scan again afterwards, for commands that change what is on disk.</summary>
    public bool RescanAfter { get; set; }

    public CustomCommand Clone() => (CustomCommand)MemberwiseClone();

    public bool AppliesToItem(bool isDirectory) =>
        AppliesTo == CommandTarget.Both || (AppliesTo == CommandTarget.Folders) == isDirectory;
}

public sealed record CommandRunResult(string Title, string Output, int? ExitCode);

public static class CommandStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StorageIt", "commands.json");

    public static List<CustomCommand> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<CustomCommand>>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A damaged file should not stop the app; fall through to the defaults.
        }
        var defaults = Defaults();
        Save(defaults);
        return defaults;
    }

    public static void Save(IEnumerable<CustomCommand> commands)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(commands.ToList(), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static List<CustomCommand> Defaults() => new()
    {
        new CustomCommand
        {
            Name = "Open PowerShell here",
            Program = "powershell.exe",
            Arguments = "-NoExit -Command \"Set-Location -LiteralPath '{folder}'\"",
            Confirm = false,
            PerItem = false,
        },
        new CustomCommand
        {
            Name = "Show SHA-256 hash",
            Program = "powershell.exe",
            Arguments = "-NoProfile -Command \"Get-FileHash -Algorithm SHA256 -LiteralPath '{path}' | Format-List\"",
            AppliesTo = CommandTarget.Files,
            Mode = CommandRunMode.HiddenWithOutput,
            Confirm = false,
        },
    };
}

public static class CommandRunner
{
    /// <summary>
    /// Replaces {path} {name} {nameNoExt} {ext} {dir} {folder} {drive} (and {paths} for a whole selection).
    /// Values are inserted as-is: put quotes around them in the command line if they may contain spaces.
    /// </summary>
    public static string Expand(string template, string path, bool isDirectory, IReadOnlyList<string>? allPaths = null)
    {
        string full = Path.GetFullPath(path);
        string dir = Path.GetDirectoryName(full.TrimEnd('\\')) ?? full;
        string name = Path.GetFileName(full.TrimEnd('\\'));
        string ext = isDirectory ? "" : Path.GetExtension(name);
        var sb = new StringBuilder(template);
        sb.Replace("{paths}", allPaths is null ? $"\"{full}\"" : string.Join(' ', allPaths.Select(p => $"\"{p}\"")));
        sb.Replace("{path}", full);
        sb.Replace("{nameNoExt}", isDirectory ? name : Path.GetFileNameWithoutExtension(name));
        sb.Replace("{name}", name);
        sb.Replace("{ext}", ext);
        sb.Replace("{dir}", dir);
        sb.Replace("{folder}", isDirectory ? full : dir);
        sb.Replace("{drive}", Path.GetPathRoot(full) ?? "");
        return sb.ToString();
    }

    /// <summary>Runs the command for the given items (path, isDirectory) and returns one result per process started.</summary>
    public static async Task<List<CommandRunResult>> RunAsync(CustomCommand cmd,
        IReadOnlyList<(string path, bool isDirectory)> items, CancellationToken ct = default)
    {
        var results = new List<CommandRunResult>();
        var jobs = cmd.PerItem || items.Count <= 1
            ? items.Select(i => (first: i, all: (IReadOnlyList<string>?)null)).ToList()
            : new() { (items[0], items.Select(i => i.path).ToList()) };

        foreach (var (first, all) in jobs)
        {
            ct.ThrowIfCancellationRequested();
            string args = Expand(cmd.Arguments, first.path, first.isDirectory, all);
            string workDir = first.isDirectory ? first.path : Path.GetDirectoryName(first.path) ?? "";
            string title = $"{cmd.Name} — {(all is null ? first.path : $"{all.Count} items")}";
            try
            {
                if (cmd.Mode == CommandRunMode.Normal)
                {
                    Process.Start(new ProcessStartInfo(cmd.Program, args)
                    {
                        UseShellExecute = true,
                        WorkingDirectory = Directory.Exists(workDir) ? workDir : "",
                    })?.Dispose();
                    results.Add(new CommandRunResult(title, "Started.", null));
                }
                else
                {
                    results.Add(await RunCaptured(cmd.Program, args, workDir, title, ct));
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                results.Add(new CommandRunResult(title, "Could not start: " + ex.Message, -1));
            }
        }
        return results;
    }

    private static async Task<CommandRunResult> RunCaptured(string program, string args, string workDir, string title, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(program, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory.Exists(workDir) ? workDir : "",
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("The process did not start.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        string text = (await stdout).TrimEnd() + (string.IsNullOrWhiteSpace(await stderr) ? "" : Environment.NewLine + (await stderr).TrimEnd());
        return new CommandRunResult(title, text, p.ExitCode);
    }
}
