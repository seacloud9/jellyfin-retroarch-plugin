package org.retrobridge.android

import java.io.File
import java.io.InputStream
import java.net.URI
import java.security.MessageDigest

// Same rules as the desktop companion (RetroBridge.Domain.LaunchLink and the launcher's server checks).
object Rules {
    const val SCHEME = "retrobridge"
    val platforms = mapOf(".nes" to "nes", ".gb" to "gb", ".gbc" to "gbc")
    const val MAX_CONTENT = 64L * 1024 * 1024

    // retrobridge://launch?ticket=<base64url>. Anything else, including extra parameters, is rejected.
    fun parseTicket(link: String): String? {
        if (link.length > 512) return null
        val uri = try { URI(link) } catch (_: Exception) { return null }
        if (uri.scheme != SCHEME || uri.host != "launch" || (uri.rawPath ?: "") !in setOf("", "/") ||
            uri.rawUserInfo != null || uri.port != -1 || uri.rawFragment != null) return null
        val query = uri.rawQuery ?: return null
        if (!query.startsWith("ticket=")) return null
        return query.removePrefix("ticket=").takeIf(::isTicket)
    }

    fun isTicket(value: String) = value.length in 20..128 && value.all { it.isLetterOrDigit() && it.code < 128 || it == '-' || it == '_' }

    // HTTPS, or HTTP only to loopback (e.g. adb reverse during development). No credentials, query, or fragment.
    fun normalizeServer(value: String): String {
        val uri = try { URI(value.trim().trimEnd('/') + "/") } catch (_: Exception) { throw IllegalArgumentException("Enter a valid server URL.") }
        val loopback = uri.host in setOf("127.0.0.1", "localhost", "[::1]", "::1")
        require(uri.scheme == "https" || (uri.scheme == "http" && loopback)) { "Use an HTTPS server URL (HTTP only on loopback)." }
        require(uri.host != null && uri.rawUserInfo == null && uri.rawQuery == null && uri.rawFragment == null) { "Remove credentials, query, or fragment from the URL." }
        return uri.toString()
    }

    fun validGame(game: Game) = platforms[game.extension] == game.platform && game.size in 0..MAX_CONTENT &&
        game.sha256.length == 64 && game.sha256.all { it in '0'..'9' || it in 'a'..'f' } && game.id == "${game.platform}-${game.sha256}"

    fun sha256(input: InputStream): String {
        val digest = MessageDigest.getInstance("SHA-256")
        val buffer = ByteArray(65536)
        while (true) { val n = input.read(buffer); if (n < 0) break; digest.update(buffer, 0, n) }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    fun verified(file: File, game: Game) = file.isFile && file.length() == game.size && file.inputStream().use(::sha256) == game.sha256
}

data class Game(val id: String, val title: String, val platform: String, val extension: String, val size: Long, val sha256: String)
