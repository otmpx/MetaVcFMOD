# Changelog

## 1.0.0 (2026-09-24)

- `VcFmodOutput` plays MetaVoiceChat audio through an FMOD user sound on an FMOD Studio bus.
- Playback uses the NetEQ jitter buffer from MetaVoiceChat v4.3.
- `GainVcInputFilter` scales mic samples.
- `PlayerVoiceController` exposes mic gain, mute, deafen, device selection and per-player output
  volume, and saves them to `PlayerPrefs`.
- `PlayerVolumeEntry` is a uGUI row that binds to one controller.
- `MetaVcFmod.prefab` is a wired voice object without a net provider and without noise
  suppression. The README shows how to add `RnnoiseVcInputFilter`.
