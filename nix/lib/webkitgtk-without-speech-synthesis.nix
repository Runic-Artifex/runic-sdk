# WebKitGTK without speech synthesis (the Web Speech API's speechSynthesis), for buildRunicApplication's
# withSpeechSynthesis = false. Speech synthesis links flite, which with the audio libraries only it needs adds about
# 70 MiB to an application's closure. nixpkgs' binary cache has no such build: WebKitGTK then builds from source,
# which takes hours and needs the big-parallel system feature.
{
  lib,
  flite,
  webkitgtk_4_1,
  webkitgtk_6_0,
}:
let
  withoutSpeechSynthesis =
    webkit:
    webkit.overrideAttrs (old: {
      buildInputs = lib.remove flite old.buildInputs;
      cmakeFlags = old.cmakeFlags ++ [ "-DENABLE_SPEECH_SYNTHESIS=OFF" ];
    });
in
{
  webkitgtk_4_1 = withoutSpeechSynthesis webkitgtk_4_1;
  webkitgtk_6_0 = withoutSpeechSynthesis webkitgtk_6_0;
}
