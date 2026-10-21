package org.retrobridge.android

// Same rules as RetroBridge.Domain.SaveSync on desktop.
data class SyncedSave(val sha256: String, val revision: Long)
data class ServerSave(val name: String, val sha256: String, val size: Long, val revision: Long)

enum class SyncAction { NONE, DOWNLOAD, UPLOAD, CONFLICT }

object SaveSync {
    val extensions = listOf(".srm", ".sav", ".rtc")
    const val MAX_BYTES = 1024L * 1024

    // RetroArch for Android sorts saves into folders named after the core's library name.
    val coreFolders = mapOf(
        "nestopia" to "Nestopia", "mesen" to "Mesen", "fceumm" to "FCEUmm", "quicknes" to "QuickNES",
        "gambatte" to "Gambatte", "sameboy" to "SameBoy", "gearboy" to "Gearboy", "mgba" to "mGBA",
    )

    fun isSyncedName(gameId: String, name: String) = name.length <= 200 && extensions.any { name == gameId + it }

    fun decide(localSha: String?, last: SyncedSave?, server: ServerSave?): SyncAction {
        if (localSha == null && server == null) return SyncAction.NONE
        if (localSha == null) return SyncAction.DOWNLOAD
        if (server == null) return SyncAction.UPLOAD
        if (localSha == server.sha256) return SyncAction.NONE
        val localChanged = last == null || last.sha256 != localSha
        val serverChanged = last == null || last.revision != server.revision
        return when {
            !localChanged && serverChanged -> SyncAction.DOWNLOAD
            localChanged && !serverChanged -> SyncAction.UPLOAD
            else -> SyncAction.CONFLICT
        }
    }
}
