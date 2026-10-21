package org.retrobridge.android

import android.content.Context
import android.os.Environment
import android.util.Log
import java.io.File

// Syncs RetroArch battery saves with the server. Android does not report when RetroArch exits, so uploads happen
// before each launch, when the app opens, and from a periodic background job. Failures never block play.
class Syncer(context: Context, private val api: Api) {
    private val state = context.getSharedPreferences("retrobridge-sync", Context.MODE_PRIVATE)
    private val saves = File(Environment.getExternalStorageDirectory(), "RetroArch/saves")
    private val savePattern = Regex("((?:nes|gb|gbc)-[0-9a-f]{64})\\.(srm|sav|rtc)")

    // Before a launch: also places a first download in the core's save folder.
    fun syncGame(gameId: String, coreFile: String?, log: (String) -> Unit = {}) {
        val folder = coreFile?.substringBefore("_libretro")?.let { SaveSync.coreFolders[it] }
        sync(gameId, folder, log)
    }

    // Pushes changes for every game that has a local save.
    fun syncAll(log: (String) -> Unit = {}) {
        val ids = saves.walkTopDown().mapNotNull { savePattern.matchEntire(it.name)?.groupValues?.get(1) }.toSet()
        for (id in ids) try { sync(id, null, log) } catch (e: Exception) { Log.w(Player.TAG, "Save sync for $id failed: ${e.message}") }
    }

    private fun sync(gameId: String, folder: String?, log: (String) -> Unit) {
        val server = (api.listSaves(gameId) ?: return).associateBy { it.name }
        val local = saves.walkTopDown().filter { it.isFile && SaveSync.isSyncedName(gameId, it.name) }.associateBy { it.name }
        for (name in (server.keys + local.keys).filter { SaveSync.isSyncedName(gameId, it) }) {
            val file = local[name] ?: folder?.let { File(saves, "$it/$name") } ?: continue
            // An empty local file is never authoritative (see the save backups in Player).
            val localSha = file.takeIf { it.isFile && it.length() > 0 }?.inputStream()?.use(Rules::sha256)
            val remote = server[name]
            val last = state.getString("$gameId/$name", null)?.split(':')?.let { SyncedSave(it[0], it[1].toLong()) }
            when (SaveSync.decide(localSha, last, remote)) {
                SyncAction.DOWNLOAD -> {
                    write(file, api.downloadSave(gameId, remote!!))
                    remember(gameId, name, remote.sha256, remote.revision)
                    log("Downloaded $name (revision ${remote.revision}).")
                }
                SyncAction.UPLOAD -> upload(gameId, name, file, last?.revision ?: 0, log)
                SyncAction.CONFLICT -> {
                    // The phone's copy wins; the other device's copy is kept beside it.
                    write(File(file.parentFile, "$name.server-r${remote!!.revision}"), api.downloadSave(gameId, remote))
                    log("Save conflict for $name: kept this phone's copy; the other copy is $name.server-r${remote.revision}.")
                    upload(gameId, name, file, remote.revision, log)
                }
                SyncAction.NONE -> if (localSha != null && remote != null) remember(gameId, name, localSha, remote.revision)
            }
        }
    }

    private fun upload(gameId: String, name: String, file: File, baseRevision: Long, log: (String) -> Unit) {
        val bytes = file.readBytes()
        if (bytes.isEmpty() || bytes.size > SaveSync.MAX_BYTES) return
        val saved = api.uploadSave(gameId, name, bytes, baseRevision) ?: return log("$name changed on the server; it will be reconciled next time.")
        remember(gameId, name, saved.sha256, saved.revision)
        Log.i(Player.TAG, "Save sync uploaded $name revision ${saved.revision}")
        log("Uploaded $name (revision ${saved.revision}).")
    }

    private fun remember(gameId: String, name: String, sha: String, revision: Long) = state.edit().putString("$gameId/$name", "$sha:$revision").apply()

    private fun write(file: File, bytes: ByteArray) {
        file.parentFile?.mkdirs()
        val temporary = File(file.parentFile, file.name + ".download")
        temporary.writeBytes(bytes)
        if (!temporary.renameTo(file)) { temporary.copyTo(file, overwrite = true); temporary.delete() }
    }
}
