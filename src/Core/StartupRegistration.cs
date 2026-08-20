using System;
using System.IO;
using Microsoft.Win32;

namespace PeripheralBatteryDashboard.Core
{
    public static class StartupRegistration
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "PeripheralBatteryDashboard";

        public static bool TrySetEnabled(bool enabled, string executablePath, out string error)
        {
            error = null;
            try
            {
                if (enabled)
                {
                    string fullPath = NormalizeExecutablePath(executablePath);
                    if (!File.Exists(fullPath))
                        throw new FileNotFoundException("자동 실행에 등록할 앱을 찾지 못했습니다.", fullPath);

                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true))
                    {
                        if (key == null)
                            throw new InvalidOperationException("Windows 자동 실행 레지스트리를 열지 못했습니다.");
                        key.SetValue(ValueName, BuildCommandLine(fullPath), RegistryValueKind.String);
                    }
                }
                else
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                    {
                        if (key != null)
                            key.DeleteValue(ValueName, false);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static bool IsEnabled(string executablePath)
        {
            try
            {
                string expected = BuildCommandLine(NormalizeExecutablePath(executablePath));
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null)
                        return false;
                    string actual = Convert.ToString(key.GetValue(ValueName, null,
                        RegistryValueOptions.DoNotExpandEnvironmentNames));
                    return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static string BuildCommandLine(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new ArgumentException("실행 파일 경로가 비어 있습니다.", "executablePath");
            if (executablePath.IndexOf('"') >= 0)
                throw new ArgumentException("실행 파일 경로에 큰따옴표를 사용할 수 없습니다.", "executablePath");
            return "\"" + executablePath + "\" --startup";
        }

        private static string NormalizeExecutablePath(string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
                throw new ArgumentException("실행 파일 경로가 비어 있습니다.", "executablePath");
            return Path.GetFullPath(executablePath);
        }
    }
}
