using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PeripheralBatteryDashboard.Core
{
    public enum DeviceConnectionState
    {
        Unknown,
        Connected,
        Sleeping,
        Disconnected,
        Busy,
        Unsupported,
        Error
    }

    public enum DeviceChargeState
    {
        Unknown,
        Discharging,
        Charging,
        Full,
        NotApplicable
    }

    public enum BatteryLevelBand
    {
        Unknown,
        Critical,
        Low,
        Medium,
        High,
        Full
    }

    public sealed class DeviceProfileDocument
    {
        public int SchemaVersion { get; set; }
        public List<DeviceProfile> Profiles { get; set; }

        public DeviceProfileDocument()
        {
            SchemaVersion = 1;
            Profiles = new List<DeviceProfile>();
        }
    }

    public sealed class DeviceProfile
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string Category { get; set; }
        public string Icon { get; set; }
        public string ProviderId { get; set; }
        public bool Enabled { get; set; }
        public int DisplayOrder { get; set; }
        public int PollSeconds { get; set; }
        public int TimeoutMilliseconds { get; set; }
        public int LowBatteryPercent { get; set; }
        public DeviceMatch Match { get; set; }
        public Dictionary<string, object> ProviderOptions { get; set; }

        public DeviceProfile()
        {
            Id = string.Empty;
            DisplayName = string.Empty;
            Category = "other";
            Icon = "device";
            ProviderId = string.Empty;
            Enabled = true;
            PollSeconds = 30;
            TimeoutMilliseconds = 1500;
            LowBatteryPercent = 20;
            Match = new DeviceMatch();
            ProviderOptions = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        public int EffectivePollSeconds
        {
            get { return PollSeconds < 10 ? 30 : Math.Min(PollSeconds, 3600); }
        }

        public int EffectiveTimeoutMilliseconds
        {
            get { return TimeoutMilliseconds < 250 ? 1500 : Math.Min(TimeoutMilliseconds, 5000); }
        }
    }

    public sealed class DeviceMatch
    {
        public string Transport { get; set; }
        public string VendorId { get; set; }
        public List<string> ProductIds { get; set; }
        public int? InterfaceNumber { get; set; }
        public string UsagePage { get; set; }
        public string Usage { get; set; }
        public int? XInputUserIndex { get; set; }

        public DeviceMatch()
        {
            Transport = "hid";
            VendorId = string.Empty;
            ProductIds = new List<string>();
            UsagePage = string.Empty;
            Usage = string.Empty;
        }

        public ushort? ParsedVendorId
        {
            get { return HexValue.TryParseUInt16(VendorId); }
        }

        public List<ushort> ParsedProductIds
        {
            get
            {
                List<ushort> result = new List<ushort>();
                foreach (string value in ProductIds ?? new List<string>())
                {
                    ushort? parsed = HexValue.TryParseUInt16(value);
                    if (parsed.HasValue)
                        result.Add(parsed.Value);
                }
                return result;
            }
        }

        public ushort? ParsedUsagePage
        {
            get { return HexValue.TryParseUInt16(UsagePage); }
        }

        public ushort? ParsedUsage
        {
            get { return HexValue.TryParseUInt16(Usage); }
        }
    }

    public sealed class BatteryReading
    {
        public string ProfileId { get; set; }
        public string DisplayName { get; set; }
        public string Category { get; set; }
        public int? Percent { get; set; }
        public bool IsApproximate { get; set; }
        public BatteryLevelBand Band { get; set; }
        public DeviceConnectionState Connection { get; set; }
        public DeviceChargeState Charge { get; set; }
        public string StatusText { get; set; }
        public string DetailText { get; set; }
        public DateTime SampledAtUtc { get; set; }
        public bool IsStale { get; set; }
        public string ErrorCode { get; set; }

        public BatteryReading()
        {
            ProfileId = string.Empty;
            DisplayName = string.Empty;
            Category = "other";
            Band = BatteryLevelBand.Unknown;
            Connection = DeviceConnectionState.Unknown;
            Charge = DeviceChargeState.Unknown;
            StatusText = "확인 중";
            DetailText = string.Empty;
            ErrorCode = string.Empty;
            SampledAtUtc = DateTime.UtcNow;
        }

        public static BatteryReading Unavailable(DeviceProfile profile, DeviceConnectionState state, string status, string detail, string errorCode)
        {
            return new BatteryReading
            {
                ProfileId = profile.Id,
                DisplayName = profile.DisplayName,
                Category = profile.Category,
                Connection = state,
                Charge = DeviceChargeState.Unknown,
                Band = BatteryLevelBand.Unknown,
                StatusText = status,
                DetailText = detail ?? string.Empty,
                ErrorCode = errorCode ?? string.Empty,
                SampledAtUtc = DateTime.UtcNow
            };
        }

        public static BatteryLevelBand BandFromPercent(int? percent)
        {
            if (!percent.HasValue)
                return BatteryLevelBand.Unknown;
            if (percent.Value <= 10)
                return BatteryLevelBand.Critical;
            if (percent.Value <= 25)
                return BatteryLevelBand.Low;
            if (percent.Value <= 55)
                return BatteryLevelBand.Medium;
            if (percent.Value < 95)
                return BatteryLevelBand.High;
            return BatteryLevelBand.Full;
        }
    }

    public sealed class BatteryReadContext
    {
        public Hardware.HidDeviceEnumerator HidDevices { get; private set; }

        public BatteryReadContext(Hardware.HidDeviceEnumerator hidDevices)
        {
            HidDevices = hidDevices;
        }
    }

    public interface IBatteryProvider
    {
        string ProviderId { get; }
        Task<BatteryReading> ReadAsync(DeviceProfile profile, BatteryReadContext context, CancellationToken cancellationToken);
    }

    public interface IBatteryProviderPlugin
    {
        string PluginId { get; }
        IEnumerable<IBatteryProvider> CreateProviders();
    }

    public static class HexValue
    {
        public static ushort? TryParseUInt16(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string text = value.Trim();
            NumberStyles style = NumberStyles.Integer;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(2);
                style = NumberStyles.AllowHexSpecifier;
            }

            ushort parsed;
            if (ushort.TryParse(text, style, CultureInfo.InvariantCulture, out parsed))
                return parsed;
            return null;
        }

        public static string Format(ushort value)
        {
            return "0x" + value.ToString("X4", CultureInfo.InvariantCulture);
        }
    }
}
