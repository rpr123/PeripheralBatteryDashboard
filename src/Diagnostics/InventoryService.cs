using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using PeripheralBatteryDashboard.Core;
using PeripheralBatteryDashboard.Hardware;

namespace PeripheralBatteryDashboard.Diagnostics
{
    public sealed class InventorySnapshot
    {
        public int SchemaVersion { get; set; }
        public string Mode { get; set; }
        public string GeneratedAtUtc { get; set; }
        public bool ProviderBatteryRequestsSent { get; set; }
        public bool Complete { get; set; }
        public string CollectionMethod { get; set; }
        public int ProfileWarningCount { get; set; }
        public bool FriendlyNamesBestEffortRedacted { get; set; }
        public bool ExternalSearchRequiresLocalReview { get; set; }
        public bool DeviceGroupIdsArePseudonymousLocalIdentifiers { get; set; }
        public bool BluetoothServiceIdsArePseudonymousLocalIdentifiers { get; set; }
        public InventoryCoverage Coverage { get; set; }
        public IList<InventoryHidCollection> HidCollections { get; set; }
        public IList<InventoryBluetoothBatteryService> BluetoothBatteryServices { get; set; }
        public IList<string> WarningCodes { get; set; }

        public InventorySnapshot()
        {
            SchemaVersion = 1;
            Mode = "metadata-only";
            GeneratedAtUtc = string.Empty;
            Complete = true;
            CollectionMethod = "Windows HID descriptor metadata and Bluetooth standard Battery " +
                "Service interface metadata only; no HID input report read, output/Feature report, " +
                "GATT characteristic value read, or provider battery/status request";
            FriendlyNamesBestEffortRedacted = true;
            ExternalSearchRequiresLocalReview = true;
            DeviceGroupIdsArePseudonymousLocalIdentifiers = true;
            BluetoothServiceIdsArePseudonymousLocalIdentifiers = true;
            Coverage = new InventoryCoverage();
            HidCollections = new List<InventoryHidCollection>();
            BluetoothBatteryServices = new List<InventoryBluetoothBatteryService>();
            WarningCodes = new List<string>();
        }
    }

    public sealed class InventoryCoverage
    {
        public bool HidCollections { get; set; }
        public bool BluetoothStandardBatteryServices { get; set; }
        public bool XInputOnlyDevices { get; set; }
        public bool BluetoothNonHidDevices { get; set; }
        public bool AudioOnlyDevices { get; set; }

        public InventoryCoverage()
        {
            HidCollections = true;
            BluetoothStandardBatteryServices = true;
        }
    }

    public sealed class InventoryBluetoothBatteryService
    {
        public string LocalServiceId { get; set; }
        public bool HasHardwareIdentity { get; set; }
        public string VendorIdSource { get; set; }
        public string VendorId { get; set; }
        public string ProductId { get; set; }
        public string BestEffortSanitizedFriendlyName { get; set; }
        public string MatchStatus { get; set; }
        public bool ResearchCandidate { get; set; }
        public int MatchedProfileCount { get; set; }

        public InventoryBluetoothBatteryService()
        {
            LocalServiceId = string.Empty;
            VendorIdSource = string.Empty;
            VendorId = string.Empty;
            ProductId = string.Empty;
            BestEffortSanitizedFriendlyName = string.Empty;
            MatchStatus = "unmatched-standard-battery-service";
        }
    }

    public sealed class InventoryHidCollection
    {
        public string VendorId { get; set; }
        public string ProductId { get; set; }
        public string VersionNumber { get; set; }
        public int? InterfaceNumber { get; set; }
        public string UsagePage { get; set; }
        public string Usage { get; set; }
        public int? InputReportLength { get; set; }
        public int? OutputReportLength { get; set; }
        public int? FeatureReportLength { get; set; }
        public string BestEffortSanitizedProductString { get; set; }
        public string MatchStatus { get; set; }
        public string DeviceGroupId { get; set; }
        public string DeviceMatchStatus { get; set; }
        public bool ResearchCandidate { get; set; }
        public int MatchedProfileCount { get; set; }
        public int RelatedProfileCount { get; set; }

