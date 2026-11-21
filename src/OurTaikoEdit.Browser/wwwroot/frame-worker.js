// A Web Worker drawing a preview video's frames (FrameWorker.cs): the app's own .NET, started here
// without its page (Main is not run), drawing with Skia into memory. Started by video.js from a blob
// that imports this file; what it imports is found next to it. video.js (parallel) hands it the
// video, then pieces of it; each frame goes back as RGBA or BGRA bytes (moved, not copied).
//   to the worker    prepare { request, font }; draw { first, count }
//   from the worker  prepared { error, rgba }; frame { frame, pixels }; drawn { first }; error { message }
import { dotnet } from './_framework/dotnet.js';

const api = (async () => {
  const runtime = await dotnet.withDiagnosticTracing(false).create();
  const exports = await runtime.getAssemblyExports('OurTaikoEdit.Browser.dll');
  return exports.OurTaikoEdit.Browser.FrameWorker;
})();

onmessage = async ({ data }) => {
  try {
    const w = await api;
    if (data.type === 'prepare') {
      w.Start(data.font && data.font.length ? data.font : null);
      const error = w.Prepare(data.request);
      postMessage({ type: 'prepared', error, rgba: error ? true : w.Rgba() });
    } else if (data.type === 'draw') {
      for (let f = data.first; f < data.first + data.count; f++) {
        const pixels = w.Draw(f);
        postMessage({ type: 'frame', frame: f, pixels }, [pixels.buffer]);
      }
      postMessage({ type: 'drawn', first: data.first });
    }
  } catch (e) {
    postMessage({ type: 'error', message: String(e?.message ?? e) });
  }
};
