// Test-only WASAPI render-endpoint loopback. Never opens a microphone.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

public sealed class RunicLoopback : IDisposable
{
    readonly string path;
    readonly Thread thread;
    readonly ManualResetEvent ready = new ManualResetEvent(false);
    volatile bool stop;
    Exception error;
    bool disposed;
    public RunicLoopback(string path)
    {
        this.path = path;
        if (File.Exists(path)) throw new IOException("Audio file already exists");
        thread = new Thread(Capture); thread.IsBackground = true; thread.Start();
        if (!ready.WaitOne(10000)) { stop = true; if (thread.Join(5000)) ready.Dispose(); throw new TimeoutException("Audio capture did not start"); }
        if (error != null) { thread.Join(5000); ready.Dispose(); throw new InvalidOperationException("Audio capture failed", error); }
    }
    void Capture()
    {
        IAudioClient client = null; IAudioCaptureClient capture = null; IMMDevice device = null;
        IMMDeviceEnumerator enumerator = null;
        IntPtr format = IntPtr.Zero;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(0, 0, out device);
            object audio; Guid clientId = typeof(IAudioClient).GUID;
            device.Activate(ref clientId, 23, IntPtr.Zero, out audio); client = (IAudioClient)audio;
            client.GetMixFormat(out format);
            int tag = (ushort)Marshal.ReadInt16(format, 0), channels = (ushort)Marshal.ReadInt16(format, 2);
            int rate = Marshal.ReadInt32(format, 4), bits = Marshal.ReadInt16(format, 14);
            if (bits != 32 || (tag != 3 && !(tag == 65534 && Marshal.ReadInt32(format, 24) == 3)))
                throw new InvalidOperationException("Expected float32 render mix for this test endpoint");
            Guid session = Guid.Empty;
            client.Initialize(0, 0x20000, 1000000, 0, format, ref session);
            Guid captureId = typeof(IAudioCaptureClient).GUID;
            client.GetService(ref captureId, out audio); capture = (IAudioCaptureClient)audio;
            using (var data = new MemoryStream())
            using (var writer = new BinaryWriter(data))
            {
                client.Start(); ready.Set();
                var deadline = DateTime.UtcNow.AddSeconds(30); double peak = 0;
                while (!stop && DateTime.UtcNow < deadline)
                {
                    uint frames; capture.GetNextPacketSize(out frames);
                    while (frames > 0)
                    {
                        IntPtr buffer; uint flags; ulong position, time;
                        capture.GetBuffer(out buffer, out frames, out flags, out position, out time);
                        try
                        {
                            float[] samples = new float[checked((int)frames * channels)];
                            if ((flags & 2) == 0) Marshal.Copy(buffer, samples, 0, samples.Length);
                            foreach (float sample in samples) { peak = Math.Max(peak, Math.Abs(sample)); writer.Write((short)(Math.Max(-1, Math.Min(1, sample)) * 32767)); }
                        }
                        finally { capture.ReleaseBuffer(frames); }
                        capture.GetNextPacketSize(out frames);
                    }
                    Thread.Sleep(5);
                }
                client.Stop();
                if (!stop) throw new TimeoutException("Audio capture exceeded its 30-second limit");
                using (var file = new BinaryWriter(new FileStream(path, FileMode.CreateNew)))
                {
                    file.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); file.Write(checked((int)data.Length + 36));
                    file.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); file.Write(16); file.Write((short)1); file.Write((short)channels);
                    file.Write(rate); file.Write(rate * channels * 2); file.Write((short)(channels * 2)); file.Write((short)16);
                    file.Write(System.Text.Encoding.ASCII.GetBytes("data")); file.Write(checked((int)data.Length)); file.Write(data.ToArray());
                }
                if (data.Length < rate * channels * 2 || peak < 0.001) throw new InvalidOperationException("Narrator recording is silent or shorter than one second");
            }
        }
        catch (Exception exception) { error = exception; }
        finally
        {
            ready.Set();
            if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (device != null) Marshal.ReleaseComObject(device);
            if (enumerator != null) Marshal.ReleaseComObject(enumerator);
        }
    }
    public void Dispose() { if (disposed) return; stop = true; if (!thread.Join(5000)) throw new TimeoutException("Audio capture did not stop"); disposed = true; ready.Dispose(); if (error != null) throw new InvalidOperationException("Audio capture failed", error); }
}
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }
[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(int flow, uint state, out IntPtr devices);
    void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
}
[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IMMDevice
{
    void Activate(ref Guid id, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
}
[ComImport, Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioClient
{
    void Initialize(int mode, uint flags, long duration, long periodicity, IntPtr format, ref Guid session);
    void GetBufferSize(out uint frames); void GetStreamLatency(out long latency); void GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int mode, IntPtr format, out IntPtr closest);
    void GetMixFormat(out IntPtr format); void GetDevicePeriod(out long normal, out long minimum);
    void Start(); void Stop(); void Reset(); void SetEventHandle(IntPtr handle);
    void GetService(ref Guid id, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}
[ComImport, Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] interface IAudioCaptureClient
{
    void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong position, out ulong time);
    void ReleaseBuffer(uint frames); void GetNextPacketSize(out uint frames);
}
