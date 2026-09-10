plugins {
    id("com.android.application")
}

android {
    namespace = "com.bwa3d.codextv"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.bwa3d.codextv"
        minSdk = 26
        targetSdk = 36
        versionCode = 3
        versionName = "0.1.2"
    }

    sourceSets {
        getByName("main") {
            java.srcDirs("../../android/tvsatellite/src/main/java")
            res.srcDirs("../../android/tvsatellite/src/main/res")
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro"
            )
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
}