        public InventoryHidCollection()
        {
            VendorId = string.Empty;
            ProductId = string.Empty;
            VersionNumber = string.Empty;
            UsagePage = string.Empty;
            Usage = string.Empty;
            BestEffortSanitizedProductString = string.Empty;
            MatchStatus = "unmatched-device";
            DeviceGroupId = string.Empty;
            DeviceMatchStatus = "unmatched-device";
        }
    }

    /// <summary>
    /// Collects OS-exposed HID descriptor and standard Bluetooth Battery Service interface
    /// metadata. It never opens a provider, reads HID input or GATT characteristic values,
    /// sends output/feature/interrupt reports, or asks for battery status.
    /// </summary>
    public sealed class InventoryService
    {
        private readonly IList<DeviceProfile> _profiles;
        private readonly HidDeviceEnumerator _hidDevices;

        public InventoryService(IList<DeviceProfile> profiles, HidDeviceEnumerator hidDevices)
        {
            _profiles = profiles ?? new List<DeviceProfile>();
            _hidDevices = hidDevices ?? throw new ArgumentNullException("hidDevices");
        }

        public InventorySnapshot Collect()
        {
            IList<HidDeviceDescriptor> descriptors = new List<HidDeviceDescriptor>();
            IList<BluetoothGattBatteryServiceDescriptor> bluetoothServices =
                new List<BluetoothGattBatteryServiceDescriptor>();
            List<string> warningCodes = new List<string>();
            try
            {
                HidEnumerationResult enumeration = _hidDevices.EnumerateMetadata();
                descriptors = enumeration.Devices;
                foreach (string warningCode in enumeration.WarningCodes)
                    if (!warningCodes.Contains(warningCode))
                        warningCodes.Add(warningCode);
            }
            catch
            {
                warningCodes.Add("hid-enumeration-failed");
            }

            try
            {
                BluetoothGattBatteryServiceEnumeration enumeration =
                    BluetoothGattBatteryReader.EnumerateBatteryServicesMetadata();
                bluetoothServices = enumeration.Services;
                foreach (string warningCode in enumeration.WarningCodes)
                    if (!warningCodes.Contains(warningCode))
                        warningCodes.Add(warningCode);
            }
            catch
            {
                warningCodes.Add("bluetooth-gatt-enumeration-failed");
            }

            InventorySnapshot snapshot = BuildSnapshot(descriptors, bluetoothServices,
                _profiles, DateTime.UtcNow);
            snapshot.Complete = warningCodes.Count == 0;
            foreach (string warningCode in warningCodes)
                snapshot.WarningCodes.Add(warningCode);
            return snapshot;
        }

