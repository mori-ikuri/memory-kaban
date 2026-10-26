using System.Globalization;

namespace Bag;

internal static class FindTime
{
    private static readonly TimeSpan JapanOffset = TimeSpan.FromHours(9);
    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
    ];
    private static readonly string[] OffsetFormats =
    [
        "yyyy-MM-dd HH:mmzzz", "yyyy-MM-dd'T'HH:mmzzz",
        "yyyy-MM-dd HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    public static bool TryParseInput(string text, out DateTimeOffset timestamp)
    {
        DateTimeOffset? explicitTime = ParseInputOffset(text);
        if (explicitTime is not null)
        {
            timestamp = explicitTime.Value;
            return true;
        }
        timestamp = default;
        if (!DateTime.TryParseExact(text, LocalFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime local))
        {
            return false;
        }
        try
        {
            timestamp = new DateTimeOffset(local, JapanOffset).ToUniversalTime();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static DateTimeOffset? ParseStored(string? text)
    {
        if (text is null || text.Length < 20 || text[10] is not ('T' or ' '))
        {
            return null;
        }
        // Match the importer's explicit-zone timestamp interpretation, including
        // fractional input beyond the runtime's 100-nanosecond comparison precision.
        bool hasZone = text.EndsWith('Z') || (text[^3] == ':' && text[^6] is '+' or '-');
        if (hasZone && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTimeOffset timestamp))
        {
            try
            {
                // UTC's maximum date can fall outside the representable Japan date.
                // Such stored values remain intact and use the unknown-time policy.
                _ = timestamp.ToOffset(JapanOffset);
                return timestamp.ToUniversalTime();
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
        return null;
    }

    private static DateTimeOffset? ParseInputOffset(string text)
    {
        string candidate = text.EndsWith('Z') ? text[..^1] + "+00:00" : text;
        if (DateTimeOffset.TryParseExact(candidate, OffsetFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTimeOffset timestamp))
        {
            return timestamp.ToUniversalTime();
        }
        return null;
    }

    public static string FormatJapan(DateTimeOffset timestamp)
    {
        return timestamp.ToOffset(JapanOffset).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", CultureInfo.InvariantCulture);
    }

    public static string JapanIso(DateTimeOffset timestamp)
    {
        return timestamp.ToOffset(JapanOffset).ToString("O", CultureInfo.InvariantCulture);
    }
}
