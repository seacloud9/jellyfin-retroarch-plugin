package org.retrobridge.android

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.os.Environment
import android.provider.Settings
import android.text.InputType
import android.util.Log
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.TextView

// Pairing and setup. Games are started from the Jellyfin play page via retrobridge:// links (LaunchActivity).
class MainActivity : Activity() {
    private lateinit var status: TextView
    private lateinit var server: EditText
    private lateinit var storage: Button
    private lateinit var play: Button
    @Volatile private var pairing = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val pad = (16 * resources.displayMetrics.density).toInt()
        val layout = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(pad, pad * 3, pad, pad) }
        layout.addView(TextView(this).apply { text = "RetroBridge"; textSize = 26f })
        layout.addView(TextView(this).apply { text = "Pair this phone with Jellyfin, then press Play on the RetroBridge play page in your browser." })
        server = EditText(this).apply { hint = "https://jellyfin.example/"; inputType = InputType.TYPE_TEXT_VARIATION_URI; setSingleLine() }
        layout.addView(server, LinearLayout.LayoutParams(MATCH_PARENT, WRAP_CONTENT))
        layout.addView(Button(this).apply { text = "Pair with Quick Connect"; setOnClickListener { pair(server.text.toString()) } })
        storage = Button(this).apply {
            text = "Allow all files access"
            setOnClickListener { startActivity(Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION, Uri.parse("package:$packageName"))) }
        }
        layout.addView(storage)
        play = Button(this).apply {
            text = "Open play page"
            setOnClickListener { Store(this@MainActivity).pairing?.let { startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(it.server + "RetroBridge/Play"))) } }
        }
        layout.addView(play)
        status = TextView(this).apply { setPadding(0, pad, 0, 0); setTextIsSelectable(true) }
        layout.addView(status)
        setContentView(layout)
        if (Store(this).pairing != null) SyncJobService.schedule(this)
        // Debug builds accept a server extra so the device smoke test can start pairing without typing.
        if (BuildConfig.DEBUG) intent.getStringExtra("server")?.let { server.setText(it); pair(it) }
    }

    override fun onResume() {
        super.onResume()
        storage.visibility = if (Environment.isExternalStorageManager()) android.view.View.GONE else android.view.View.VISIBLE
        if (!pairing) showState()
        // Upload saves made since the last launch whenever the app comes to the front.
        val current = Store(this).pairing
        if (current != null && Environment.isExternalStorageManager()) Thread {
            try { Syncer(this, Api(current.server, current.deviceId, current.token)).syncAll() }
            catch (e: Exception) { Log.w(Player.TAG, "Save sync failed: ${e.message}"); Store(this).lastProblem = "Save sync: ${e.message}" }
        }.start()
    }

    private fun showState() {
        val current = Store(this).pairing
        play.visibility = if (current == null) android.view.View.GONE else android.view.View.VISIBLE
        val retroArch = Player(this).retroArchPackage()
        status.text = buildString {
            appendLine(if (current == null) "Not paired." else "Paired with ${current.server}\nDevice: ${current.deviceId}")
            appendLine(if (retroArch == null) "RetroArch for Android is not installed." else "RetroArch: $retroArch")
            if (!Environment.isExternalStorageManager()) appendLine("All files access is needed so RetroArch can open downloaded games.")
            appendLine("Version ${BuildConfig.VERSION_NAME}")
            Store(this@MainActivity).lastProblem?.let { appendLine("\nLast problem: $it") }
        }
    }

    private fun pair(input: String) {
        if (pairing) return
        pairing = true
        status.text = "Contacting server…"
        Thread {
            try {
                val url = Rules.normalizeServer(input)
                val device = Store.newDeviceId()
                val api = Api(url, device, null)
                if (!api.quickConnectEnabled()) throw IllegalStateException("Quick Connect is disabled on this server. An administrator can enable it under Dashboard → General.")
                val (secret, code) = api.initiate()
                if (BuildConfig.DEBUG) Log.i(Player.TAG, "Quick Connect code: $code")
                runOnUiThread { status.text = "Quick Connect code: $code\n\nIn Jellyfin, open your profile → Quick Connect and enter this code." }
                val deadline = System.currentTimeMillis() + 5 * 60_000
                while (!api.authenticated(secret)) {
                    if (System.currentTimeMillis() > deadline) throw IllegalStateException("Quick Connect timed out. Try again.")
                    Thread.sleep(2000)
                }
                Store(this).save(Pairing(url, device, api.authenticate(secret)))
                SyncJobService.schedule(this)
                Log.i(Player.TAG, "Paired device $device")
                runOnUiThread { pairing = false; showState() }
            } catch (e: Exception) {
                Log.w(Player.TAG, "Pairing failed", e)
                Store(this).lastProblem = "Pairing: ${e.message}"
                runOnUiThread { pairing = false; status.text = "Pairing failed: ${e.message}" }
            }
        }.start()
    }
}
