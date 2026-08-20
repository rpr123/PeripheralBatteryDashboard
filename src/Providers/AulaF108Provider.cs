using System;
using System.Threading;
using System.Threading.Tasks;
using PeripheralBatteryDashboard.Core;
using PeripheralBatteryDashboard.Hardware;

namespace PeripheralBatteryDashboard.Providers
{
    public sealed class AulaF108Provider : IBatteryProvider
    {
        public string ProviderId { get { return "builtin.aula.f108"; } }

        public async Task<BatteryReading> ReadAsync(DeviceProfile profile, BatteryReadContext context, CancellationToken cancellationToken)
        {
            HidDeviceDescriptor device = context.HidDevices.Find(profile);
            if (device == null)
                return BatteryReading.Unavailable(profile, DeviceConnectionState.Disconnected,
                    "동글 연결 안 됨", "F108Pro Dongle을 확인하세요.", "not-found");

            try
            {
                using (HidSession session = HidSession.Open(device))
                {
                    byte[] payload = new byte[32];
                    payload[0] = 0x20;
                    payload[1] = 0x01;
                    payload[31] = 0x21;
                    byte[] report = new byte[33];
                    Buffer.BlockCopy(payload, 0, report, 1, payload.Length);

                    await session.WriteInterruptAsync(report, profile.EffectiveTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);

                    for (int attempt = 0; attempt < 8; attempt++)
                    {
                        byte[] raw = await session.ReadInputReportAsync(profile.EffectiveTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                        byte[] data = ProviderSupport.StripSyntheticZeroReportId(raw);
                        if (data.Length < 32 || data[0] != 0x20 || data[1] != 0x01 || data[2] != 0x00)
                            continue;
                        if (!ProviderSupport.AulaChecksumIsValid(data))
                            continue;

                        int percent = data[3];
                        if (!ProviderSupport.IsValidBatteryPercent(percent))
                            return ProviderSupport.InvalidBatteryPercent(profile, percent);
                        if (percent <= 0)
                            return BatteryReading.Unavailable(profile, DeviceConnectionState.Sleeping,
                                "키보드 응답 없음", "키보드가 절전 중일 수 있습니다.", "zero-battery-response");

                        return ProviderSupport.Connected(profile, percent,
                            BatteryReading.BandFromPercent(percent),
                            DeviceChargeState.Discharging,
                            "연결됨",
                            "2.4GHz 동글 · " + device.SafeIdentity,
                            false);
                    }
                }

                return BatteryReading.Unavailable(profile, DeviceConnectionState.Sleeping,
                    "상태 응답 없음", "키보드가 절전 중일 수 있습니다.", "unexpected-response");
            }
            catch (Exception ex)
            {
                return ProviderSupport.FromException(profile, ex);
            }
        }
    }
}
