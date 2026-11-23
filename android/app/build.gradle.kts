plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

// Release signing: values come from ~/.gradle/gradle.properties or environment variables
// (SIDELIGHT_STORE_FILE, SIDELIGHT_STORE_PASSWORD, SIDELIGHT_KEY_ALIAS, SIDELIGHT_KEY_PASSWORD).
// Without them, release builds fall back to the debug key so the project still builds anywhere.
fun signingValue(name: String): String? =
    (project.findProperty(name) as String?) ?: System.getenv(name)

android {
    namespace = "io.github.devpatel04.sidelight"
    compileSdk = 35

    defaultConfig {
        applicationId = "io.github.devpatel04.sidelight"
        minSdk = 21 // Android 5.0; see CONTRIBUTING.md before changing it
        targetSdk = 35
        versionCode = 48
        versionName = "0.9.27"
    }

    signingConfigs {
        val storeFile = signingValue("SIDELIGHT_STORE_FILE")
        if (storeFile != null && file(storeFile).exists()) {
            create("release") {
                this.storeFile = file(storeFile)
                storePassword = signingValue("SIDELIGHT_STORE_PASSWORD")
                keyAlias = signingValue("SIDELIGHT_KEY_ALIAS")
                keyPassword = signingValue("SIDELIGHT_KEY_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }

    buildFeatures {
        buildConfig = true
    }

    lint {
        // Unsupported framework calls can crash old phones before the panel opens.
        fatal += "NewApi"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}

dependencies {
    implementation("androidx.webkit:webkit:1.12.1")
    testImplementation("junit:junit:4.13.2")
    // android.jar's org.json is a stub on the JVM; the real one lets the unit tests build and read JSON
    testImplementation("org.json:json:20240303")
}
