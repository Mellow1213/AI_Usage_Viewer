using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

// Measures private resident pages instead of summing working sets with shared-page duplication.
// CPU percentages use all logical processors, matching Task Manager's scale.
internal static class PerformanceMeasurement
{
    internal sealed record Result(string Mode, double Seconds, double CpuSeconds, double CpuPercent,
        double PrivateResidentMB, double PrivateCommitMB, double RendererTaskSeconds, int ProcessCount);
    internal static async Task<Result> MeasureAsync(CoreWebView2 web, string mode, int seconds)
    {
        var ids = web.Environment.GetProcessInfos().Select(item => item.ProcessId).Append(Environment.ProcessId).Order().ToArray();
        using var memoryBuffer = new WorkingSetBuffer();
        double Cpu() => ids.Sum(id => { using var process = Process.GetProcessById(id); return process.TotalProcessorTime.TotalSeconds; });
        (double resident, double commit) Memory()
        {
            double resident = 0, commit = 0;
            foreach (var id in ids)
            {
                using var process = Process.GetProcessById(id);
                resident += memoryBuffer.PrivateResidentBytes(process.Handle);
                commit += process.PrivateMemorySize64;
            }
            return (resident / 1048576, commit / 1048576);
        }
        async Task<double> TaskDuration()
        {
            using var json = JsonDocument.Parse(await web.CallDevToolsProtocolMethodAsync("Performance.getMetrics", "{}"));
            return json.RootElement.GetProperty("metrics").EnumerateArray().First(item => item.GetProperty("name").GetString() == "TaskDuration").GetProperty("value").GetDouble();
        }
        var startTasks = await TaskDuration();
        var startCpu = Cpu();
        var watch = Stopwatch.StartNew();
        var samples = new List<(double resident, double commit)> { Memory() };
        for (var i = 0; i < seconds / 5; i++)
        {
            await Task.Delay(5000);
            var currentIds = web.Environment.GetProcessInfos().Select(item => item.ProcessId).Append(Environment.ProcessId).Order().ToArray();
            if (!ids.SequenceEqual(currentIds)) throw new InvalidOperationException("WebView2 processes changed during measurement; rerun instead of comparing incomplete CPU totals.");
            samples.Add(Memory());
        }
        var elapsed = watch.Elapsed.TotalSeconds;
        var cpu = Cpu() - startCpu;
        var tasks = await TaskDuration() - startTasks;
        return new(mode, elapsed, cpu, cpu / elapsed / Environment.ProcessorCount * 100,
            samples.Average(item => item.resident), samples.Average(item => item.commit), tasks, ids.Length);
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryWorkingSet(IntPtr process, IntPtr buffer, int size);
    private sealed class WorkingSetBuffer : IDisposable
    {
        private IntPtr _buffer = Marshal.AllocHGlobal(1024 * 1024);
        private int _size = 1024 * 1024;
        internal long PrivateResidentBytes(IntPtr process)
        {
            while (!QueryWorkingSet(process, _buffer, _size))
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 24 || _size >= 16 * 1024 * 1024) throw new System.ComponentModel.Win32Exception(error);
                Marshal.FreeHGlobal(_buffer);
                _size *= 2;
                _buffer = Marshal.AllocHGlobal(_size);
            }
            var count = Marshal.ReadIntPtr(_buffer).ToInt64();
            long privatePages = 0;
            for (var i = 1; i <= count; i++)
                if ((Marshal.ReadIntPtr(_buffer, checked((int)(i * IntPtr.Size))).ToInt64() & (1 << 8)) == 0) privatePages++;
            return privatePages * Environment.SystemPageSize;
        }
        public void Dispose() => Marshal.FreeHGlobal(_buffer);
    }
}
