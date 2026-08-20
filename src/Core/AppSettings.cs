using System;
using System.IO;
using System.Web.Script.Serialization;

namespace PeripheralBatteryDashboard.Core
{
    public sealed class AppSettings
    {
        public const string TrayIconModePerDevice = "per-device";
        public const string TrayIconModeCombined = "combined";

        public int PollSeconds { get; set; }
        public bool NotificationsEnabled { get; set; }
        public bool MinimizeToTrayOnClose { get; set; }
        public bool StartMinimized { get; set; }
        public bool StartWithWindows { get; set; }
        public string TrayIconMode { get; set; }

        public AppSettings()
        {
            PollSeconds = 30;
            NotificationsEnabled = true;
            MinimizeToTrayOnClose = true;
            StartMinimized = false;
            StartWithWindows = true;
            TrayIconMode = TrayIconModePerDevice;
        }

        public static string NormalizeTrayIconMode(string value)
        {
            if (string.Equals(value, TrayIconModeCombined, StringComparison.OrdinalIgnoreCase))
                return TrayIconModeCombined;
            return TrayIconModePerDevice;
        }
    }

    public sealed class AppSettingsStore
    {
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        public string SettingsPath { get; private set; }

        public AppSettingsStore()
        {
            SettingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PeripheralBatteryDashboard",
                "settings.json");
        }

        public AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new AppSettings();
                AppSettings settings = _json.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (settings == null)
                    return new AppSettings();
                if (settings.PollSeconds != 15 && settings.PollSeconds != 30 && settings.PollSeconds != 60 && settings.PollSeconds != 120)
                    settings.PollSeconds = 30;
                settings.TrayIconMode = AppSettings.NormalizeTrayIconMode(settings.TrayIconMode);
                return settings;
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Save(AppSettings settings)
        {
            string directory = Path.GetDirectoryName(SettingsPath);
            Directory.CreateDirectory(directory);
            string temporaryPath = SettingsPath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, _json.Serialize(settings));
                if (File.Exists(SettingsPath))
                    File.Replace(temporaryPath, SettingsPath, null);
                else
                    File.Move(temporaryPath, SettingsPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
    }
}
