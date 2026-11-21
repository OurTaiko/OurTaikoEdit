import { dotnet } from './_framework/dotnet.js';
import { audio } from './audio.js';
import { ffmpeg } from './ffmpeg-bridge.js';
import { video } from './video.js';

// The app's files: .NET's System.IO works on Emscripten's file system, in memory. The home folder
// (projects, settings, the app's data) is mounted on IndexedDB and written back to it whenever it
// changed, so it is there next time; everything else (/tmp: files picked to convert) is not kept.
const HOME = '/home/web_user';

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
});

const config = runtime.getConfig();
await runtime.runMain(config.mainAssemblyName, [globalThis.location.href]);
