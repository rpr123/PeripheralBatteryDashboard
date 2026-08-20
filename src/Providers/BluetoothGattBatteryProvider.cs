using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PeripheralBatteryDashboard.Core;
using PeripheralBatteryDashboard.Hardware;

namespace PeripheralBatteryDashboard.Providers
{
    /// <summary>
    /// Reads the Bluetooth SIG Battery Service (0x180F) Battery Level
    /// characteristic (0x2A19) for an exact per-PC service identity, with VID/PID
    /// as optional additional conditions. It does not send a vendor command and
    /// intentionally does not infer charging state.
    /// </summary>
    public sealed class BluetoothGattBatteryProvider : IBatteryProvider
    {
        public string ProviderId { get { return "builtin.bluetooth.gatt-battery"; } }

        public Task<BatteryReading> ReadAsync(DeviceProfile profile,
            BatteryReadContext context, CancellationToken cancellationToken)
        {
            return Task.Run(() => Read(profile, cancellationToken), cancellationToken);
        }

        private static BatteryReading Read(DeviceProfile profile, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!HasExactIdentity(profile))
            {
                return BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Unsupported,
                    "정확한 Bluetooth 식별자 필요",
                    "표준 GATT 배터리 조회에는 인벤토리에서 확인한 이 PC 전용 로컬 서비스 ID가 필요합니다.",
                    "exact-bluetooth-identity-required");
            }

            string friendlyName = GetStringOption(profile, "BluetoothNameContains", null);
            BluetoothGattBatteryReadResult result =
                BluetoothGattBatteryReader.ReadPercent(friendlyName,
                profile.Match.ParsedVendorId,
                profile.Match.ParsedProductIds,
                profile.Match.BluetoothServiceId);
            if (result.Status == BluetoothGattBatteryReadStatus.Success &&
                result.Percent.HasValue)
            {
                return ProviderSupport.Connected(profile, result.Percent.Value,
                    BatteryReading.BandFromPercent(result.Percent.Value),
                    DeviceChargeState.Unknown,
                    "연결됨",
                    "Bluetooth LE GATT · 표준 Battery Service 180F/2A19",
                    false);
            }

            if (result.Status == BluetoothGattBatteryReadStatus.FoundUnavailable)
            {
                return PresentUnavailable(profile, DeviceConnectionState.Busy,
                    "Bluetooth 배터리 조회 대기",
                    "표준 Battery Service는 감지됐지만 현재 값을 읽을 수 없습니다.",
                    "standard-battery-read-unavailable");
            }
            if (result.Status == BluetoothGattBatteryReadStatus.Ambiguous)
            {
                return PresentUnavailable(profile, DeviceConnectionState.Error,
                    "Bluetooth 장치 구분 필요",
                    "같은 조건의 표준 Battery Service가 " + result.CandidateCount +
                    "개 감지됐습니다. 이름 보조 조건이나 더 좁은 식별자가 필요합니다.",
                    "standard-battery-service-ambiguous");
            }
            if (result.Status == BluetoothGattBatteryReadStatus.EnumerationUnavailable)
            {
                if (result.CandidateCount > 0)
                {
                    return PresentUnavailable(profile, DeviceConnectionState.Error,
                        "Bluetooth 열거 불완전",
                        "정확한 표준 Battery Service는 감지됐지만 다른 후보를 완전히 확인하지 못해 값을 읽지 않았습니다.",
                        "standard-battery-enumeration-unavailable");
                }
                return BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Error,
                    "Bluetooth 열거 불완전",
                    "Windows에서 표준 Battery Service 목록을 완전히 확인하지 못했습니다.",
                    "standard-battery-enumeration-unavailable");
            }

            return BatteryReading.Unavailable(profile,
                DeviceConnectionState.Disconnected,
                "Bluetooth 장치 연결 안 됨",
                "정확히 일치하는 표준 Battery Service가 현재 감지되지 않았습니다.",
                "standard-battery-service-not-found");
        }

        internal static bool HasExactIdentity(DeviceProfile profile)
        {
            return profile != null && profile.Match != null &&
                   profile.Match.HasValidBluetoothServiceId;
        }

        private static BatteryReading PresentUnavailable(DeviceProfile profile,
            DeviceConnectionState connection, string status, string detail,
            string errorCode)
        {
            BatteryReading reading = BatteryReading.Unavailable(profile, connection,
                status, detail, errorCode);
            reading.Presence = DevicePresenceState.Present;
            return reading;
        }

        private static string GetStringOption(DeviceProfile profile, string key,
            string defaultValue)
        {
            object value;
            if (profile != null && profile.ProviderOptions != null &&
                profile.ProviderOptions.TryGetValue(key, out value) && value != null)
            {
                string text = Convert.ToString(value);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            return defaultValue;
        }
    }
}
