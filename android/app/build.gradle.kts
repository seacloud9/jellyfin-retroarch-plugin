plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "org.retrobridge.android"
    compileSdk = 36
    defaultConfig {
        applicationId = "org.retrobridge.android"
        minSdk = 30
        targetSdk = 36
        versionCode = 3
        versionName = "0.2.1"
    }
    buildFeatures { buildConfig = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin { compilerOptions { jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17) } }

dependencies {
    testImplementation("junit:junit:4.13.2")
    // org.json is an Android framework class; unit tests on the JVM need a real implementation.
    testImplementation("org.json:json:20250517")
}
