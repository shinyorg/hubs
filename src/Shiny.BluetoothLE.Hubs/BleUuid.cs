namespace Shiny.BluetoothLE.Hubs;

internal static class BleUuid
{
    /// <summary>
    /// Validates and normalizes a 128-bit UUID (Android rejects short forms)
    /// </summary>
    public static string Normalize(string uuid, string paramName)
    {
        if (!Guid.TryParse(uuid, out var guid))
            throw new ArgumentException($"'{uuid}' must be a full 128-bit UUID", paramName);

        return guid.ToString();
    }
}
