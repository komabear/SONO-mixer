using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

// Modes:
//   (no args)                  → device inventory
//   setdefault <id>            → set default render endpoint
//   tone <id> <seconds>        → play a 440 Hz tone into a render endpoint
//   meter <id> <seconds>       → peak level of a render endpoint via loopback capture
var en = new MMDeviceEnumerator();

if (args.Contains("inspect"))
{
    foreach (var m in typeof(ISampleProvider).GetMethods())
        Console.WriteLine("ISampleProvider: " + m);
    Console.WriteLine("=== builder/loopback types ===");
    var asm = typeof(MMDeviceEnumerator).Assembly;
    foreach (var t in asm.GetExportedTypes().Where(t => t.Name.Contains("Recorder") || t.Name.Contains("Player") || t.Name.Contains("Loopback")).OrderBy(t => t.FullName))
    {
        Console.WriteLine($"-- {t.FullName}");
        foreach (var m in t.GetMembers().Where(m => m.MemberType is System.Reflection.MemberTypes.Method or System.Reflection.MemberTypes.Property))
        {
            var s = m.ToString();
            if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_") || m.Name.StartsWith("add_") || m.Name.StartsWith("remove_")) continue;
            if (s!.StartsWith("System." + m.Name) || s.Contains(" " + m.Name + "("))
                Console.WriteLine("     " + s);
        }
    }
    Console.WriteLine("=== ProcessLoopbackMode values (reflection) ===");
    var wasapiAsm = typeof(NAudio.Wave.WasapiRecorder).Assembly;
    var plm = wasapiAsm.GetType("NAudio.CoreAudioApi.ProcessLoopbackMode");
    if (plm is not null)
        foreach (var v in Enum.GetValues(plm))
            Console.WriteLine($"  {v} = {(int)v}");
    Console.WriteLine("=== CaptureDataAvailableHandler signature ===");
    var h = typeof(NAudio.Wave.CaptureDataAvailableHandler).GetMethod("Invoke");
    Console.WriteLine("  " + h);
    Console.WriteLine("=== WasapiRecorder WaveFormat property? ===");
    foreach (var p in typeof(NAudio.Wave.WasapiRecorder).GetProperties())
        Console.WriteLine($"  {p.PropertyType} {p.Name}");
    Console.WriteLine("=== WasapiRecorderBuilder ctor ===");
    foreach (var c in typeof(NAudio.Wave.WasapiRecorderBuilder).GetConstructors())
        Console.WriteLine("  " + c);
    return;
}

if (args.Length == 2 && args[0] == "setdefault")
{
    SONO.Core.Audio.PolicyConfigApi.SetDefaultDevice(args[1]);
    Console.WriteLine($"Default render set to: {en.GetDevice(args[1]).FriendlyName}");
    return;
}

if (args.Length == 2 && args[0] == "tonedefault")
{
    var defDev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
    using var outStream = new WasapiOut(defDev, AudioClientShareMode.Shared, false, 60);
    outStream.Init(new SampleToWaveProvider(new Sine48k()));
    outStream.Play();
    Console.WriteLine($"tone → (default) {defDev.FriendlyName}");
    await Task.Delay(int.Parse(args[1]) * 1000);
    Console.WriteLine("tone done");
    return;
}

if (args.Length == 2 && args[0] == "mute")
{
    // mute/unmute every session of the given pid on every device
    uint pid = uint.Parse(args[1]);
    foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        try
        {
            var muteCol = d.AudioSessionManager.Sessions;
            for (int i = 0; i < muteCol.Count; i++)
            {
                if (muteCol[i].GetProcessID == pid)
                {
                    muteCol[i].SimpleAudioVolume.Mute = true;
                    muteCol[i].SimpleAudioVolume.Volume = 0f;
                    Console.WriteLine($"muted pid {pid} on {d.FriendlyName}");
                }
            }
        }
        catch { }
    }
    return;
}

