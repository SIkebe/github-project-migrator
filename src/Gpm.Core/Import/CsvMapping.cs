using System.Globalization;

namespace Gpm.Core.Import;

/// <summary>
/// Minimal parser for the mapping CSV files used by <see cref="ItemImporter"/>:
/// repository mapping (source "org/repo" → target "org/repo") and user mapping
/// (source login → target login). Repository mappings use the two-column header
/// <c>source,target</c>. User mappings use GitHub Enterprise Importer's mannequin
/// reclaim CSV format (<c>mannequin-user,mannequin-id,target-user</c>), where the
/// mannequin ID column is ignored. Rows whose target column is blank are ignored,
/// so templates can be used as-is with only the needed rows filled in. Lookups are
/// case-insensitive (GitHub logins and repository names are case-insensitive).
/// </summary>
public static class CsvMapping
{
    /// <summary>Loads and parses a mapping CSV file.</summary>
    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(File.ReadLines(path));
    }

    /// <summary>Loads and parses a user mapping CSV file (header <c>mannequin-user,mannequin-id,target-user</c>).</summary>
    public static IReadOnlyDictionary<string, string> LoadUserMapping(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ParseUserMapping(File.ReadLines(path));
    }

    /// <summary>Parses mapping CSV lines (header <c>source,target</c> followed by one mapping per line).</summary>
    public static IReadOnlyDictionary<string, string> Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headerSeen = false;
        var lineNumber = 0;

        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length != 2)
            {
                throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                    $"Mapping CSV line {lineNumber}: expected exactly two columns 'source,target' but got '{line}'."));
            }

            var source = parts[0].Trim();
            var target = parts[1].Trim();

            if (!headerSeen)
            {
                if (!string.Equals(source, "source", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(target, "target", StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException("Mapping CSV must start with the header line 'source,target'.");
                }

                headerSeen = true;
                continue;
            }

            if (target.Length == 0)
            {
                continue; // Unfilled template row (no mapping for this source).
            }

            if (source.Length == 0)
            {
                throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                    $"Mapping CSV line {lineNumber}: source must be non-empty."));
            }

            map[source] = target;
        }

        if (!headerSeen)
        {
            throw new FormatException("Mapping CSV must start with the header line 'source,target'.");
        }

        return map;
    }

    /// <summary>Parses user mapping CSV lines (header <c>mannequin-user,mannequin-id,target-user</c>).</summary>
    public static IReadOnlyDictionary<string, string> ParseUserMapping(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        using var enumerator = lines.GetEnumerator();
        var headerLineNumber = 0;
        string? headerLine = null;
        while (enumerator.MoveNext())
        {
            headerLineNumber++;
            headerLine = enumerator.Current.Trim();
            if (headerLine.Length > 0)
            {
                break;
            }
        }

        if (string.IsNullOrEmpty(headerLine))
        {
            throw new FormatException("User mapping CSV must start with the header line 'mannequin-user,mannequin-id,target-user'.");
        }

        var headerParts = headerLine.Split(',').Select(p => p.Trim()).ToArray();
        if (headerParts.Length == 3
            && string.Equals(headerParts[0], "mannequin-user", StringComparison.OrdinalIgnoreCase)
            && string.Equals(headerParts[1], "mannequin-id", StringComparison.OrdinalIgnoreCase)
            && string.Equals(headerParts[2], "target-user", StringComparison.OrdinalIgnoreCase))
        {
            return ParseMannequinUserMapping(enumerator, headerLineNumber);
        }

        throw new FormatException("User mapping CSV must start with the header line 'mannequin-user,mannequin-id,target-user'.");
    }

    private static Dictionary<string, string> ParseMannequinUserMapping(IEnumerator<string> lines, int headerLineNumber)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = headerLineNumber;

        while (lines.MoveNext())
        {
            lineNumber++;
            var line = lines.Current.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length != 3)
            {
                throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                    $"Mapping CSV line {lineNumber}: expected exactly three columns 'mannequin-user,mannequin-id,target-user' but got '{line}'."));
            }

            var source = parts[0].Trim();
            var target = parts[2].Trim();

            if (target.Length == 0)
            {
                continue; // Unfilled mannequin CSV row (no mapping for this source).
            }

            if (source.Length == 0)
            {
                throw new FormatException(string.Create(CultureInfo.InvariantCulture,
                    $"Mapping CSV line {lineNumber}: mannequin-user must be non-empty."));
            }

            map[source] = target;
        }

        return map;
    }
}
