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

// Frames drawn by Web Workers (frame-worker.js, FrameWorker.cs), for ParallelFrames.cs: the video
// in pieces of a few frames, each to the next worker free, the frames put back in order and encoded.
// Few frames are held at once: a worker gets a piece only while the encoder is not far behind.
const PIECE = 3;
const parallels = new Map();

// A worker of frame-worker.js, which may be of another origin than the page (the website serves the
// page and the app's files come from distrib or the mirror): a worker's script must be the page's,
// so a blob of the page's imports it (a module may come from anywhere that allows it, as main.js does).
function frameWorker() {
  const script = new URL('./frame-worker.js', import.meta.url).href;
  const blob = URL.createObjectURL(new Blob([`import ${JSON.stringify(script)};`], { type: 'text/javascript' }));
  return { worker: new Worker(blob, { type: 'module' }), blob };
}

function encodeFrame(e, bytes, index, rgba) {
  const frame = new VideoFrame(bytes, {
    format: rgba ? 'RGBA' : 'BGRA', codedWidth: e.width, codedHeight: e.height,
    timestamp: Math.round(index * 1e6 / e.fps), duration: Math.round(1e6 / e.fps),
  });
  e.encoder.encode(frame, { keyFrame: index % (e.fps * 2) === 0 });
  frame.close();
}

export const video = {
  // The computer, for the number of workers (VideoWorkers.cs): its cores, and its memory in MB as
  // the browser says it (0: it does not).
  cores: () => navigator.hardwareConcurrency || 2,
  memory: () => (navigator.deviceMemory || 0) * 1024,
  parallel(id, request, frames, count, fontView, progress) {
    const font = fontView.slice();   // .NET's memory: copied before anything else runs
    const e = encoders.get(id);
    if (!e) return Promise.resolve('No such encoder.');
    return new Promise(resolve => {
      const run = { workers: [], done: false };
      parallels.set(id, run);
      // A failure before any frame was encoded can be drawn on the page instead ("fallback: …").
      const finish = message => {
        if (run.done) return;
        run.done = true;
        parallels.delete(id);
        clearTimeout(run.timeout);
        for (const w of run.workers) { w.worker.terminate(); URL.revokeObjectURL(w.blob); }
        progress(next);
        resolve(message && next === 0 && message !== 'Stopped.' ? 'fallback: ' + message : message);
      };
      // Workers that have not started in a minute (a script that did not load) are not waited for.
      run.timeout = setTimeout(() => {
        if (!run.workers.some(w => w.prepared)) finish('The workers did not start.');
      }, 60000);
      run.stop = () => finish('Stopped.');
      const pieces = Math.ceil(frames / PIECE);
      const ahead = count * PIECE * 2;
      const ready = new Map();
      let nextPiece = 0, next = 0, encoding = false, told = 0;
      const give = () => {
        for (const w of run.workers) {
          if (run.done || w.busy || !w.prepared || nextPiece >= pieces || nextPiece * PIECE - next >= ahead) continue;
          const first = nextPiece++ * PIECE;
          w.busy = true;
          w.worker.postMessage({ type: 'draw', first, count: Math.min(PIECE, frames - first) });
        }
      };
      const encode = async () => {
        if (encoding) return;
        encoding = true;
        try {
          while (!run.done && ready.has(next)) {
            if (e.error) return finish(String(e.error.message ?? e.error));
            const { pixels, rgba } = ready.get(next);
            ready.delete(next);
            encodeFrame(e, pixels, next, rgba);
            next++;
            if (performance.now() - told > 100) { told = performance.now(); progress(next); }
            while (!run.done && !e.error && e.encoder.encodeQueueSize > 6)
              await new Promise(r => e.encoder.addEventListener('dequeue', r, { once: true }));
            give();
          }
          if (next === frames) finish('');
        } finally {
          encoding = false;
        }
      };
      for (let i = 0; i < count; i++) {
        const w = { ...frameWorker(), busy: false, prepared: false, rgba: true };
        run.workers.push(w);
        w.worker.onerror = ev => finish('A worker failed: ' + (ev.message || 'it could not start'));
        w.worker.onmessage = ({ data }) => {
          if (run.done) return;
          if (data.type === 'prepared') {
            if (data.error) return finish(data.error);
            w.prepared = true;
            w.rgba = data.rgba;
            give();
          } else if (data.type === 'frame') {
            ready.set(data.frame, { pixels: data.pixels, rgba: w.rgba });
            encode();
          } else if (data.type === 'drawn') {
            w.busy = false;
            give();
          } else if (data.type === 'error') {
            finish(data.message);
          }
        };
        w.worker.postMessage({ type: 'prepare', request, font });
      }
    });
  },
  stopParallel(id) {
    parallels.get(id)?.stop();
  },
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
