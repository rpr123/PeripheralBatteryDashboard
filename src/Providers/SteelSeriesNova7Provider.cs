using System;
using System.Threading;
using System.Threading.Tasks;
using PeripheralBatteryDashboard.Core;
using PeripheralBatteryDashboard.Hardware;

namespace PeripheralBatteryDashboard.Providers
{
    public sealed class SteelSeriesNova7Provider : IBatteryProvider
    {
        public string ProviderId { get { return "builtin.steelseries.nova7"; } }

        public async Task<BatteryReading> ReadAsync(DeviceProfile profile, BatteryReadContext context, CancellationToken cancellationToken)
        {
            HidDeviceDescriptor device = context.HidDevices.Find(profile);
            if (device == null)
                return BatteryReading.Unavailable(profile, DeviceConnectionState.Disconnected,
                    "동글 연결 안 됨", "USB-C 동글을 확인하세요.", "not-found");

            try
            {
                using (HidSession session = HidSession.Open(device))
                {
                    await session.WriteInterruptAsync(new byte[] { 0x00, 0xB0 },
                        profile.EffectiveTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);

                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        byte[] raw = await session.ReadInputReportAsync(profile.EffectiveTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
                        byte[] data = ProviderSupport.StripSyntheticZeroReportId(raw);
                        if (data.Length < 4 || data[0] != 0xB0)
                            continue;

                        int percent = data[2];
                        if (!ProviderSupport.IsValidBatteryPercent(percent))
                            return ProviderSupport.InvalidBatteryPercent(profile, percent);
                        byte state = data[3];
                        if (state == 0)
                            return BatteryReading.Unavailable(profile, DeviceConnectionState.Sleeping,
                                "헤드셋 전원 꺼짐", "동글은 연결되어 있습니다.", "headset-offline");

                        bool charging = state == 1 || state == 2;
                        string status = charging ? "충전 중" : "연결됨";
                        return ProviderSupport.Connected(profile, percent,
                            BatteryReading.BandFromPercent(percent),
                            charging ? DeviceChargeState.Charging : DeviceChargeState.Discharging,
                            status,
                            "2.4GHz 동글 · " + device.SafeIdentity,
                            false);
                    }
                }

                return BatteryReading.Unavailable(profile, DeviceConnectionState.Sleeping,
                    "상태 응답 없음", "헤드셋이 절전 중일 수 있습니다.", "unexpected-response");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ProviderSupport.FromException(profile, ex);
            }
        }
    }
}
