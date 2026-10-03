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
          function readMicLevel() {
            var v = window.__etMicLevel;
            if (typeof v !== 'number') {
              try { v = window.top && window.top.__etMicLevel; } catch (e) { v = 0; }
            }
            if (typeof v !== 'number' || !isFinite(v)) return 0;
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
          }
          window.__etReadMicLevel = readMicLevel;
          function stopMeter() {
            var meter = window.__etMeter;
            window.__etMeter = null;
            try { if (window.__etMeterRaf) cancelAnimationFrame(window.__etMeterRaf); } catch (e) {}
            window.__etMeterRaf = 0;
            try { if (meter && meter.ctx) meter.ctx.close(); } catch (e) {}
            flag('__etMicLevel', 0);
          }
          window.__etStopMeter = stopMeter;
          function ensureMeter(stream) {
            if (!stream) return;
            try {
              var AC = window.AudioContext || window.webkitAudioContext;
              if (!AC) return;
              if (!window.__etMeter) {
                var ctx = new AC();
                var analyser = ctx.createAnalyser();
                analyser.fftSize = 1024;
                analyser.smoothingTimeConstant = 0.32;
                var time = new Uint8Array(analyser.fftSize);
                function tick() {
                  if (!window.__etMeter || window.__etMeter.ctx !== ctx) return;
                  try { if (ctx.state === 'suspended') ctx.resume(); } catch (e) {}
                  try {
                    analyser.getByteTimeDomainData(time);
                    var sum = 0;
                    for (var i = 0; i < time.length; i++) {
                      var s = (time[i] - 128) / 128;
                      sum += s * s;
                    }
                    var rms = Math.sqrt(sum / time.length);
                    var floor = 0.012;
                    var level = 0;
                    if (rms > floor) {
                      var n = Math.max(0, Math.min(1, (rms - floor) / 0.16));
                      level = Math.pow(n, 0.5);
                    }
                    flag('__etMicLevel', level);
                  } catch (e) {
                    flag('__etMicLevel', 0);
                  }
                  window.__etMeterRaf = requestAnimationFrame(tick);
                }
                window.__etMeter = { ctx: ctx, analyser: analyser };
                try { ctx.resume(); } catch (e) {}
                tick();
              }
              if (stream.__etMeterCtx === window.__etMeter.ctx) return;
              var src = window.__etMeter.ctx.createMediaStreamSource(stream);
              src.connect(window.__etMeter.analyser);
              stream.__etMeterCtx = window.__etMeter.ctx;
              try { window.__etMeter.ctx.resume(); } catch (e) {}
            } catch (e) {}
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
            try { ensureMeter(stream); } catch (e) {}
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
          try {
            var existing = window.__etStreams || [];
            for (var si = 0; si < existing.length; si++) ensureMeter(existing[si]);
          } catch (e) {}
        })();
        """;
}
