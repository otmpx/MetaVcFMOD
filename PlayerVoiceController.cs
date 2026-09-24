using System;
using System.Collections.Generic;
using MetaVoiceChat;
using UnityEngine;

namespace BuburitoGames.MetaVcFMOD
{
    /// <summary>
    /// Bridges the MetaVoiceChat components of one player to the game UI.
    /// Place it on the GameObject that holds MetaVc. The owner calls Initialize from its network start hook.
    /// </summary>
    public class PlayerVoiceController : MonoBehaviour
    {
        public static PlayerVoiceController LocalInstance { get; private set; }
        static readonly List<PlayerVoiceController> _instances = new();
        public static IReadOnlyList<PlayerVoiceController> Instances => _instances;
        public static event Action OnInstancesChanged;

        public const float MaxVolume = 2f;

        [SerializeField] MetaVc vc;
        [SerializeField] VcFmodMicAudioInput audioInput;
        [SerializeField] GainVcInputFilter gainFilter;
        [SerializeField] VcFmodOutput audioOutput;

        public bool IsLocalPlayer { get; private set; }

        /// <summary>
        /// The key that saves this player's output volume across sessions. Empty disables the save.
        /// </summary>
        public string PersistenceKey { get; private set; }

        float outputVolume = 1f;
        FmodRecordDevicesListener devicesListener;

        const string InputVolumeKey = "VoiceChat_InputVolume";
        const string OutputVolumeKey = "VoiceChat_OutputVolume";
        const string InputDeviceKey = "VoiceChat_InputDevice";
        const string InputMutedKey = "VoiceChat_InputMuted";
        const string DeafenedKey = "VoiceChat_Deafened";

        void OnDisable()
        {
            if (LocalInstance == this)
                LocalInstance = null;

            _instances.Remove(this);
            OnInstancesChanged?.Invoke();
        }

        /// <summary>
        /// Registers this voice as live. Only a voice that will play or record must call this.
        /// </summary>
        public void Initialize(bool isLocal, string persistenceKey)
        {
            IsLocalPlayer = isLocal;
            PersistenceKey = persistenceKey;
            _instances.Add(this);

            if (isLocal)
            {
                LocalInstance = this;
                devicesListener = new FmodRecordDevicesListener(ApplySavedInputDevice);
                LoadInputSettings();
            }

            if (!string.IsNullOrEmpty(PersistenceKey))
            {
                outputVolume = PlayerPrefs.GetFloat(OutputVolumePrefKey, 1f);
                ApplyOutputVolume();
            }

            OnInstancesChanged?.Invoke();
        }

        void Update()
        {
            if (IsLocalPlayer)
                devicesListener.Poll();
        }

        public void SetInputVolume(float volume)
        {
            gainFilter.Gain = Mathf.Clamp(volume, 0f, MaxVolume);
            PlayerPrefs.SetFloat(InputVolumeKey, gainFilter.Gain);
        }

        public float GetInputVolume() => gainFilter.Gain;

        public void SetInputMuted(bool muted)
        {
            vc.isInputMuted.Value = muted;
            PlayerPrefs.SetInt(InputMutedKey, muted ? 1 : 0);
        }

        public bool GetInputMuted() => vc.isInputMuted.Value;

        public void SetDeafened(bool deafened)
        {
            vc.isDeafened.Value = deafened;
            PlayerPrefs.SetInt(DeafenedKey, deafened ? 1 : 0);
        }

        public bool GetDeafened() => vc.isDeafened.Value;

        /// <summary>
        /// Sets how loud the local player hears this voice. Range 0 to MaxVolume.
        /// </summary>
        public void SetOutputVolume(float volume)
        {
            outputVolume = Mathf.Clamp(volume, 0f, MaxVolume);
            ApplyOutputVolume();

            if (!string.IsNullOrEmpty(PersistenceKey))
                PlayerPrefs.SetFloat(OutputVolumePrefKey, outputVolume);
        }

        public float GetOutputVolume() => outputVolume;

        void ApplyOutputVolume() => audioOutput.SetVolume(outputVolume / MaxVolume);

        public void SetOutputMuted(bool muted) => vc.isOutputMuted.Value = muted;

        public bool GetOutputMuted() => vc.isOutputMuted.Value;

        public void SetInputDevice(string device)
        {
            if (!audioInput.IsInitialized) return;

            audioInput.SetSelectedDevice(device);
            PlayerPrefs.SetString(InputDeviceKey, device);
        }

        public string GetActiveInputDevice() => audioInput.ActiveDevice;

        void LoadInputSettings()
        {
            gainFilter.Gain = PlayerPrefs.GetFloat(InputVolumeKey, 1f);
            vc.isInputMuted.Value = PlayerPrefs.GetInt(InputMutedKey, 0) == 1;
            vc.isDeafened.Value = PlayerPrefs.GetInt(DeafenedKey, 0) == 1;
            ApplySavedInputDevice();
        }

        // The mic can initialize after Initialize runs, so the device is re-applied on each device change.
        void ApplySavedInputDevice()
        {
            string savedDevice = PlayerPrefs.GetString(InputDeviceKey, "");
            if (!string.IsNullOrEmpty(savedDevice) && audioInput.IsInitialized)
                audioInput.SetSelectedDevice(savedDevice);
        }

        string OutputVolumePrefKey => $"{OutputVolumeKey}_{PersistenceKey}";
    }
}
