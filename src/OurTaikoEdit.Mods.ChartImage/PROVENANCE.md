# Where the chart image decoder comes from

`ChartImageDecoder.cs`, `ChartImageFont.cs` and the TJA writer in `ChartImageImporter.cs` started as a C# port
of the image-to-TJA tool in [TJAAnalyzer](https://github.com/jack9966qk/TJAAnalyzer) (fork: `OurTaiko/TJAAnalyzer`,
commit `4caafa9`), files `offline-analysis/chart-image-decoder.ts`, `chart-image-font.ts` and
`chart-image-to-tja.ts`. TJAAnalyzer is MIT licensed, © 2026 jackq:

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
> documentation files (the "Software"), to deal in the Software without restriction, including without limitation
> the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
> to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of
> the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
> THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
> CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
> IN THE SOFTWARE.

What differs from the original:

- How notes and drumrolls are recognised is written anew (2026-11-11). The original measures each note on the
  lane's centre line and sizes it by how tall its visible part is, and leaves a drumroll whose start is under
  another note to the grid, refusing when the grid has more than one place for it. Here a note is the size
  whose whole disc, less what the notes before it hide, is what the pixels show, and a drumroll starts where
  the rows of it that nothing hides say. The layout, the labels, the grid fitting and the long notes' ends
  are the original's. Images the original reads are read the same (its test image, text for text); images
  like `covered_drumroll_starts.png` it refuses are read too.
- The PNG is read by `Png.cs` (the original uses `sharp`), so only PNG files are read.
- Reasons for refusing an image are in the app's three languages and fewer than the original's.
- The TJA is written with the project's title and audio, `SUBTITLE:--`, `DEMOSTART:0`, and the balloon
  counts under `COURSE:` rather than above it; the chart between `#START` and `#END` is the same text.
- The written chart is read back by the target chart language instead of TJARenderer's parser.

The test image `tests/OurTaikoEdit.Tests/Fixtures/wrapped_drumrolls.png` and the expected chart next to it
are TJAAnalyzer's `tests/data/chart-images/wrapped_drumrolls.png` and what its tool writes for it.
`covered_drumroll_starts.png` is a chart image of the same kind that the user tried on 2026-11-11 and the
original tool refuses (measure 49).
