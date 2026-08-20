using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBatteryDashboard.Hardware
{
    internal sealed class BluetoothGattBatteryServiceDescriptor
    {
        public byte? VendorIdSource { get; set; }
        public ushort? VendorId { get; set; }
        public ushort? ProductId { get; set; }
        public string LocalServiceId { get; set; }
        public string FriendlyName { get; set; }

        public BluetoothGattBatteryServiceDescriptor()
        {
            LocalServiceId = string.Empty;
            FriendlyName = string.Empty;
        }
    }

    internal sealed class BluetoothGattBatteryServiceEnumeration
    {
        public IList<BluetoothGattBatteryServiceDescriptor> Services { get; private set; }
        public IList<string> WarningCodes { get; private set; }

        public BluetoothGattBatteryServiceEnumeration(
            IList<BluetoothGattBatteryServiceDescriptor> services,
            IList<string> warningCodes)
        {
            Services = services ?? new List<BluetoothGattBatteryServiceDescriptor>();
            WarningCodes = warningCodes ?? new List<string>();
        }
    }

    internal enum BluetoothGattBatteryReadStatus
    {
        NotFound,
        Success,
        FoundUnavailable,
        Ambiguous,
        EnumerationUnavailable
    }

    internal sealed class BluetoothGattBatteryReadResult
    {
        public BluetoothGattBatteryReadStatus Status { get; private set; }
        public int? Percent { get; private set; }
        public int CandidateCount { get; private set; }

        public BluetoothGattBatteryReadResult(BluetoothGattBatteryReadStatus status,
            int? percent, int candidateCount)
        {
            Status = status;
            Percent = percent;
            CandidateCount = candidateCount;
        }
    }

    internal static class BluetoothGattBatteryReader
    {
        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfDeviceInterface = 0x00000010;
        private const int ErrorNoMoreItems = 259;
        private const int ErrorInsufficientBuffer = 122;
        private const int ErrorMoreDataHResult = unchecked((int)0x800700EA);
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint ForceReadFromDevice = 0x00000004;
        private const uint ForceReadFromCache = 0x00000008;
        private const ushort BatteryLevelUuid = 0x2A19;

        private static readonly Guid GattServiceInterfaceGuid =
            new Guid("6E3BB679-4372-40C8-9EAA-4509DF260CD8");
        private static readonly Guid DevicePropertySet =
            new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0");
        private static readonly object RefreshGate = new object();
        private static readonly Dictionary<string, DateTime> LastDeviceRefreshAttempts =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> LastDeviceRefreshFailures =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> DeviceRefreshInFlight =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromMinutes(5);

        internal static bool InteropLayoutIsValid
        {
            get
            {
                return Marshal.SizeOf(typeof(BluetoothUuid)) == 20 &&
                    Marshal.SizeOf(typeof(GattCharacteristic)) == 36;
            }
        }

        internal static BluetoothGattBatteryServiceEnumeration EnumerateBatteryServicesMetadata()
        {
            List<BluetoothGattBatteryServiceDescriptor> services =
                new List<BluetoothGattBatteryServiceDescriptor>();
            List<string> warnings = new List<string>();
            Guid interfaceGuid = GattServiceInterfaceGuid;
            IntPtr infoSet = Native.SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero,
                DigcfPresent | DigcfDeviceInterface);
            if (infoSet == Native.InvalidHandleValue)
            {
                warnings.Add("bluetooth-gatt-service-set-open-failed");
                return new BluetoothGattBatteryServiceEnumeration(services, warnings);
            }

            try
            {
                for (uint index = 0; ; index++)
                {
                    DeviceInterfaceData interfaceData = new DeviceInterfaceData();
                    interfaceData.Size = (uint)Marshal.SizeOf(typeof(DeviceInterfaceData));
                    if (!Native.SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero,
                        ref interfaceGuid, index, ref interfaceData))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == ErrorNoMoreItems)
                            break;
                        AddWarning(warnings, "bluetooth-gatt-interface-enumeration-failed");
                        continue;
                    }

                    DeviceInfoData deviceInfo = new DeviceInfoData();
                    deviceInfo.Size = (uint)Marshal.SizeOf(typeof(DeviceInfoData));
                    string path = ReadDevicePath(infoSet, ref interfaceData, ref deviceInfo);
                    if (string.IsNullOrEmpty(path))
                    {
                        AddWarning(warnings, "bluetooth-gatt-interface-detail-failed");
                        continue;
                    }
                    if (path.IndexOf("{0000180f-0000-1000-8000-00805f9b34fb}",
                        StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    byte? vendorIdSource;
                    ushort vendorId;
                    List<ushort> productIds;
                    bool hasHardwareIdentity =
                        TryParseHardwareIdentity(path, out vendorIdSource, out vendorId,
                            out productIds);
                    string friendlyName = ReadParentFriendlyName(deviceInfo.DevInst) ?? string.Empty;
                    string localServiceId = ComputeLocalServiceId(path);
                    if (!hasHardwareIdentity)
                    {
                        services.Add(new BluetoothGattBatteryServiceDescriptor
                        {
                            LocalServiceId = localServiceId,
                            FriendlyName = friendlyName
                        });
                        continue;
                    }

                    foreach (ushort productId in productIds)
                    {
                        bool duplicate = services.Exists(item =>
                            string.Equals(item.LocalServiceId, localServiceId,
                                StringComparison.OrdinalIgnoreCase) &&
                            item.VendorId == vendorId && item.ProductId == productId);
                        if (duplicate)
                            continue;
                        services.Add(new BluetoothGattBatteryServiceDescriptor
                        {
                            VendorIdSource = vendorIdSource,
                            VendorId = vendorId,
                            ProductId = productId,
                            LocalServiceId = localServiceId,
                            FriendlyName = friendlyName
                        });
                    }
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(infoSet);
            }

            return new BluetoothGattBatteryServiceEnumeration(services, warnings);
        }

        internal static bool TryReadPercent(string friendlyNameContains, ushort vendorId,
            IList<ushort> productIds, out int percent)
        {
            BluetoothGattBatteryReadResult result = ReadPercent(friendlyNameContains,
                vendorId, productIds, null);
            percent = result.Percent.GetValueOrDefault();
            return result.Status == BluetoothGattBatteryReadStatus.Success;
        }

        internal static BluetoothGattBatteryReadResult ReadPercent(
            string friendlyNameContains, ushort vendorId, IList<ushort> productIds)
        {
            return ReadPercent(friendlyNameContains, (ushort?)vendorId, productIds, null);
        }

        internal static BluetoothGattBatteryReadResult ReadPercent(
            string friendlyNameContains, ushort? vendorId, IList<ushort> productIds,
            string localServiceId, byte? vendorIdSource = null)
        {
            List<string> matchingPaths = new List<string>();
            bool enumerationIncomplete = false;
            Guid interfaceGuid = GattServiceInterfaceGuid;
            IntPtr infoSet = Native.SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero,
                DigcfPresent | DigcfDeviceInterface);
            if (infoSet == Native.InvalidHandleValue)
            {
                return new BluetoothGattBatteryReadResult(
                    BluetoothGattBatteryReadStatus.EnumerationUnavailable, null, 0);
            }

            try
            {
                for (uint index = 0; ; index++)
                {
                    DeviceInterfaceData interfaceData = new DeviceInterfaceData();
                    interfaceData.Size = (uint)Marshal.SizeOf(typeof(DeviceInterfaceData));
                    if (!Native.SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero,
                        ref interfaceGuid, index, ref interfaceData))
                    {
                        if (Marshal.GetLastWin32Error() == ErrorNoMoreItems)
                            break;
                        enumerationIncomplete = true;
                        continue;
                    }

                    DeviceInfoData deviceInfo = new DeviceInfoData();
                    deviceInfo.Size = (uint)Marshal.SizeOf(typeof(DeviceInfoData));
                    string path = ReadDevicePath(infoSet, ref interfaceData, ref deviceInfo);
                    if (string.IsNullOrEmpty(path))
                    {
                        enumerationIncomplete = true;
                        continue;
                    }
                    if (path.IndexOf("{0000180f-0000-1000-8000-00805f9b34fb}",
                        StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    string parentName = ReadParentFriendlyName(deviceInfo.DevInst);
                    if (!CandidateMatches(path, parentName, friendlyNameContains,
                        vendorId, productIds, localServiceId, vendorIdSource))
                        continue;
                    if (!matchingPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                        matchingPaths.Add(path);
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(infoSet);
            }

            bool hasUniqueLocalIdentity = !string.IsNullOrWhiteSpace(localServiceId);
            BluetoothGattBatteryReadStatus candidateStatus = ClassifyCandidateSet(
                matchingPaths.Count, enumerationIncomplete, hasUniqueLocalIdentity);
            if (matchingPaths.Count != 1 ||
                candidateStatus == BluetoothGattBatteryReadStatus.EnumerationUnavailable ||
                candidateStatus == BluetoothGattBatteryReadStatus.Ambiguous)
            {
                return new BluetoothGattBatteryReadResult(
                    candidateStatus, null, matchingPaths.Count);
            }

            string selectedPath = matchingPaths[0];
            using (SafeFileHandle handle = Native.CreateFile(selectedPath,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    return new BluetoothGattBatteryReadResult(
                        BluetoothGattBatteryReadStatus.FoundUnavailable, null, 1);
                }
                // Refresh the physical device at most once every five minutes.
                // Native FORCE_DEVICE calls cannot be cancelled, so the monitor
                // quarantines this per-device attempt if it misses its watchdog.
                // Normal polls between refreshes use the Windows cache.
                int percent;
                bool deviceRefreshFailed = false;
                if (TryBeginDeviceRefresh(selectedPath))
                {
                    Mutex refreshMutex = null;
                    bool ownsRefreshMutex = false;
                    try
                    {
                        ownsRefreshMutex = TryAcquireDeviceRefreshMutex(selectedPath,
                            out refreshMutex);
                        if (ownsRefreshMutex)
                        {
                            deviceRefreshFailed = true;
                            if (TryReadBatteryLevel(handle, ForceReadFromDevice,
                                out percent))
                            {
                                SetDeviceRefreshFailed(selectedPath, false);
                                return new BluetoothGattBatteryReadResult(
                                    BluetoothGattBatteryReadStatus.Success, percent, 1);
                            }
                            SetDeviceRefreshFailed(selectedPath, true);
                        }
                        else
                        {
                            // Another process is refreshing this exact service, or
                            // the cross-process guard is unavailable. Do not claim
                            // the cached value is fresh and retry on the next poll.
                            deviceRefreshFailed = true;
                            SetDeviceRefreshFailed(selectedPath, true);
                            RollbackDeviceRefreshAttempt(selectedPath);
                        }
                    }
                    finally
                    {
                        if (refreshMutex != null)
                        {
                            if (ownsRefreshMutex)
                            {
                                try { refreshMutex.ReleaseMutex(); }
                                catch { }
                            }
                            refreshMutex.Dispose();
                        }
                        EndDeviceRefresh(selectedPath);
                    }
                }
                if (TryReadBatteryLevel(handle, ForceReadFromCache, out percent))
                {
                    return new BluetoothGattBatteryReadResult(
                        (deviceRefreshFailed || WasLastDeviceRefreshFailed(selectedPath))
                            ? BluetoothGattBatteryReadStatus.FoundUnavailable
                            : BluetoothGattBatteryReadStatus.Success,
                        percent,
                        1);
                }
            }
            return new BluetoothGattBatteryReadResult(
                BluetoothGattBatteryReadStatus.FoundUnavailable, null, 1);
        }

        private static string ReadDevicePath(IntPtr infoSet,
            ref DeviceInterfaceData interfaceData, ref DeviceInfoData deviceInfo)
        {
            uint required;
            Native.SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData,
                IntPtr.Zero, 0, out required, ref deviceInfo);
            if (required == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                return null;

            IntPtr detail = Marshal.AllocHGlobal((int)required);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!Native.SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData,
                    detail, required, out required, ref deviceInfo))
                    return null;
                return Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }

        private static string ReadParentFriendlyName(uint childDevInst)
        {
            uint parentDevInst;
            if (Native.CM_Get_Parent(out parentDevInst, childDevInst, 0) != 0)
                return null;

            DevPropKey key = new DevPropKey
            {
                FormatId = DevicePropertySet,
                PropertyId = 14 // DEVPKEY_Device_FriendlyName
            };
            byte[] buffer = new byte[1024];
            uint propertyType;
            uint size = (uint)buffer.Length;
            if (Native.CM_Get_DevNode_Property(parentDevInst, ref key, out propertyType,
                buffer, ref size, 0) != 0 || size < 2)
                return null;
            int length = (int)Math.Min(size, (uint)buffer.Length);
            return System.Text.Encoding.Unicode.GetString(buffer, 0, length).TrimEnd('\0');
        }

        internal static bool PathMatchesHardware(string path, ushort vendorId,
            IList<ushort> productIds)
        {
            return PathMatchesHardware(path, vendorId, productIds, null);
        }

        internal static bool PathMatchesHardware(string path, ushort vendorId,
            IList<ushort> productIds, byte? requiredVendorIdSource)
        {
            if (vendorId == 0 || productIds == null || productIds.Count == 0)
                return false;

            byte? pathVendorIdSource;
            ushort pathVendor;
            List<ushort> pathProducts;
            if (!TryParseHardwareIdentity(path, out pathVendorIdSource, out pathVendor,
                    out pathProducts) ||
                pathVendor != vendorId)
                return false;
            if (requiredVendorIdSource.HasValue &&
                pathVendorIdSource != requiredVendorIdSource)
                return false;
            return pathProducts.Any(productIds.Contains);
        }

        internal static bool TryParseHardwareIdentity(string path, out ushort vendorId,
            out List<ushort> productIds)
        {
            byte? vendorIdSource;
            return TryParseHardwareIdentity(path, out vendorIdSource, out vendorId,
                out productIds);
        }

        internal static bool TryParseHardwareIdentity(string path, out byte? vendorIdSource,
            out ushort vendorId, out List<ushort> productIds)
        {
            vendorIdSource = null;
            vendorId = 0;
            productIds = new List<ushort>();
            Match vendorMatch = Regex.Match(path ?? string.Empty,
                @"VID(?:&(?:(?<source>[0-9A-F]{2}))?|_)(?<vendor>[0-9A-F]{4})(?![0-9A-F])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!vendorMatch.Success ||
                !ushort.TryParse(vendorMatch.Groups["vendor"].Value,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out vendorId))
                return false;
            byte parsedSource;
            if (vendorMatch.Groups["source"].Success &&
                byte.TryParse(vendorMatch.Groups["source"].Value,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out parsedSource))
                vendorIdSource = parsedSource;

            MatchCollection productMatches = Regex.Matches(path ?? string.Empty,
                @"PID[&_]([0-9A-F]{4})(?![0-9A-F])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (Match productMatch in productMatches)
            {
                ushort pathProduct;
                if (!ushort.TryParse(productMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out pathProduct))
                    continue;
                if (!productIds.Contains(pathProduct))
                    productIds.Add(pathProduct);
            }
            return vendorId != 0 && productIds.Count > 0;
        }

        internal static string ComputeLocalServiceId(string path)
        {
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
            {
                // Windows device-interface paths are case-insensitive. Hash a
                // canonical form so harmless casing changes do not invalidate a
                // per-PC service identity.
                string canonicalPath = (path ?? string.Empty).ToUpperInvariant();
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalPath));
            }
            StringBuilder result = new StringBuilder("bt-bas-");
            for (int index = 0; index < 12; index++)
                result.Append(digest[index].ToString("x2"));
            return result.ToString();
        }

        internal static bool CandidateMatches(string path, string parentName,
            string friendlyNameContains, ushort? vendorId, IList<ushort> productIds,
            string localServiceId, byte? vendorIdSource = null)
        {
            bool hasLocalIdentity = !string.IsNullOrWhiteSpace(localServiceId);
            bool hasHardwareIdentity = vendorId.HasValue && productIds != null &&
                productIds.Count > 0;
            if (!hasLocalIdentity && !hasHardwareIdentity)
                return false;

            if (hasLocalIdentity)
            {
                if (!string.Equals(ComputeLocalServiceId(path), localServiceId,
                    StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            if (hasHardwareIdentity &&
                !PathMatchesHardware(path, vendorId.Value, productIds, vendorIdSource))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(friendlyNameContains) &&
                (string.IsNullOrWhiteSpace(parentName) ||
                 parentName.IndexOf(friendlyNameContains,
                     StringComparison.OrdinalIgnoreCase) < 0))
                return false;
            return true;
        }

        internal static BluetoothGattBatteryReadStatus ClassifyCandidateSet(
            int candidateCount, bool enumerationIncomplete,
            bool hasUniqueLocalIdentity = false)
        {
            if (candidateCount > 1)
                return BluetoothGattBatteryReadStatus.Ambiguous;
            if (candidateCount == 0)
            {
                return enumerationIncomplete
                    ? BluetoothGattBatteryReadStatus.EnumerationUnavailable
                    : BluetoothGattBatteryReadStatus.NotFound;
            }
            // A partial enumeration cannot prove that a VID/PID or friendly-name
            // selector has only one candidate. A per-PC service ID identifies one
            // exact interface, so unrelated enumeration failures do not make that
            // selection ambiguous.
            if (enumerationIncomplete && !hasUniqueLocalIdentity)
                return BluetoothGattBatteryReadStatus.EnumerationUnavailable;
            return BluetoothGattBatteryReadStatus.FoundUnavailable;
        }

        private static void AddWarning(IList<string> warnings, string warning)
        {
            if (!warnings.Contains(warning))
                warnings.Add(warning);
        }

        private static bool TryBeginDeviceRefresh(string path)
        {
            DateTime now = DateTime.UtcNow;
            lock (RefreshGate)
            {
                if (DeviceRefreshInFlight.Contains(path))
                    return false;
                DateTime last;
                if (LastDeviceRefreshAttempts.TryGetValue(path, out last) &&
                    now - last < DeviceRefreshInterval)
                    return false;
                LastDeviceRefreshAttempts[path] = now;
                DeviceRefreshInFlight.Add(path);
                return true;
            }
        }

        private static void EndDeviceRefresh(string path)
        {
            lock (RefreshGate)
                DeviceRefreshInFlight.Remove(path);
        }

        private static void RollbackDeviceRefreshAttempt(string path)
        {
            lock (RefreshGate)
                LastDeviceRefreshAttempts.Remove(path);
        }

        private static void SetDeviceRefreshFailed(string path, bool failed)
        {
            lock (RefreshGate)
                LastDeviceRefreshFailures[path] = failed;
        }

        private static bool WasLastDeviceRefreshFailed(string path)
        {
            lock (RefreshGate)
            {
                bool failed;
                return LastDeviceRefreshFailures.TryGetValue(path, out failed) && failed;
            }
        }

        internal static string BuildDeviceRefreshMutexName(string path)
        {
            string localId = ComputeLocalServiceId(path ?? string.Empty);
            string suffix = localId.StartsWith("bt-bas-", StringComparison.Ordinal)
                ? localId.Substring("bt-bas-".Length)
                : localId;
            return @"Local\PeripheralBatteryDashboard.GattRefresh." + suffix;
        }

        private static bool TryAcquireDeviceRefreshMutex(string path,
            out Mutex mutex)
        {
            mutex = null;
            try
            {
                mutex = new Mutex(false, BuildDeviceRefreshMutexName(path));
                try
                {
                    return mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    return true;
                }
            }
            catch
            {
                if (mutex != null)
                    mutex.Dispose();
                mutex = null;
                // Fail closed: cache may still be shown as stale, but two
                // processes must never issue an uncoordinated physical refresh.
                return false;
            }
        }

        private static bool TryReadBatteryLevel(SafeFileHandle handle, uint flags,
            out int percent)
        {
            percent = 0;
            ushort required;
            int result = Native.BluetoothGATTGetCharacteristics(handle, IntPtr.Zero,
                0, null, out required, 0);
            if ((result != 0 && result != ErrorMoreDataHResult) || required == 0)
                return false;

            GattCharacteristic[] characteristics = new GattCharacteristic[required];
            ushort actual;
            result = Native.BluetoothGATTGetCharacteristics(handle, IntPtr.Zero,
                required, characteristics, out actual, 0);
            if (result != 0)
                return false;

            int count = Math.Min(actual, (ushort)characteristics.Length);
            for (int i = 0; i < count; i++)
            {
                GattCharacteristic characteristic = characteristics[i];
                if (characteristic.CharacteristicUuid.IsShortUuid == 0 ||
                    characteristic.CharacteristicUuid.ShortUuid != BatteryLevelUuid ||
                    characteristic.IsReadable == 0)
                    continue;
                if (TryReadCharacteristicValue(handle, ref characteristic, flags,
                    out percent))
                    return true;
            }
            return false;
        }

        private static bool TryReadCharacteristicValue(SafeFileHandle handle,
            ref GattCharacteristic characteristic, uint flags, out int percent)
        {
            percent = 0;
            ushort required;
            int result = Native.BluetoothGATTGetCharacteristicValue(handle,
                ref characteristic, 0, IntPtr.Zero, out required, flags);
            if ((result != ErrorMoreDataHResult && result != 0) || required < 5)
                return false;

            IntPtr buffer = Marshal.AllocHGlobal(required);
            try
            {
                ushort actual;
                result = Native.BluetoothGATTGetCharacteristicValue(handle,
                    ref characteristic, required, buffer, out actual, flags);
                if (result != 0 || actual < 5 || Marshal.ReadInt32(buffer, 0) < 1)
                    return false;
                int value = Marshal.ReadByte(buffer, 4);
                if (value > 100)
                    return false;
                percent = value;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInterfaceData
        {
            public uint Size;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfoData
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DevInst;
            public UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DevPropKey
        {
            public Guid FormatId;
            public uint PropertyId;
        }

        [StructLayout(LayoutKind.Explicit, Size = 20, Pack = 4)]
        private struct BluetoothUuid
        {
            [FieldOffset(0)] public byte IsShortUuid;
            [FieldOffset(4)] public ushort ShortUuid;
            [FieldOffset(4)] public Guid LongUuid;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct GattCharacteristic
        {
            public ushort ServiceHandle;
            public BluetoothUuid CharacteristicUuid;
            public ushort AttributeHandle;
            public ushort CharacteristicValueHandle;
            public byte IsBroadcastable;
            public byte IsReadable;
            public byte IsWritable;
            public byte IsWritableWithoutResponse;
            public byte IsSignedWritable;
            public byte IsNotifiable;
            public byte IsIndicatable;
            public byte HasExtendedProperties;
        }

        private static class Native
        {
            internal static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid,
                string enumerator, IntPtr hwndParent, uint flags);

            [DllImport("setupapi.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet,
                IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex,
                ref DeviceInterfaceData deviceInterfaceData);

            [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
                ref DeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
                uint deviceInterfaceDetailDataSize, out uint requiredSize,
                ref DeviceInfoData deviceInfoData);

            [DllImport("setupapi.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

            [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
            internal static extern uint CM_Get_Parent(out uint parentDevInst,
                uint devInst, uint flags);

            [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW",
                CharSet = CharSet.Unicode)]
            internal static extern uint CM_Get_DevNode_Property(uint devInst,
                ref DevPropKey propertyKey, out uint propertyType, byte[] propertyBuffer,
                ref uint propertyBufferSize, uint flags);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern SafeFileHandle CreateFile(string fileName,
                uint desiredAccess, uint shareMode, IntPtr securityAttributes,
                uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

            [DllImport("BluetoothAPIs.dll")]
            internal static extern int BluetoothGATTGetCharacteristics(
                SafeFileHandle device, IntPtr service, ushort characteristicsBufferCount,
                [Out] GattCharacteristic[] characteristicsBuffer,
                out ushort characteristicsBufferActual, uint flags);

            [DllImport("BluetoothAPIs.dll")]
            internal static extern int BluetoothGATTGetCharacteristicValue(
                SafeFileHandle device, ref GattCharacteristic characteristic,
                uint characteristicValueDataSize, IntPtr characteristicValue,
                out ushort characteristicValueSizeRequired, uint flags);
        }
    }
}
