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
        val device = Build.MODEL.filter { it.isLetterOrDigit() }.ifEmpty { "Android" }
        connection.setRequestProperty("Authorization",
            "MediaBrowser Client=\"RetroBridge Android\", Device=\"$device\", DeviceId=\"$deviceId\", Version=\"0.1.0\"" +
                (token?.let { ", Token=\"$it\"" } ?: ""))
        if (body != null) {
            connection.doOutput = true
            connection.setRequestProperty("Content-Type", "application/json")
            connection.outputStream.use { it.write(body.toString().toByteArray()) }
        }
        return connection
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
