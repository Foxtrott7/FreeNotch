using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsNotch.Modules.QuickActions
{
    internal sealed class AudioAppSession
    {
        public uint ProcessId { get; init; }
        public string Name { get; init; } = string.Empty;
        public float Volume { get; set; }
        public bool IsMuted { get; set; }
        public bool IsActive { get; init; }
        public bool IsSystemSounds { get; init; }
        internal string SessionIdentifier { get; init; } = string.Empty;
    }

    /// <summary>
    /// Steuert laufende Windows-Audio-Sessions per WASAPI (IAudioSessionManager2 / ISimpleAudioVolume).
    /// </summary>
    internal static class AudioSessionMixerService
    {
        private const int ClsctxAll = 23;

        public static List<AudioAppSession> GetSessions()
        {
            var result = new List<AudioAppSession>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioSessionManager2? mgr = null;
            IAudioSessionEnumerator? sessionEnum = null;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device));
                var iidMgr = typeof(IAudioSessionManager2).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iidMgr, ClsctxAll, IntPtr.Zero, out var mgrObj));
                mgr = (IAudioSessionManager2)mgrObj;
                Marshal.ThrowExceptionForHR(mgr.GetSessionEnumerator(out sessionEnum));
                Marshal.ThrowExceptionForHR(sessionEnum.GetCount(out int count));

                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl? ctrl = null;
                    try
                    {
                        if (sessionEnum.GetSession(i, out ctrl) < 0 || ctrl == null) continue;
                        if (ctrl.GetState(out int state) < 0 || state == 2 /* Expired */) continue;

                        var ctrl2 = ctrl as IAudioSessionControl2;
                        uint pid = 0;
                        bool isSystem = false;
                        string sessionId = string.Empty;
                        if (ctrl2 != null)
                        {
                            ctrl2.GetProcessId(out pid);
                            isSystem = ctrl2.IsSystemSoundsSession() == 0;
                            ctrl2.GetSessionIdentifier(out sessionId);
                        }

                        if (ctrl is not ISimpleAudioVolume vol) continue;
                        vol.GetMasterVolume(out float level);
                        vol.GetMute(out bool muted);

                        ctrl.GetDisplayName(out string displayName);
                        string name = ResolveFriendlyName(displayName, pid, isSystem);
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        string dedupeKey = pid > 0 ? $"pid:{pid}" : $"sys:{name}";
                        if (!seenKeys.Add(dedupeKey)) continue;

                        result.Add(new AudioAppSession
                        {
                            ProcessId = pid,
                            Name = name,
                            Volume = Math.Clamp(level, 0f, 1f),
                            IsMuted = muted,
                            IsActive = state == 1,
                            IsSystemSounds = isSystem,
                            SessionIdentifier = sessionId ?? string.Empty
                        });
                    }
                    catch { }
                    finally
                    {
                        if (ctrl != null) Marshal.ReleaseComObject(ctrl);
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                if (sessionEnum != null) Marshal.ReleaseComObject(sessionEnum);
                if (mgr != null) Marshal.ReleaseComObject(mgr);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }

            result.Sort((a, b) =>
            {
                int activeCmp = b.IsActive.CompareTo(a.IsActive);
                if (activeCmp != 0) return activeCmp;
                int sysCmp = a.IsSystemSounds.CompareTo(b.IsSystemSounds);
                return sysCmp != 0 ? sysCmp : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            return result;
        }

        public static void SetSessionVolume(uint processId, bool isSystemSounds, float volume)
        {
            ApplyToMatchingSessions(processId, isSystemSounds, vol =>
            {
                var ctx = Guid.Empty;
                vol.SetMasterVolume(Math.Clamp(volume, 0f, 1f), ref ctx);
            });
        }

        public static void SetSessionMute(uint processId, bool isSystemSounds, bool muted)
        {
            ApplyToMatchingSessions(processId, isSystemSounds, vol =>
            {
                var ctx = Guid.Empty;
                vol.SetMute(muted, ref ctx);
            });
        }

        private static void ApplyToMatchingSessions(uint processId, bool isSystemSounds, Action<ISimpleAudioVolume> action)
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioSessionManager2? mgr = null;
            IAudioSessionEnumerator? sessionEnum = null;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) < 0 || device == null) return;
                var iidMgr = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref iidMgr, ClsctxAll, IntPtr.Zero, out var mgrObj) < 0) return;
                mgr = (IAudioSessionManager2)mgrObj;
                if (mgr.GetSessionEnumerator(out sessionEnum) < 0 || sessionEnum == null) return;
                if (sessionEnum.GetCount(out int count) < 0) return;

                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl? ctrl = null;
                    try
                    {
                        if (sessionEnum.GetSession(i, out ctrl) < 0 || ctrl == null) continue;
                        if (ctrl is not IAudioSessionControl2 ctrl2) continue;
                        ctrl2.GetProcessId(out uint pid);
                        bool sys = ctrl2.IsSystemSoundsSession() == 0;

                        bool match = isSystemSounds ? sys : (pid == processId && pid != 0);
                        if (match && ctrl is ISimpleAudioVolume vol)
                            action(vol);
                    }
                    catch { }
                    finally
                    {
                        if (ctrl != null) Marshal.ReleaseComObject(ctrl);
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                if (sessionEnum != null) Marshal.ReleaseComObject(sessionEnum);
                if (mgr != null) Marshal.ReleaseComObject(mgr);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        private static string ResolveFriendlyName(string? displayName, uint pid, bool isSystem)
        {
            if (isSystem)
                return Services.L.T("mixer.system_sounds");

            if (!string.IsNullOrWhiteSpace(displayName) && !displayName.StartsWith("@", StringComparison.Ordinal))
                return displayName;

            if (pid > 0)
            {
                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    string procName = proc.ProcessName;
                    if (!string.IsNullOrWhiteSpace(procName))
                        return char.ToUpper(procName[0]) + procName[1..];
                }
                catch { }
            }

            return string.Empty;
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorCom { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IAudioSessionControl sessionControl);
            int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
            int RegisterSessionNotification(IntPtr sessionNotification);
            int UnregisterSessionNotification(IntPtr sessionNotification);
            int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);
            int UnregisterDuckNotification(IntPtr duckNotification);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int sessionCount);
            int GetSession(int sessionCount, out IAudioSessionControl session);
        }

        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            int GetState(out int state);
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetGroupingParam(out Guid groupingParam);
            int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
            int RegisterAudioSessionNotification(IntPtr newNotifications);
            int UnregisterAudioSessionNotification(IntPtr newNotifications);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl
            int GetState(out int state);
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetGroupingParam(out Guid groupingParam);
            int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
            int RegisterAudioSessionNotification(IntPtr newNotifications);
            int UnregisterAudioSessionNotification(IntPtr newNotifications);
            // IAudioSessionControl2
            int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
            int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
            int GetProcessId(out uint retVal);
            [PreserveSig]
            int IsSystemSoundsSession();
            int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            int SetMasterVolume(float level, ref Guid eventContext);
            int GetMasterVolume(out float level);
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
        }
    }
}
