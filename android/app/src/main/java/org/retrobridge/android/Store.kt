package org.retrobridge.android

import android.content.Context
import java.security.SecureRandom

data class Pairing(val server: String, val deviceId: String, val token: String)

// Pairing lives in app-private storage, which other apps cannot read.
class Store(context: Context) {
    private val prefs = context.getSharedPreferences("retrobridge", Context.MODE_PRIVATE)

    val pairing: Pairing?
        get() {
            val server = prefs.getString("server", null) ?: return null
            val device = prefs.getString("device", null) ?: return null
            val token = prefs.getString("token", null) ?: return null
            return Pairing(server, device, token)
        }

    fun save(pairing: Pairing) = prefs.edit().putString("server", pairing.server).putString("device", pairing.deviceId).putString("token", pairing.token).commit()
    fun clear() = prefs.edit().clear().commit()

    companion object {
        fun newDeviceId() = "retrobridge-" + ByteArray(16).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it) }
    }
}
