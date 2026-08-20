using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using PeripheralBatteryDashboard.Core;

namespace PeripheralBatteryDashboard.Hardware
{
    public sealed class HidDeviceDescriptor
    {
        public string DevicePath { get; internal set; }
        public ushort VendorId { get; internal set; }
        public ushort ProductId { get; internal set; }
        public ushort VersionNumber { get; internal set; }
        public ushort UsagePage { get; internal set; }
        public ushort Usage { get; internal set; }
        public ushort InputReportLength { get; internal set; }
        public ushort OutputReportLength { get; internal set; }
        public ushort FeatureReportLength { get; internal set; }
        public bool CapabilitiesAvailable { get; internal set; }
        internal Guid? ContainerId { get; set; }
        public int? InterfaceNumber { get; internal set; }
        public string ProductName { get; internal set; }

        public HidDeviceDescriptor()
        {
            DevicePath = string.Empty;
            ProductName = string.Empty;
        }

        public string SafeIdentity
        {
            get
            {
                return string.Format("{0:X4}:{1:X4} MI={2} UP={3:X4} U={4:X4}",
                    VendorId,
                    ProductId,
                    InterfaceNumber.HasValue ? InterfaceNumber.Value.ToString("X2") : "--",
                    UsagePage,
                    Usage);
            }
        }

        public bool Matches(DeviceProfile profile)
        {
            if (profile == null || profile.Match == null)
                return false;
            ushort? vid = profile.Match.ParsedVendorId;
            if (!vid.HasValue || VendorId != vid.Value)
                return false;
            if (!profile.Match.ParsedProductIds.Contains(ProductId))
                return false;
            if (profile.Match.RequireNoInterfaceNumber)
            {
                if (InterfaceNumber.HasValue)
                    return false;
            }
            else if (profile.Match.InterfaceNumber.HasValue &&
                     InterfaceNumber != profile.Match.InterfaceNumber)
            {
                return false;
            }
            ushort? usagePage = profile.Match.ParsedUsagePage;
            if (usagePage.HasValue && UsagePage != usagePage.Value)
                return false;
            ushort? usage = profile.Match.ParsedUsage;
            if (usage.HasValue && Usage != usage.Value)
                return false;
            return true;
        }
    }

    public sealed class HidEnumerationResult
    {
        public IList<HidDeviceDescriptor> Devices { get; private set; }
        public IList<string> WarningCodes { get; private set; }

        public bool Complete { get { return WarningCodes.Count == 0; } }

        internal HidEnumerationResult(IList<HidDeviceDescriptor> devices,
            IList<string> warningCodes)
        {
            Devices = devices ?? new List<HidDeviceDescriptor>();
            WarningCodes = warningCodes ?? new List<string>();
        }
    }

