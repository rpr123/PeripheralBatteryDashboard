using System;
using System.Collections.Generic;
using System.IO;
using PeripheralBatteryDashboard.Core;

namespace PeripheralBatteryDashboard.Providers
{
    internal static class ProviderSupport
    {
        internal static byte[] StripSyntheticZeroReportId(byte[] raw)
        {
            if (raw == null || raw.Length == 0)
                return new byte[0];
            if (raw[0] != 0)
                return raw;
            byte[] normalized = new byte[raw.Length - 1];
            Buffer.BlockCopy(raw, 1, normalized, 0, normalized.Length);
            return normalized;
        }

        internal static bool SumChecksumEquals(byte[] data, int length, byte expected)
        {
            if (data == null || data.Length < length)
                return false;
            int sum = 0;
            for (int i = 0; i < length; i++)
                sum = (sum + data[i]) & 0xFF;
            return sum == expected;
        }

        internal static bool AulaChecksumIsValid(byte[] payload)
        {
            if (payload == null || payload.Length < 32)
                return false;
            int sum = 0;
            for (int i = 0; i < 31; i++)
                sum = (sum + payload[i]) & 0xFF;
            return payload[31] == (byte)sum;
        }

        internal static bool IsValidBatteryPercent(int percent)
        {
            return percent >= 0 && percent <= 100;
        }

        internal static BatteryReading InvalidBatteryPercent(DeviceProfile profile, int percent)
        {
            return BatteryReading.Unavailable(profile, DeviceConnectionState.Error,
                "잘못된 배터리 응답", "허용 범위를 벗어난 값: " + percent, "battery-out-of-range");
        }

        internal static BatteryReading Connected(DeviceProfile profile, int? percent,
            BatteryLevelBand band, DeviceChargeState charge, string status, string detail, bool approximate)
        {
            return new BatteryReading
            {
                ProfileId = profile.Id,
                DisplayName = profile.DisplayName,
                Category = profile.Category,
                Percent = percent,
                IsApproximate = approximate,
                Band = band,
                Connection = DeviceConnectionState.Connected,
                Charge = charge,
                StatusText = status,
                DetailText = detail ?? string.Empty,
                SampledAtUtc = DateTime.UtcNow
            };
        }

        internal static BatteryReading FromException(DeviceProfile profile, Exception ex)
        {
            if (ex is TimeoutException)
                return BatteryReading.Unavailable(profile, DeviceConnectionState.Sleeping,
                    "절전 또는 응답 없음", "다음 조회에서 다시 시도합니다.", "timeout");

            IOException io = ex as IOException;
            if (io != null)
            {
                int code = 0;
                System.ComponentModel.Win32Exception win32 = io.InnerException as System.ComponentModel.Win32Exception;
                if (win32 != null)
                    code = win32.NativeErrorCode;
                if (code == 5 || code == 32)
                    return BatteryReading.Unavailable(profile, DeviceConnectionState.Busy,
                        "다른 앱이 장치 사용 중", "제조사 앱을 닫거나 잠시 후 다시 시도하세요.", "busy");
            }

            return BatteryReading.Unavailable(profile, DeviceConnectionState.Error,
                "조회 오류", ex.Message, "provider-error");
        }
    }
}
