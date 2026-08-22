using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PeripheralBatteryDashboard.Core
{
    public sealed class BatteryHistoryStore : IDisposable
    {
        private const int CurrentSchemaVersion = 1;
        private const long MaximumFileBytes = 1024 * 1024;
        private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(5);

        private readonly object _sync = new object();
        private readonly object _saveSync = new object();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer
        {
            MaxJsonLength = (int)MaximumFileBytes
        };
        private readonly Dictionary<string, BatteryHistoryEntry> _entries =
            new Dictionary<string, BatteryHistoryEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _persistedSuccessTimes =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private bool _dirty;
        private bool _writeScheduled;
        private bool _disposed;
        private long _changeVersion;

        public string HistoryPath { get; private set; }
        public string LastError { get; private set; }

        public BatteryHistoryStore()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PeripheralBatteryDashboard",
                "battery-history.json"))
        {
        }

        internal BatteryHistoryStore(string historyPath)
        {
            if (string.IsNullOrWhiteSpace(historyPath))
                throw new ArgumentException("배터리 기록 경로가 필요합니다.", "historyPath");
            HistoryPath = Path.GetFullPath(historyPath);
            LastError = string.Empty;
            Load();
        }

        internal LastSuccessfulValueSnapshot GetSnapshot(DeviceProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Id))
                return null;
            lock (_sync)
            {
                BatteryHistoryEntry entry;
                if (!_entries.TryGetValue(profile.Id, out entry) ||
                    !IsValidEntry(entry) ||
                    !string.Equals(entry.ProfileFingerprint,
                        ProfileFingerprint(profile), StringComparison.Ordinal))
                    return null;

                DateTime successfulAtUtc = NormalizeUtc(entry.SuccessfulAtUtc);
                if (successfulAtUtc > DateTime.UtcNow.Add(MaximumFutureSkew))
                    return null;
                return new LastSuccessfulValueSnapshot(entry.Percent, entry.Band,
                    entry.IsApproximate, DeviceChargeState.Unknown, successfulAtUtc);
            }
        }

        internal void RecordSuccessfulReading(DeviceProfile profile,
            BatteryReading reading)
        {
            if (profile == null || reading == null ||
                string.IsNullOrWhiteSpace(profile.Id) ||
                reading.Presence != DevicePresenceState.Present ||
                reading.Connection != DeviceConnectionState.Connected ||
                reading.IsStale || !reading.LastSuccessfulAtUtc.HasValue ||
                !HasUsableValue(reading.Percent, reading.Band))
                return;

            DateTime successfulAtUtc = NormalizeUtc(reading.LastSuccessfulAtUtc.Value);
            if (successfulAtUtc > DateTime.UtcNow.Add(MaximumFutureSkew))
                return;

            lock (_sync)
            {
                if (_disposed)
                    return;
                BatteryHistoryEntry previous;
                _entries.TryGetValue(profile.Id, out previous);
                string fingerprint = ProfileFingerprint(profile);
                bool valueChanged = previous == null ||
                    !string.Equals(previous.ProfileFingerprint, fingerprint,
                        StringComparison.Ordinal) ||
                    previous.Percent != reading.Percent ||
                    previous.Band != reading.Band ||
                    previous.IsApproximate != reading.IsApproximate;

                _entries[profile.Id] = new BatteryHistoryEntry
                {
                    ProfileId = profile.Id,
                    ProfileFingerprint = fingerprint,
                    Percent = reading.Percent,
                    Band = reading.Band,
                    IsApproximate = reading.IsApproximate,
                    SuccessfulAtUtc = successfulAtUtc
                };
                _dirty = true;
                _changeVersion++;

                DateTime persistedAt;
                bool timestampDue = !_persistedSuccessTimes.TryGetValue(profile.Id,
                    out persistedAt) || successfulAtUtc - persistedAt >= PersistInterval;
                if (valueChanged || timestampDue || !string.IsNullOrEmpty(LastError))
                    ScheduleWriteLocked();
            }
        }

        public void Flush()
        {
            Save();
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
            }
            Save();
        }

        private void Load()
        {
            lock (_sync)
            {
                try
                {
                    if (!File.Exists(HistoryPath))
                        return;
                    FileInfo file = new FileInfo(HistoryPath);
                    if (file.Length <= 0 || file.Length > MaximumFileBytes)
                        throw new InvalidDataException("배터리 기록 파일 크기가 올바르지 않습니다.");
                    BatteryHistoryDocument document = _json.Deserialize<BatteryHistoryDocument>(
                        File.ReadAllText(HistoryPath));
                    if (document == null || document.SchemaVersion != CurrentSchemaVersion ||
                        document.Entries == null)
                        throw new InvalidDataException("지원하지 않는 배터리 기록 형식입니다.");

                    foreach (BatteryHistoryEntry entry in document.Entries)
                    {
                        if (!IsValidEntry(entry))
                            continue;
                        entry.SuccessfulAtUtc = NormalizeUtc(entry.SuccessfulAtUtc);
                        _entries[entry.ProfileId] = entry;
                        _persistedSuccessTimes[entry.ProfileId] = entry.SuccessfulAtUtc;
                    }
                    LastError = string.Empty;
                }
                catch (Exception ex)
                {
                    _entries.Clear();
                    _persistedSuccessTimes.Clear();
                    LastError = "battery-history-load-failed: " + ex.Message;
                }
            }
        }

        private void ScheduleWriteLocked()
        {
            if (_writeScheduled || _disposed)
                return;
            _writeScheduled = true;
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    lock (_sync)
                    {
                        _writeScheduled = false;
                        if (_disposed || !_dirty)
                            return;
                    }
                    Save();
                });
            }
            catch (Exception ex)
            {
                _writeScheduled = false;
                LastError = "battery-history-write-queue-failed: " + ex.Message;
            }
        }

        private void Save()
        {
            lock (_saveSync)
            {
                BatteryHistoryDocument document;
                long savedVersion;
                lock (_sync)
                {
                    if (!_dirty)
                        return;
                    savedVersion = _changeVersion;
                    document = new BatteryHistoryDocument
                    {
                        SchemaVersion = CurrentSchemaVersion,
                        Entries = _entries.Values
                        .Where(IsValidEntry)
                        .OrderBy(entry => entry.ProfileId,
                            StringComparer.OrdinalIgnoreCase)
                        .Select(CloneEntry)
                        .ToList()
                    };
                }

                string temporaryPath = HistoryPath + ".tmp";
                try
                {
                    string directory = Path.GetDirectoryName(HistoryPath);
                    if (string.IsNullOrWhiteSpace(directory))
                        throw new InvalidDataException(
                            "배터리 기록 폴더를 확인할 수 없습니다.");
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(temporaryPath, _json.Serialize(document),
                        new UTF8Encoding(false));
                    if (File.Exists(HistoryPath))
                        File.Replace(temporaryPath, HistoryPath, null);
                    else
                        File.Move(temporaryPath, HistoryPath);

                    lock (_sync)
                    {
                        _persistedSuccessTimes.Clear();
                        foreach (BatteryHistoryEntry entry in document.Entries)
                        {
                            _persistedSuccessTimes[entry.ProfileId] =
                                entry.SuccessfulAtUtc;
                        }
                        if (_changeVersion == savedVersion)
                            _dirty = false;
                        LastError = string.Empty;
                    }
                }
                catch (Exception ex)
                {
                    lock (_sync)
                        LastError = "battery-history-save-failed: " + ex.Message;
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporaryPath))
                            File.Delete(temporaryPath);
                    }
                    catch { }
                }
            }
        }

        private static bool IsValidEntry(BatteryHistoryEntry entry)
        {
            return entry != null &&
                   !string.IsNullOrWhiteSpace(entry.ProfileId) &&
                   !string.IsNullOrWhiteSpace(entry.ProfileFingerprint) &&
                   entry.SuccessfulAtUtc != DateTime.MinValue &&
                   Enum.IsDefined(typeof(BatteryLevelBand), entry.Band) &&
                   HasUsableValue(entry.Percent, entry.Band);
        }

        private static bool HasUsableValue(int? percent, BatteryLevelBand band)
        {
            if (percent.HasValue)
                return percent.Value >= 0 && percent.Value <= 100;
            return band != BatteryLevelBand.Unknown;
        }

        private static string ProfileFingerprint(DeviceProfile profile)
        {
            DeviceMatch match = profile.Match ?? new DeviceMatch();
            string products = string.Join(",", (match.ProductIds ?? new List<string>())
                .Select(NormalizeHex)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray());
            string canonical = string.Join("\n", new[]
            {
                (profile.ProviderId ?? string.Empty).Trim().ToLowerInvariant(),
                (match.Transport ?? string.Empty).Trim().ToLowerInvariant(),
                NormalizeHex(match.VendorId),
                products,
                match.InterfaceNumber.HasValue
                    ? match.InterfaceNumber.Value.ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
                match.RequireNoInterfaceNumber ? "no-interface" : "interface-unspecified",
                NormalizeHex(match.UsagePage),
                NormalizeHex(match.Usage),
                (match.BluetoothServiceId ?? string.Empty).Trim().ToLowerInvariant(),
                match.XInputUserIndex.HasValue
                    ? match.XInputUserIndex.Value.ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
                GetStringOption(profile, "BluetoothNameContains")
                    .Trim().ToLowerInvariant(),
                GetBoolOption(profile, "AllowUnboundXInput") ? "true" : "false"
            });
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                StringBuilder text = new StringBuilder(digest.Length * 2);
                foreach (byte value in digest)
                    text.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        private static string NormalizeHex(string value)
        {
            ushort? parsed = HexValue.TryParseUInt16(value);
            return parsed.HasValue
                ? parsed.Value.ToString("X4", CultureInfo.InvariantCulture)
                : (value ?? string.Empty).Trim().ToUpperInvariant();
        }

        private static string GetStringOption(DeviceProfile profile, string key)
        {
            object value;
            if (profile != null && profile.ProviderOptions != null &&
                profile.ProviderOptions.TryGetValue(key, out value) && value != null)
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            return string.Empty;
        }

        private static bool GetBoolOption(DeviceProfile profile, string key)
        {
            bool parsed;
            return bool.TryParse(GetStringOption(profile, key), out parsed) && parsed;
        }

        private static BatteryHistoryEntry CloneEntry(BatteryHistoryEntry entry)
        {
            return new BatteryHistoryEntry
            {
                ProfileId = entry.ProfileId,
                ProfileFingerprint = entry.ProfileFingerprint,
                Percent = entry.Percent,
                Band = entry.Band,
                IsApproximate = entry.IsApproximate,
                SuccessfulAtUtc = entry.SuccessfulAtUtc
            };
        }

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
                return value;
            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();
            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private sealed class BatteryHistoryDocument
        {
            public int SchemaVersion { get; set; }
            public List<BatteryHistoryEntry> Entries { get; set; }

            public BatteryHistoryDocument()
            {
                Entries = new List<BatteryHistoryEntry>();
            }
        }

        private sealed class BatteryHistoryEntry
        {
            public string ProfileId { get; set; }
            public string ProfileFingerprint { get; set; }
            public int? Percent { get; set; }
            public BatteryLevelBand Band { get; set; }
            public bool IsApproximate { get; set; }
            public DateTime SuccessfulAtUtc { get; set; }

            public BatteryHistoryEntry()
            {
                ProfileId = string.Empty;
                ProfileFingerprint = string.Empty;
            }
        }
    }
}
