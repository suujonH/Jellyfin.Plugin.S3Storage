using System.Text;

namespace Jellyfin.Plugin.S3Storage;

/// <summary>Reads the Linux mount table to learn which file system, if any, is mounted at a directory.</summary>
internal static class MountInfo
{
    private const string MountInfoPath = "/proc/self/mountinfo";

    /// <summary>Returns the file system type (for example <c>fuse.s3fs</c>) mounted at <paramref name="mountPoint"/>; null when nothing is, and always null off Linux.</summary>
    public static string? FileSystemType(string mountPoint)
    {
        if (!OperatingSystem.IsLinux()) return null;
        return Parse(File.ReadLines(MountInfoPath), Path.TrimEndingDirectorySeparator(Path.GetFullPath(mountPoint)));
    }

    /// <summary>
    /// Finds the file system type of the mount at <paramref name="mountPoint"/> (an absolute path without trailing separator) in the lines
    /// of <c>/proc/self/mountinfo</c>. A directory mounted over repeatedly is served by the last mount listed.
    /// </summary>
    /// <remarks>
    /// A line reads <c>id parent major:minor root mountpoint options [optional fields...] - type source super-options</c>;
    /// spaces, tabs, line feeds and backslashes inside paths are written as octal escapes.
    /// </remarks>
    internal static string? Parse(IEnumerable<string> lines, string mountPoint)
    {
        string? type = null;
        foreach (var line in lines)
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) continue;
            var fields = line[..separator].Split(' ');
            if (fields.Length < 5 || Decode(fields[4]) != mountPoint) continue;
            var rest = line[(separator + 3)..];
            var end = rest.IndexOf(' ');
            type = end < 0 ? rest : rest[..end];
        }
        return type;
    }

    private static string Decode(string value)
    {
        if (!value.Contains('\\')) return value;
        var decoded = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            // One left-to-right pass: a decoded backslash must not start another escape.
            if (value[i] == '\\' && i + 3 < value.Length && Unescape(value.AsSpan(i + 1, 3)) is { } character)
            {
                decoded.Append(character);
                i += 3;
            }
            else decoded.Append(value[i]);
        }
        return decoded.ToString();
    }

    private static char? Unescape(ReadOnlySpan<char> octal) => octal switch
    {
        "040" => ' ',
        "011" => '\t',
        "012" => '\n',
        "134" => '\\',
        _ => null
    };
}
