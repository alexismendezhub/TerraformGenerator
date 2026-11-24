using System.Diagnostics;

namespace TerraformGenerator.Infrastructure.Terraform;

public sealed record TerraformStep(string Command, int ExitCode, string Output);

public sealed record TerraformCheck(bool Available, bool Passed, IReadOnlyList<TerraformStep> Steps);

// Runs terraform init (no backend), validate and fmt -check, and the Bicep compiler, on the generated files in a
// temporary folder.
// It never runs plan or apply: those need the person's Azure sign-in and stay on their computer.
public sealed class TerraformRunner(ILogger<TerraformRunner> logger) : IDisposable
{
    private static readonly TimeSpan StepTimeout = TimeSpan.FromMinutes(4);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    public async Task<TerraformCheck> CheckAsync(IReadOnlyDictionary<string, string> files, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        var folder = Path.Combine(Path.GetTempPath(), "terraformgenerator", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var (name, content) in files)
            {
                // Names come from the generator (README.md, bicep/main.bicep...), never from the request.
                var path = Path.GetFullPath(Path.Combine(folder, name));
                if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content, cancellationToken);
            }

            var commands = new List<(string Tool, string Arguments, string Missing)>();
            if (files.ContainsKey("main.tf"))
            {
                const string missing = "Terraform is not installed or not on the PATH. Install it with: winget install Hashicorp.Terraform";
                commands.Add(("terraform", "init -backend=false -input=false -no-color", missing));
                commands.Add(("terraform", "validate -no-color", missing));
                commands.Add(("terraform", "fmt -check -no-color", missing));
            }

            if (files.ContainsKey("bicep/main.bicep"))
            {
                const string missing = "Bicep is not installed. Install it with: az bicep install";
                commands.Add((BicepTool, "build bicep/main.bicep --stdout", missing));
            }

            var steps = new List<TerraformStep>();
            foreach (var (tool, arguments, missing) in commands)
            {
                var step = await RunAsync(folder, tool, arguments, cancellationToken);
                if (step is null)
                {
                    return new TerraformCheck(false, false, [.. steps, new(Path.GetFileNameWithoutExtension(tool), -1, missing)]);
                }

                steps.Add(step);
                if (step.ExitCode != 0)
                {
                    return new TerraformCheck(true, false, steps);
                }
            }

            return new TerraformCheck(true, true, steps);
        }
        finally
        {
            _gate.Release();
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Could not remove {Folder}", folder);
            }
            catch (UnauthorizedAccessException exception)
            {
                logger.LogWarning(exception, "Could not remove {Folder}", folder);
            }
        }
    }

    // The Bicep CLI that az bicep install puts in the profile, or one on the PATH.
    private static string BicepTool
    {
        get
        {
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".azure", "bin", OperatingSystem.IsWindows() ? "bicep.exe" : "bicep");
            return File.Exists(installed) ? installed : "bicep";
        }
    }

    private static async Task<TerraformStep?> RunAsync(string folder, string tool, string arguments, CancellationToken cancellationToken)
    {
        var name = Path.GetFileNameWithoutExtension(tool);
        var start = new ProcessStartInfo(tool, arguments)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Providers are downloaded once and reused by every check.
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerraformGenerator", "plugin-cache");
        Directory.CreateDirectory(cache);
        start.Environment["TF_PLUGIN_CACHE_DIR"] = cache;
        start.Environment["TF_IN_AUTOMATION"] = "1";
        start.Environment["CHECKPOINT_DISABLE"] = "1";

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"{name} did not start");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StepTimeout);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                return new TerraformStep($"{name} {arguments}", -1, "Stopped: it took longer than four minutes.");
            }

            // Bicep writes the compiled template to the output; only its messages matter here.
            var text = (name == "bicep" ? await error : (await output) + (await error)).Trim();
            var exitCode = process.ExitCode;
            if (name == "bicep" && exitCode == 0 && text.Contains("Warning", StringComparison.Ordinal))
            {
                exitCode = 1;
            }

            if (arguments.StartsWith("fmt", StringComparison.Ordinal) && exitCode != 0)
            {
                text = "These files are not in terraform fmt style:\n" + text;
            }

            return new TerraformStep($"{name} {arguments}", exitCode, text.Length == 0 && name == "bicep" ? "Compiled with no errors or warnings." : text);
        }
    }
}
