package org.retrobridge.android

import android.app.Activity
import android.os.Bundle
import android.util.Log
import android.widget.Toast

// Receives retrobridge://launch?ticket=… from the browser. The link carries only the ticket; the server and
// credentials come from this device's pairing, and RetroArch and its core are resolved locally.
class LaunchActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val ticket = intent.dataString?.let(Rules::parseTicket)
        val pairing = Store(this).pairing
        when {
            ticket == null -> done("Not a valid RetroBridge launch link.")
            pairing == null -> done("Open RetroBridge and pair this phone first.")
            else -> Thread {
                try {
                    val api = Api(pairing.server, pairing.deviceId, pairing.token)
                    val game = api.redeem(ticket)
                    Player(this).play(api, game) { message -> runOnUiThread { Toast.makeText(this, message, Toast.LENGTH_SHORT).show() } }
                    runOnUiThread { finish() }
                } catch (e: HttpStatusException) {
                    done(when (e.status) { 401 -> "This phone is no longer paired. Pair again."; 403 -> "Your account no longer has RetroBridge access."; 404 -> "Launch ticket is invalid or expired. Press Play again."; else -> e.message })
                } catch (e: Exception) {
                    done(e.message)
                }
            }.start()
        }
    }

    private fun done(message: String?) = runOnUiThread {
        Log.w(Player.TAG, "Launch failed: $message")
        Store(this).lastProblem = "Launch: " + (message ?: "unknown error")
        Toast.makeText(this, message ?: "Could not start the game.", Toast.LENGTH_LONG).show()
        finish()
    }
}