        public string ToJson(InventorySnapshot snapshot)
        {
            JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            return json.Serialize(new
            {
                schemaVersion = snapshot.SchemaVersion,
                mode = snapshot.Mode,
                generatedAtUtc = snapshot.GeneratedAtUtc,
                providerBatteryRequestsSent = snapshot.ProviderBatteryRequestsSent,
                complete = snapshot.Complete,
                collectionMethod = snapshot.CollectionMethod,
                profileWarningCount = snapshot.ProfileWarningCount,
                privacy = new
                {
                    friendlyNamesBestEffortRedacted = snapshot.FriendlyNamesBestEffortRedacted,
                    externalSearchRequiresLocalReview = snapshot.ExternalSearchRequiresLocalReview,
                    deviceGroupIdsArePseudonymousLocalIdentifiers =
                        snapshot.DeviceGroupIdsArePseudonymousLocalIdentifiers,
                    bluetoothServiceIdsArePseudonymousLocalIdentifiers =
                        snapshot.BluetoothServiceIdsArePseudonymousLocalIdentifiers
                },
                coverage = new
                {
                    hidCollections = snapshot.Coverage.HidCollections,
                    bluetoothStandardBatteryServices =
                        snapshot.Coverage.BluetoothStandardBatteryServices,
                    xinputOnlyDevices = snapshot.Coverage.XInputOnlyDevices,
                    bluetoothNonHidDevices = snapshot.Coverage.BluetoothNonHidDevices,
                    audioOnlyDevices = snapshot.Coverage.AudioOnlyDevices
                },
                hidCollections = snapshot.HidCollections.Select(device => new
                {
                    deviceGroupId = device.DeviceGroupId,
                    vendorId = device.VendorId,
                    productId = device.ProductId,
                    versionNumber = device.VersionNumber,
                    interfaceNumber = device.InterfaceNumber,
                    usagePage = device.UsagePage,
                    usage = device.Usage,
                    inputReportLength = device.InputReportLength,
                    outputReportLength = device.OutputReportLength,
                    featureReportLength = device.FeatureReportLength,
                    bestEffortSanitizedProductString = device.BestEffortSanitizedProductString,
                    deviceMatchStatus = device.DeviceMatchStatus,
                    researchCandidate = device.ResearchCandidate,
                    matchStatus = device.MatchStatus,
                    matchedProfileCount = device.MatchedProfileCount,
                    relatedProfileCount = device.RelatedProfileCount
                }).ToArray(),
                bluetoothBatteryServices = snapshot.BluetoothBatteryServices.Select(service => new
                {
                    localServiceId = service.LocalServiceId,
                    hasHardwareIdentity = service.HasHardwareIdentity,
                    vendorIdSource = service.VendorIdSource,
                    vendorId = service.VendorId,
                    productId = service.ProductId,
                    bestEffortSanitizedFriendlyName =
                        service.BestEffortSanitizedFriendlyName,
                    matchStatus = service.MatchStatus,
                    researchCandidate = service.ResearchCandidate,
                    matchedProfileCount = service.MatchedProfileCount
                }).ToArray(),
                warningCodes = snapshot.WarningCodes
            });
        }

        internal static InventorySnapshot BuildSnapshot(
            IEnumerable<HidDeviceDescriptor> descriptors,
            IEnumerable<DeviceProfile> profiles,
            DateTime generatedAtUtc)
        {
            return BuildSnapshot(descriptors,
                Enumerable.Empty<BluetoothGattBatteryServiceDescriptor>(),
                profiles, generatedAtUtc);
        }

