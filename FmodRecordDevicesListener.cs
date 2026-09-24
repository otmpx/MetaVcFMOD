using System;
using System.Collections.Generic;

namespace BuburitoGames.MetaVcFMOD
{
    /// <summary>
    /// Polls the FMOD record driver list and raises a callback when it changes.
    /// A sibling of MicrophoneDevicesListener that does not touch UnityEngine.Microphone.
    /// </summary>
    public class FmodRecordDevicesListener
    {
        readonly HashSet<string> devices = new();
        readonly Action onDevicesChanged;

        public FmodRecordDevicesListener(Action onDevicesChanged)
        {
            this.onDevicesChanged = onDevicesChanged;
        }

        public void Poll()
        {
            string[] actual = VcFmodMic.GetConnectedDevices();
            if (!HasChanged(actual)) return;

            devices.Clear();
            foreach (string device in actual)
                devices.Add(device);

            onDevicesChanged?.Invoke();
        }

        bool HasChanged(string[] actual)
        {
            if (actual.Length != devices.Count) return true;

            foreach (string device in actual)
            {
                if (!devices.Contains(device)) return true;
            }
            return false;
        }
    }
}
