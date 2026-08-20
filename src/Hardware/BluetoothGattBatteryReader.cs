using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBatteryDashboard.Hardware
{
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
        private static readonly TimeSpan DeviceRefreshInterval = TimeSpan.FromMinutes(5);

        internal static bool InteropLayoutIsValid
        {
            get
            {
                return Marshal.SizeOf(typeof(BluetoothUuid)) == 20 &&
                    Marshal.SizeOf(typeof(GattCharacteristic)) == 36;
            }
        }

        internal static bool TryReadPercent(string friendlyNameContains, ushort vendorId,
            IList<ushort> productIds, out int percent)
        {
            percent = 0;
            Guid interfaceGuid = GattServiceInterfaceGuid;
            IntPtr infoSet = Native.SetupDiGetClassDevs(ref interfaceGuid, null, IntPtr.Zero,
                DigcfPresent | DigcfDeviceInterface);
            if (infoSet == Native.InvalidHandleValue)
                return false;

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
                        continue;
                    }

                    DeviceInfoData deviceInfo = new DeviceInfoData();
                    deviceInfo.Size = (uint)Marshal.SizeOf(typeof(DeviceInfoData));
                    string path = ReadDevicePath(infoSet, ref interfaceData, ref deviceInfo);
                    if (string.IsNullOrEmpty(path) ||
                        path.IndexOf("{0000180f-0000-1000-8000-00805f9b34fb}",
                            StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    string parentName = ReadParentFriendlyName(deviceInfo.DevInst);
                    if (!PathMatchesHardware(path, vendorId, productIds))
                        continue;
                    if (!string.IsNullOrWhiteSpace(friendlyNameContains) &&
                        !string.IsNullOrWhiteSpace(parentName) &&
                        parentName.IndexOf(friendlyNameContains,
                            StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    using (SafeFileHandle handle = Native.CreateFile(path,
                        GenericRead | GenericWrite,
                        FileShareRead | FileShareWrite,
                        IntPtr.Zero,
                        OpenExisting,
                        FileAttributeNormal,
                        IntPtr.Zero))
                    {
                        if (handle.IsInvalid)
                            continue;
                        // Refresh the physical controller once at discovery and then
                        // at most every five minutes. All normal 15-second polls use
                        // the Windows Bluetooth cache and do not wake the controller.
                        bool refreshFromDevice = TakeDeviceRefreshSlot(path);
                        if ((refreshFromDevice &&
                             TryReadBatteryLevel(handle, ForceReadFromDevice, out percent)) ||
                            TryReadBatteryLevel(handle, ForceReadFromCache, out percent))
                            return true;
                    }
                }
            }
            finally
            {
                Native.SetupDiDestroyDeviceInfoList(infoSet);
            }
            return false;
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
            if (vendorId == 0 || productIds == null || productIds.Count == 0)
                return false;

            Match vendorMatch = Regex.Match(path,
                @"VID(?:&(?:[0-9A-F]{2})?|_)([0-9A-F]{4})(?![0-9A-F])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            ushort pathVendor;
            if (!vendorMatch.Success ||
                !ushort.TryParse(vendorMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out pathVendor) ||
                pathVendor != vendorId)
                return false;

            MatchCollection productMatches = Regex.Matches(path,
                @"PID[&_]([0-9A-F]{4})(?![0-9A-F])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (Match productMatch in productMatches)
            {
                ushort pathProduct;
                if (!ushort.TryParse(productMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out pathProduct))
                    continue;
                if (productIds.Contains(pathProduct))
                    return true;
            }
            return false;
        }

        private static bool TakeDeviceRefreshSlot(string path)
        {
            DateTime now = DateTime.UtcNow;
            lock (RefreshGate)
            {
                DateTime last;
                if (LastDeviceRefreshAttempts.TryGetValue(path, out last) &&
                    now - last < DeviceRefreshInterval)
                    return false;
                LastDeviceRefreshAttempts[path] = now;
                return true;
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
