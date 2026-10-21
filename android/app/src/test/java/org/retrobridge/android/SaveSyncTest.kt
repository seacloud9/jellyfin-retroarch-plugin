package org.retrobridge.android

import org.junit.Assert.assertEquals
import org.junit.Test

class SaveSyncTest {
    private fun server(sha: String, revision: Long) = ServerSave("nes-x.srm", sha, 8192, revision)

    @Test fun matchesTheDesktopDecisions() {
        assertEquals(SyncAction.NONE, SaveSync.decide(null, null, null))
        assertEquals(SyncAction.DOWNLOAD, SaveSync.decide(null, null, server("s1", 1)))
        assertEquals(SyncAction.UPLOAD, SaveSync.decide("l1", null, null))
        assertEquals(SyncAction.NONE, SaveSync.decide("s1", null, server("s1", 1)))
        assertEquals(SyncAction.DOWNLOAD, SaveSync.decide("a", SyncedSave("a", 1), server("b", 2)))
        assertEquals(SyncAction.UPLOAD, SaveSync.decide("b", SyncedSave("a", 1), server("a", 1)))
        assertEquals(SyncAction.CONFLICT, SaveSync.decide("b", SyncedSave("a", 1), server("c", 2)))
        assertEquals(SyncAction.CONFLICT, SaveSync.decide("l1", null, server("s1", 1)))
    }

    @Test fun onlyBatterySavesForTheGame() {
        assert(SaveSync.isSyncedName("nes-abc", "nes-abc.srm"))
        listOf("nes-abc.state", "nes-other.srm", "../nes-abc.srm").forEach { assert(!SaveSync.isSyncedName("nes-abc", it)) { it } }
    }
}
