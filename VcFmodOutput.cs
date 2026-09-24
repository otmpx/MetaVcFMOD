using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FMOD;
using FMODUnity;
using MetaVoiceChat;
using MetaVoiceChat.NetEq;
using MetaVoiceChat.Output;
using MetaVoiceChat.Output.OnAudioFilterReadVcOutput;
using MetaVoiceChat.Utils;
using UnityEngine;

namespace BuburitoGames.MetaVcFMOD
{
    /// <summary>
    /// Plays decoded voice through an FMOD user sound instead of a Unity AudioSource.
    /// NetEQ holds the jitter buffer and conceals lost packets. FMOD resamples the 48 kHz stream.
    /// </summary>
    public class VcFmodOutput : VcAudioOutput
    {
        [Tooltip("FMOD Studio bus path, for example bus:/VoiceChat. Leave empty for the master bus.")]
        public string fmodBusPath = "bus:/VoiceChat";
        public OnAudioFilterReadVcConfig netEqConfig;

        // The same per-callback caps as OnAudioFilterReadVcOutput. They bound the mixer-thread work.
        const int MaxPacketsInsertedPerRead = 32;
        const int MaxNetEqReadsPerRead = 32;
        const int NetEqChunkMs = 10;
        const int NetEqChunkSamples = VcConfig.SamplesPerSecond * NetEqChunkMs / 1000;
        const int BytesPerSample = sizeof(float);
        const int MsPerSecond = 1000;

        class PendingFrame
        {
            public float[] samples;
            public int length;
            public ushort sequenceNumber;
            public uint timestamp;
            public bool isSilence;
        }

        static readonly ConcurrentDictionary<IntPtr, VcFmodOutput> s_instances = new();
        static readonly SOUND_PCMREAD_CALLBACK s_pcmRead = StaticPCMRead;
        static readonly SOUND_PCMSETPOS_CALLBACK s_pcmSetPos = StaticPCMSetPos;

        FMOD.System coreSystem;
        Sound fmodSound;
        Channel fmodChannel;
        IntPtr soundRawHandle;

        // The main thread enqueues frames. The FMOD mixer thread dequeues them and owns NetEQ.
        readonly ConcurrentQueue<PendingFrame> pendingFrames = new();
        readonly ConcurrentQueue<PendingFrame> freeFrames = new();
        IntPtr netEq;
        readonly UnmanagedFloatArray packetBuffer = new();
        readonly UnmanagedFloatArray chunkBuffer = new();
        readonly float[] leftover = new float[NetEqChunkSamples];
        int leftoverStart;
        int leftoverCount;
        int samplesPerFrame;
        uint packetDurationMs;

        void Start()
        {
            samplesPerFrame = metaVc.config.samplesPerFrame;
            packetDurationMs = (uint)Mathf.RoundToInt(samplesPerFrame * (float)MsPerSecond / VcConfig.SamplesPerSecond);
            netEq = CreateNetEq();

            coreSystem = RuntimeManager.CoreSystem;
            ChannelGroup voiceGroup = ResolveChannelGroup();

            int decodeChunks = Mathf.CeilToInt(samplesPerFrame / (float)NetEqChunkSamples);
            CREATESOUNDEXINFO exinfo = new()
            {
                cbsize = Marshal.SizeOf(typeof(CREATESOUNDEXINFO)),
                numchannels = 1,
                defaultfrequency = VcConfig.SamplesPerSecond,
                decodebuffersize = (uint)(decodeChunks * NetEqChunkSamples),
                length = (uint)(VcConfig.SamplesPerClip * BytesPerSample),
                format = SOUND_FORMAT.PCMFLOAT,
                pcmreadcallback = s_pcmRead,
                pcmsetposcallback = s_pcmSetPos,
            };

            coreSystem.createSound("", MODE.OPENUSER | MODE.LOOP_NORMAL | MODE.CREATESTREAM, ref exinfo, out fmodSound);
            soundRawHandle = fmodSound.handle;
            s_instances[soundRawHandle] = this;
            coreSystem.playSound(fmodSound, voiceGroup, false, out fmodChannel);
        }

        IntPtr CreateNetEq()
        {
            int packetMs = (int)packetDurationMs;
            int minDelayMs;
            int maxDelayMs;
            if (netEqConfig.jitterBufferMode == OnAudioFilterReadVcConfig.JitterBufferMode.Custom)
            {
                minDelayMs = netEqConfig.customMinDelayMs;
                maxDelayMs = netEqConfig.customMaxDelayMs;
            }
            else
            {
                minDelayMs = OnAudioFilterReadVcConfig.GetMinDelayMs(packetMs, netEqConfig.jitterBufferMode);
                maxDelayMs = OnAudioFilterReadVcConfig.GetMaxDelayMs(packetMs, netEqConfig.jitterBufferMode);
            }
            maxDelayMs = Mathf.Max(minDelayMs, maxDelayMs);

            return NetEqInterop.CreateNetEq(
                VcConfig.SamplesPerSecond,
                1,
                netEqConfig.maxPacketsInBuffer,
                (uint)maxDelayMs,
                (uint)minDelayMs,
                (uint)netEqConfig.additionalDelayMs);
        }

