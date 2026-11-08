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
        assert(!Rules.validGame(game.copy(size = Rules.CARTRIDGE + 1)))
        // Discs may be larger than cartridges.
        val disc = Game("psx-$sha", "Disc", "psx", ".chd", Rules.CARTRIDGE * 8, sha)
        assert(Rules.validGame(disc))
        assert(!Rules.validGame(disc.copy(size = Rules.DISC + 1)))
        assert(!Rules.validGame(disc.copy(extension = ".cue")))
    }

    @Test fun platformTableMatchesTheServer() {
        assertEquals(listOf("nes", "snes", "n64", "gb", "gbc", "gba", "genesis", "psx", "nova64"), Rules.systems.map { it.id })
        assertEquals("snes", Rules.platforms[".smc"])
        assertEquals("genesis", Rules.platforms[".gen"])
        assertNull(Rules.platforms[".bin"])
        // Every preferred core has a save folder, so a first download lands where RetroArch looks for it.
        Rules.systems.flatMap { it.cores }.forEach { assert(SaveSync.coreFolders.containsKey(it)) { it } }
    }

    @Test fun choosesTheBestInstalledCore() {
        val installed = listOf("/data/user/0/com.retroarch/cores/fceumm_libretro_android.so", "mgba_libretro_android.so", "readme.txt")
        assertEquals("/data/user/0/com.retroarch/cores/fceumm_libretro_android.so", Rules.chooseCore("nes", installed))
        assertEquals("mgba_libretro_android.so", Rules.chooseCore("gbc", installed))
        assertEquals("nestopia_libretro_android.so", Rules.chooseCore("nes", installed + "nestopia_libretro_android.so"))
        assertNull(Rules.chooseCore("gb", listOf("fceumm_libretro_android.so")))
        assertNull(Rules.chooseCore("snes", installed))
        assertEquals("mgba_libretro_android.so", Rules.chooseCore("gba", installed))
        assertEquals("mupen64plus_next_gles3_libretro_android.so", Rules.chooseCore("n64", listOf("parallel_n64_libretro_android.so", "mupen64plus_next_gles3_libretro_android.so")))
        assertEquals("pcsx_rearmed_libretro_android.so", Rules.chooseCore("psx", listOf("mednafen_psx_libretro_android.so", "pcsx_rearmed_libretro_android.so")))
        assertNull(Rules.chooseCore("nes", listOf("nestopia_libretro_android.so.partial")))
    }

    @Test fun coreNamesCannotEscapeTheCoresFolder() {
        assert(Rules.isCoreFileName("nestopia_libretro_android.so"))
        listOf("../evil_libretro_android.so", "nestopia_libretro_android.so/x", "Nestopia_libretro_android.so", "evil.so", "a b_libretro.so")
            .forEach { assert(!Rules.isCoreFileName(it)) { it } }
    }
}
