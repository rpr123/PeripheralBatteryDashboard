using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PeripheralBatteryDashboard.Core;
using PeripheralBatteryDashboard.Hardware;

namespace PeripheralBatteryDashboard.Providers
{
    public sealed class XboxControllerProvider : IBatteryProvider
    {
        public string ProviderId { get { return "builtin.xbox.xinput"; } }

        public Task<BatteryReading> ReadAsync(DeviceProfile profile, BatteryReadContext context, CancellationToken cancellationToken)
        {
            return Task.Run(() => Read(profile, cancellationToken), cancellationToken);
        }

        private static BatteryReading Read(DeviceProfile profile, CancellationToken token)
        {
            string bluetoothName = GetStringOption(profile, "BluetoothNameContains", null);
            ushort? vendorId = profile.Match == null
                ? null
                : profile.Match.ParsedVendorId;
            List<ushort> productIds = profile.Match == null
                ? new List<ushort>()
                : profile.Match.ParsedProductIds;

            BluetoothGattBatteryReadResult bluetoothResult = null;
            if (vendorId.HasValue && productIds.Count > 0)
                bluetoothResult = BluetoothGattBatteryReader.ReadPercent(bluetoothName,
                    vendorId, productIds, null, 0x02);
            if (bluetoothResult != null &&
                bluetoothResult.Status == BluetoothGattBatteryReadStatus.Success &&
                bluetoothResult.Percent.HasValue)
            {
                return ProviderSupport.Connected(profile, bluetoothResult.Percent.Value,
                    BatteryReading.BandFromPercent(bluetoothResult.Percent.Value),
                    DeviceChargeState.Discharging,
                    "연결됨",
                    "Bluetooth LE GATT · exact VID/PID · 표준 Battery Level",
                    false);
            }
            if (bluetoothResult != null &&
                bluetoothResult.Status == BluetoothGattBatteryReadStatus.FoundUnavailable)
            {
                BatteryReading unavailable = PresentUnavailable(profile,
                    bluetoothResult.Percent.HasValue
                        ? DeviceConnectionState.Error
                        : DeviceConnectionState.Busy,
                    bluetoothResult.Percent.HasValue
                        ? "컨트롤러 새 값 조회 실패"
                        : "컨트롤러 배터리 조회 대기",
                    bluetoothResult.Percent.HasValue
                        ? "물리 장치 갱신에 실패해 마지막 Windows 캐시 값을 표시합니다."
                        : "정확한 Bluetooth Battery Service는 감지됐지만 현재 값을 읽을 수 없습니다.",
                    "controller-battery-read-unavailable");
                ApplyStaleCache(unavailable, bluetoothResult.Percent);
                return unavailable;
            }
            if (bluetoothResult != null &&
                bluetoothResult.Status == BluetoothGattBatteryReadStatus.Ambiguous)
            {
                return PresentUnavailable(profile, DeviceConnectionState.Error,
                    "컨트롤러 구분 필요",
                    "같은 조건의 Battery Service가 " + bluetoothResult.CandidateCount +
                    "개 감지됐습니다.",
                    "controller-battery-service-ambiguous");
            }
            if (bluetoothResult != null &&
                bluetoothResult.Status == BluetoothGattBatteryReadStatus.EnumerationUnavailable)
            {
                if (bluetoothResult.CandidateCount > 0)
                {
                    return PresentUnavailable(profile, DeviceConnectionState.Error,
                        "Bluetooth 열거 불완전",
                        "정확한 컨트롤러 서비스는 감지됐지만 다른 후보를 완전히 확인하지 못해 값을 읽지 않았습니다.",
                        "controller-battery-enumeration-unavailable");
                }
                return BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Error,
                    "Bluetooth 열거 불완전",
                    "Windows에서 Battery Service 목록을 완전히 확인하지 못했습니다.",
                    "controller-battery-enumeration-unavailable");
            }

            bool allowUnboundXInput = AllowsUnboundXInput(profile);
            if (!allowUnboundXInput)
            {
                if (!HasExactGattIdentity(profile))
                {
                    return BatteryReading.Unavailable(profile,
                        DeviceConnectionState.Unsupported,
                        "정확한 컨트롤러 식별자 필요",
                        "Bluetooth GATT 조회에는 프로필의 exact VID/PID가 필요합니다.",
                        "exact-controller-identity-required");
                }
                return BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Disconnected,
                    "컨트롤러 연결 안 됨",
                    "정확히 일치하는 Bluetooth Battery Service가 현재 감지되지 않았습니다.",
                    "exact-controller-not-found");
            }

