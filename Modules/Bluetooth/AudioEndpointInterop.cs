using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WindowsNotch.Modules.Bluetooth
{
    public sealed record AudioOutputDeviceInfo(
        string Id,
        string Name,
        bool IsDefault);

    /// <summary>
    /// Schlanke CoreAudio-COM-Interop-Schicht zum Auflisten, Umschalten und Regeln
    /// von Windows-Audio-Ausgabegeräten ohne externe Abhängigkeiten.
    /// </summary>
    internal static class AudioEndpointInterop
    {
        private const int DEVICE_STATE_ACTIVE = 0x00000001;
        private const int STGM_READ = 0x00000000;

        private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
        private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
        private static readonly Guid CLSID_PolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

        private static readonly PropertyKey PKEY_Device_FriendlyName = new(
            new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

        public static List<AudioOutputDeviceInfo> GetActiveOutputDevices()
        {
            var result = new List<AudioOutputDeviceInfo>();
            IMMDeviceEnumerator? enumerator = null;
            IMMDeviceCollection? collection = null;
            IMMDevice? defaultDevice = null;

            try
            {
                enumerator = CreateEnumerator();
                if (enumerator == null) return result;

                string? defaultId = null;
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out defaultDevice) == 0 &&
                    defaultDevice != null)
                {
                    defaultDevice.GetId(out defaultId);
                }

                if (enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out collection) != 0 ||
                    collection == null)
                {
                    return result;
                }

                collection.GetCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice? dev = null;
                    IPropertyStore? store = null;
                    try
                    {
                        if (collection.Item(i, out dev) != 0 || dev == null) continue;
                        dev.GetId(out string id);

                        string name = "Audio-Ausgabe";
                        if (dev.OpenPropertyStore(STGM_READ, out store) == 0 && store != null)
                        {
                            var key = PKEY_Device_FriendlyName;
                            if (store.GetValue(ref key, out var prop) == 0)
                            {
                                var str = prop.GetStringValue();
                                if (!string.IsNullOrWhiteSpace(str))
                                    name = str;
                                PropVariantClear(ref prop);
                            }
                        }

                        bool isDef = !string.IsNullOrEmpty(defaultId) &&
                                     string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                        result.Add(new AudioOutputDeviceInfo(id, name, isDef));
                    }
                    finally
                    {
                        if (store != null) Marshal.ReleaseComObject(store);
                        if (dev != null) Marshal.ReleaseComObject(dev);
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                if (defaultDevice != null) Marshal.ReleaseComObject(defaultDevice);
                if (collection != null) Marshal.ReleaseComObject(collection);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }

            return result;
        }

        public static bool SetDefaultOutputDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;

            object? policyObj = null;
            try
            {
                var type = Type.GetTypeFromCLSID(CLSID_PolicyConfigClient);
                if (type == null) return false;

                policyObj = Activator.CreateInstance(type);
                if (policyObj is IPolicyConfig policy)
                {
                    int hr1 = policy.SetDefaultEndpoint(deviceId, ERole.eConsole);
                    int hr2 = policy.SetDefaultEndpoint(deviceId, ERole.eMultimedia);
                    int hr3 = policy.SetDefaultEndpoint(deviceId, ERole.eCommunications);
                    return hr1 == 0 || hr2 == 0 || hr3 == 0;
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                if (policyObj != null) Marshal.ReleaseComObject(policyObj);
            }

            return false;
        }

        public static (double Volume, bool Muted) GetMasterVolume()
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? defaultDevice = null;
            IAudioEndpointVolume? endpointVolume = null;

            try
            {
                enumerator = CreateEnumerator();
                if (enumerator == null) return (0.5, false);

                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out defaultDevice) != 0 ||
                    defaultDevice == null)
                {
                    return (0.5, false);
                }

                var iid = IID_IAudioEndpointVolume;
                if (defaultDevice.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out object volObj) != 0 ||
                    volObj is not IAudioEndpointVolume vol)
                {
                    return (0.5, false);
                }

                endpointVolume = vol;
                endpointVolume.GetMasterVolumeLevelScalar(out float level);
                endpointVolume.GetMute(out bool muted);
                return (Math.Clamp(level, 0f, 1f), muted);
            }
            catch
            {
                return (0.5, false);
            }
            finally
            {
                if (endpointVolume != null) Marshal.ReleaseComObject(endpointVolume);
                if (defaultDevice != null) Marshal.ReleaseComObject(defaultDevice);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        public static void SetMasterVolume(double scalar)
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? defaultDevice = null;
            IAudioEndpointVolume? endpointVolume = null;

            try
            {
                enumerator = CreateEnumerator();
                if (enumerator == null) return;

                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out defaultDevice) != 0 ||
                    defaultDevice == null)
                    return;

                var iid = IID_IAudioEndpointVolume;
                if (defaultDevice.Activate(ref iid, 0x17, IntPtr.Zero, out object volObj) == 0 &&
                    volObj is IAudioEndpointVolume vol)
                {
                    endpointVolume = vol;
                    var ctx = Guid.Empty;
                    endpointVolume.SetMasterVolumeLevelScalar((float)Math.Clamp(scalar, 0.0, 1.0), ref ctx);
                }
            }
            catch
            {
                // Ignorieren, falls Gerät gerade getrennt wird
            }
            finally
            {
                if (endpointVolume != null) Marshal.ReleaseComObject(endpointVolume);
                if (defaultDevice != null) Marshal.ReleaseComObject(defaultDevice);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        public static void SetMasterMute(bool muted)
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? defaultDevice = null;
            IAudioEndpointVolume? endpointVolume = null;

            try
            {
                enumerator = CreateEnumerator();
                if (enumerator == null) return;

                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eConsole, out defaultDevice) != 0 ||
                    defaultDevice == null)
                    return;

                var iid = IID_IAudioEndpointVolume;
                if (defaultDevice.Activate(ref iid, 0x17, IntPtr.Zero, out object volObj) == 0 &&
                    volObj is IAudioEndpointVolume vol)
                {
                    endpointVolume = vol;
                    var ctx = Guid.Empty;
                    endpointVolume.SetMute(muted, ref ctx);
                }
            }
            catch
            {
                // Ignorieren
            }
            finally
            {
                if (endpointVolume != null) Marshal.ReleaseComObject(endpointVolume);
                if (defaultDevice != null) Marshal.ReleaseComObject(defaultDevice);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        private static IMMDeviceEnumerator? CreateEnumerator()
        {
            var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
            if (type == null) return null;
            return Activator.CreateInstance(type) as IMMDeviceEnumerator;
        }

        private enum EDataFlow
        {
            eRender = 0,
            eCapture = 1,
            eAll = 2
        }

        private enum ERole
        {
            eConsole = 0,
            eMultimedia = 1,
            eCommunications = 2
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public uint pid;

            public PropertyKey(Guid fmtid, uint pid)
            {
                this.fmtid = fmtid;
                this.pid = pid;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort vt;
            public ushort wReserved1;
            public ushort wReserved2;
            public ushort wReserved3;
            public IntPtr p;
            public IntPtr p2;

            public string? GetStringValue()
            {
                // VT_LPWSTR = 31
                if (vt == 31 && p != IntPtr.Zero)
                    return Marshal.PtrToStringUni(p);
                return null;
            }
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
            int RegisterEndpointNotificationCallback(IntPtr pClient);
            int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport]
        [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            int GetCount(out uint pcDevices);
            int Item(uint nDevice, out IMMDevice ppDevice);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            int GetState(out int pdwState);
        }

        [ComImport]
        [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PropertyKey pkey);
            int GetValue(ref PropertyKey key, out PropVariant pv);
            int SetValue(ref PropertyKey key, ref PropVariant propvar);
            int Commit();
        }

        [ComImport]
        [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume
        {
            int RegisterControlChangeNotify(IntPtr pNotify);
            int UnregisterControlChangeNotify(IntPtr pNotify);
            int GetChannelCount(out uint pnChannelCount);
            int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
            int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
            int GetMasterVolumeLevel(out float pfLevelDB);
            int GetMasterVolumeLevelScalar(out float pfLevel);
            int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
            int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
            int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
            int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }

        [ComImport]
        [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            int GetMixFormat(string pszDeviceName, IntPtr ppFormat);
            int GetDeviceFormat(string pszDeviceName, bool bDefault, IntPtr ppFormat);
            int ResetDeviceFormat(string pszDeviceName);
            int SetDeviceFormat(string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
            int GetProcessingPeriod(string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
            int SetProcessingPeriod(string pszDeviceName, IntPtr pmftPeriod);
            int GetShareMode(string pszDeviceName, IntPtr pMode);
            int SetShareMode(string pszDeviceName, IntPtr mode);
            int GetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
            int SetPropertyValue(string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
            int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
            int SetEndpointVisibility(string pszDeviceName, bool bVisible);
        }
    }
}
