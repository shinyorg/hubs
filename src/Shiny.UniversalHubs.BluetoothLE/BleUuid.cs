using System.Globalization;

namespace Shiny.UniversalHubs;

internal static class BleUuid
{
    // 0000xxxx-0000-1000-8000-00805F9B34FB - the Bluetooth base UUID short forms expand into
    const string BaseSuffix = "-0000-1000-8000-00805f9b34fb";


    /// <summary>
    /// Validates a UUID and gives its full 128-bit form - Android only takes those. 16-bit (<c>180F</c>) and 32-bit
    /// (<c>0000180F</c>) forms expand onto the Bluetooth base UUID; they advertise in 2 and 4 bytes instead of 16. Short
    /// UUIDs the Bluetooth SIG hasn't assigned to you work, but aren't compliant.
    /// </summary>
    public static string Normalize(string uuid, string paramName)
    {
        var trimmed = uuid?.Trim() ?? String.Empty;
        if (trimmed.Length is 4 or 8 && UInt32.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var shortForm))
            return shortForm.ToString("x8", CultureInfo.InvariantCulture) + BaseSuffix;

        if (!Guid.TryParse(trimmed, out var guid))
            throw new ArgumentException($"'{uuid}' must be a 128-bit UUID, or a 16-bit or 32-bit one (eg. 180F)", paramName);

        return guid.ToString();
    }


    /// <summary>
    /// How many bytes a (normalized) UUID takes in an advertisement: 2 for a 16-bit one, 4 for 32-bit, 16 otherwise
    /// </summary>
    public static int AdvertisedSize(string normalized)
    {
        if (!normalized.EndsWith(BaseSuffix, StringComparison.OrdinalIgnoreCase))
            return 16;

        return normalized.StartsWith("0000", StringComparison.Ordinal) ? 2 : 4;
    }
}


/// <summary>
/// What fits in a BLE advertisement: 31 bytes, 3 of them the flags, and each size of service UUID in a field of its own
/// (2 bytes of header plus the UUIDs). One 128-bit UUID fits beside a short name; two don't fit at all.
/// </summary>
internal static class BleAdvertisement
{
    public const int MaxLength = 31;
    const int FlagsLength = 3;
    const int FieldHeaderLength = 2;


    /// <summary>
    /// The bytes the service UUIDs of <paramref name="hubs"/> set up to be advertised take, with the flags
    /// </summary>
    public static int Length(IEnumerable<BleHubSettings> hubs)
    {
        var sizes = hubs
            .Where(x => x.Advertise)
            .Select(x => x.ServiceUuid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(BleUuid.AdvertisedSize);

        return FlagsLength + sizes.Sum(g => FieldHeaderLength + g.Key * g.Count());
    }


    /// <summary>
    /// Throws when every advertised hub's service, running or not, wouldn't fit in one advertisement
    /// </summary>
    public static void Check(IEnumerable<BleHubSettings> hubs)
    {
        var list = hubs.ToList();
        var length = Length(list);
        if (length > MaxLength)
        {
            var uuids = String.Join(", ", list.Where(x => x.Advertise).Select(x => x.ServiceUuid).Distinct(StringComparer.OrdinalIgnoreCase));
            throw new InvalidOperationException(
                $"The advertised BLE services ({uuids}) need {length} bytes and an advertisement holds {MaxLength} - use 16-bit or 32-bit " +
                "service UUIDs, share a service between hubs, or set advertise: false on hubs found another way"
            );
        }
    }
}