if (args.Length == 3 && args[0] == "plooptest")
{
    // capture the given process's audio via PROCESS loopback and print the peak
    uint pid = uint.Parse(args[1]);
    float max = 0;
#pragma warning disable CS0618
    var rec = await new WasapiRecorderBuilder()
        .WithProcessLoopback(pid, ProcessLoopbackMode.ExcludeTargetProcessTree)
        .WithSharedMode()
        .BuildAsync();
#pragma warning restore CS0618
    rec.DataAvailable += (ReadOnlySpan<byte> data, AudioClientBufferFlags f, long p, long q) =>
    {
        for (int i = 0; i + 4 <= data.Length; i += 4)
        {
            float v = MathF.Abs(System.Runtime.InteropServices.MemoryMarshal.Read<float>(data.Slice(i)));
            if (v > max) max = v;
        }
    };
    rec.StartRecording();
    Console.WriteLine($"capturing pid {pid}…");
    await Task.Delay(int.Parse(args[2]) * 1000);
    rec.StopRecording();
    Console.WriteLine($"process-loopback peak={max:0.000}");
    return;
}

if (args.Length == 3 && args[0] == "tone")
{
    var dev = en.GetDevice(args[1]);
    using var outStream = new WasapiOut(dev, AudioClientShareMode.Shared, false, 60);
    outStream.Init(new SampleToWaveProvider(new Sine48k()));
    outStream.Play();
    Console.WriteLine($"tone → {dev.FriendlyName}");
    await Task.Delay(int.Parse(args[2]) * 1000);
    Console.WriteLine("tone done");
    return;
}

if (args.Length == 3 && args[0] == "meter")
{
    var dev = en.GetDevice(args[1]);
    float max = 0;
    using var cap = new WasapiLoopbackCapture(dev);
    cap.DataAvailable += (_, e) =>
    {
        int bytesPerSample = cap.WaveFormat.BitsPerSample / 8;
        int frames = e.BytesRecorded / bytesPerSample;
        if (cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            for (int i = 0; i < frames; i++)
            {
                float v = MathF.Abs(BitConverter.ToSingle(e.Buffer, i * 4));
                if (v > max) max = v;
            }
        }
        else // 16-bit PCM
        {
            for (int i = 0; i < frames; i++)
            {
                float v = MathF.Abs(Math.Abs((float)BitConverter.ToInt16(e.Buffer, i * 2)) / 32768f);
                if (v > max) max = v;
            }
        }
    };
    cap.StartRecording();
    await Task.Delay(int.Parse(args[2]) * 1000);
    cap.StopRecording();
    Console.WriteLine($"{dev.FriendlyName}: peak={max:0.000}");
    return;
}

if (args.Length == 1 && args[0] == "sess")
{
    Console.WriteLine("== sessions per render device ==");
    foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    {
        try
        {
            var sessCol = d.AudioSessionManager.Sessions;
            if (sessCol.Count == 0) continue;
            Console.WriteLine($"-- {d.FriendlyName}");
            for (int i = 0; i < sessCol.Count; i++)
            {
                var s = sessCol[i];
                uint pid = 0;
                try { pid = s.GetProcessID; } catch { }
                string name = "?";
                try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
                if (pid == 0) name = "system";
                Console.WriteLine($"     {name,-22} vol={s.SimpleAudioVolume.Volume:0.00} mute={s.SimpleAudioVolume.Mute}");
            }
        }
        catch { }
    }
    return;
}

Console.WriteLine("== RENDER endpoints ==");
foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
    Console.WriteLine($"  {d.FriendlyName,-45} [{d.ID}]");

Console.WriteLine("== CAPTURE endpoints ==");
foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
    Console.WriteLine($"  {d.FriendlyName,-45} [{d.ID}]");

var def = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
Console.WriteLine($"== Default render: {def.FriendlyName}");
var col = def.AudioSessionManager.Sessions;
Console.WriteLine($"== Sessions on default device: {col.Count}");

/// <summary>Minimal 440 Hz stereo sine at 48 kHz.</summary>
file class Sine48k : ISampleProvider
{
    private long _n;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    public int Read(Span<float> buffer)
    {
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = 0.4f * MathF.Sin(2 * MathF.PI * 440 * (_n + i / 2) / 48000);
        _n += buffer.Length / 2;
        return buffer.Length;
    }
}
