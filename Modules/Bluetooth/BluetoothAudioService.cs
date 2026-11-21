using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using WindowsNotch.Services;

namespace WindowsNotch.Modules.Bluetooth
{
    public sealed record BluetoothDeviceItem(
        string Id,
        string Name,
        bool IsConnected,
        bool IsBle,
        int? BatteryPercent,
        string CategoryHint);

    /// <summary>
    /// Ermittelt verbundene/gekoppelte Bluetooth-Geräte inkl. gemeldetem Akku-Stand
    /// sowie aktive Windows-Audio-Ausgabegeräte und ermöglicht direktes Umschalten.
    /// </summary>
    public sealed class BluetoothAudioService
    {
        private const string BatteryPropKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        private const string ContainerIdKey = "System.Devices.Aep.ContainerId";
        private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
        private const string CategoryKey = "System.Devices.Aep.Category";

        private readonly SemaphoreSlim _refreshGate = new(1, 1);

        public IReadOnlyList<BluetoothDeviceItem> BluetoothDevices { get; private set; } = Array.Empty<BluetoothDeviceItem>();
        public IReadOnlyList<AudioOutputDeviceInfo> AudioOutputs { get; private set; } = Array.Empty<AudioOutputDeviceInfo>();
        public double MasterVolume { get; private set; } = 0.5;
        public bool IsMuted { get; private set; }
        public bool IsRefreshing { get; private set; }

        public event Action? Updated;

        public async Task RefreshAsync()
        {
            if (!await _refreshGate.WaitAsync(0))
                return;

            IsRefreshing = true;
            try
            {
                var audioTask = Task.Run(() =>
                {
                    var outputs = AudioEndpointInterop.GetActiveOutputDevices();
                    var (vol, muted) = AudioEndpointInterop.GetMasterVolume();
                    return (outputs, vol, muted);
                });

                var btTask = QueryBluetoothDevicesAsync();

                await Task.WhenAll(audioTask, btTask);

                var audio = await audioTask;
                AudioOutputs = audio.outputs;
                MasterVolume = audio.vol;
                IsMuted = audio.muted;
                BluetoothDevices = await btTask;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                IsRefreshing = false;
                _refreshGate.Release();
                Updated?.Invoke();
            }
        }

        public bool SwitchAudioOutput(string deviceId)
        {
            bool ok = AudioEndpointInterop.SetDefaultOutputDevice(deviceId);
            if (ok)
            {
                AudioOutputs = AudioEndpointInterop.GetActiveOutputDevices();
                var (vol, muted) = AudioEndpointInterop.GetMasterVolume();
                MasterVolume = vol;
                IsMuted = muted;
                Updated?.Invoke();
            }
            return ok;
        }

        public void SetVolume(double scalar)
        {
            MasterVolume = Math.Clamp(scalar, 0.0, 1.0);
            AudioEndpointInterop.SetMasterVolume(MasterVolume);
        }

        public void ToggleMute()
        {
            IsMuted = !IsMuted;
            AudioEndpointInterop.SetMasterMute(IsMuted);
            Updated?.Invoke();
        }

