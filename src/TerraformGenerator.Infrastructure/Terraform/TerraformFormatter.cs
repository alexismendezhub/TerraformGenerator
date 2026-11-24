using System.Diagnostics;
using TerraformGenerator.Domain;

namespace TerraformGenerator.Infrastructure.Terraform;

// HCL typed by hand into a box comes as the person wrote it; terraform fmt lines it up like the rest of the code.
// Without terraform the files stay as they are.
public sealed class TerraformFormatter(ILogger<TerraformFormatter> logger)
{
    public async Task<GenerationResult> FormatAsync(GenerationResult result, CancellationToken cancellationToken)
    {
        if (result.HasErrors || !result.Files.TryGetValue("resources.tf", out var resources) || !resources.Contains("Written by hand", StringComparison.Ordinal))
        {
            return result;
        }

        var formatted = await FormatAsync(resources, cancellationToken);
        if (formatted is null)
        {
            return result;
        }

        var files = new Dictionary<string, string>(result.Files, StringComparer.Ordinal) { ["resources.tf"] = formatted };
        return result with { Files = files };
    }

    private async Task<string?> FormatAsync(string text, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("terraform", "fmt -no-color -")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode == 0 ? output.ReplaceLineEndings("\n") : null;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            logger.LogDebug(exception, "terraform is not installed; the hand-written HCL is not formatted");
            return null;
        }
    }
}
