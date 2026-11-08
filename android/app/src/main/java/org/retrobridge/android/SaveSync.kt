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
        "snes9x" to "Snes9x", "bsnes" to "bsnes", "snes9x2010" to "Snes9x 2010",
        "mupen64plus_next_gles3" to "Mupen64Plus-Next", "mupen64plus_next" to "Mupen64Plus-Next", "parallel_n64" to "ParaLLEl N64",
        "vba_next" to "VBA Next", "vbam" to "VBA-M", "gpsp" to "gpSP",
        "genesis_plus_gx" to "Genesis Plus GX", "picodrive" to "PicoDrive", "clownmdemu" to "ClownMDEmu",
        "pcsx_rearmed" to "PCSX-ReARMed", "swanstation" to "SwanStation", "mednafen_psx_hw" to "Beetle PSX HW", "mednafen_psx" to "Beetle PSX",
        "nova64" to "Nova64",
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
