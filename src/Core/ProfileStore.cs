using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace PeripheralBatteryDashboard.Core
{
    public sealed class ProfileStore
    {
        private readonly string _baseDirectory;
        private readonly JavaScriptSerializer _json;

        public string UserProfileDirectory { get; private set; }
        public List<string> LoadWarnings { get; private set; }

        public ProfileStore(string baseDirectory)
        {
            _baseDirectory = baseDirectory;
            _json = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
            UserProfileDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PeripheralBatteryDashboard",
                "Profiles");
            LoadWarnings = new List<string>();
        }

        public IList<DeviceProfile> LoadProfiles()
        {
            LoadWarnings.Clear();
            Dictionary<string, DeviceProfile> merged = new Dictionary<string, DeviceProfile>(StringComparer.OrdinalIgnoreCase);

            LoadInto(merged, Path.Combine(_baseDirectory, "Profiles", "builtin.devices.json"), false);

            string pluginRoot = Path.Combine(_baseDirectory, "Plugins");
            if (Directory.Exists(pluginRoot))
            {
                foreach (string file in Directory.GetFiles(pluginRoot, "*.devices.json", SearchOption.AllDirectories))
                    LoadInto(merged, file, false);
            }

            if (Directory.Exists(UserProfileDirectory))
            {
                try
                {
                    foreach (string file in Directory.GetFiles(UserProfileDirectory, "*.json", SearchOption.TopDirectoryOnly))
                        LoadInto(merged, file, true);
                }
                catch (Exception ex)
                {
                    LoadWarnings.Add("사용자 프로필 폴더: " + ex.Message);
                }
            }

            return merged.Values
                .Where(p => p.Enabled)
                .OrderBy(p => p.DisplayOrder)
                .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public IList<DeviceProfile> ReadProfileFile(string path)
        {
            string json = File.ReadAllText(path);
            DeviceProfileDocument doc = _json.Deserialize<DeviceProfileDocument>(json);
            if (doc == null || doc.SchemaVersion != 1 || doc.Profiles == null)
                throw new InvalidDataException("지원하지 않는 장치 프로필 형식입니다.");

            foreach (DeviceProfile profile in doc.Profiles)
                Validate(profile, path);
            return doc.Profiles;
        }

        public string ImportProfileFile(string sourcePath)
        {
            IList<DeviceProfile> incoming = ReadProfileFile(sourcePath);
            Directory.CreateDirectory(UserProfileDirectory);

            string userPath = Path.Combine(UserProfileDirectory, "devices.user.json");
            Dictionary<string, DeviceProfile> merged = new Dictionary<string, DeviceProfile>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(userPath))
            {
                foreach (DeviceProfile existing in ReadProfileFile(userPath))
                    merged[existing.Id] = existing;
            }

            foreach (DeviceProfile profile in incoming)
                merged[profile.Id] = profile;

            DeviceProfileDocument doc = new DeviceProfileDocument();
            doc.Profiles = merged.Values.OrderBy(p => p.DisplayOrder).ThenBy(p => p.DisplayName).ToList();
            File.WriteAllText(userPath, _json.Serialize(doc));
            return userPath;
        }

        private void LoadInto(Dictionary<string, DeviceProfile> merged, string path, bool isUserFile)
        {
            if (!File.Exists(path))
            {
                if (!isUserFile)
                    LoadWarnings.Add("프로필 파일 없음: " + path);
                return;
            }

            try
            {
                foreach (DeviceProfile profile in ReadProfileFile(path))
                    merged[profile.Id] = profile;
            }
            catch (Exception ex)
            {
                LoadWarnings.Add(Path.GetFileName(path) + ": " + ex.Message);
            }
        }

        private static void Validate(DeviceProfile profile, string source)
        {
            if (profile == null)
                throw new InvalidDataException("빈 프로필이 있습니다: " + source);
            if (string.IsNullOrWhiteSpace(profile.Id))
                throw new InvalidDataException("id가 없는 프로필이 있습니다: " + source);
            if (string.IsNullOrWhiteSpace(profile.DisplayName))
                throw new InvalidDataException(profile.Id + "의 displayName이 비어 있습니다.");
            if (string.IsNullOrWhiteSpace(profile.ProviderId))
                throw new InvalidDataException(profile.Id + "의 providerId가 비어 있습니다.");
            if (profile.Match == null)
                throw new InvalidDataException(profile.Id + "의 match가 비어 있습니다.");
            if (!string.Equals(profile.Match.Transport, "xinput", StringComparison.OrdinalIgnoreCase))
            {
                if (!profile.Match.ParsedVendorId.HasValue || profile.Match.ParsedProductIds.Count == 0)
                    throw new InvalidDataException(profile.Id + "의 VID/PID가 올바르지 않습니다.");
            }
        }
    }
}
