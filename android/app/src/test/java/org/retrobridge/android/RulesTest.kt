package org.retrobridge.android

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class RulesTest {
    private val ticket = "AbCdEfGhIjKlMnOpQrStUv-_0123456789"

    @Test fun parsesOnlyAnOpaqueTicket() = assertEquals(ticket, Rules.parseTicket("retrobridge://launch?ticket=$ticket"))

    @Test fun rejectsHostileLinks() {
        listOf(
            "retrobridge://launch?ticket=short",
            "retrobridge://launch?ticket=$ticket&server=https://evil",
            "retrobridge://launch?server=https://evil&ticket=$ticket",
            "retrobridge://launch?ticket=$ticket%26core=x",
            "retrobridge://launch/../x?ticket=$ticket",
            "retrobridge://evil?ticket=$ticket",
            "retrobridge://user@launch?ticket=$ticket",
            "retrobridge://launch:99?ticket=$ticket",
            "retrobridge://launch?ticket=$ticket#x",
            "https://launch?ticket=$ticket",
            "retrobridge://launch?ticket=${ticket}é",
            "not a url",
        ).forEach { assertNull(it, Rules.parseTicket(it)) }
    }

    @Test fun serverRules() {
        assertEquals("https://jf.example/base/", Rules.normalizeServer("https://jf.example/base"))
        assertEquals("http://127.0.0.1:8097/", Rules.normalizeServer("http://127.0.0.1:8097"))
        listOf("http://jf.example/", "https://user:pw@jf.example/", "https://jf.example/?x=1", "ftp://jf.example/").forEach {
            assertThrows(it, IllegalArgumentException::class.java) { Rules.normalizeServer(it) }
        }
    }

    @Test fun gameIdentityMustBeConsistent() {
        val sha = "a".repeat(64)
        val game = Game("nes-$sha", "Fixture", "nes", ".nes", 10, sha)
        assert(Rules.validGame(game))
        assert(!Rules.validGame(game.copy(platform = "gb")))
        assert(!Rules.validGame(game.copy(id = "nes-../../x")))
        assert(!Rules.validGame(game.copy(size = Rules.MAX_CONTENT + 1)))
    }
}
