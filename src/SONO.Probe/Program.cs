using NAudio.CoreAudioApi;

// Device inventory: all active render + capture endpoints.
var en = new MMDeviceEnumerator();

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
