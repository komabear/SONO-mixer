using System.Reflection;

if (args.Contains("reflect"))
{
    var asm = typeof(NAudio.CoreAudioApi.MMDeviceEnumerator).Assembly;
    Console.WriteLine("== Types containing Session/State/State ==");
    foreach (var t in asm.GetExportedTypes().Where(t => t.Name.Contains("Session") || t.Name.Contains("State")).OrderBy(t => t.FullName))
        Console.WriteLine("  " + t.FullName);

    Console.WriteLine("== AudioSessionState-like enums ==");
    foreach (var t in asm.GetExportedTypes().Where(t => t.IsEnum))
    {
        var names = Enum.GetNames(t);
        if (names.Any(n => n.Contains("AudioSessionState")))
            Console.WriteLine("  " + t.FullName + " { " + string.Join(", ", names) + " }");
    }

    Console.WriteLine("== AudioSessionControl public members ==");
    foreach (var m in typeof(NAudio.CoreAudioApi.AudioSessionControl).GetMembers(BindingFlags.Public | BindingFlags.Instance)
                 .Where(m => m.MemberType is MemberTypes.Property or MemberTypes.Method)
                 .OrderBy(m => m.Name))
        Console.WriteLine("  " + m);
    return;
}

// Smoke test: validates the NAudio API surface the engine relies on, without the UI.
var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
var dev = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Console);
Console.WriteLine($"Render : {dev.FriendlyName}");

var col = dev.AudioSessionManager.Sessions;
Console.WriteLine($"Sessions: {col.Count}");
for (int i = 0; i < col.Count; i++)
{
    var s = col[i];
    string id;
    try { id = s.GetSessionIdentifier ?? ""; } catch { id = "<n/a>"; }
    Console.WriteLine(
        $"  pid={s.GetProcessID,-6} {s.State,-32} vol={s.SimpleAudioVolume.Volume:0.00} mute={s.SimpleAudioVolume.Mute}  id={id}");
}

try
{
    var mic = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console);
    Console.WriteLine(
        $"Capture: {mic.FriendlyName} vol={mic.AudioEndpointVolume.MasterVolumeLevelScalar:0.00} mute={mic.AudioEndpointVolume.Mute}");
}
catch (Exception e)
{
    Console.WriteLine($"Capture: unavailable ({e.Message})");
}
