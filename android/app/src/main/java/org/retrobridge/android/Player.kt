package org.retrobridge.android

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Environment
import android.util.Log
import java.io.File
import java.io.IOException

// Downloads and verifies a game, protects its saves, and starts RetroArch through its exported activity.
class Player(private val context: Context) {
    private val root = File(Environment.getExternalStorageDirectory(), "RetroBridge")
    private val retroArchSaves = File(Environment.getExternalStorageDirectory(), "RetroArch/saves")

    fun retroArchPackage(): String? = RETROARCH_PACKAGES.firstOrNull {
        try { context.packageManager.getPackageInfo(it, 0); true } catch (_: PackageManager.NameNotFoundException) { false }
    }

    fun play(api: Api, game: Game, log: (String) -> Unit) {
        require(Rules.validGame(game)) { "Unsupported or invalid game metadata." }
        val retroArch = retroArchPackage() ?: throw IOException("Install RetroArch for Android first.")
        if (!Environment.isExternalStorageManager()) throw IOException("Allow RetroBridge \"All files access\" so RetroArch can read downloaded games.")
        val core = resolveCore(retroArch, game.platform)
        // RetroArch cannot read other apps' private files, so verified content goes to shared storage.
        val cache = File(root, "cache").apply { mkdirs() }
        val content = File(cache, game.id + game.extension)
        if (!Rules.verified(content, game)) { log("Downloading ${game.title}…"); api.download(game, content) }
        protectSaves(game, log)
        val intent = Intent().apply {
            component = ComponentName(retroArch, "com.retroarch.browser.retroactivity.RetroActivityFuture")
            putExtra("ROM", content.absolutePath)
            putExtra("LIBRETRO", core)
            // Exit cleanly when RetroArch loses focus so SRAM is always flushed.
            putExtra("QUITFOCUS", true)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        log("Starting ${game.title}.")
        Log.i(TAG, "Launching ${game.id} with $core")
        context.startActivity(intent)
    }

    // Asks RetroArch which cores are installed (InstalledCoresReceiver). Older builds without the receiver
    // fall back to the default core in RetroArch's data folder for this Android user.
    private fun resolveCore(retroArch: String, platform: String): String {
        val coresDir = "/data/user/${android.os.Process.myUserHandle().hashCode()}/$retroArch/cores"
        val reported = queryInstalledCores(retroArch)
        if (reported == null) {
            Log.w(TAG, "RetroArch did not report installed cores; assuming defaults")
            return "$coresDir/" + (CORES[platform] ?: throw IOException("No core is known for $platform."))
        }
        val chosen = Rules.chooseCore(platform, reported) ?: throw IOException(
            "No ${platform.uppercase()} core is installed in RetroArch. Open RetroArch → Online Updater → Core Downloader and install " +
                Rules.preferredCores[platform].orEmpty().first().replaceFirstChar { it.uppercase() } + ".")
        // Replies arrive on an exported receiver, so never trust a reported path: keep only a well-formed
        // file name and resolve it inside RetroArch's own cores folder.
        val name = chosen.substringAfterLast('/')
        if (!Rules.isCoreFileName(name)) throw IOException("RetroArch reported an unexpected core name.")
        return "$coresDir/$name"
    }

    private fun queryInstalledCores(retroArch: String): List<String>? {
        val result = java.util.concurrent.LinkedBlockingQueue<List<String>>()
        val receiver = object : android.content.BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) {
                result.offer(intent.getStringArrayExtra("CORES")?.toList().orEmpty())
            }
        }
        val thread = android.os.HandlerThread("retrobridge-cores").apply { start() }
        context.registerReceiver(receiver, android.content.IntentFilter(INSTALLED_CORES_RESULT), null, android.os.Handler(thread.looper), Context.RECEIVER_EXPORTED)
        try {
            context.sendBroadcast(Intent(QUERY_INSTALLED_CORES).setComponent(ComponentName(retroArch, "com.retroarch.browser.receiver.InstalledCoresReceiver")))
            return result.poll(3, java.util.concurrent.TimeUnit.SECONDS)?.also { Log.i(TAG, "RetroArch reported ${it.size} cores") }
        } finally {
            context.unregisterReceiver(receiver)
            thread.quitSafely()
        }
    }

    // Saves are named after the cached file (game ID), so they are unique per content.
    // Back them up before each launch and restore one that was found empty.
    private fun protectSaves(game: Game, log: (String) -> Unit) {
        val saves = retroArchSaves.walkTopDown().filter { it.isFile && it.name.startsWith(game.id + ".") && !it.name.endsWith(".tmp") }.toList()
        val backups = File(root, "backups/${game.id}")
        for (save in saves) {
            val history = File(backups, save.name).apply { mkdirs() }
            if (save.length() == 0L) {
                val latest = history.listFiles()?.filter { it.length() > 0 }?.maxByOrNull { it.name }
                if (latest != null) { latest.copyTo(save, overwrite = true); log("Restored ${save.name} from a backup."); Log.w(TAG, "Restored empty ${save.name}") }
                continue
            }
            save.copyTo(File(history, System.currentTimeMillis().toString()), overwrite = true)
            history.listFiles()?.sortedByDescending { it.name }?.drop(KEEP_BACKUPS)?.forEach { it.delete() }
        }
    }

    companion object {
        const val TAG = "RetroBridge"
        const val KEEP_BACKUPS = 5
        const val QUERY_INSTALLED_CORES = "com.retroarch.QUERY_INSTALLED_CORES"
        const val INSTALLED_CORES_RESULT = "com.retroarch.INSTALLED_CORES_RESULT"
        val RETROARCH_PACKAGES = listOf("com.retroarch", "com.retroarch.aarch64", "com.retroarch.ra32")
        val CORES = mapOf("nes" to "nestopia_libretro_android.so", "gb" to "gambatte_libretro_android.so", "gbc" to "gambatte_libretro_android.so")
    }
}
