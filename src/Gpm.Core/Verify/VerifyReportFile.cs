using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gpm.Core.Verify;

public static class VerifyReportFile
{
    public static async Task SaveAsync(VerifyReport report, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(fullPath);
        await JsonSerializer.SerializeAsync(
            stream,
            report,
            VerifyReportJsonContext.Default.VerifyReport,
            cancellationToken).ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(VerifyReport))]
internal sealed partial class VerifyReportJsonContext : JsonSerializerContext;
