using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using PeripheralBatteryDashboard.Core;
using PeripheralBatteryDashboard.Hardware;

namespace PeripheralBatteryDashboard.Diagnostics
{
    public sealed class DiagnosticsService
    {
        private readonly IList<DeviceProfile> _profiles;
        private readonly ProviderRegistry _registry;
        private readonly BatteryReadContext _context;

        public DiagnosticsService(IList<DeviceProfile> profiles, ProviderRegistry registry, BatteryReadContext context)
        {
            _profiles = profiles;
            _registry = registry;
            _context = context;
        }

        public async Task<IList<BatteryReading>> ReadOnceAsync(CancellationToken token)
        {
            List<BatteryReading> readings = new List<BatteryReading>();
            foreach (DeviceProfile profile in _profiles)
            {
                token.ThrowIfCancellationRequested();
                IBatteryProvider provider;
                if (!_registry.TryGet(profile.ProviderId, out provider))
                {
                    readings.Add(BatteryReading.Unavailable(profile, DeviceConnectionState.Unsupported,
                        "지원 모듈 없음", profile.ProviderId, "provider-not-found"));
                    continue;
                }

                try
                {
                    using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timeout.CancelAfter(Math.Max(5000, profile.EffectiveTimeoutMilliseconds * 8));
                        readings.Add(await provider.ReadAsync(profile, _context, timeout.Token).ConfigureAwait(false));
                    }
                }
                catch (Exception ex)
                {
                    readings.Add(BatteryReading.Unavailable(profile, DeviceConnectionState.Error,
                        "조회 오류", ex.Message, "exception"));
                }
            }
            return readings;
        }

        public string BuildText(IList<BatteryReading> readings)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("Peripheral Battery Dashboard diagnostics");
            text.AppendLine("Generated: " + DateTimeOffset.Now.ToString("u"));
            text.AppendLine("Runtime: " + Environment.Version);
            text.AppendLine("OS: " + Environment.OSVersion);
            text.AppendLine("Providers: " + string.Join(", ", _registry.ProviderIds.ToArray()));
            if (_registry.PluginWarnings.Count > 0)
            {
                text.AppendLine("Plugin warnings:");
                foreach (string warning in _registry.PluginWarnings)
                    text.AppendLine("- " + warning);
            }
            text.AppendLine();
            text.AppendLine("Battery readings");
            foreach (BatteryReading reading in readings)
            {
                text.Append("- ").Append(reading.DisplayName)
                    .Append(" | ").Append(reading.Connection)
                    .Append(" | ").Append(reading.Percent.HasValue ? reading.Percent.Value + "%" : reading.StatusText)
                    .Append(" | ").Append(reading.Charge)
                    .Append(" | ").Append(reading.DetailText)
                    .AppendLine();
            }

            text.AppendLine();
            text.AppendLine("Configured profiles");
            foreach (DeviceProfile profile in _profiles)
            {
                string ids = string.Equals(profile.Match.Transport, "xinput", StringComparison.OrdinalIgnoreCase)
                    ? "XInput"
                    : (profile.Match.VendorId + ":" + string.Join(",", profile.Match.ProductIds.ToArray()));
                text.AppendLine("- " + profile.Id + " | " + profile.ProviderId + " | " + ids);
            }

            text.AppendLine();
            text.AppendLine("HID collections (paths and serials omitted)");
            try
            {
                foreach (HidDeviceDescriptor device in _context.HidDevices.Enumerate())
                {
                    text.Append("- ").Append(device.SafeIdentity)
                        .Append(" | ").Append(device.ProductName)
                        .Append(" | IN=").Append(device.InputReportLength)
                        .Append(" OUT=").Append(device.OutputReportLength)
                        .Append(" FEATURE=").Append(device.FeatureReportLength)
                        .AppendLine();
                }
            }
            catch (Exception ex)
            {
                text.AppendLine("- enumeration error: " + ex.Message);
            }
            return text.ToString();
        }

        public string ToJson(IList<BatteryReading> readings)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            return json.Serialize(readings.Select(r => new
            {
                id = r.ProfileId,
                name = r.DisplayName,
                connection = r.Connection.ToString(),
                percent = r.Percent,
                approximate = r.IsApproximate,
                band = r.Band.ToString(),
                charge = r.Charge.ToString(),
                status = r.StatusText,
                detail = r.DetailText,
                sampledAtUtc = r.SampledAtUtc.ToString("o"),
                error = r.ErrorCode
            }).ToArray());
        }

        public string Export(string path, IList<BatteryReading> readings)
        {
            File.WriteAllText(path, BuildText(readings));
            return path;
        }
    }
}
