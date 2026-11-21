// ffmpeg.wasm for WasmFfmpeg (C#): loaded on first use (the core is about 31 MB, from jsDelivr),
// one job at a time. Files go between .NET's file system (globalThis.ourtaikoedit.FS) and
// ffmpeg's own; the C# side has already put the names in the arguments.
const CORE = 'https://cdn.jsdelivr.net/npm/@ffmpeg/core@0.12.10/dist/umd/';

let ff = null, loading = null, queue = Promise.resolve();
let lines = [], onTime = null;
const net = () => globalThis.ourtaikoedit.FS;

async function blobUrl(url, type) {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`${url}: ${response.status}`);
  return URL.createObjectURL(new Blob([await response.arrayBuffer()], { type }));
}

async function ensure() {
  if (ff) return ff;
  loading ??= (async () => {
    const f = new globalThis.FFmpegWASM.FFmpeg();
    f.on('log', ({ message }) => {
      lines.push(message);
      if (onTime) {
        const m = /time=(-?\d+):(\d+):([\d.]+)/.exec(message);
        if (m) onTime(Number(m[1]) * 3600 + Number(m[2]) * 60 + Number(m[3]));
      }
    });
    await f.load({
      coreURL: await blobUrl(CORE + 'ffmpeg-core.js', 'text/javascript'),
      wasmURL: await blobUrl(CORE + 'ffmpeg-core.wasm', 'application/wasm'),
    });
    ff = f;
    return f;
  })().finally(() => { loading = null; });
  return loading;
}

function serial(job) {
  const run = queue.then(job, job);
  queue = run.catch(() => {});
  return run;
}

export const ffmpeg = {
  // inputs: [[path in .NET, name in ffmpeg]]; output: [name in ffmpeg, path in .NET] or null.
  // What ffmpeg said, and its exit code, as JSON.
  run: (inputs, args, output, progress) => serial(async () => {
    const f = await ensure();
    inputs = JSON.parse(inputs);
    args = JSON.parse(args);
    output = output ? JSON.parse(output) : null;
    try {
      for (const [path, name] of inputs) await f.writeFile(name, net().readFile(path));
      lines = [];
      onTime = progress;
      let code;
      try { code = await f.exec(args); } finally { onTime = null; }
      if (output && code === 0) net().writeFile(output[1], await f.readFile(output[0]));
      return JSON.stringify({ code, log: lines.join('\n') });
    } finally {
      for (const name of [...inputs.map(i => i[1]), ...(output ? [output[0]] : [])]) {
        try { await f.deleteFile(name); } catch { }
      }
    }
  }),
  // Stops what runs (ffmpeg.wasm can only be stopped by ending it; it loads again when next asked).
  cancel() {
    if (!ff) return;
    ff.terminate();
    ff = null;
  },
};
