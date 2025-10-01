using BepInEx;
using BepInEx.Configuration;
using NAudio.Wave;
using R2API;
using RiskOfOptions;
using RiskOfOptions.OptionConfigs;
using RiskOfOptions.Options;
using RoR2;
using SimpleJSON;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;
using Random = UnityEngine.Random;

namespace ExamplePlugin
{
    [BepInDependency(ItemAPI.PluginGUID)]
    [BepInDependency(LanguageAPI.PluginGUID)]
    [BepInDependency("com.rune580.riskofoptions")]

    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class TiktokNarrator : BaseUnityPlugin
    {
        public const string PluginGUID = $"{PluginAuthor}.{PluginName}";
        public const string PluginAuthor = "KAUTARUMA";
        public const string PluginName = "TiktokNarrator";
        public const string PluginVersion = "1.0.1";

        static readonly Regex XmlRegex = new("<(?>(?:<(?<B>)|>(?<-B>)|[^<>]+)*)>", RegexOptions.Compiled);

        static readonly string[] Voices = [
            "en_male_sing_deep_jingle", 
            "en_male_sing_funny_it_goes_up", 
            "en_male_m03_lobby", 
            "en_male_m03_sunshine_soon", 
            "en_male_m2_xhxs_m03_silly",
            "en_male_sing_funny_thanksgiving",
            "en_female_f08_salut_damour",
            "en_female_f08_warmy_breeze",
            "en_female_ht_f08_glorious",
            "en_female_ht_f08_wonderful_world",
            "en_female_ht_f08_halloween",
            "en_female_ht_f08_newyear",
            "en_female_f08_twinkle"
        ];

        const string Endpoint = "https://tiktok-tts.weilnet.workers.dev/api/generation";
        
        ConfigEntry<float> VolumeMultiplier;

        public void Awake()
        {
            Log.Init(Logger);

            VolumeMultiplier = Config.Bind("Audio", "Volume Multiplier", 2f, "Multiplier for TTS volume.");
            ModSettingsManager.AddOption(new SliderOption(VolumeMultiplier, new StepSliderConfig() { min = 0f, max = 4f, increment = 0.15f }));

            On.RoR2.Inventory.GiveItem_ItemIndex_int += Inventory_GiveItem;
        }

        private void Inventory_GiveItem(On.RoR2.Inventory.orig_GiveItem_ItemIndex_int orig, Inventory self, ItemIndex itemIndex, int count)
        {
            orig(self, itemIndex, count);

            if (!self.GetComponent<CharacterMaster>()?.playerCharacterMasterController)
                return;

            var itemDef = ItemCatalog.GetItemDef(itemIndex);
            if (!itemDef)
                return;

            string itemName = Language.GetString(itemDef.nameToken);
            string itemDesc = Language.GetString(itemDef.pickupToken);

            itemDesc = XmlRegex.Replace(itemDesc, "");

            StartCoroutine(FetchAndPlaySound($"{itemName}. {itemDesc}", Voices[Random.Range(0, Voices.Length)]));
        }

        private IEnumerator FetchAndPlaySound(string text, string voice)
        {
            Log.Info($"Beginning Tiktok Coroutine... \"{text}\" with voice \"{voice}\"");

            JSONObject json = new JSONObject();
            json["text"] = text;
            json["voice"] = voice;

            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json.ToString());

            using UnityWebRequest request = new UnityWebRequest(Endpoint, "POST");
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (IsErr(request, out var returnJson))
            {
                Log.Info("Failed to get audio in post step!! :(");
                yield break;
            }

            JSONNode node = JSONObject.Parse(returnJson);
            byte[] b64Audio = Convert.FromBase64String(node.AsObject["data"]);

            float[] samples = DecodeMp3ToFloats(b64Audio, out int sampleRate, out int channels);

            int lengthSamples = samples.Length / channels;
            
            var go = new GameObject("TTS_AudioInput");
            go.name = "himynameiskautaruma";

            var input = go.AddComponent<StreamedWwiseAudio>();
            input.EventId = 2603804773;
            input.VolumeMultiplier = VolumeMultiplier.Value;
            input.FeedSamples(samples, sampleRate, channels);
        }

        private static bool IsErr(UnityWebRequest web, out string data)
        {
            var error = web.error;
            var code = web.responseCode;
            var downloadHandler = web.downloadHandler;
            data = downloadHandler.text;

            if (error is null && code is >= 200 and < 300 && data is { Length: not 0 })
                return false;

            data = "";
            Log.Error($"Tiktok audio fetch error! {code}: {error}");
            return true;
        }

        public static float[] DecodeMp3ToFloats(byte[] mp3Data, out int sampleRate, out int channels)
        {
            using (var mp3Stream = new MemoryStream(mp3Data))
            using (var mp3Reader = new Mp3FileReader(mp3Stream))
            {
                var sampleProvider = mp3Reader.ToSampleProvider();

                sampleRate = sampleProvider.WaveFormat.SampleRate;
                channels = sampleProvider.WaveFormat.Channels;

                List<float> samplesList = new List<float>();
                float[] buffer = new float[sampleRate * channels];
                int read;

                while ((read = sampleProvider.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (int i = 0; i < read; i++)
                        samplesList.Add(buffer[i]);
                }

                return samplesList.ToArray();
            }
        }
    }

    public class StreamedWwiseAudio : MonoBehaviour
    {
        public uint EventId;

        public uint SampleRate = 48000;
        public uint NumberOfChannels = 1;

        public float VolumeMultiplier = 2f;

        private bool IsPlaying = true;
        private Queue<float> SampleQueue = new Queue<float>();
           
        void AudioFormatDelegate(uint playingID, AkAudioFormat audioFormat)
        {
            audioFormat.channelConfig.uNumChannels = NumberOfChannels;
            audioFormat.uSampleRate = SampleRate;
        }
        
        bool AudioSamplesDelegate(uint playingID, uint channelIndex, float[] samples)
        {
            for (int i = 0; i < samples.Length; ++i)
            {
                if (SampleQueue.Count > 0)
                {
                    samples[i] = SampleQueue.Dequeue() * VolumeMultiplier;
                    samples[i] = Mathf.Clamp(samples[i], -1f, 1f);
                }
                else
                {
                    samples[i] = 0f;
                    StopSound();
                }
            }

            return IsPlaying;
        }

        private void Start()
        {
            AkAudioInputManager.PostAudioInputEvent(EventId, gameObject, AudioSamplesDelegate, AudioFormatDelegate);
        }

        public void FeedSamples(float[] decoded, int sampleRate, int channels)
        {
            SampleRate = (uint)sampleRate;
            NumberOfChannels = (uint)channels;

            foreach (var s in decoded)
                SampleQueue.Enqueue(s);

            IsPlaying = true;
        }

        public void StopSound()
        {
            IsPlaying = false;

            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            AkSoundEngine.StopPlayingID(EventId);
        }
    }
}
