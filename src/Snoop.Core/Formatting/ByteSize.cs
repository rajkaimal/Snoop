using System.Globalization;

namespace Snoop.Core.Formatting;

/// <summary>Formats byte counts for people: 512 B, 1.5 KB, 86.96 KB, 3.2 MB.</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            return "-" + Format(-bytes);
        }

        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var format = value >= 100 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }
}
