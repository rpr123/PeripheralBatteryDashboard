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
            int first = profile.Match.XInputUserIndex.HasValue ? profile.Match.XInputUserIndex.Value : 0;
            int last = profile.Match.XInputUserIndex.HasValue ? first : 3;
            string bluetoothName = GetStringOption(profile, "BluetoothNameContains", null);
            ushort vendorId = profile.Match != null && profile.Match.ParsedVendorId.HasValue
                ? profile.Match.ParsedVendorId.Value
                : (ushort)0x045E;
            List<ushort> productIds = profile.Match == null
                ? new List<ushort>()
                : profile.Match.ParsedProductIds;
            if (productIds.Count == 0)
                productIds.Add(0x0B13);
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

                int bluetoothPercent;
                if (BluetoothGattBatteryReader.TryReadPercent(bluetoothName, vendorId,
                    productIds, out bluetoothPercent))
                {
                    return ProviderSupport.Connected(profile, bluetoothPercent,
                        BatteryReading.BandFromPercent(bluetoothPercent),
                        DeviceChargeState.Discharging,
                        "연결됨",
                        "Bluetooth LE GATT · 표준 Battery Level · XInput 슬롯 " + index,
                        false);
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

    }
}
