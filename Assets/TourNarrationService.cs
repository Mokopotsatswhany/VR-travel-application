using System;
using System.Text;
using UnityEngine;

[DisallowMultipleComponent]
public class TourNarrationService : MonoBehaviour
{
    [Header("Narration Mode")]
    public bool preferTextToSpeechOverAudioClips = true;
    public bool includeLocationLabel = true;
    public bool includeHistoricalFact = true;

    [Header("Android Voice")]
    [Range(0.5f, 1.5f)]
    public float speechRate = 0.95f;

    [Range(0.5f, 1.5f)]
    public float speechPitch = 1f;

    public string androidLanguageCode = "en";
    public string androidCountryCode = "ZA";

    private readonly StringBuilder narrationBuilder = new StringBuilder(512);

#if UNITY_ANDROID && !UNITY_EDITOR
    private AndroidJavaObject textToSpeech;
    private bool textToSpeechReady;

    private sealed class TextToSpeechInitListener : AndroidJavaProxy
    {
        private readonly Action<int> onInit;

        public TextToSpeechInitListener(Action<int> onInitCallback)
            : base("android.speech.tts.TextToSpeech$OnInitListener")
        {
            onInit = onInitCallback;
        }

        public void onInit(int status)
        {
            onInit?.Invoke(status);
        }
    }
#endif

    public bool PrefersTextToSpeech => preferTextToSpeechOverAudioClips;

    public bool IsTextToSpeechAvailable
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return textToSpeechReady && textToSpeech != null;
#else
            return false;
#endif
        }
    }

    private void Awake()
    {
        InitializeTextToSpeech();
    }

    private void OnDestroy()
    {
        ShutdownTextToSpeech();
    }

    public void PlayNarration(TourSystem.TourStop stop, AudioSource audioSource)
    {
        StopNarration(audioSource);

        if (stop == null)
        {
            return;
        }

        var script = BuildNarrationScript(stop);
        if (preferTextToSpeechOverAudioClips && TrySpeak(script))
        {
            return;
        }

        if (stop.narrationClip != null && audioSource != null)
        {
            audioSource.clip = stop.narrationClip;
            audioSource.Play();
            return;
        }

        TrySpeak(script);
    }

    public void StopNarration(AudioSource audioSource)
    {
        if (audioSource != null && audioSource.isPlaying)
        {
            audioSource.Stop();
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        if (textToSpeech != null)
        {
            textToSpeech.Call<int>("stop");
        }
#endif
    }

    public float GetNarrationDuration(TourSystem.TourStop stop, float fallbackWordsPerMinute, float minimumStopDuration)
    {
        if (stop == null)
        {
            return minimumStopDuration;
        }

        if (!preferTextToSpeechOverAudioClips && stop.narrationClip != null)
        {
            return stop.narrationClip.length;
        }

        if (preferTextToSpeechOverAudioClips && IsTextToSpeechAvailable)
        {
            return EstimateSpeechDuration(stop, fallbackWordsPerMinute, minimumStopDuration);
        }

        if (stop.narrationClip != null)
        {
            return stop.narrationClip.length;
        }

        return EstimateSpeechDuration(stop, fallbackWordsPerMinute, minimumStopDuration);
    }

    public string DescribeNarrationMode(TourSystem.TourStop stop)
    {
        if (preferTextToSpeechOverAudioClips && IsTextToSpeechAvailable)
        {
            return "Android text-to-speech voice narration is active.";
        }

        if (stop != null && stop.narrationClip != null)
        {
            return $"Playing audio clip narration: {stop.narrationClip.name}.";
        }

        if (IsTextToSpeechAvailable)
        {
            return "Audio clip missing, so device text-to-speech will read the tour facts.";
        }

        return "No live voice is available on this platform yet. Android builds will use device text-to-speech.";
    }

    public string BuildNarrationScript(TourSystem.TourStop stop)
    {
        narrationBuilder.Clear();

        if (stop == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(stop.title))
        {
            narrationBuilder.Append(stop.title).Append(". ");
        }

        if (includeLocationLabel && !string.IsNullOrWhiteSpace(stop.locationLabel))
        {
            narrationBuilder.Append(stop.locationLabel).Append(". ");
        }

        if (!string.IsNullOrWhiteSpace(stop.summary))
        {
            narrationBuilder.Append(stop.summary).Append(' ');
        }

        if (includeHistoricalFact && !string.IsNullOrWhiteSpace(stop.historicalFact))
        {
            narrationBuilder.Append("Historical note. ").Append(stop.historicalFact);
        }

        return narrationBuilder.ToString().Trim();
    }

    private float EstimateSpeechDuration(TourSystem.TourStop stop, float fallbackWordsPerMinute, float minimumStopDuration)
    {
        var script = BuildNarrationScript(stop);
        if (string.IsNullOrWhiteSpace(script))
        {
            return minimumStopDuration;
        }

        var wordCount = script.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
        var wordsPerSecond = Mathf.Max(1f, fallbackWordsPerMinute / 60f);
        return Mathf.Max(minimumStopDuration, wordCount / wordsPerSecond);
    }

    private bool TrySpeak(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return false;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!textToSpeechReady || textToSpeech == null)
        {
            return false;
        }

        textToSpeech.Call<int>("stop");
        var utteranceId = $"tour_{Time.frameCount}";
        textToSpeech.Call<int>("speak", script, 0, null, utteranceId);
        return true;
#else
        return false;
#endif
    }

    private void InitializeTextToSpeech()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (textToSpeech != null)
        {
            return;
        }

        using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        {
            var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            textToSpeech = new AndroidJavaObject(
                "android.speech.tts.TextToSpeech",
                activity,
                new TextToSpeechInitListener(OnTextToSpeechInitialized));
        }
#endif
    }

    private void ShutdownTextToSpeech()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (textToSpeech == null)
        {
            return;
        }

        textToSpeech.Call<int>("stop");
        textToSpeech.Call("shutdown");
        textToSpeech.Dispose();
        textToSpeech = null;
        textToSpeechReady = false;
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private void OnTextToSpeechInitialized(int status)
    {
        textToSpeechReady = status == 0 && textToSpeech != null;
        if (!textToSpeechReady)
        {
            Debug.LogWarning("Android text-to-speech could not initialize. Audio clip fallback will be used.");
            return;
        }

        using (var locale = new AndroidJavaObject("java.util.Locale", androidLanguageCode, androidCountryCode))
        {
            textToSpeech.Call<int>("setLanguage", locale);
        }

        textToSpeech.Call<int>("setSpeechRate", speechRate);
        textToSpeech.Call<int>("setPitch", speechPitch);
    }
#endif
}
