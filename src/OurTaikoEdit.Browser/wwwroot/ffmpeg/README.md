`ffmpeg.js` and `814.ffmpeg.js` are @ffmpeg/ffmpeg 0.12.15 (MIT, https://github.com/ffmpegwasm/ffmpeg.wasm),
the UMD build, byte for byte the same as `dist/umd/` on npm. The worker chunk has to be served from the
page's own origin, so these two are here; the core (@ffmpeg/core 0.12.10: FFmpeg built to WebAssembly,
GPL) is loaded from jsDelivr by `ffmpeg-bridge.js`.