    // INVENTORY_METADATA_ENUMERATOR_BEGIN
    public sealed class HidDeviceEnumerator
    {
        private static readonly Regex InterfaceRegex = new Regex("&mi_([0-9a-f]{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public IList<HidDeviceDescriptor> Enumerate()
        {
            return EnumerateMetadata().Devices;
        }

        public HidEnumerationResult EnumerateMetadata()
        {
            List<HidDeviceDescriptor> result = new List<HidDeviceDescriptor>();
            List<string> warningCodes = new List<string>();
            Guid hidGuid;
            HidNative.HidD_GetHidGuid(out hidGuid);
            IntPtr infoSet = HidNative.SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero,
                HidNative.DIGCF_PRESENT | HidNative.DIGCF_DEVICEINTERFACE);
            if (infoSet == HidNative.INVALID_HANDLE_VALUE)
            {
                AddWarning(warningCodes, "hid-device-set-open-failed");
                return new HidEnumerationResult(result, warningCodes);
            }

            try
            {
                uint index = 0;
                while (true)
                {
                    HidNative.SP_DEVICE_INTERFACE_DATA interfaceData = new HidNative.SP_DEVICE_INTERFACE_DATA();
                    interfaceData.cbSize = Marshal.SizeOf(typeof(HidNative.SP_DEVICE_INTERFACE_DATA));
                    if (!HidNative.SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == HidNative.ERROR_NO_MORE_ITEMS)
                            break;
                        AddWarning(warningCodes, "hid-interface-enumeration-failed");
                        index++;
                        continue;
                    }
                    index++;

                    uint requiredSize;
                    HidNative.SetupDiGetDeviceInterfaceDetail(infoSet, ref interfaceData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);
                    if (requiredSize == 0)
                    {
                        AddWarning(warningCodes, "hid-interface-detail-size-failed");
                        continue;
                    }

                    IntPtr detail = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        HidNative.SP_DEVINFO_DATA deviceInfo = new HidNative.SP_DEVINFO_DATA();
                        deviceInfo.cbSize = Marshal.SizeOf(typeof(HidNative.SP_DEVINFO_DATA));
                        if (!HidNative.SetupDiGetDeviceInterfaceDetailWithDeviceInfo(infoSet,
                            ref interfaceData, detail, requiredSize, out requiredSize,
                            ref deviceInfo))
                        {
                            AddWarning(warningCodes, "hid-interface-detail-failed");
                            continue;
                        }

                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (string.IsNullOrEmpty(path))
                        {
                            AddWarning(warningCodes, "hid-interface-path-empty");
                            continue;
                        }
                        HidDeviceDescriptor descriptor;
                        try
                        {
                            descriptor = ReadDescriptor(path);
                        }
                        catch
                        {
                            AddWarning(warningCodes, "hid-descriptor-read-failed");
                            continue;
                        }
                        if (descriptor != null)
                        {
                            descriptor.ContainerId = ReadContainerId(infoSet, ref deviceInfo);
                            if (!descriptor.CapabilitiesAvailable)
                                AddWarning(warningCodes, "hid-capabilities-unavailable");
                            result.Add(descriptor);
                        }
                        else
                        {
                            AddWarning(warningCodes, "hid-descriptor-read-failed");
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                HidNative.SetupDiDestroyDeviceInfoList(infoSet);
            }

            IList<HidDeviceDescriptor> ordered = result
                .OrderBy(d => d.VendorId)
                .ThenBy(d => d.ProductId)
                .ThenBy(d => d.InterfaceNumber ?? 255)
                .ThenBy(d => d.UsagePage)
                .ThenBy(d => d.Usage)
                .ToList();
            return new HidEnumerationResult(ordered, warningCodes);
        }

        public HidDeviceDescriptor Find(DeviceProfile profile)
        {
            return Enumerate().FirstOrDefault(d => d.Matches(profile));
        }

        public IList<HidDeviceDescriptor> FindAll(DeviceProfile profile)
        {
            return Enumerate().Where(d => d.Matches(profile)).ToList();
        }

        private static HidDeviceDescriptor ReadDescriptor(string path)
        {
            using (SafeFileHandle handle = HidNative.CreateFile(path, 0,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING, 0, IntPtr.Zero))
            {
                if (handle.IsInvalid)
                    return ReadDescriptorFromPathOnly(path);

                HidNative.HIDD_ATTRIBUTES attributes = new HidNative.HIDD_ATTRIBUTES();
                attributes.Size = Marshal.SizeOf(typeof(HidNative.HIDD_ATTRIBUTES));
                if (!HidNative.HidD_GetAttributes(handle, ref attributes))
                    return ReadDescriptorFromPathOnly(path);

                HidDeviceDescriptor descriptor = new HidDeviceDescriptor();
                descriptor.DevicePath = path;
                descriptor.VendorId = attributes.VendorID;
                descriptor.ProductId = attributes.ProductID;
                descriptor.VersionNumber = attributes.VersionNumber;
                descriptor.InterfaceNumber = ParseInterfaceNumber(path);

                StringBuilder product = new StringBuilder(256);
                if (HidNative.HidD_GetProductString(handle, product, product.Capacity * 2))
                    descriptor.ProductName = product.ToString();

                IntPtr preparsed;
                if (HidNative.HidD_GetPreparsedData(handle, out preparsed))
                {
                    try
                    {
                        HidNative.HIDP_CAPS caps = HidNative.HIDP_CAPS.Create();
                        int status = HidNative.HidP_GetCaps(preparsed, ref caps);
                        if ((status & unchecked((int)0xC0000000)) == 0)
                        {
                            descriptor.Usage = caps.Usage;
                            descriptor.UsagePage = caps.UsagePage;
                            descriptor.InputReportLength = caps.InputReportByteLength;
                            descriptor.OutputReportLength = caps.OutputReportByteLength;
                            descriptor.FeatureReportLength = caps.FeatureReportByteLength;
                            descriptor.CapabilitiesAvailable = true;
                        }
                    }
                    finally
                    {
                        HidNative.HidD_FreePreparsedData(preparsed);
                    }
                }
                return descriptor;
            }
        }

        private static HidDeviceDescriptor ReadDescriptorFromPathOnly(string path)
        {
            Match vid = Regex.Match(path, "vid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
            Match pid = Regex.Match(path, "pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
            if (!vid.Success || !pid.Success)
                return null;
            return new HidDeviceDescriptor
            {
                DevicePath = path,
                VendorId = Convert.ToUInt16(vid.Groups[1].Value, 16),
                ProductId = Convert.ToUInt16(pid.Groups[1].Value, 16),
                InterfaceNumber = ParseInterfaceNumber(path)
            };
        }

        private static int? ParseInterfaceNumber(string path)
        {
            Match match = InterfaceRegex.Match(path ?? string.Empty);
            if (!match.Success)
                return null;
            return Convert.ToInt32(match.Groups[1].Value, 16);
        }

        private static Guid? ReadContainerId(IntPtr infoSet,
            ref HidNative.SP_DEVINFO_DATA deviceInfo)
        {
            byte[] buffer = new byte[16];
            uint propertyType;
            uint requiredSize;
            HidNative.DEVPROPKEY key = new HidNative.DEVPROPKEY
            {
                fmtid = new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"),
                pid = 2
            };
            if (!HidNative.SetupDiGetDeviceProperty(infoSet, ref deviceInfo, ref key,
                out propertyType, buffer, (uint)buffer.Length, out requiredSize, 0) ||
                propertyType != HidNative.DEVPROP_TYPE_GUID || requiredSize != 16)
                return null;
            return new Guid(buffer);
        }

        private static void AddWarning(ICollection<string> warnings, string code)
        {
            if (!warnings.Contains(code))
                warnings.Add(code);
        }
    }

    // INVENTORY_METADATA_ENUMERATOR_END

    public sealed class HidSession : IDisposable
    {
        private SafeFileHandle _handle;
        private FileStream _stream;
        private bool _canWrite;
        private bool _disposed;

        public HidDeviceDescriptor Descriptor { get; private set; }

        private HidSession(HidDeviceDescriptor descriptor, SafeFileHandle handle,
            FileAccess access, bool canWrite)
        {
            Descriptor = descriptor;
            _handle = handle;
            _stream = new FileStream(handle, access, 4096, true);
            _canWrite = canWrite;
        }

        public static HidSession Open(HidDeviceDescriptor descriptor)
        {
            return OpenCore(descriptor, HidNative.GENERIC_READ | HidNative.GENERIC_WRITE,
                FileAccess.ReadWrite, true);
        }

        public static HidSession OpenReadOnly(HidDeviceDescriptor descriptor)
        {
            return OpenCore(descriptor, HidNative.GENERIC_READ, FileAccess.Read, false);
        }

        private static HidSession OpenCore(HidDeviceDescriptor descriptor, uint desiredAccess,
            FileAccess access, bool canWrite)
        {
            SafeFileHandle handle = HidNative.CreateFile(descriptor.DevicePath,
                desiredAccess,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                IntPtr.Zero,
                HidNative.OPEN_EXISTING,
                HidNative.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException("HID 장치를 열 수 없습니다.", new Win32Exception(error));
            }
            return new HidSession(descriptor, handle, access, canWrite);
        }

        public byte[] PrepareOutputReport(byte[] data)
        {
            int length = Math.Max(data.Length, Descriptor.OutputReportLength);
            byte[] report = new byte[length];
            Buffer.BlockCopy(data, 0, report, 0, Math.Min(data.Length, report.Length));
            return report;
        }

        public bool SetOutputReport(byte[] data)
        {
            ThrowIfDisposed();
            ThrowIfReadOnly();
            byte[] report = PrepareOutputReport(data);
            return HidNative.HidD_SetOutputReport(_handle, report, report.Length);
        }

        public bool SetFeature(byte[] data)
        {
            ThrowIfDisposed();
            ThrowIfReadOnly();
            int length = Math.Max(data.Length, Descriptor.FeatureReportLength);
            byte[] report = new byte[length];
            Buffer.BlockCopy(data, 0, report, 0, Math.Min(data.Length, report.Length));
            return HidNative.HidD_SetFeature(_handle, report, report.Length);
        }

        public byte[] GetFeature(byte reportId)
        {
            ThrowIfDisposed();
            int length = Math.Max(1, (int)Descriptor.FeatureReportLength);
            byte[] report = new byte[length];
            report[0] = reportId;
            if (!HidNative.HidD_GetFeature(_handle, report, report.Length))
                throw new IOException("HID feature report를 읽지 못했습니다.", new Win32Exception(Marshal.GetLastWin32Error()));
            return report;
        }

        public async Task WriteInterruptAsync(byte[] data, int timeoutMilliseconds, CancellationToken token)
        {
            ThrowIfDisposed();
            ThrowIfReadOnly();
            byte[] report = PrepareOutputReport(data);
            Task writeTask = _stream.WriteAsync(report, 0, report.Length, token);
            Task delay = Task.Delay(timeoutMilliseconds, token);
            Task completed = await Task.WhenAny(writeTask, delay).ConfigureAwait(false);
            if (completed != writeTask)
            {
                if (token.IsCancellationRequested)
                    token.ThrowIfCancellationRequested();
                Dispose();
                ObserveFault(writeTask);
                throw new TimeoutException("HID 쓰기 시간이 초과되었습니다.");
            }
            await writeTask.ConfigureAwait(false);
        }

        public async Task<byte[]> ReadInputReportAsync(int timeoutMilliseconds, CancellationToken token)
        {
            ThrowIfDisposed();
            int length = Math.Max(1, (int)Descriptor.InputReportLength);
            byte[] buffer = new byte[length];
            Task<int> readTask = _stream.ReadAsync(buffer, 0, buffer.Length, token);
            Task delay = Task.Delay(timeoutMilliseconds, token);
            Task completed = await Task.WhenAny(readTask, delay).ConfigureAwait(false);
            if (completed != readTask)
            {
                if (token.IsCancellationRequested)
                    token.ThrowIfCancellationRequested();
                Dispose();
                ObserveFault(readTask);
                throw new TimeoutException("HID 응답 시간이 초과되었습니다.");
            }

            int read = await readTask.ConfigureAwait(false);
            if (read <= 0)
                return new byte[0];
            if (read == buffer.Length)
                return buffer;
            byte[] result = new byte[read];
            Buffer.BlockCopy(buffer, 0, result, 0, read);
            return result;
        }

        private static void ObserveFault(Task task)
        {
            task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("HidSession");
        }

        private void ThrowIfReadOnly()
        {
            if (!_canWrite)
                throw new InvalidOperationException("읽기 전용 HID 세션에서는 보고서를 보낼 수 없습니다.");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_stream != null)
            {
                try { _stream.Dispose(); }
                catch { }
                _stream = null;
            }
            else if (_handle != null)
            {
                _handle.Dispose();
            }
            _handle = null;
        }
    }

    internal static class HidNative
    {
        internal const uint DIGCF_PRESENT = 0x00000002;
        internal const uint DIGCF_DEVICEINTERFACE = 0x00000010;
        internal const int ERROR_NO_MORE_ITEMS = 259;
        internal const uint DEVPROP_TYPE_GUID = 0x0000000D;
        internal static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        internal const uint GENERIC_READ = 0x80000000;
        internal const uint GENERIC_WRITE = 0x40000000;
        internal const uint FILE_SHARE_READ = 0x00000001;
        internal const uint FILE_SHARE_WRITE = 0x00000002;
        internal const uint OPEN_EXISTING = 3;
        internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public UIntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;

            public static HIDP_CAPS Create()
            {
                HIDP_CAPS caps = new HIDP_CAPS();
                caps.Reserved = new ushort[17];
                return caps;
            }
        }

        [DllImport("hid.dll")]
        internal static extern void HidD_GetHidGuid(out Guid HidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetAttributes(SafeFileHandle HidDeviceObject, ref HIDD_ATTRIBUTES Attributes);

        [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetProductString(SafeFileHandle HidDeviceObject, StringBuilder Buffer, int BufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

        [DllImport("hid.dll")]
        internal static extern int HidP_GetCaps(IntPtr PreparsedData, ref HIDP_CAPS Capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_SetOutputReport(SafeFileHandle HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_SetFeature(SafeFileHandle HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool HidD_GetFeature(SafeFileHandle HidDeviceObject, byte[] ReportBuffer, int ReportBufferLength);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, IntPtr DeviceInfoData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceInterfaceDetailWithDeviceInfo(
            IntPtr DeviceInfoSet,
            ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData,
            IntPtr DeviceInterfaceDetailData,
            uint DeviceInterfaceDetailDataSize,
            out uint RequiredSize,
            ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW",
            CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiGetDeviceProperty(
            IntPtr DeviceInfoSet,
            ref SP_DEVINFO_DATA DeviceInfoData,
            ref DEVPROPKEY PropertyKey,
            out uint PropertyType,
            [Out] byte[] PropertyBuffer,
            uint PropertyBufferSize,
            out uint RequiredSize,
            uint Flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    }
}
