using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using FMOD;
using FMODUnity;
using MetaVoiceChat;
using MetaVoiceChat.Output.OnAudioFilterReadVcOutput;
using MetaVoiceChat.Utils;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace BuburitoGames.MetaVcFMOD
{
    /// <summary>
    /// Records the microphone through FMOD Core instead of UnityEngine.Microphone.
    /// Emits mono 48 kHz frames of samplesPerFrame samples, the same contract as VcMic.
    /// </summary>
    public class VcFmodMic : IDisposable
    {
        const int RingSeconds = VcConfig.ClipLoopSeconds;
        const int BytesPerSample = sizeof(float);
        const int DriverNameLength = 256;

        readonly MonoBehaviour coroutineProvider;
        readonly int samplesPerFrame;

        public bool IsRecording { get; private set; }
        public string[] Devices => GetConnectedDevices();
        public string SelectedDevice { get; private set; }
        public string ActiveDevice { get; private set; }

        int nextFrameIndex;
        Coroutine recordCoroutine;

        int driverId;
        int driverRate;
        int driverChannels;
        Sound sound;
        int ringSamples;
        int inputSamplesPerFrame;

        readonly OneWayResampler resampler = new();
        float[] interleaved;
        float[] mono;
        float[] resampled;
        float[] pending;
        int pendingCount;
        readonly float[] frame;

        public event Action<int, float[]> OnFrameReady;
        public event Action<string> OnActiveDeviceChanged;

        public VcFmodMic(MonoBehaviour coroutineProvider, int samplesPerFrame)
        {
            this.coroutineProvider = coroutineProvider;
            this.samplesPerFrame = samplesPerFrame;
            frame = new float[samplesPerFrame];
        }

        static FMOD.System CoreSystem => RuntimeManager.CoreSystem;

        /// <summary>
        /// Returns the names of the connected record drivers, in FMOD driver order.
        /// </summary>
        public static string[] GetConnectedDevices()
        {
            CoreSystem.getRecordNumDrivers(out int numDrivers, out _);
            var names = new List<string>(numDrivers);
            for (int id = 0; id < numDrivers; id++)
            {
                CoreSystem.getRecordDriverInfo(id, out string name, DriverNameLength, out _, out _, out _, out _, out DRIVER_STATE state);
                if ((state & DRIVER_STATE.CONNECTED) != 0)
                    names.Add(name);
            }
            return names.ToArray();
        }

        static bool TryGetDriver(string device, out int id, out int rate, out int channels)
        {
            CoreSystem.getRecordNumDrivers(out int numDrivers, out _);
            for (id = 0; id < numDrivers; id++)
            {
                CoreSystem.getRecordDriverInfo(id, out string name, DriverNameLength, out _, out rate, out _, out channels, out DRIVER_STATE state);
                if (name == device && (state & DRIVER_STATE.CONNECTED) != 0)
                    return true;
            }

            rate = 0;
            channels = 0;
            return false;
        }

        public void SetSelectedDevice(string device)
        {
            if (device == SelectedDevice) return;

            SelectedDevice = device;
            if (IsRecording)
                StartRecording();
        }

        public bool StartRecording()
        {
            StopRecording();

            string[] devices = Devices;
            if (devices.Length == 0)
            {
                Debug.LogWarning("No microphone detected for voice chat!");
                return false;
            }

            ActiveDevice = devices.Contains(SelectedDevice) ? SelectedDevice : devices[0];
            OnActiveDeviceChanged?.Invoke(ActiveDevice);

            if (!TryGetDriver(ActiveDevice, out driverId, out driverRate, out driverChannels))
            {
                Debug.LogWarning("Microphone failed to start recording for voice chat!");
                StopRecording();
                return false;
            }

            ringSamples = driverRate * RingSeconds;
            inputSamplesPerFrame = samplesPerFrame * driverRate / VcConfig.SamplesPerSecond;

            CREATESOUNDEXINFO exinfo = new()
            {
                cbsize = Marshal.SizeOf(typeof(CREATESOUNDEXINFO)),
                numchannels = driverChannels,
                defaultfrequency = driverRate,
                format = SOUND_FORMAT.PCMFLOAT,
                length = (uint)(ringSamples * driverChannels * BytesPerSample),
            };
            CoreSystem.createSound("", MODE.OPENUSER | MODE.LOOP_NORMAL, ref exinfo, out sound);

            if (CoreSystem.recordStart(driverId, sound, true) != RESULT.OK)
            {
                Debug.LogWarning("Microphone failed to start recording for voice chat!");
                StopRecording();
                return false;
            }

            interleaved = new float[inputSamplesPerFrame * driverChannels];
            mono = new float[inputSamplesPerFrame];
            if (driverRate != VcConfig.SamplesPerSecond)
            {
                resampler.Configure(1, driverRate, VcConfig.SamplesPerSecond, OnAudioFilterReadVcOutput.DefaultResamplerQuality);
                resampler.ResetMem();
                // One input frame resamples to about samplesPerFrame samples. Two frames of room covers rounding.
                resampled = new float[samplesPerFrame * 2];
                pending = new float[samplesPerFrame * 3];
                pendingCount = 0;
            }

            recordCoroutine = coroutineProvider.StartCoroutine(CoRecord());
            IsRecording = true;
            return true;
        }

        public void StopRecording()
        {
            if (recordCoroutine != null)
            {
                coroutineProvider.StopCoroutine(recordCoroutine);
                recordCoroutine = null;
            }

            IsRecording = false;

            if (sound.hasHandle())
            {
                CoreSystem.isRecording(driverId, out bool recording);
                if (recording)
                    CoreSystem.recordStop(driverId);
                sound.release();
                sound.clearHandle();
            }

            if (ActiveDevice != null)
            {
                ActiveDevice = null;
                OnActiveDeviceChanged?.Invoke(ActiveDevice);
            }
        }

        IEnumerator CoRecord()
        {
            int wraps = 0;
            int prevPos = 0;
            long readAbsPos = 0;

            while (sound.hasHandle() && IsDriverRecording())
            {
                CoreSystem.getRecordPosition(driverId, out uint pos);
                if (pos < prevPos) wraps++;
                prevPos = (int)pos;
                long currAbsPos = (long)wraps * ringSamples + pos;

                // The writer got more than a full ring ahead, for example across an editor pause.
                // Skip to one frame behind the write cursor.
                if (currAbsPos - readAbsPos > ringSamples)
                {
                    readAbsPos = currAbsPos - inputSamplesPerFrame;
                    resampler.ResetMem();
                    pendingCount = 0;
                }

                while (readAbsPos + inputSamplesPerFrame < currAbsPos)
                {
                    ReadRing((int)(readAbsPos % ringSamples));
                    EmitFrames();
                    readAbsPos += inputSamplesPerFrame;
                }

                yield return null;
            }

            StopRecording();
        }

        bool IsDriverRecording()
        {
            CoreSystem.isRecording(driverId, out bool recording);
            return recording;
        }

        // Copies one input frame out of the FMOD ring into mono. The lock returns two spans when the
        // range wraps the ring end.
        void ReadRing(int offsetSamples)
        {
            uint offsetBytes = (uint)(offsetSamples * driverChannels * BytesPerSample);
            uint lengthBytes = (uint)(interleaved.Length * BytesPerSample);
            sound.@lock(offsetBytes, lengthBytes, out IntPtr ptr1, out IntPtr ptr2, out uint len1, out uint len2);
            int count1 = (int)(len1 / BytesPerSample);
            Marshal.Copy(ptr1, interleaved, 0, count1);
            if (len2 > 0)
                Marshal.Copy(ptr2, interleaved, count1, (int)(len2 / BytesPerSample));
            sound.unlock(ptr1, ptr2, len1, len2);

            for (int i = 0; i < mono.Length; i++)
            {
                float sum = 0f;
                for (int c = 0; c < driverChannels; c++)
                    sum += interleaved[i * driverChannels + c];
                mono[i] = sum / driverChannels;
            }
        }

        void EmitFrames()
        {
            if (driverRate == VcConfig.SamplesPerSecond)
            {
                Array.Copy(mono, frame, samplesPerFrame);
                OnFrameReady?.Invoke(nextFrameIndex++, frame);
                return;
            }

            int inLen = mono.Length;
            int outLen = resampled.Length;
            resampler.ProcessInterleaved(mono, ref inLen, resampled, ref outLen);
            Array.Copy(resampled, 0, pending, pendingCount, outLen);
            pendingCount += outLen;

            while (pendingCount >= samplesPerFrame)
            {
                Array.Copy(pending, frame, samplesPerFrame);
                OnFrameReady?.Invoke(nextFrameIndex++, frame);
                pendingCount -= samplesPerFrame;
                Array.Copy(pending, samplesPerFrame, pending, 0, pendingCount);
            }
        }

        public void Dispose()
        {
            StopRecording();
            resampler.Free();
        }
    }
}
