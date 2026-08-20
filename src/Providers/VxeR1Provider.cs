using System;
using System.Threading;
using System.Threading.Tasks;
using PeripheralBatteryDashboard.Core;
using PeripheralBatteryDashboard.Hardware;

namespace PeripheralBatteryDashboard.Providers
{
    public sealed class VxeR1Provider : IBatteryProvider
    {
        public string ProviderId { get { return "builtin.vxe.r1"; } }

        public async Task<BatteryReading> ReadAsync(DeviceProfile profile, BatteryReadContext context, CancellationToken cancellationToken)
        {
            HidDeviceDescriptor device = context.HidDevices.Find(profile);
            if (device == null)
                return BatteryReading.Unavailable(profile, DeviceConnectionState.Disconnected,
                    "동글 연결 안 됨", "VXE Mouse 1K Dongle을 확인하세요.", "not-found");

            try
            {
                using (HidSession session = HidSession.Open(device))
                {
                    byte[] request = new byte[17];
                    request[0] = 0x08;
                    request[1] = 0x04;
                    request[16] = 0x49;
                    if (!session.SetOutputReport(request))
                        throw new System.IO.IOException("VXE 상태 요청을 보내지 못했습니다.",
                            new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error()));

                    for (int attempt = 0; attempt < 6; attempt++)
                    {
                        byte[] data = await session.ReadInputReportAsync(profile.EffectiveTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                        if (data.Length < 17 || data[0] != 0x08 || data[1] != 0x04)
                            continue;
                        if (!ProviderSupport.SumChecksumEquals(data, 17, 0x55))
                            continue;

                        int percent = data[6];
                        if (!ProviderSupport.IsValidBatteryPercent(percent))
                            return ProviderSupport.InvalidBatteryPercent(profile, percent);
                        bool charging = data[7] == 1;
                        int millivolts = (data[8] << 8) | data[9];
                        string detail = "2.4GHz 동글";
                        if (millivolts > 0)
                            detail += " · " + millivolts + " mV";
                        detail += " · " + device.SafeIdentity;

                        return ProviderSupport.Connected(profile, percent,
                            BatteryReading.BandFromPercent(percent),
                            charging ? DeviceChargeState.Charging : DeviceChargeState.Discharging,
                            charging ? "충전 중" : "연결됨",
                            detail,
                            false);
                    }
                }

                return BatteryReading.Unavailable(profile, DeviceConnectionState.Sleeping,
                    "마우스 응답 없음", "마우스가 절전 중일 수 있습니다.", "unexpected-response");
            }
            catch (Exception ex)
            {
                return ProviderSupport.FromException(profile, ex);
            }
        }
    }
}
