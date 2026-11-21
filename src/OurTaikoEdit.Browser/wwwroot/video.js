// A preview video's frames through the browser's H.264 encoder (WebCodecs), for WebCodecsSink
// (C#): Annex B chunks one after another, written as a raw .h264 file into .NET's file system,
// where ffmpeg.wasm puts it in an MP4 with the sound.
const encoders = new Map();
let nextId = 1;

// The first H.264 profile and level the browser takes for the size (High, then Main, then Baseline).
async function config(width, height, fps) {
  const bitrate = Math.round(Math.min(40e6, Math.max(2e6, width * height * fps * 0.07)));
  for (const codec of ['avc1.640034', 'avc1.640033', 'avc1.640028', 'avc1.4d0034', 'avc1.4d0028', 'avc1.42e034', 'avc1.42e028']) {
    const c = { codec, width, height, bitrate, framerate: fps, avc: { format: 'annexb' }, latencyMode: 'quality' };
    try { if ((await VideoEncoder.isConfigSupported(c)).supported) return c; } catch { }
  }
  return null;
}

export const video = {
  supported: () => typeof VideoEncoder === 'function' && typeof VideoFrame === 'function',
  async start(width, height, fps, rgba) {
    const c = await config(width, height, fps);
    if (!c) throw new Error(`This browser cannot encode H.264 at ${width}×${height}.`);
    const e = { chunks: [], size: 0, error: null, fps, width, height, format: rgba ? 'RGBA' : 'BGRA' };
    e.encoder = new VideoEncoder({
      output: chunk => { const b = new Uint8Array(chunk.byteLength); chunk.copyTo(b); e.chunks.push(b); e.size += b.length; },
      error: err => { e.error = err; },
    });
    e.encoder.configure(c);
    const id = nextId++;
    encoders.set(id, e);
    return id;
  },
  // A frame (its bytes copied at once: the buffer is the caller's again on return). False once the
  // encoder has failed.
  frame(id, bytes, index) {
    const e = encoders.get(id);
    if (!e || e.error) return false;
    const frame = new VideoFrame(bytes.slice(), {
      format: e.format, codedWidth: e.width, codedHeight: e.height,
      timestamp: Math.round(index * 1e6 / e.fps), duration: Math.round(1e6 / e.fps),
    });
    e.encoder.encode(frame, { keyFrame: index % (e.fps * 2) === 0 });
    frame.close();
    return true;
  },
  // Waits while more than a few frames are waiting to be encoded.
  async drain(id) {
    const e = encoders.get(id);
    while (e && !e.error && e.encoder.encodeQueueSize > 4)
      await new Promise(r => e.encoder.addEventListener('dequeue', r, { once: true }));
  },
  // The encoded stream written to `path` in .NET's file system.
  async finish(id, path) {
    const e = encoders.get(id);
    if (!e) throw new Error('No such encoder.');
    try {
      await e.encoder.flush();
      if (e.error) throw e.error;
      const all = new Uint8Array(e.size);
      let at = 0;
      for (const c of e.chunks) { all.set(c, at); at += c.length; }
      e.chunks = [];
      globalThis.ourtaikoedit.FS.writeFile(path, all);
    } finally {
      video.abort(id);
    }
  },
  abort(id) {
    const e = encoders.get(id);
    if (!e) return;
    encoders.delete(id);
    try { e.encoder.close(); } catch { }
  },
};