        public int? FindBatteryForAudioDevice(string audioDeviceName)
        {
            if (string.IsNullOrWhiteSpace(audioDeviceName)) return null;

            foreach (var bt in BluetoothDevices)
            {
                if (!bt.IsConnected || bt.BatteryPercent == null) continue;
                if (audioDeviceName.Contains(bt.Name, StringComparison.OrdinalIgnoreCase) ||
                    bt.Name.Contains(audioDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return bt.BatteryPercent;
                }
            }
            return null;
        }

        private static async Task<List<BluetoothDeviceItem>> QueryBluetoothDevicesAsync()
        {
            var results = new Dictionary<string, BluetoothDeviceItem>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // 1. Akku-Stände aus PnP-Geräteknoten (Windows 10/11 meldet hier Bluetooth-Batterieprozente)
                var batteryByContainer = new Dictionary<Guid, int>();
                var batteryByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                try
                {
                    var pnpProps = new[] { BatteryPropKey, "System.Devices.ContainerId", "System.ItemNameDisplay" };
                    var pnpDevices = await DeviceInformation.FindAllAsync(
                        string.Empty,
                        pnpProps,
                        DeviceInformationKind.Device);

                    foreach (var dev in pnpDevices)
                    {
                        if (!TryReadBattery(dev.Properties, out int pct)) continue;

                        if (dev.Properties.TryGetValue("System.Devices.ContainerId", out var cidObj) &&
                            cidObj is Guid cid && cid != Guid.Empty)
                        {
                            batteryByContainer[cid] = pct;
                        }

                        if (!string.IsNullOrWhiteSpace(dev.Name))
                            batteryByName[dev.Name.Trim()] = pct;
                    }
                }
                catch
                {
                    // PnP-Abfrage optional; manche Systeme sperren einzelne Knoten
                }

                // 2. Klassische Bluetooth-Geräte + BLE-Geräte abfragen
                var aepProps = new[] { BatteryPropKey, ContainerIdKey, IsConnectedKey, CategoryKey };

                await CollectSelectorAsync(
                    BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                    isBle: false,
                    aepProps,
                    batteryByContainer,
                    batteryByName,
                    results);

                await CollectSelectorAsync(
                    BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                    isBle: true,
                    aepProps,
                    batteryByContainer,
                    batteryByName,
                    results);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }

            return results.Values
                .OrderByDescending(d => d.IsConnected)
                .ThenByDescending(d => d.BatteryPercent.HasValue)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static async Task CollectSelectorAsync(
            string selector,
            bool isBle,
            string[] props,
            Dictionary<Guid, int> batteryByContainer,
            Dictionary<string, int> batteryByName,
            Dictionary<string, BluetoothDeviceItem> target)
        {
            try
            {
                var found = await DeviceInformation.FindAllAsync(selector, props);
                foreach (var dev in found)
                {
                    string name = string.IsNullOrWhiteSpace(dev.Name) ? L.T("bt.default_device") : dev.Name.Trim();
                    bool isConnected = dev.Properties.TryGetValue(IsConnectedKey, out var connObj) &&
                                       connObj is bool b && b;

                    int? battery = null;
                    if (TryReadBattery(dev.Properties, out int directPct))
                    {
                        battery = directPct;
                    }
                    else if (dev.Properties.TryGetValue(ContainerIdKey, out var cidObj) &&
                             cidObj is Guid cid &&
                             batteryByContainer.TryGetValue(cid, out int contPct))
                    {
                        battery = contPct;
                    }
                    else if (batteryByName.TryGetValue(name, out int namePct))
                    {
                        battery = namePct;
                    }

                    string category = string.Empty;
                    if (dev.Properties.TryGetValue(CategoryKey, out var catObj) && catObj is string[] cats && cats.Length > 0)
                        category = string.Join(",", cats);

                    // Duplikate (Classic + BLE mit gleichem Namen) sinnvoll zusammenführen
                    string key = name;
                    if (target.TryGetValue(key, out var existing))
                    {
                        target[key] = existing with
                        {
                            IsConnected = existing.IsConnected || isConnected,
                            BatteryPercent = existing.BatteryPercent ?? battery,
                            CategoryHint = string.IsNullOrEmpty(existing.CategoryHint) ? category : existing.CategoryHint
                        };
                    }
                    else
                    {
                        target[key] = new BluetoothDeviceItem(
                            dev.Id,
                            name,
                            isConnected,
                            isBle,
                            battery,
                            category);
                    }
                }
            }
            catch
            {
                // Ignorieren, falls Bluetooth-Adapter deaktiviert ist
            }
        }

        private static bool TryReadBattery(IReadOnlyDictionary<string, object> props, out int percent)
        {
            percent = 0;
            if (!props.TryGetValue(BatteryPropKey, out var val) || val == null)
                return false;

            try
            {
                int raw = Convert.ToInt32(val);
                if (raw is >= 0 and <= 100)
                {
                    percent = raw;
                    return true;
                }
            }
            catch
            {
                // Ignorieren
            }
            return false;
        }
    }
}
