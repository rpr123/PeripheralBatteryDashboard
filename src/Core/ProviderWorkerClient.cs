using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace PeripheralBatteryDashboard.Core
{
    public static class ProviderWorkerSafety
    {
        public static bool RequiresExactHidSelector(DeviceProfile profile)
        {
            return profile != null &&
                (DeviceMonitorService.IsHidProfile(profile) ||
                 ProviderSafetyPolicy.RequiresExactHidSelector(profile.ProviderId));
        }

        public static bool HasExactHidSelector(DeviceProfile profile)
        {
            return profile != null && DeviceMonitorService.HasExactHidSelector(profile);
        }

        public static bool IsSafeHidWorkerProfile(DeviceProfile profile)
        {
            return profile != null && profile.Enabled &&
                !string.IsNullOrWhiteSpace(profile.Id) && profile.Id.Length <= 200 &&
                !string.IsNullOrWhiteSpace(profile.ProviderId) &&
                profile.ProviderId.Length <= 200 &&
                RequiresExactHidSelector(profile) && HasExactHidSelector(profile);
        }
    }

    public interface IProviderReadExecutor
    {
        Task<BatteryReading> ReadAsync(DeviceProfile profile,
            CancellationToken cancellationToken);
    }

    public sealed class ProviderWorkerRequest
    {
        public string RequestId { get; set; }
        public DeviceProfile Profile { get; set; }

        public ProviderWorkerRequest()
        {
            RequestId = string.Empty;
        }
    }

    public sealed class ProviderWorkerResponse
    {
        public string RequestId { get; set; }
        public BatteryReading Reading { get; set; }

        public ProviderWorkerResponse()
        {
            RequestId = string.Empty;
        }
    }

    public static class ProviderWorkerProtocol
    {
        public const int MaximumMessageCharacters = 65536;
        public const string ResponsePrefix = "PBDW2:";

        public static string SerializeRequest(string requestId, DeviceProfile profile)
        {
            string json = new JavaScriptSerializer().Serialize(new ProviderWorkerRequest
            {
                RequestId = requestId ?? string.Empty,
                Profile = profile
            });
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }

        public static bool TryDeserializeRequest(string input,
            out ProviderWorkerRequest request)
        {
            request = null;
            if (string.IsNullOrWhiteSpace(input) ||
                input.Length > MaximumMessageCharacters)
                return false;
            try
            {
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(input));
                if (json.Length > MaximumMessageCharacters)
                    return false;
                request = new JavaScriptSerializer().Deserialize<ProviderWorkerRequest>(json);
                bool valid = request != null &&
                    !string.IsNullOrWhiteSpace(request.RequestId) &&
                    request.RequestId.Length <= 64 &&
                    request.Profile != null &&
                    !string.IsNullOrWhiteSpace(request.Profile.Id);
                if (!valid)
                    return false;
                Dictionary<string, object> options =
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                if (request.Profile.ProviderOptions != null)
                {
                    foreach (KeyValuePair<string, object> pair in
                        request.Profile.ProviderOptions)
                        options[pair.Key] = pair.Value;
                }
                request.Profile.ProviderOptions = options;
                return true;
            }
            catch
            {
                request = null;
                return false;
            }
        }

        public static string SerializeResponse(string requestId, BatteryReading reading)
        {
            return ResponsePrefix + new JavaScriptSerializer().Serialize(
                new ProviderWorkerResponse
                {
                    RequestId = requestId ?? string.Empty,
                    Reading = reading
                });
        }

        public static bool TryDeserializeResponse(string output,
            string expectedRequestId, out BatteryReading reading)
        {
            reading = null;
            if (string.IsNullOrEmpty(output) ||
                output.Length > MaximumMessageCharacters ||
                string.IsNullOrWhiteSpace(expectedRequestId))
                return false;
            string[] lines = output.Replace("\r\n", "\n").Split('\n');
            for (int index = lines.Length - 1; index >= 0; index--)
            {
                string line = lines[index];
                if (!line.StartsWith(ResponsePrefix, StringComparison.Ordinal))
                    continue;
                try
                {
                    ProviderWorkerResponse response =
                        new JavaScriptSerializer().Deserialize<ProviderWorkerResponse>(
                            line.Substring(ResponsePrefix.Length));
                    if (response == null || response.Reading == null ||
                        !string.Equals(response.RequestId, expectedRequestId,
                            StringComparison.Ordinal))
                        return false;
                    reading = response.Reading;
                    return true;
                }
                catch
                {
                    reading = null;
                    return false;
                }
            }
            return false;
        }
    }

    internal sealed class BoundedTextResult
    {
        public string Text;
        public bool Truncated;
    }

    public sealed class ProviderWorkerClient : IProviderReadExecutor
    {
        private const int ReapTimeoutMilliseconds = 5000;
        private readonly string _workerPath;
        private readonly string _workerMode;
        private static readonly object WorkerProcessLock = new object();
        private static readonly HashSet<int> ActiveWorkerProcessIds = new HashSet<int>();
        private static int _lastStartedWorkerProcessId;

        public ProviderWorkerClient(string baseDirectory)
            : this(baseDirectory, "--provider-worker")
        {
        }

        internal ProviderWorkerClient(string baseDirectory, string workerMode)
        {
            _workerPath = Path.Combine(baseDirectory ?? string.Empty,
                "PeripheralBatteryDashboard.Diagnostics.exe");
            _workerMode = string.IsNullOrWhiteSpace(workerMode)
                ? "--provider-worker"
                : workerMode;
        }

        internal static int ActiveWorkerCount
        {
            get
            {
                lock (WorkerProcessLock)
                    return ActiveWorkerProcessIds.Count;
            }
        }

        internal static int LastStartedWorkerProcessId
        {
            get { return Volatile.Read(ref _lastStartedWorkerProcessId); }
        }

        public async Task<BatteryReading> ReadAsync(DeviceProfile profile,
            CancellationToken cancellationToken)
        {
            if (profile == null)
                throw new ArgumentNullException("profile");
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(_workerPath))
            {
                return BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Error,
                    "조회 보조 프로세스 없음",
                    "Diagnostics 실행 파일이 없어 안전한 장치 조회를 시작하지 않았습니다.",
                    "provider-worker-missing");
            }

            string requestId = Guid.NewGuid().ToString("N");
            string requestText = ProviderWorkerProtocol.SerializeRequest(requestId, profile);
            if (requestText.Length > ProviderWorkerProtocol.MaximumMessageCharacters)
            {
                return BatteryReading.Unavailable(profile,
                    DeviceConnectionState.Error,
                    "조회 요청이 너무 큼",
                    "장치 프로필이 조회 보조 프로세스의 안전 한도를 넘었습니다.",
                    "provider-worker-request-too-large");
            }

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = _workerPath,
                Arguments = _workerMode + " " +
                    Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
                WorkingDirectory = Path.GetDirectoryName(_workerPath),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };

            using (Process process = new Process())
            using (WorkerJob job = WorkerJob.TryCreate())
            {
                if (job == null)
                {
                    return BatteryReading.Unavailable(profile,
                        DeviceConnectionState.Error,
                        "조회 격리 준비 실패",
                        "안전한 조회 프로세스 격리를 준비하지 못했습니다.",
                        "provider-worker-job-unavailable");
                }
                process.StartInfo = startInfo;
                process.EnableRaisingEvents = true;
                TaskCompletionSource<bool> exited = NewCompletionSource();
                process.Exited += delegate { exited.TrySetResult(true); };
                bool started = false;
                bool reaped = false;
                int processId = 0;
                try
                {
                    if (!process.Start())
                        throw new InvalidOperationException("조회 보조 프로세스를 시작하지 못했습니다.");
                    started = true;
                    processId = process.Id;
                    RegisterWorkerProcess(processId);
                    if (!job.TryAssign(process))
                    {
                        return BatteryReading.Unavailable(profile,
                            DeviceConnectionState.Error,
                            "조회 격리 연결 실패",
                            "조회 프로세스를 안전한 작업 경계에 연결하지 못했습니다.",
                            "provider-worker-job-assign-failed");
                    }

                    Task<BoundedTextResult> outputTask = ReadBoundedAsync(
                        process.StandardOutput, ProviderWorkerProtocol.MaximumMessageCharacters);
                    Task<BoundedTextResult> errorTask = ReadBoundedAsync(
                        process.StandardError, 4096);
                    Task writeTask = WriteRequestAsync(process, requestText);
                    if (process.HasExited)
                        exited.TrySetResult(true);

                    TaskCompletionSource<bool> cancelled = NewCompletionSource();
                    using (cancellationToken.Register(() => cancelled.TrySetResult(true)))
                    {
                        Task winner = await Task.WhenAny(exited.Task, cancelled.Task)
                            .ConfigureAwait(false);
                        if (winner == cancelled.Task)
                        {
                            reaped = await TerminateAndReapAsync(process, exited.Task,
                                    job)
                                .ConfigureAwait(false);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }

                    job.Terminate();
                    await writeTask.ConfigureAwait(false);
                    BoundedTextResult output = await outputTask.ConfigureAwait(false);
                    BoundedTextResult error = await errorTask.ConfigureAwait(false);
                    reaped = true;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (output.Truncated || error.Truncated)
                    {
                        return BatteryReading.Unavailable(profile,
                            DeviceConnectionState.Error,
                            "조회 응답 한도 초과",
                            "장치 조회 프로세스의 출력이 안전 한도를 넘었습니다.",
                            "provider-worker-output-too-large");
                    }
                    if (process.ExitCode != 0)
                    {
                        return BatteryReading.Unavailable(profile,
                            DeviceConnectionState.Error,
                            "조회 보조 프로세스 오류",
                            "장치 조회 프로세스가 비정상 종료되었습니다.",
                            "provider-worker-exit");
                    }

                    BatteryReading reading;
                    if (!ProviderWorkerProtocol.TryDeserializeResponse(output.Text,
                            requestId, out reading) ||
                        !string.Equals(reading.ProfileId, profile.Id,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return BatteryReading.Unavailable(profile,
                            DeviceConnectionState.Error,
                            "조회 응답 해석 실패",
                            "장치 조회 프로세스가 유효한 결과를 반환하지 않았습니다.",
                            "provider-worker-output-invalid");
                    }
                    if (reading.Percent.HasValue &&
                        (reading.Percent.Value < 0 || reading.Percent.Value > 100))
                    {
                        return BatteryReading.Unavailable(profile,
                            DeviceConnectionState.Error,
                            "잘못된 배터리 응답",
                            "조회 프로세스가 허용 범위를 벗어난 값을 반환했습니다.",
                            "provider-worker-battery-out-of-range");
                    }
                    reading.ProfileId = profile.Id;
                    reading.DisplayName = profile.DisplayName;
                    reading.Category = profile.Category;
                    return reading;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    return BatteryReading.Unavailable(profile,
                        DeviceConnectionState.Error,
                        "조회 보조 프로세스 오류",
                        "장치 조회 프로세스를 안전하게 실행하지 못했습니다.",
                        "provider-worker-exception");
                }
                finally
                {
                    if (started && !reaped)
                        reaped = await TerminateAndReapAsync(process, exited.Task, job)
                            .ConfigureAwait(false);
                    if (started && reaped)
                        UnregisterWorkerProcess(processId);
                }
            }
        }

        private static TaskCompletionSource<bool> NewCompletionSource()
        {
            return new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static async Task WriteRequestAsync(Process process, string request)
        {
            await process.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
            process.StandardInput.Close();
        }

        private static async Task<BoundedTextResult> ReadBoundedAsync(StreamReader reader,
            int maximumCharacters)
        {
            char[] buffer = new char[4096];
            StringBuilder kept = new StringBuilder(Math.Min(maximumCharacters, 4096));
            bool truncated = false;
            while (true)
            {
                int read = await reader.ReadAsync(buffer, 0, buffer.Length)
                    .ConfigureAwait(false);
                if (read <= 0)
                    break;
                int remaining = maximumCharacters - kept.Length;
                if (remaining > 0)
                    kept.Append(buffer, 0, Math.Min(remaining, read));
                if (read > remaining)
                    truncated = true;
            }
            return new BoundedTextResult { Text = kept.ToString(), Truncated = truncated };
        }

        private static async Task<bool> TerminateAndReapAsync(Process process,
            Task exitedTask, WorkerJob job)
        {
            if (job != null)
                job.Terminate();
            TryKill(process);
            Task completed = await Task.WhenAny(exitedTask,
                Task.Delay(ReapTimeoutMilliseconds)).ConfigureAwait(false);
            if (completed != exitedTask)
            {
                // Releasing the device I/O key while this child is still alive could
                // overlap two writes to one receiver. Preserve quarantine in this
                // exceptional case until Windows confirms process termination.
                await exitedTask.ConfigureAwait(false);
            }
            try { process.WaitForExit(); }
            catch { }
            return HasExited(process);
        }

        private static bool HasExited(Process process)
        {
            try { return process.HasExited; }
            catch { return true; }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill();
            }
            catch { }
        }

        private static void RegisterWorkerProcess(int processId)
        {
            lock (WorkerProcessLock)
                ActiveWorkerProcessIds.Add(processId);
            Volatile.Write(ref _lastStartedWorkerProcessId, processId);
        }

        private static void UnregisterWorkerProcess(int processId)
        {
            lock (WorkerProcessLock)
                ActiveWorkerProcessIds.Remove(processId);
        }
    }

    internal sealed class WorkerJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private readonly SafeFileHandle _handle;

        private WorkerJob(SafeFileHandle handle)
        {
            _handle = handle;
        }

        public static WorkerJob TryCreate()
        {
            try
            {
                SafeFileHandle handle = JobNative.CreateJobObject(IntPtr.Zero, null);
                if (handle == null || handle.IsInvalid)
                {
                    if (handle != null)
                        handle.Dispose();
                    return null;
                }
                JobNative.JobObjectExtendedLimitInformation information =
                    new JobNative.JobObjectExtendedLimitInformation();
                information.BasicLimitInformation.LimitFlags =
                    JobObjectLimitKillOnJobClose;
                int length = Marshal.SizeOf(typeof(
                    JobNative.JobObjectExtendedLimitInformation));
                if (!JobNative.SetInformationJobObject(handle, 9,
                        ref information, (uint)length))
                {
                    handle.Dispose();
                    return null;
                }
                return new WorkerJob(handle);
            }
            catch
            {
                return null;
            }
        }

        public bool TryAssign(Process process)
        {
            if (process == null || _handle == null || _handle.IsInvalid)
                return false;
            try
            {
                return JobNative.AssignProcessToJobObject(_handle, process.Handle);
            }
            catch
            {
                return false;
            }
        }

        public void Terminate()
        {
            if (_handle == null || _handle.IsInvalid)
                return;
            try { JobNative.TerminateJobObject(_handle, 1); }
            catch { }
        }

        public void Dispose()
        {
            if (_handle != null)
                _handle.Dispose();
        }
    }

    internal static class JobNative
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public IntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObject(
            IntPtr jobAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeFileHandle job, int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(
            SafeFileHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(
            SafeFileHandle job, uint exitCode);
    }
}