        internal static InventorySnapshot BuildSnapshot(
            IEnumerable<HidDeviceDescriptor> descriptors,
            IEnumerable<BluetoothGattBatteryServiceDescriptor> bluetoothServices,
            IEnumerable<DeviceProfile> profiles,
            DateTime generatedAtUtc)
        {
            List<DeviceProfile> profileList = (profiles ?? Enumerable.Empty<DeviceProfile>()).ToList();
            InventorySnapshot snapshot = new InventorySnapshot
            {
                GeneratedAtUtc = generatedAtUtc.ToUniversalTime().ToString("o"),
                ProviderBatteryRequestsSent = false,
                HidCollections = new List<InventoryHidCollection>(),
                BluetoothBatteryServices = new List<InventoryBluetoothBatteryService>()
            };
            List<DeviceProfile> hidProfiles = profileList
                .Where(profile => profile != null && profile.Match != null &&
                    string.Equals(profile.Match.Transport, "hid",
                        StringComparison.OrdinalIgnoreCase))
                .ToList();

            List<HidDeviceDescriptor> orderedDescriptors = (descriptors ?? Enumerable.Empty<HidDeviceDescriptor>())
                .OrderBy(device => device.VendorId)
                .ThenBy(device => device.ProductId)
                .ThenBy(device => device.InterfaceNumber ?? 255)
                .ThenBy(device => device.UsagePage)
                .ThenBy(device => device.Usage)
                .ToList();

            foreach (IGrouping<string, HidDeviceDescriptor> group in orderedDescriptors.GroupBy(
                BuildPrivateGroupKey,
                StringComparer.Ordinal))
            {
                List<DeviceProfile> relatedProfiles = hidProfiles
                    .Where(profile => profile.Match.ParsedVendorId == group.First().VendorId &&
                        profile.Match.ParsedProductIds.Contains(group.First().ProductId))
                    .ToList();
                bool groupHasExactSelector = group.Any(descriptor => hidProfiles.Any(profile =>
                    descriptor.Matches(profile) && HasExactSelector(profile)));
                bool groupHasBroadSelector = group.Any(descriptor => hidProfiles.Any(profile =>
                    descriptor.Matches(profile) && !HasExactSelector(profile)));
                string deviceMatchStatus = groupHasExactSelector
                    ? "exact-profile-selector-present"
                    : (groupHasBroadSelector
                        ? "broad-profile-selector-present"
                        : (relatedProfiles.Count > 0
                            ? "known-vid-pid-selector-missing"
                            : "unmatched-device"));

                HidDeviceDescriptor first = group.First();
                string deviceGroupId = BuildDeviceGroupId(first, group.Key);

                foreach (HidDeviceDescriptor descriptor in group)
                {
                    List<DeviceProfile> matchedProfiles = hidProfiles
                        .Where(descriptor.Matches)
                        .ToList();
                    bool hasExactSelector = matchedProfiles.Any(HasExactSelector);

                    snapshot.HidCollections.Add(new InventoryHidCollection
                    {
                        DeviceGroupId = deviceGroupId,
                        VendorId = FormatHex(descriptor.VendorId),
                        ProductId = FormatHex(descriptor.ProductId),
                        VersionNumber = FormatHex(descriptor.VersionNumber),
                        InterfaceNumber = descriptor.InterfaceNumber,
                        UsagePage = FormatHex(descriptor.UsagePage),
                        Usage = FormatHex(descriptor.Usage),
                        InputReportLength = descriptor.CapabilitiesAvailable
                            ? (int?)descriptor.InputReportLength : null,
                        OutputReportLength = descriptor.CapabilitiesAvailable
                            ? (int?)descriptor.OutputReportLength : null,
                        FeatureReportLength = descriptor.CapabilitiesAvailable
                            ? (int?)descriptor.FeatureReportLength : null,
                        BestEffortSanitizedProductString = SanitizeProductName(descriptor.ProductName),
                        DeviceMatchStatus = deviceMatchStatus,
                        ResearchCandidate = !groupHasExactSelector,
                        MatchStatus = hasExactSelector
                            ? "exact-selector-match"
                            : (matchedProfiles.Count > 0
                                ? "broad-selector-match"
                                : (relatedProfiles.Count > 0
                                    ? "same-vid-pid-sibling"
                                    : "unmatched-hid-collection")),
                        MatchedProfileCount = matchedProfiles.Count,
                        RelatedProfileCount = relatedProfiles.Count
                    });
                }
            }

            List<DeviceProfile> bluetoothProfiles = profileList
                .Where(IsStandardBluetoothBatteryProfile)
                .ToList();
            List<BluetoothGattBatteryServiceDescriptor> orderedBluetoothServices =
                (bluetoothServices ?? Enumerable.Empty<BluetoothGattBatteryServiceDescriptor>())
                    .OrderBy(item => item.VendorId)
                    .ThenBy(item => item.ProductId)
                    .ThenBy(item => item.FriendlyName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            foreach (BluetoothGattBatteryServiceDescriptor service in
                orderedBluetoothServices)
            {
                List<DeviceProfile> matchedProfiles = bluetoothProfiles
                    .Where(profile => StandardBluetoothProfileMatches(profile, service))
                    .ToList();
                bool ambiguousProfile = matchedProfiles.Any(profile =>
                    orderedBluetoothServices
                        .Where(candidate => StandardBluetoothProfileMatches(profile, candidate))
                        .Select(candidate => candidate.LocalServiceId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() > 1);
                snapshot.BluetoothBatteryServices.Add(
                    new InventoryBluetoothBatteryService
                    {
                        LocalServiceId = service.LocalServiceId,
                        HasHardwareIdentity = service.VendorId.HasValue &&
                            service.ProductId.HasValue,
                        VendorIdSource = service.VendorIdSource.HasValue
                            ? FormatByteHex(service.VendorIdSource.Value) : string.Empty,
                        VendorId = service.VendorId.HasValue
                            ? FormatHex(service.VendorId.Value) : string.Empty,
                        ProductId = service.ProductId.HasValue
                            ? FormatHex(service.ProductId.Value) : string.Empty,
                        BestEffortSanitizedFriendlyName =
                            SanitizeProductName(service.FriendlyName),
                        MatchStatus = ambiguousProfile
                            ? "ambiguous-standard-battery-profile"
                            : (matchedProfiles.Count > 0
                                ? "exact-standard-battery-profile-present"
                                : "unmatched-standard-battery-service"),
                        ResearchCandidate = matchedProfiles.Count == 0 ||
                            ambiguousProfile,
                        MatchedProfileCount = matchedProfiles.Count
                    });
            }
            return snapshot;
        }

        private static bool IsStandardBluetoothBatteryProfile(DeviceProfile profile)
        {
            if (profile == null || profile.Match == null)
                return false;
            return string.Equals(profile.ProviderId, "builtin.bluetooth.gatt-battery",
                        StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(profile.ProviderId, "builtin.xbox.xinput",
                       StringComparison.OrdinalIgnoreCase);
        }

        internal static bool StandardBluetoothProfileMatches(DeviceProfile profile,
            BluetoothGattBatteryServiceDescriptor service)
        {
            if (!IsStandardBluetoothBatteryProfile(profile) || service == null)
                return false;

            bool hasLocalIdentity = profile.Match.HasValidBluetoothServiceId;
            bool hasHardwareIdentity = profile.Match.ParsedVendorId.HasValue &&
                profile.Match.ParsedProductIds.Count > 0;
            if (!hasLocalIdentity && !hasHardwareIdentity)
                return false;
            if (hasLocalIdentity &&
                !string.Equals(profile.Match.BluetoothServiceId, service.LocalServiceId,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (hasHardwareIdentity &&
                (!service.VendorId.HasValue || !service.ProductId.HasValue ||
                 profile.Match.ParsedVendorId != service.VendorId ||
                 !profile.Match.ParsedProductIds.Contains(service.ProductId.Value)))
                return false;

            if (string.Equals(profile.ProviderId, "builtin.xbox.xinput",
                    StringComparison.OrdinalIgnoreCase) &&
                service.VendorIdSource != 0x02)
                return false;

            string friendlyNameContains = GetProfileOptionString(profile,
                "BluetoothNameContains");
            if (!string.IsNullOrWhiteSpace(friendlyNameContains) &&
                (string.IsNullOrWhiteSpace(service.FriendlyName) ||
                 service.FriendlyName.IndexOf(friendlyNameContains,
                     StringComparison.OrdinalIgnoreCase) < 0))
                return false;
            return true;
        }

        private static string GetProfileOptionString(DeviceProfile profile, string key)
        {
            object value;
            if (profile != null && profile.ProviderOptions != null &&
                profile.ProviderOptions.TryGetValue(key, out value) && value != null)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            return null;
        }

        internal static string SanitizeProductName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            StringBuilder cleaned = new StringBuilder();
            bool previousSpace = false;
            foreach (char character in value.Trim())
            {
                UnicodeCategory category = char.GetUnicodeCategory(character);
                char normalized = char.IsControl(character) ||
                    category == UnicodeCategory.Format ? ' ' : character;
                if (char.IsWhiteSpace(normalized))
                {
                    if (previousSpace)
                        continue;
                    normalized = ' ';
                    previousSpace = true;
                }
                else
                {
                    previousSpace = false;
                }
                cleaned.Append(normalized);
            }

            string result = cleaned.ToString();
            string userName = Environment.UserName;
            if (!string.IsNullOrWhiteSpace(userName))
                result = ReplaceOrdinalIgnoreCase(result, userName, "[redacted]");
            string machineName = Environment.MachineName;
            if (!string.IsNullOrWhiteSpace(machineName))
                result = ReplaceOrdinalIgnoreCase(result, machineName, "[redacted]");
            string userDomainName = Environment.UserDomainName;
            if (!string.IsNullOrWhiteSpace(userDomainName))
                result = ReplaceOrdinalIgnoreCase(result, userDomainName, "[redacted]");
            result = Regex.Replace(result,
                @"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", "[redacted]");
            result = Regex.Replace(result,
                @"(?i)\b(?:[0-9a-f]{4}\.){2}[0-9a-f]{4}\b", "[redacted]");
            result = Regex.Replace(result, @"(?i)\b[0-9a-f]{12}\b", "[redacted]");
            result = Regex.Replace(result,
                @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", "[redacted]");
            result = Regex.Replace(result,
                @"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b",
                "[redacted]");
            result = Regex.Replace(result, @"(?i)\b[0-9a-f]{16,}\b", "[redacted]");
            result = Regex.Replace(result, @"(?i)(?:[a-z]:\\|\\\\)[^\s]*", "[redacted]");
            if (result.Length > 96)
                result = result.Substring(0, 95) + "…";
            return result;
        }

        private static string ReplaceOrdinalIgnoreCase(string value, string oldValue, string newValue)
        {
            int startIndex = 0;
            StringBuilder result = new StringBuilder();
            while (startIndex < value.Length)
            {
                int matchIndex = value.IndexOf(oldValue, startIndex, StringComparison.OrdinalIgnoreCase);
                if (matchIndex < 0)
                {
                    result.Append(value, startIndex, value.Length - startIndex);
                    break;
                }
                result.Append(value, startIndex, matchIndex - startIndex);
                result.Append(newValue);
                startIndex = matchIndex + oldValue.Length;
            }
            return result.ToString();
        }

        private static bool HasExactSelector(DeviceProfile profile)
        {
            return profile != null && profile.Match != null &&
                (profile.Match.InterfaceNumber.HasValue ||
                 profile.Match.RequireNoInterfaceNumber) &&
                profile.Match.ParsedUsagePage.HasValue &&
                profile.Match.ParsedUsage.HasValue;
        }

        private static string BuildPrivateGroupKey(HidDeviceDescriptor descriptor)
        {
            if (descriptor.ContainerId.HasValue)
                return "container|" + descriptor.ContainerId.Value.ToString("N");
            return string.Format(CultureInfo.InvariantCulture,
                "collection|{0:X4}|{1:X4}|{2:X4}|{3}|{4:X4}|{5:X4}|{6}|{7}|{8}",
                descriptor.VendorId, descriptor.ProductId, descriptor.VersionNumber,
                descriptor.InterfaceNumber.HasValue
                    ? descriptor.InterfaceNumber.Value.ToString(CultureInfo.InvariantCulture)
                    : "--",
                descriptor.UsagePage, descriptor.Usage, descriptor.InputReportLength,
                descriptor.OutputReportLength, descriptor.FeatureReportLength);
        }

        private static string BuildDeviceGroupId(HidDeviceDescriptor descriptor, string groupKey)
        {
            if (descriptor.ContainerId.HasValue)
            {
                byte[] digest;
                using (SHA256 sha = SHA256.Create())
                    digest = sha.ComputeHash(Encoding.UTF8.GetBytes(groupKey));
                StringBuilder suffix = new StringBuilder(12);
                for (int index = 0; index < 6; index++)
                    suffix.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
                return "hid-c-" + suffix;
            }
            return string.Format(CultureInfo.InvariantCulture,
                "hid-s-{0:X4}-{1:X4}-{2:X4}-{3}-{4:X4}-{5:X4}",
                descriptor.VendorId, descriptor.ProductId, descriptor.VersionNumber,
                descriptor.InterfaceNumber.HasValue
                    ? descriptor.InterfaceNumber.Value.ToString("X2", CultureInfo.InvariantCulture)
                    : "--",
                descriptor.UsagePage, descriptor.Usage).ToLowerInvariant();
        }

        private static string FormatHex(ushort value)
        {
            return "0x" + value.ToString("X4");
        }

        private static string FormatByteHex(byte value)
        {
            return "0x" + value.ToString("X2", CultureInfo.InvariantCulture);
        }
    }
}