        ChannelGroup ResolveChannelGroup()
        {
            if (!string.IsNullOrEmpty(fmodBusPath)
                && RuntimeManager.StudioSystem.getBus(fmodBusPath, out FMOD.Studio.Bus bus) == RESULT.OK
                && bus.getChannelGroup(out ChannelGroup busGroup) == RESULT.OK
                && busGroup.hasHandle())
            {
                return busGroup;
            }

            coreSystem.getMasterChannelGroup(out ChannelGroup master);
            return master;
        }

        protected override void ReceiveFrame(int index, float[] samples, float targetLatency)
        {
            if (netEq == IntPtr.Zero) return;

            if (!freeFrames.TryDequeue(out PendingFrame frame))
                frame = new PendingFrame { samples = new float[samplesPerFrame] };

            ulong frameIndex = (ulong)Mathf.Max(index, 0);
            frame.sequenceNumber = (ushort)(frameIndex % (ushort.MaxValue + 1UL));
            frame.timestamp = (uint)(frameIndex * (ulong)samplesPerFrame % (uint.MaxValue + 1UL));
            frame.length = samplesPerFrame;
            frame.isSilence = samples == null;
            if (!frame.isSilence)
                Array.Copy(samples, frame.samples, samplesPerFrame);

            pendingFrames.Enqueue(frame);
        }

        public void SetVolume(float volume)
        {
            if (fmodChannel.hasHandle())
                fmodChannel.setVolume(volume);
        }

        [AOT.MonoPInvokeCallback(typeof(SOUND_PCMREAD_CALLBACK))]
        static RESULT StaticPCMRead(IntPtr soundraw, IntPtr data, uint datalen)
        {
            if (s_instances.TryGetValue(soundraw, out VcFmodOutput inst))
                inst.FillBuffer(data, (int)(datalen / BytesPerSample));
            return RESULT.OK;
        }

        [AOT.MonoPInvokeCallback(typeof(SOUND_PCMSETPOS_CALLBACK))]
        static RESULT StaticPCMSetPos(IntPtr soundraw, int sub, uint pos, TIMEUNIT unit) => RESULT.OK;

        void FillBuffer(IntPtr data, int count)
        {
            InsertPendingPackets();

            int written = 0;
            int reads = 0;
            while (written < count && (leftoverCount > 0 || reads < MaxNetEqReadsPerRead))
            {
                if (leftoverCount == 0)
                {
                    reads++;
                    ReadNetEqChunk();
                }

                int take = Mathf.Min(leftoverCount, count - written);
                Marshal.Copy(leftover, leftoverStart, data + written * BytesPerSample, take);
                leftoverStart += take;
                leftoverCount -= take;
                written += take;
            }

            for (int i = written; i < count; i++)
                Marshal.WriteInt32(data, i * BytesPerSample, 0);
        }

        void InsertPendingPackets()
        {
            int inserted = 0;
            while (inserted < MaxPacketsInsertedPerRead && pendingFrames.TryDequeue(out PendingFrame frame))
            {
                inserted++;
                IntPtr samplesPtr = packetBuffer.GetOrInit(frame.length, out _);
                if (frame.isSilence)
                    packetBuffer.Zero();
                else
                    packetBuffer.Fill(new ArraySegment<float>(frame.samples, 0, frame.length));

                NetEqInterop.InsertPacket(
                    netEq,
                    frame.sequenceNumber,
                    frame.timestamp,
                    samplesPtr,
                    frame.length,
                    VcConfig.SamplesPerSecond,
                    1,
                    packetDurationMs);

                freeFrames.Enqueue(frame);
            }
        }

        // Fills leftover with one 10 ms chunk. NetEQ conceals a gap, so a short read pads with silence.
        void ReadNetEqChunk()
        {
            IntPtr chunkPtr = chunkBuffer.GetOrInit(NetEqChunkSamples, out float[] chunk, initToZero: false);
            int read = NetEqInterop.GetAudio(netEq, chunkPtr, NetEqChunkSamples);
            read = Mathf.Clamp(read, 0, NetEqChunkSamples);
            chunkBuffer.ReadFromUnmanaged(read);

            Array.Copy(chunk, leftover, read);
            Array.Clear(leftover, read, NetEqChunkSamples - read);
            leftoverStart = 0;
            leftoverCount = NetEqChunkSamples;
        }

        void OnDestroy()
        {
            // Release the sound first. That stops the mixer callback before NetEQ is freed.
            s_instances.TryRemove(soundRawHandle, out _);
            if (fmodSound.hasHandle())
                fmodSound.release();

            if (netEq != IntPtr.Zero)
            {
                NetEqInterop.FreeNetEq(netEq);
                netEq = IntPtr.Zero;
            }
            packetBuffer.Free();
            chunkBuffer.Free();
        }
    }
}
