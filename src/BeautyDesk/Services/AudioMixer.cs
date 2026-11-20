using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace BeautyDesk.Services;

/// <summary>
/// Микшер громкости: устройства вывода (с выбором устройства по умолчанию) и громкость каждой программы,
/// которая сейчас издаёт звук или держит аудиосеанс открытым.
/// </summary>
internal sealed class AudioMixer : IDisposable
{
    public sealed record Device(string Id, string Name, bool Default);
    /// <param name="Id">Идентификатор сеанса — по нему меняется громкость.</param>
    /// <param name="Icon">Ключ иконки программы (путь к exe) или null.</param>
    public sealed record App(string Id, string Name, string? Icon, int Volume, bool Muted, bool Active);
    public sealed record Snapshot(IReadOnlyList<Device> Devices, int? Master, bool MasterMuted, IReadOnlyList<App> Apps);

    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dictionary<uint, (string Name, string? Path)> _processes = [];
    private readonly DeviceWatcher _watcher;
    // Перечислить устройства вывода долго (у кого их много — больше секунды), поэтому список держится
    // готовым и перечитывается в фоне, только когда Windows сообщает, что устройства изменились.
    private volatile IReadOnlyList<Device> _devices = [];
    private volatile bool _devicesDirty = true;
    private int _loading;

    public AudioMixer()
    {
        _watcher = new DeviceWatcher(() => _devicesDirty = true);
        _enumerator.RegisterEndpointNotificationCallback(_watcher);
    }

    /// <summary>Список устройств изменился (подключили наушники, сменили устройство по умолчанию).</summary>
    public event Action? DevicesChanged;

    public Snapshot Read()
    {
        if (_devicesDirty) RefreshDevicesInBackground();
        var devices = _devices;
        int? master = null;
        bool masterMuted = false;
        var apps = new List<App>();
        try
        {
            if (!_enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return new Snapshot(devices, null, false, apps);
            string defaultId = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
            devices = devices.Select(d => d with { Default = d.Id == defaultId }).ToList();

            using var current = _enumerator.GetDevice(defaultId);
            master = (int)Math.Round(current.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
            masterMuted = current.AudioEndpointVolume.Mute;

            var sessions = current.AudioSessionManager.Sessions;
            current.AudioSessionManager.RefreshSessions();
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.IsSystemSoundsSession || session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                var (name, path) = Describe(session.GetProcessID, session.DisplayName);
                if (string.IsNullOrWhiteSpace(name)) continue;
                apps.Add(new App(session.GetSessionInstanceIdentifier, name, path,
                    (int)Math.Round(session.SimpleAudioVolume.Volume * 100), session.SimpleAudioVolume.Mute,
                    session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive));
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException)
        {
            Log.Write($"Микшер: {e.Message}");
        }
        // Звучащие сейчас — сверху; одна программа может держать несколько сеансов — оставляем по одному.
        var unique = apps.OrderByDescending(a => a.Active).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .GroupBy(a => a.Icon ?? a.Name).Select(g => g.First()).ToList();
        return new Snapshot(devices, master, masterMuted, unique);
    }

    public void SetAppVolume(string sessionId, int volume, bool? mute)
    {
        WithSession(sessionId, session =>
        {
            session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0, 100) / 100f;
            if (mute is { } m) session.SimpleAudioVolume.Mute = m;
        });
    }

    public void SetMaster(int volume, bool? mute)
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0, 100) / 100f;
            if (mute is { } m) device.AudioEndpointVolume.Mute = m;
        }
        catch (Exception e) when (e is COMException or ArgumentException) { Log.Write($"Микшер: {e.Message}"); }
    }

    /// <summary>
    /// Делает устройство выводом по умолчанию — как выбор в быстрых настройках Windows.
    /// Публичного способа для этого у Windows нет; используется тот же интерфейс, что у EarTrumpet и SoundSwitch.
    /// </summary>
    public void SetDefaultDevice(string deviceId)
    {
        try
        {
            var config = (IPolicyConfig)new PolicyConfigClient();
            foreach (var role in new[] { 0, 1, 2 }) config.SetDefaultEndpoint(deviceId, role); // консоль, мультимедиа, связь
            Marshal.ReleaseComObject(config);
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { Log.Write($"Устройство вывода не переключено: {e.Message}"); }
    }

    public void Dispose()
    {
        try { _enumerator.UnregisterEndpointNotificationCallback(_watcher); } catch (COMException) { }
        _enumerator.Dispose();
    }

    private void RefreshDevicesInBackground()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        _devicesDirty = false;
        Task.Run(() =>
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var list = new List<Device>();
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                    using (device) list.Add(new Device(device.ID, device.FriendlyName, false));
                _devices = list.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
            catch (Exception e) when (e is COMException or InvalidCastException) { Log.Write($"Устройства вывода не прочитаны: {e.Message}"); }
            finally { Interlocked.Exchange(ref _loading, 0); }
            DevicesChanged?.Invoke();
        });
    }

    /// <summary>Сообщения Windows об изменениях устройств — приходят из чужого потока.</summary>
    private sealed class DeviceWatcher(Action changed) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed();
        public void OnDeviceAdded(string pwstrDeviceId) => changed();
        public void OnDeviceRemoved(string deviceId) => changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => changed();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    private void WithSession(string sessionId, Action<AudioSessionControl> action)
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.GetSessionInstanceIdentifier != sessionId) continue;
                // У программы бывает несколько сеансов: громкость меняется у всех сеансов того же процесса.
                uint pid = session.GetProcessID;
                for (int j = 0; j < sessions.Count; j++)
                    if (sessions[j].GetProcessID == pid) action(sessions[j]);
                return;
            }
        }
        catch (Exception e) when (e is COMException or ArgumentException) { Log.Write($"Микшер: {e.Message}"); }
    }

    private (string Name, string? Path) Describe(uint pid, string displayName)
    {
        if (_processes.TryGetValue(pid, out var known)) return known;
        string name = displayName;
        string? path = null;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            path = process.MainModule?.FileName;
            if (path is not null)
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith('@')) name = !string.IsNullOrWhiteSpace(description) ? description : System.IO.Path.GetFileNameWithoutExtension(path);
            }
            else if (string.IsNullOrWhiteSpace(name)) name = process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { }
        if (name.StartsWith('@')) name = "";
        return _processes[pid] = (name, path);
    }

    // ---------- переключение устройства по умолчанию ----------

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class PolicyConfigClient { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // Методы до SetDefaultEndpoint не используются, но должны стоять на своих местах.
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility();
    }
}
