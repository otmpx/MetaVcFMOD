using MetaVoiceChat.Input;
using UnityEngine;

namespace BuburitoGames.MetaVcFMOD
{
    public class GainVcInputFilter : VcInputFilter
    {
        [Range(0f, 2f)]
        public float Gain = 1f;

        protected override void Filter(int index, ref float[] samples)
        {
            if (samples == null) return;

            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] *= Gain;
            }
        }
    }
}
