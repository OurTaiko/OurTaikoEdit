// The song and the hit sounds for WebAudioPlayer (C#): one AudioContext; the song is one
// AudioBuffer played from an offset, and every sound is started on the context's clock, so a hit
// asked for a time of the song starts on that very sample of it.

let ctx = null;
const players = new Map();
let nextId = 1;

function context() {
  ctx ??= new AudioContext({ latencyHint: 'interactive' });
  return ctx;
}

// Interleaved float samples (as bytes) to a buffer of 1 or 2 channels.
function buffer(bytes, channels, rate) {
  const c = context();
  const samples = new Float32Array(bytes.slice().buffer);
  const frames = Math.floor(samples.length / channels);
  const out = c.createBuffer(Math.min(channels, 2), Math.max(1, frames), rate);
  if (channels === 1) out.copyToChannel(samples.subarray(0, frames), 0);
  else {
    const l = new Float32Array(frames), r = new Float32Array(frames);
    for (let f = 0, i = 0; f < frames; f++, i += channels) { l[f] = samples[i]; r[f] = samples[i + 1]; }
    out.copyToChannel(l, 0);
    out.copyToChannel(r, 1);
  }
  return out;
}

export const audio = {
  // A player for a song; its id.
  open(bytes, channels, rate) {
    const c = context();
    const gain = c.createGain();
    gain.connect(c.destination);
    const p = { song: buffer(bytes, channels, rate), gain, source: null, startedAt: 0, from: 0, sounds: {}, scheduled: [] };
    const id = nextId++;
    players.set(id, p);
    return id;
  },
  close(id) {
    const p = players.get(id);
    if (!p) return;
    audio.pause(id);
    p.gain.disconnect();
    players.delete(id);
  },
  // A sound by name (don, ka, balloon, kusudama).
  sound(id, name, bytes, channels, rate) {
    const p = players.get(id);
    if (p) p.sounds[name] = buffer(bytes, channels, rate);
  },
  duration: id => players.get(id)?.song.duration ?? 0,
  playing: id => !!players.get(id)?.source,
  // Where the song is (seconds): from where it was started, by the context's clock.
  position(id) {
    const p = players.get(id);
    if (!p) return 0;
    if (!p.source) return p.from;
    return Math.min(p.song.duration, p.from + Math.max(0, ctx.currentTime - p.startedAt));
  },
  volume(id, v) { const p = players.get(id); if (p) p.gain.gain.value = v; },
  play(id, at) {
    const p = players.get(id);
    if (!p || p.source) return;
    const c = context();
    if (c.state === 'suspended') c.resume();
    const s = c.createBufferSource();
    s.buffer = p.song;
    s.connect(p.gain);
    p.from = Math.max(0, Math.min(at, p.song.duration));
    p.startedAt = c.currentTime;
    s.start(p.startedAt, p.from);
    s.onended = () => { if (p.source === s) { p.from = p.song.duration; p.source = null; } };
    p.source = s;
  },
  pause(id) {
    const p = players.get(id);
    if (!p || !p.source) return;
    p.from = audio.position(id);
    const s = p.source;
    p.source = null;
    s.onended = null;
    s.stop();
    audio.cancel(id);
  },
  // A sound now (songTime < 0), or on a time of the song still to come while it plays.
  hit(id, name, gain, songTime) {
    const p = players.get(id);
    const b = p?.sounds[name];
    if (!b) return;
    const c = context();
    if (c.state === 'suspended') c.resume();
    let when = 0;
    if (songTime >= 0 && p.source) when = p.startedAt + (songTime - p.from);
    if (when < c.currentTime) when = 0;
    const s = c.createBufferSource();
    s.buffer = b;
    const g = c.createGain();
    g.gain.value = gain;
    s.connect(g);
    g.connect(c.destination);
    s.start(when);
    if (when > 0) {
      const entry = { s, when };
      p.scheduled.push(entry);
      s.onended = () => { const i = p.scheduled.indexOf(entry); if (i >= 0) p.scheduled.splice(i, 1); };
    }
  },
  // Sounds asked for ahead that have not started are dropped.
  cancel(id) {
    const p = players.get(id);
    if (!p) return;
    const now = ctx?.currentTime ?? 0;
    for (const e of p.scheduled) if (e.when > now) { try { e.s.stop(); } catch { } }
    p.scheduled = p.scheduled.filter(e => e.when <= now);
  },
  // Seconds from a sample being scheduled to it being heard, where the browser says.
  latency: () => ctx ? (ctx.outputLatency || 0) + (ctx.baseLatency || 0) : 0,
};
