using System;
using System.Collections;
using System.Linq;
using MetaVoiceChat.Input;
using UnityEngine;

namespace BuburitoGames.MetaVcFMOD
{
    /// <summary>
    /// The VcAudioInput that records through FMOD. A sibling of VcMicAudioInput with the same surface.
    /// </summary>
    public class VcFmodMicAudioInput : VcAudioInput
    {
        const float ReconnectStartDelay = 1f;
        const float ReconnectRetryDelay = 4f;
        const float NoDeviceRetryDelay = 1f;

        public event Action<string> OnActiveDeviceChanged;

        public string ActiveDevice => Mic?.ActiveDevice;
        public VcFmodMic Mic { get; private set; }
        public bool IsInitialized => Mic != null;

        public override void StartLocalPlayer()
        {
            Mic = new VcFmodMic(this, metaVc.config.samplesPerFrame);
            Mic.OnFrameReady += SendAndFilterFrame;
            Mic.OnActiveDeviceChanged += RaiseActiveDeviceChanged;

            if (Mic.Devices.Length > 0)
                Mic.StartRecording();

            StartCoroutine(CoReconnect());
        }

        void OnDestroy()
        {
            if (Mic == null) return;

            Mic.OnFrameReady -= SendAndFilterFrame;
            Mic.OnActiveDeviceChanged -= RaiseActiveDeviceChanged;
            Mic.Dispose();
            Mic = null;
            StopAllCoroutines();
        }

        // Mic becomes null in OnDestroy while this coroutine yields, so each resume re-checks it.
        IEnumerator CoReconnect()
        {
            yield return new WaitForSecondsRealtime(ReconnectStartDelay);

            while (Mic != null)
            {
                while (!ShouldReconnect())
                    yield return null;

                if (Mic == null) yield break;

                Mic.StopRecording();
                yield return null;
                yield return null;

                if (Mic == null) yield break;

                if (Mic.Devices.Length > 0)
                {
                    if (!Mic.StartRecording())
                        yield return new WaitForSecondsRealtime(ReconnectRetryDelay);
                }
                else
                {
                    yield return new WaitForSecondsRealtime(NoDeviceRetryDelay);
                }

                yield return null;
                yield return null;
            }
        }

        void RaiseActiveDeviceChanged(string device) => OnActiveDeviceChanged?.Invoke(device);

        public void SetSelectedDevice(string device) => Mic?.SetSelectedDevice(device);

        bool ShouldReconnect()
        {
            if (Mic == null) return true;

            string[] devices = Mic.Devices;
            if (!Mic.IsRecording || !devices.Contains(Mic.ActiveDevice)) return true;

            return Mic.SelectedDevice != Mic.ActiveDevice && devices.Contains(Mic.SelectedDevice);
        }
    }
}
