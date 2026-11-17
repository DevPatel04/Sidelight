package net.oxog.phonebox.deskpanel

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import org.json.JSONObject

/**
 * Speech to text for the Dictation widget, with the phone's own speech recognizer (on most phones Google's, which may
 * use its servers unless "on the phone only" is asked for and the offline language is installed).
 *
 * One session at a time, at most [MAX_MS] long, cancelled when the panel pauses (screen off, another app in front).
 * Results go to the panel's main frame only, through window.onAndroidSpeech(kind, text); a sandboxed widget frame never
 * sees them. Nothing is stored. SpeechRecognizer must be used on the main thread, so every entry point posts there.
 */
class Dictation(private val activity: Activity, private val js: (String) -> Unit) {
    private val main = Handler(Looper.getMainLooper())
    private var recognizer: SpeechRecognizer? = null
    private var waitingFor: Request? = null
    private val timeLimit = Runnable { recognizer?.stopListening() }

    private data class Request(val lang: String, val offline: Boolean)

    fun available(): Boolean = SpeechRecognizer.isRecognitionAvailable(activity)

    /** Starts listening, asking for the microphone first if needed. [lang] is a BCP 47 tag, or "" for the phone's. */
    fun start(lang: String, offline: Boolean) = main.post {
        val request = Request(lang, offline)
        if (activity.checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) begin(request)
        else if (waitingFor == null) {
            waitingFor = request
            activity.requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), PERMISSION_REQUEST)
        }
    }

    /** From MainActivity.onRequestPermissionsResult. */
    fun onPermission(granted: Boolean) {
        val request = waitingFor ?: return
        waitingFor = null
        when {
            granted -> begin(request)
            // "don't ask again": Android no longer shows the prompt, only its settings can turn the microphone on
            !activity.shouldShowRequestPermissionRationale(Manifest.permission.RECORD_AUDIO) -> send("error", "permission-blocked")
            else -> send("error", "permission")
        }
    }

    /** Stops listening and delivers what was heard so far. */
    fun stop() = main.post { recognizer?.stopListening() }

    /** Drops the session without a result (the panel paused or the widget went away). */
    fun cancel() = main.post { if (recognizer != null) { end(); send("error", "cancelled") } }

    private fun begin(request: Request) {
        end()
        if (!available()) { send("error", "unavailable"); return }
        val r = SpeechRecognizer.createSpeechRecognizer(activity)
        recognizer = r
        r.setRecognitionListener(object : RecognitionListener {
            override fun onReadyForSpeech(params: Bundle?) = send("ready", "")
            override fun onBeginningOfSpeech() {}
            override fun onRmsChanged(rmsdB: Float) {}
            override fun onBufferReceived(buffer: ByteArray?) {}
            override fun onEndOfSpeech() = send("end", "")
            override fun onError(error: Int) { end(); send("error", error.toString()) }
            override fun onResults(results: Bundle?) { end(); send("final", first(results)) }
            override fun onPartialResults(partialResults: Bundle?) = send("partial", first(partialResults))
            override fun onEvent(eventType: Int, params: Bundle?) {}
        })
        val intent = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
            .putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
            .putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
            .putExtra(RecognizerIntent.EXTRA_CALLING_PACKAGE, activity.packageName)
        if (request.offline) intent.putExtra(RecognizerIntent.EXTRA_PREFER_OFFLINE, true)
        if (request.lang.isNotBlank()) intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE, request.lang)
        r.startListening(intent)
        main.postDelayed(timeLimit, MAX_MS)
    }

    private fun end() {
        main.removeCallbacks(timeLimit)
        recognizer?.let { it.cancel(); it.destroy() }
        recognizer = null
    }

    private fun first(b: Bundle?) = b?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull() ?: ""

    private fun send(kind: String, text: String) =
        js("window.onAndroidSpeech && window.onAndroidSpeech(${JSONObject.quote(kind)}, ${JSONObject.quote(text.take(MAX_CHARS))})")

    companion object {
        const val PERMISSION_REQUEST = 41
        private const val MAX_MS = 60_000L
        private const val MAX_CHARS = 2000
    }
}
