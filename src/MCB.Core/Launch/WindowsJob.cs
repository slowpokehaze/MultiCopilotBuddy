using System.Diagnostics;
using System.IO.Pipes;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MCB.Core.Launch;

/// <summary>
/// Windows Job Object (integration-cb-into-mcb.md §3): все агенты одного MCB
/// в job с KILL_ON_JOB_CLOSE — умер менеджер, умерли все агенты. Зомби-боты
/// умирают архитектурно, а не taskkill-обходом.
/// </summary>
internal static class WindowsJob
{
    private static IntPtr _jobHandle = IntPtr.Zero;
    private static readonly object Lock = new();
    private static bool _creationFailed;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JobObjectExtendedLimit info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimit
    {
        public UInt64 PerProcessUserTimeLimit;
        public UInt64 PerJobUserTimeLimit;
        public UInt32 LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public UInt32 ActiveProcessLimit;
        public UIntPtr Affinity;
        public UInt32 PriorityClass;
        public UInt32 SchedulingClass;
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const UInt32 JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    /// <summary>Job текущего процесса MCB (лениво, один на процесс). IntPtr.Zero = не вышло.</summary>
    public static IntPtr GetOrCreate()
    {
        lock (Lock)
        {
            if (_jobHandle != IntPtr.Zero || _creationFailed)
            {
                return _jobHandle;
            }

            try
            {
                var handle = CreateJobObjectW(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                {
                    throw new Win32Exception();
                }

                var limit = new JobObjectExtendedLimit
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                };
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limit,
                        System.Runtime.InteropServices.Marshal.SizeOf<JobObjectExtendedLimit>()))
                {
                    throw new Win32Exception();
                }

                _jobHandle = handle;
            }
            catch (Exception)
            {
                _creationFailed = true;
                _jobHandle = IntPtr.Zero;
            }

            return _jobHandle;
        }
    }

    /// <summary>Добавляет процесс в job. false = не вышло (например, elevated-ребёнок из не-админского MCB).</summary>
    public static bool Assign(Process process)
    {
        var job = GetOrCreate();
        if (job == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return AssignProcessToJobObject(job, process.Handle);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>MCB сам запущен с правами администратора (иначе — UAC-fallback при старте агента).</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}
