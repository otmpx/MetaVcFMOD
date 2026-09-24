# MetaVcFMOD

FMOD output and player controls for [MetaVoiceChat](https://github.com/Metater/MetaVoiceChat).

MetaVoiceChat plays voice through a Unity `AudioSource`. This package replaces that output with an
FMOD user sound, so voice routes through an FMOD Studio bus like every other sound in an FMOD
project. Playback uses the NetEQ jitter buffer that ships with MetaVoiceChat v4.3.

## Terms

- **Voice object** — the GameObject that holds `MetaVc`, the audio input, the audio output and the
  net provider. One exists per networked player.
- **Net provider** — the MetaVoiceChat component that sends and receives frames for one network
  library, for example `MirrorNetProvider`.
- **Persistence key** — a string that identifies one remote player across sessions. The package
  saves that player's output volume under this key.

## Requirements

| Dependency | Version | Notes |
|---|---|---|
| Unity | 2022.3 or newer | Tested on Unity 6000.0 |
| MetaVoiceChat | v4.3 or newer | Installed in `Assets/Metater/MetaVoiceChat` |
| FMOD for Unity | 2.02 or newer | The `FMODUnity` assembly must be present |
| TextMeshPro and uGUI | Any | Only for the optional UI row |

NetEQ ships as a Windows DLL in MetaVoiceChat v4.3. `VcFmodOutput` runs only on platforms where
`meta_voice_chat_neteq` loads. Use `VcAudioSourceOutput` on other platforms.

## Installation

1. Install MetaVoiceChat v4.3 from its Releases page.
2. Install FMOD for Unity and link your FMOD Studio project.
3. Download the latest `MetaVcFMOD.unitypackage` from the Releases page of this repository and
   import it. The files land in `Assets/Metater/MetaVoiceChat/FMOD`.

The package compiles into the default assembly with MetaVoiceChat. It has no assembly definition.

## Contents

| File | Role |
|---|---|
| `VcFmodOutput.cs` | The `VcAudioOutput` that plays through FMOD. Assign it as the `MetaVc` audio output. |
| `GainVcInputFilter.cs` | A `VcInputFilter` that multiplies mic samples by a gain value. |
| `PlayerVoiceController.cs` | A facade over the voice object. Mic gain, mute, deafen, device selection and per-player output volume. Saves settings to `PlayerPrefs`. |
| `UI/PlayerVolumeEntry.cs` | One UI row with a slider and a mute button. Binds to one `PlayerVoiceController`. |
| `Prefabs/MetaVcFmod.prefab` | A voice object with every component wired, except the net provider. |

All scripts live in the `BuburitoGames.MetaVcFMOD` namespace.

## Setup

### 1. Add the voice object

1. Drag `Prefabs/MetaVcFmod.prefab` under your networked player prefab.
2. Add the net provider for your network library to the voice object. For example, add
   `MirrorNetProvider` for Mirror. See the MetaVoiceChat README for the list of providers.

The prefab ships with `isInputMuted` set to true. Players start muted until they unmute.

The mic input chain on the prefab is `VcMicAudioInput` > `GainVcInputFilter`. Add more input
filters after the gain filter. See [Optional: noise suppression](#optional-noise-suppression).

### 2. Create the FMOD bus

`VcFmodOutput.fmodBusPath` is `bus:/VoiceChat` by default. Create a bus with that path in FMOD
Studio and build banks. Set the field to another path to use a different bus. Leave it empty to
play on the master bus.

### 3. Assign a NetEQ config

`VcFmodOutput.netEqConfig` takes an `OnAudioFilterReadVcConfig` asset. The prefab references the
default asset in `Assets/Metater/MetaVoiceChat/Configs`. Create your own asset from the menu
`Create > MetaVoiceChat > OnAudioFilterReadVcConfig` to tune the jitter buffer.

Only the NetEQ fields of that asset apply. `VcFmodOutput` ignores the resampler fields. FMOD
resamples the 48 kHz stream to the mixer rate.

### 4. Initialize the controller

Call `Initialize` on `PlayerVoiceController` from the network start hook of the owning player.
Pass whether the player is local, and a persistence key for the remote player.

```csharp
using BuburitoGames.MetaVcFMOD;
using Mirror;

public class Player : NetworkBehaviour
{
    public PlayerVoiceController voiceController;
    [SyncVar] public ulong steamId;

    public override void OnStartClient()
    {
        voiceController.Initialize(isLocalPlayer, steamId.ToString());
    }
}
```

Pass an empty string as the key to disable the saved volume for that player.

Only call `Initialize` on a voice object that will record or play. A voice object that never
initializes does not appear in `PlayerVoiceController.Instances`.

### 5. Deactivate the voice object on a server-only copy

`VcFmodOutput` claims its FMOD stream in `Start` and releases it in `OnDestroy`. On a host, the
server copy of a remote player starts before `OnStartClient` runs. Deactivate the voice object
before you call `NetworkServer.Spawn` for a player that the host does not hear, or the host plays
a stream that never receives frames.

### Optional: noise suppression

MetaVoiceChat ships `RnnoiseVcInputFilter`, an input filter that runs the RNNoise denoiser over
each 10 ms block of mic audio. The package prefab does not include it. Add it like this:

1. Install RNNoise4Unity through its scoped registry. Follow the steps in the
   [RNNoise4Unity README](https://github.com/adrenak/RNNoise4Unity).
2. Uncomment `#define ENABLE_RNNOISE_FOR_META_VOICE_CHAT` at the top of
   `Assets/Metater/MetaVoiceChat/rnnoise/RnnoiseVcInputFilter.cs`.
3. Add `RnnoiseVcInputFilter` to the voice object. Assign its `metaVc` field.
4. On `GainVcInputFilter`, set `optionalNextInputFilter` to the new component. The chain is then
   `VcMicAudioInput` > `GainVcInputFilter` > `RnnoiseVcInputFilter`.

The filter only runs when it sits in the chain. A component that is on the object but not linked
does nothing. The `MetaVc` frame size must be a multiple of 10 ms, which every preset satisfies.

## API

### PlayerVoiceController

| Member | Scope | Description |
|---|---|---|
| `LocalInstance` | static | The local player's controller. Null before `Initialize` runs. |
| `Instances` | static | Every initialized controller. |
| `OnInstancesChanged` | static event | Fires when a controller initializes or disables. |
| `MaxVolume` | const | The upper bound of every volume value. It is `2`. |
| `Initialize(bool isLocal, string persistenceKey)` | | Registers the controller and loads saved settings. |
| `SetInputVolume` / `GetInputVolume` | local | Mic gain from `0` to `MaxVolume`. |
| `SetInputMuted` / `GetInputMuted` | local | Mutes the mic. |
| `SetDeafened` / `GetDeafened` | local | Mutes every incoming voice. |
| `SetInputDevice` / `GetActiveInputDevice` | local | Selects the mic device. |
| `SetOutputVolume` / `GetOutputVolume` | remote | How loud the local player hears this voice, from `0` to `MaxVolume`. |
| `SetOutputMuted` / `GetOutputMuted` | remote | Mutes this one voice. |

### PlayerVolumeEntry

Call `Bind(controller, isLocal)`. With `isLocal` true, the row controls the mic gain and the mic
mute. With `isLocal` false, the row controls the output volume and the output mute of that
remote player. Assign the four mute icons in the inspector.

A settings panel can rebuild its rows on `PlayerVoiceController.OnInstancesChanged`:

```csharp
foreach (PlayerVoiceController pvc in PlayerVoiceController.Instances)
{
    PlayerVolumeEntry row = Instantiate(rowPrefab, listParent);
    row.Bind(pvc, pvc.IsLocalPlayer);
}
```

### VcFmodOutput

`SetVolume(float volume)` sets the FMOD channel volume from `0` to `1`. `PlayerVoiceController`
calls it. Call it directly only when you do not use the controller.

## How playback works

1. `MetaVc` decodes a frame and calls `ReceiveFrame` on the main thread.
2. `VcFmodOutput` copies the frame into a pooled buffer and enqueues it.
3. The FMOD mixer thread runs the PCM read callback. The callback inserts pending frames into
   NetEQ, then pulls 10 ms chunks from NetEQ until the FMOD buffer is full.
4. NetEQ stretches, compresses or conceals audio to hold its target delay. No pitch shift is
   applied on the FMOD channel.

Each callback inserts at most 32 packets and reads at most 32 chunks. Those caps match
`OnAudioFilterReadVcOutput` in MetaVoiceChat.

## Saved settings

`PlayerVoiceController` writes these `PlayerPrefs` keys:

| Key | Value |
|---|---|
| `VoiceChat_InputVolume` | Mic gain |
| `VoiceChat_InputMuted` | `1` when muted |
| `VoiceChat_Deafened` | `1` when deafened |
| `VoiceChat_InputDevice` | The selected mic device name |
| `VoiceChat_OutputVolume_<persistenceKey>` | Output volume for one remote player |

## License

MIT. See [LICENSE](LICENSE).
