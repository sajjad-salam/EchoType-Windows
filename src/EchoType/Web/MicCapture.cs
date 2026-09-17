namespace EchoType.Web;

/// <summary>
/// Page-world hooks that notice when ChatGPT or Gemini silently ends the
/// microphone. Injected early (document created) and again with the driver
/// script so a reload still sees live-track state.
/// </summary>
internal static class MicCapture {

    public const string HookScript = """
        (function () {
          if (!window.__etStreams) window.__etStreams = [];
          function flag(name, value) {
            try { window[name] = value; } catch (e) {}
            try { if (window.top && window.top !== window) window.top[name] = value; } catch (e) {}
          }
          function refreshMicLive() {
            var streams = window.__etStreams || [];
            if (!streams.length) {
              return !!window.__etMicLive;
            }
            var live = false;
            for (var i = 0; i < streams.length; i++) {
              try {
                var tracks = streams[i].getTracks ? streams[i].getTracks() : [];
                for (var j = 0; j < tracks.length; j++) {
                  if (tracks[j].kind === 'audio' && tracks[j].readyState === 'live') live = true;
                }
              } catch (e) {}
            }
            flag('__etMicLive', live);
            if (!live) {
              flag('__etGUM', 'ended');
            }
            return live;
          }
          window.__etRefreshMic = refreshMicLive;
          function attach(stream) {
            if (!stream) return;
            window.__etStreams.push(stream);
            var onDead = function () { refreshMicLive(); };
            try {
              var tracks = stream.getTracks();
              for (var i = 0; i < tracks.length; i++) {
                tracks[i].addEventListener('ended', onDead);
                tracks[i].addEventListener('mute', onDead);
              }
              if (typeof stream.addEventListener === 'function') {
                stream.addEventListener('inactive', onDead);
                stream.addEventListener('removetrack', onDead);
              }
            } catch (e) {}
            flag('__etGUM', 'ok');
            flag('__etMicLive', true);
            refreshMicLive();
          }
          window.__etAttachStream = attach;
          try {
            if (navigator.mediaDevices && navigator.mediaDevices.__etGumWrap !== 2) {
              var orig = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
              navigator.mediaDevices.getUserMedia = function (c) {
                flag('__etGUM', 'requested');
                flag('__etMicLive', false);
                return orig(c).then(function (s) {
                  flag('__etGUM', 'ok');
                  flag('__etMicLive', true);
                  try { attach(s); } catch (e) {}
                  return s;
                }).catch(function (e) {
                  flag('__etGUM', 'err:' + e.name + ':' + e.message);
                  flag('__etMicLive', false);
                  throw e;
                });
              };
              navigator.mediaDevices.__etWrapped = true;
              navigator.mediaDevices.__etGumWrap = 2;
            }
          } catch (e) {}
          try {
            if (typeof MediaStreamTrack !== 'undefined' && !MediaStreamTrack.prototype.__etStopWrapped) {
              var origStop = MediaStreamTrack.prototype.stop;
              MediaStreamTrack.prototype.stop = function () {
                try {
                  return origStop.apply(this, arguments);
                } finally {
                  try {
                    if (this.kind === 'audio') setTimeout(refreshMicLive, 0);
                  } catch (e) {}
                }
              };
              MediaStreamTrack.prototype.__etStopWrapped = true;
            }
          } catch (e) {}
        })();
        """;
}
