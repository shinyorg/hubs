using System.Text;
using System.Text.RegularExpressions;
using Shiny.BluetoothLE.Hubs;

namespace Shiny.UniversalHubs;

/// <summary>
/// What is Multipeer's own: the service type and the advertisement (discoveryInfo) keys. One MCSession per hub
/// connection carries <see cref="PeerProtocol"/> messages; files ride Multipeer resources named by
/// <see cref="PeerProtocol.FileResourcePrefix"/>.
/// </summary>
public static partial class MultipeerProtocol
{
    // advertisement (discoveryInfo) keys - the same as the mDNS TXT keys
    public const string InfoProtocol = UniversalHubProtocol.TxtProtocol;
    public const string InfoHostId = UniversalHubProtocol.TxtHostId;
    public const string InfoName = UniversalHubProtocol.TxtName;
    public const string InfoHubs = UniversalHubProtocol.TxtHubs;

    /// <summary>
    /// Throws unless <paramref name="serviceType"/> is a Multipeer service type: 1-15 characters of lowercase ASCII letters,
    /// digits and hyphens, with at least one letter and no leading, trailing or doubled hyphen. It must differ from the
    /// hubs' mDNS service types - Multipeer advertises over Bonjour as <c>_{serviceType}._tcp</c> too.
    /// </summary>
    public static void ValidateServiceType(string serviceType)
    {
        if (String.IsNullOrWhiteSpace(serviceType) || !ServiceTypeRegex().IsMatch(serviceType) || serviceType.Contains("--", StringComparison.Ordinal) || !serviceType.Any(Char.IsAsciiLetterLower))
            throw new ArgumentException($"'{serviceType}' is not a Multipeer service type - expected 1-15 lowercase letters, digits and hyphens, like mygame-mc", nameof(serviceType));
    }

    /// <summary>
    /// The NSBonjourServices entries an iOS / macOS app needs for <paramref name="serviceType"/>
    /// </summary>
    public static IReadOnlyList<string> BonjourServices(string serviceType) => [$"_{serviceType}._tcp", $"_{serviceType}._udp"];

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,13}[a-z0-9])?$")]
    private static partial Regex ServiceTypeRegex();
}
