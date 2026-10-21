package org.retrobridge.android

import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import android.os.Environment
import android.util.Log

// Periodically uploads saves made since the last launch, because Android gives no signal when RetroArch exits.
class SyncJobService : JobService() {
    override fun onStartJob(params: JobParameters): Boolean {
        val pairing = Store(this).pairing ?: return false
        if (!Environment.isExternalStorageManager()) return false
        Thread {
            try { Syncer(this, Api(pairing.server, pairing.deviceId, pairing.token)).syncAll() }
            catch (e: Exception) { Log.w(Player.TAG, "Background save sync failed: ${e.message}") }
            finally { jobFinished(params, false) }
        }.start()
        return true
    }

    override fun onStopJob(params: JobParameters) = true

    companion object {
        private const val JOB_ID = 4711

        fun schedule(context: Context) {
            val job = JobInfo.Builder(JOB_ID, ComponentName(context, SyncJobService::class.java))
                .setRequiredNetworkType(JobInfo.NETWORK_TYPE_ANY)
                .setPeriodic(15 * 60 * 1000L)
                .setPersisted(true)
                .build()
            // Background sync is a convenience; scheduling problems must never break pairing or launching.
            try { context.getSystemService(JobScheduler::class.java).schedule(job) }
            catch (e: RuntimeException) { Log.w(Player.TAG, "Could not schedule background save sync: ${e.message}") }
        }
    }
}
