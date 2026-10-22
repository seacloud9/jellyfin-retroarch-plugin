package org.retrobridge.android

import android.os.Build
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL

class HttpStatusException(val status: Int) : IOException("Server returned HTTP $status")

// Minimal Jellyfin client. Jellyfin 12.1 requires the MediaBrowser Authorization header; redirects are refused.
class Api(server: String, private val deviceId: String, private val token: String?) {
    private val base = Rules.normalizeServer(server)

    private fun open(method: String, path: String, body: JSONObject? = null): HttpURLConnection {
        val connection = URL(base + path).openConnection() as HttpURLConnection
        connection.instanceFollowRedirects = false
        connection.connectTimeout = 15_000
        connection.readTimeout = 60_000
        connection.requestMethod = method
        connection.setRequestProperty("Authorization", authorization())
        if (body != null) {
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "application/json")
            connection.outputStream.use { it.write(body.toString().toByteArray()) }
        }
        return connection
    }

    private fun authorization(): String {
        val device = Build.MODEL.filter { it.isLetterOrDigit() }.ifEmpty { "Android" }
        return "MediaBrowser Client=\"RetroBridge Android\", Device=\"$device\", DeviceId=\"$deviceId\", Version=\"${BuildConfig.VERSION_NAME}\"" +
            (token?.let { ", Token=\"$it\"" } ?: "")
    }

    private fun json(method: String, path: String, body: JSONObject? = null): String? {
        val connection = open(method, path, body)
        try {
            val status = connection.responseCode
            if (status == 204) return null
            if (status !in 200..299) throw HttpStatusException(status)
            return connection.inputStream.bufferedReader().use { it.readText() }
        } finally { connection.disconnect() }
    }

    fun quickConnectEnabled() = json("GET", "QuickConnect/Enabled") == "true"
    fun initiate(): Pair<String, String> = JSONObject(json("POST", "QuickConnect/Initiate")!!).let { it.getString("Secret") to it.getString("Code") }
    fun authenticated(secret: String) = JSONObject(json("GET", "QuickConnect/Connect?secret=" + java.net.URLEncoder.encode(secret, "UTF-8"))!!).getBoolean("Authenticated")
    fun authenticate(secret: String): String = JSONObject(json("POST", "Users/AuthenticateWithQuickConnect", JSONObject().put("Secret", secret))!!).getString("AccessToken")
    fun userId(): String = JSONObject(json("GET", "Users/Me")!!).getString("Id")

    fun redeem(ticket: String): Game = game(JSONObject(json("POST", "RetroBridge/LaunchTickets/Redeem", JSONObject().put("Ticket", ticket))!!))
    fun claim(): Game? = json("POST", "RetroBridge/LaunchTickets/Claim")?.let { game(JSONObject(it)) }

    private fun savesRoute(gameId: String) = "RetroBridge/Games/" + java.net.URLEncoder.encode(gameId, "UTF-8") + "/Saves"

    // null when the server has no save sync or the game is not visible to this user.
    fun listSaves(gameId: String): List<ServerSave>? = try {
        val array = org.json.JSONArray(json("GET", savesRoute(gameId)) ?: "[]")
        (0 until array.length()).map { array.getJSONObject(it) }.map { ServerSave(it.getString("Name"), it.getString("Sha256"), it.getLong("Size"), it.getLong("Revision")) }
    } catch (e: HttpStatusException) { if (e.status == 404) null else throw e }

    fun downloadSave(gameId: String, save: ServerSave): ByteArray {
        val connection = open("GET", savesRoute(gameId) + "/" + java.net.URLEncoder.encode(save.name, "UTF-8"))
        try {
            if (connection.responseCode != 200) throw HttpStatusException(connection.responseCode)
            // Bounded read (InputStream.readNBytes needs API 33).
            val bytes = connection.inputStream.use { input ->
                val out = java.io.ByteArrayOutputStream()
                val buffer = ByteArray(16384)
                while (out.size() <= SaveSync.MAX_BYTES) { val n = input.read(buffer); if (n < 0) break; out.write(buffer, 0, n) }
                out.toByteArray()
            }
            if (bytes.size > SaveSync.MAX_BYTES || Rules.sha256(bytes.inputStream()) != save.sha256) throw IOException("Downloaded save ${save.name} failed verification.")
            return bytes
        } finally { connection.disconnect() }
    }

    // null on 409: the server moved on; the next sync reconciles.
    fun uploadSave(gameId: String, name: String, bytes: ByteArray, baseRevision: Long): ServerSave? {
        val connection = URL(base + savesRoute(gameId) + "/" + java.net.URLEncoder.encode(name, "UTF-8") + "?baseRevision=$baseRevision").openConnection() as HttpURLConnection
        connection.instanceFollowRedirects = false
        connection.requestMethod = "PUT"
        connection.doOutput = true
        connection.setRequestProperty("Authorization", authorization())
        connection.setRequestProperty("Content-Type", "application/octet-stream")
        try {
            connection.outputStream.use { it.write(bytes) }
            if (connection.responseCode == 409) return null
            if (connection.responseCode != 200) throw HttpStatusException(connection.responseCode)
            val o = JSONObject(connection.inputStream.bufferedReader().use { it.readText() })
            return ServerSave(o.getString("Name"), o.getString("Sha256"), o.getLong("Size"), o.getLong("Revision"))
        } finally { connection.disconnect() }
    }

    // Streams to a temporary file, enforces the expected size, and only moves verified content into place.
    fun download(game: Game, destination: File) {
        val temporary = File(destination.parentFile, destination.name + ".partial")
        try {
            val connection = open("GET", "RetroBridge/Games/" + java.net.URLEncoder.encode(game.id, "UTF-8") + "/Content")
            try {
                if (connection.responseCode != 200) throw HttpStatusException(connection.responseCode)
                connection.inputStream.use { input ->
                    temporary.outputStream().use { output ->
                        val buffer = ByteArray(65536); var total = 0L
                        while (true) {
                            val n = input.read(buffer); if (n < 0) break
                            total += n
                            if (total > game.size) throw IOException("Download exceeded expected size.")
                            output.write(buffer, 0, n)
                        }
                    }
                }
            } finally { connection.disconnect() }
            if (!Rules.verified(temporary, game)) throw IOException("Downloaded content failed checksum verification. Rescan the server library.")
            if (!temporary.renameTo(destination)) throw IOException("Could not store the game.")
        } finally { temporary.delete() }
    }

    private fun game(o: JSONObject) = Game(o.getString("Id"), o.getString("Title"), o.getString("Platform"), o.getString("Extension"), o.getLong("Size"), o.getString("Sha256"))
}
