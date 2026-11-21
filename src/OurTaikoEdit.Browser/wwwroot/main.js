import { dotnet } from './_framework/dotnet.js';
import { audio } from './audio.js';
import { ffmpeg } from './ffmpeg-bridge.js';
import { video } from './video.js';

// The app's files: .NET's System.IO works on Emscripten's file system, in memory. The home folder
// (projects, settings, the app's data) is mounted on IndexedDB and written back to it whenever it
// changed, so it is there next time; everything else (/tmp: files picked to convert) is not kept.
const HOME = '/home/web_user';

// The editor in a website's page: see listen() below.
let nextRequest = 1;
const waiting = new Map();
const embedded = parent !== window && new URLSearchParams(location.search).has('embed');
const embed = {
  save: tja => new Promise(resolve => {
    const request = nextRequest++;
    waiting.set(request, resolve);
    tell({ type: 'save', request, tja });
  }),
  leave: () => tell({ type: 'leave' }),
  dirty: dirty => tell({ type: 'dirty', dirty }),
};

const runtime = await dotnet
  .withDiagnosticTracing(false)
  .withEnvironmentVariable('HOME', HOME)
  .withEnvironmentVariable('XDG_CONFIG_HOME', HOME + '/.config')
  .withApplicationArgumentsFromQuery()
  .create();

const FS = runtime.Module.FS;
// The file system for the bridges (ffmpeg-bridge.js, video.js), and to look into from the console.
globalThis.ourtaikoedit = { FS, save: () => sync(false) };
FS.mkdirTree(HOME);
FS.mount(FS.filesystems.IDBFS, { autoPersist: false }, HOME);
const sync = populate => new Promise((resolve, reject) => FS.syncfs(populate, e => e ? reject(e) : resolve()));
await sync(true);

let saving = null, again = false;
async function save() {
  if (saving) { again = true; return saving; }
  saving = (async () => {
    do { again = false; await sync(false); } while (again);
  })().catch(e => console.error('Saving to IndexedDB failed', e)).finally(() => { saving = null; });
  return saving;
}
// Written a little after the app writes (it asks), and when the page is left.
setInterval(save, 5000);
addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') save(); });
addEventListener('pagehide', save);

runtime.setModuleImports('main.js', {
  audio,
  ffmpeg,
  video,
  storage: {
    save,
    // Asks the browser not to clear the app's storage when space is short.
    persist: async () => (await navigator.storage?.persist?.()) ?? false,
  },
  files: {
    // A file handed to the person: saved where they choose (where the browser can), else downloaded.
    offer: async (bytes, name, mime) => {
      const blob = new Blob([bytes.slice()], { type: mime || 'application/octet-stream' });
      const a = document.createElement('a');
      a.href = URL.createObjectURL(blob);
      a.download = name;
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(a.href), 60000);
    },
  },
  page: {
    loaded: () => document.querySelector('.splash')?.remove(),
  },
  embed,
});

const config = runtime.getConfig();
await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
if (embedded) await listen();

// The editor in a website's page (?embed, in an iframe of the same origin): the page hands it a
// chart and stores what is saved (Embed.cs, EmbedHost). Messages, each {source, type, …}:
//   to the page   ready; opened | open-failed {message}; save {request, tja};
//                 dirty {dirty}; leave
//   from the page open {chart: {id, name, tjaName, tja, audioName, audio (ArrayBuffer|null)}};
//                 save-result {request, saved, message?, line?}
function tell(message) {
  parent.postMessage({ source: 'ourtaikoedit', ...message }, location.origin);
}

async function listen() {
  const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
  const host = exports.OurTaikoEdit.Browser.Embed;
  addEventListener('message', event => {
    if (event.source !== parent || event.origin !== location.origin) return;
    const message = event.data;
    if (!message || message.source !== 'ourtaiko-net') return;
    if (message.type === 'open') {
      const chart = message.chart;
      try {
        const audioPath = host.Prepare(String(chart.id), String(chart.audioName || 'audio'));
        if (chart.audio) FS.writeFile(audioPath, new Uint8Array(chart.audio));
        const failed = host.Open(String(chart.id), String(chart.name || ''), String(chart.tjaName || 'chart.tja'), String(chart.tja));
        tell(failed ? { type: 'open-failed', message: failed } : { type: 'opened' });
      } catch (e) {
        tell({ type: 'open-failed', message: String(e?.message ?? e) });
      }
    } else if (message.type === 'save-result') {
      const resolve = waiting.get(message.request);
      waiting.delete(message.request);
      resolve?.(JSON.stringify({ saved: !!message.saved, message: message.message ?? null, line: message.line ?? null }));
    }
  });
  tell({ type: 'ready' });
}