            int first = profile.Match.XInputUserIndex.Value;
            int last = first;
            for (int index = first; index <= last; index++)
            {
                token.ThrowIfCancellationRequested();
                XInputNative.XINPUT_STATE state;
                uint stateResult;
                try
                {
                    stateResult = XInputNative.XInputGetState((uint)index, out state);
                }
                catch (DllNotFoundException ex)
                {
                    return BatteryReading.Unavailable(profile, DeviceConnectionState.Unsupported,
                        "XInput 사용 불가", ex.Message, "xinput-missing");
                }
                if (stateResult != 0)
                    continue;

                XInputNative.XINPUT_BATTERY_INFORMATION info;
                uint result;
                result = XInputNative.XInputGetBatteryInformation((uint)index,
                    XInputNative.BATTERY_DEVTYPE_GAMEPAD, out info);

                if (result == 0 && info.BatteryType == XInputNative.BATTERY_TYPE_WIRED)
                    return ProviderSupport.Connected(profile, null, BatteryLevelBand.Full,
                        DeviceChargeState.NotApplicable, "유선 연결", "XInput 슬롯 " + index, false);

                if (result == 0 &&
                    (info.BatteryType == XInputNative.BATTERY_TYPE_ALKALINE ||
                     info.BatteryType == XInputNative.BATTERY_TYPE_NIMH))
                {
                    return FromXInputLevel(profile, info, index);
                }

                return ProviderSupport.Connected(profile, null, BatteryLevelBand.Unknown,
                    DeviceChargeState.Unknown, "연결됨 · 잔량 정보 없음",
                    "XInput 슬롯 " + index, false);
            }

            return BatteryReading.Unavailable(profile, DeviceConnectionState.Disconnected,
                "컨트롤러 연결 안 됨", "Bluetooth 전원과 페어링 상태를 확인하세요.", "not-found");
        }

        private static BatteryReading FromXInputLevel(DeviceProfile profile,
            XInputNative.XINPUT_BATTERY_INFORMATION info, int index)
        {
            string chemistry = info.BatteryType == XInputNative.BATTERY_TYPE_NIMH ? "NiMH" : "알카라인";
            switch (info.BatteryLevel)
            {
                case XInputNative.BATTERY_LEVEL_EMPTY:
                    return ProviderSupport.Connected(profile, null, BatteryLevelBand.Critical,
                        DeviceChargeState.Discharging, "교체 필요", "XInput 4단계: 방전 · " + chemistry + " · 슬롯 " + index, true);
                case XInputNative.BATTERY_LEVEL_LOW:
                    return ProviderSupport.Connected(profile, null, BatteryLevelBand.Low,
                        DeviceChargeState.Discharging, "부족", "XInput 4단계: 낮음 · " + chemistry + " · 슬롯 " + index, true);
                case XInputNative.BATTERY_LEVEL_MEDIUM:
                    return ProviderSupport.Connected(profile, null, BatteryLevelBand.Medium,
                        DeviceChargeState.Discharging, "보통", "XInput 4단계: 중간 · " + chemistry + " · 슬롯 " + index, true);
                default:
                    return ProviderSupport.Connected(profile, null, BatteryLevelBand.High,
                        DeviceChargeState.Discharging, "충분", "XInput 4단계: 가득 참 · " + chemistry + " · 슬롯 " + index, true);
            }
        }

        private static string GetStringOption(DeviceProfile profile, string key, string defaultValue)
        {
            object value;
            if (profile.ProviderOptions != null && profile.ProviderOptions.TryGetValue(key, out value) && value != null)
            {
                string text = Convert.ToString(value);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            return defaultValue;
        }

        internal static bool GetBoolOption(DeviceProfile profile, string key, bool defaultValue)
        {
            object value;
            if (profile != null && profile.ProviderOptions != null &&
                profile.ProviderOptions.TryGetValue(key, out value) && value != null)
            {
                bool parsed;
                if (bool.TryParse(Convert.ToString(value), out parsed))
                    return parsed;
            }
            return defaultValue;
        }

        internal static bool AllowsUnboundXInput(DeviceProfile profile)
        {
            return profile != null && profile.Match != null &&
                   profile.Match.XInputUserIndex.HasValue &&
                   GetBoolOption(profile, "AllowUnboundXInput", false);
        }

        internal static bool HasExactGattIdentity(DeviceProfile profile)
        {
            return profile != null && profile.Match != null &&
                   profile.Match.ParsedVendorId.HasValue &&
                   profile.Match.ParsedProductIds.Count > 0;
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

        private static void ApplyStaleCache(BatteryReading reading, int? percent)
        {
            if (reading == null || !percent.HasValue)
                return;
            reading.Percent = percent.Value;
            reading.Band = BatteryReading.BandFromPercent(percent.Value);
            reading.IsStale = true;
        }

    }
}
