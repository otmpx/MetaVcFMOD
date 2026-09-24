using UnityEngine;
using UnityEngine.UI;

namespace BuburitoGames.MetaVcFMOD
{
    /// <summary>
    /// A single row in the per-player voice volume list.
    /// For the local player, controls input (mic gain) settings.
    /// For remote players, controls output (playback) settings as heard locally.
    /// </summary>
    public class PlayerVolumeEntry : MonoBehaviour
    {
        public Slider volumeSlider;
        public Button muteButton;

        public Sprite inputUnmutedIcon;
        public Sprite inputMutedIcon;
        public Sprite outputUnmutedIcon;
        public Sprite outputMutedIcon;

        PlayerVoiceController boundController;
        bool bindsInput;
        bool isMuted;

        void Awake() => SetDisabled();

        // The row's state before Bind runs.
        void SetDisabled()
        {
            Unbind();
            volumeSlider.interactable = false;
            muteButton.interactable = false;
        }

        public void Bind(PlayerVoiceController controller, bool isLocal)
        {
            // Remove the old listeners before you add new ones.
            Unbind();

            boundController = controller;
            bindsInput = isLocal;

            float volume = bindsInput ? controller.GetInputVolume() : controller.GetOutputVolume();
            isMuted = bindsInput ? controller.GetInputMuted() : controller.GetOutputMuted();

            if (bindsInput)
            {
                controller.SetInputVolume(volume);
                controller.SetInputMuted(isMuted);
            }
            else
            {
                controller.SetOutputVolume(volume);
                controller.SetOutputMuted(isMuted);
            }

            volumeSlider.SetValueWithoutNotify(volume);
            volumeSlider.onValueChanged.AddListener(OnSliderChanged);
            muteButton.onClick.AddListener(OnMuteToggle);

            muteButton.interactable = true;

            UpdateMuteIcon(); // UpdateMuteIcon sets volumeSlider.interactable.
        }

        void Unbind()
        {
            boundController = null;
            volumeSlider.onValueChanged.RemoveListener(OnSliderChanged);
            muteButton.onClick.RemoveListener(OnMuteToggle);
        }

        void OnDestroy() => Unbind();

        void OnSliderChanged(float value)
        {
            if (boundController == null) return;

            if (bindsInput)
                boundController.SetInputVolume(value);
            else
                boundController.SetOutputVolume(value);
        }

        void OnMuteToggle()
        {
            if (boundController == null) return;

            isMuted = !isMuted;
            if (bindsInput)
                boundController.SetInputMuted(isMuted);
            else
                boundController.SetOutputMuted(isMuted);

            UpdateMuteIcon();
        }

        void UpdateMuteIcon()
        {
            muteButton.image.sprite = bindsInput
                ? (isMuted ? inputMutedIcon : inputUnmutedIcon)
                : (isMuted ? outputMutedIcon : outputUnmutedIcon);
            volumeSlider.interactable = !isMuted;
        }
    }
}
