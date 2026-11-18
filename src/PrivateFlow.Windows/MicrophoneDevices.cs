using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace PrivateFlow.Windows;

internal sealed record MicrophoneDevice(string Id, string Name)
{
    public override string ToString() => Name;
}
internal sealed class MicrophoneDevices : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly Dispatcher dispatcher;
    private bool disposed;
    public event Action? Changed;
    public MicrophoneDevices(Dispatcher dispatcher) { this.dispatcher = dispatcher; enumerator.RegisterEndpointNotificationCallback(this); }
    public List<MicrophoneDevice> List()
    {
        var result = Active(DataFlow.Capture);
        if (result.Count > 0) result.Insert(0, new("", "Follow Windows default microphone"));
        return result;
    }
    // The output devices whose sound a meeting can record; the Windows default comes first, also while none is connected.
    public List<MicrophoneDevice> ListPlayback()
    {
        var result = Active(DataFlow.Render);
        result.Insert(0, new("", "Follow Windows default output"));
        return result;
    }
    private List<MicrophoneDevice> Active(DataFlow flow)
    {
        var result = new List<MicrophoneDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)) { using (device) result.Add(new(device.ID, device.FriendlyName)); }
        return result;
    }
    public MMDevice Open(string id) => id.Length == 0 ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia) : enumerator.GetDevice(id);
    // The chosen output device while it is connected, otherwise the Windows default; opened here so its name can be kept with the meeting.
    public MMDevice OpenPlayback(string id)
    {
        if (id.Length > 0)
        {
            try
            {
                var chosen = enumerator.GetDevice(id);
                if (chosen.State == DeviceState.Active) return chosen;
                chosen.Dispose();
            }
            catch (COMException) { }
        }
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }
    private void Refresh() { if (!disposed) dispatcher.BeginInvoke(() => { if (!disposed) Changed?.Invoke(); }); }
    public void OnDeviceStateChanged(string id, DeviceState state) => Refresh();
    public void OnDeviceAdded(string id) => Refresh();
    public void OnDeviceRemoved(string id) => Refresh();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string id) { if (flow == DataFlow.Capture) Refresh(); }
    public void OnPropertyValueChanged(string id, PropertyKey key) => Refresh();
    public void Dispose() { disposed = true; enumerator.UnregisterEndpointNotificationCallback(this); enumerator.Dispose(); }
}
