# Keep Vosk/JNA bridge classes used through native/JNA lookups.
-keep class org.vosk.** { *; }
-keep class com.sun.jna.** { *; }
-dontwarn com.sun.jna.**
