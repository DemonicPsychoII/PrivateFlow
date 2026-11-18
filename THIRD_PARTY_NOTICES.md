# Third-party components

Private Flow's own code is MIT-licensed; see LICENSE. Third-party licences remain
applicable. Setup downloads are pinned and SHA-256 verified by `scripts/models.json`
and the evaluation scripts. Models and native runtimes are downloaded separately,
kept locally and not included in the app ZIP. The self-contained app ships .NET
and NAudio. Preserve the upstream runtime licence and notice files.

## App and optional local inference

The licence files and model cards below were read upstream on 2026-11-18.

| Component | Version or revision and downloaded files | Licence and upstream source |
| --- | --- | --- |
| .NET / WPF | .NET 10 self-contained Windows x64 runtime (servicing version follows the build SDK); includes Windows Forms | MIT: [runtime](https://github.com/dotnet/runtime/blob/v10.0.0/LICENSE.TXT), [WPF](https://github.com/dotnet/wpf/blob/v10.0.0/LICENSE.TXT), [Windows Forms](https://github.com/dotnet/winforms/blob/v10.0.0/LICENSE.TXT). Runtime third-party components retain their bundled notices. |
| NAudio and its Core, Asio, Midi, Wasapi, WinForms and WinMM packages | 2.2.1 | [MIT](https://github.com/naudio/NAudio/blob/v2.2.1/license.txt), copyright 2020 Mark Heath |
| whisper.cpp | v1.9.2: `whisper-bin-x64.zip`, `whisper-cublas-12.4.0-bin-x64.zip` | [MIT](https://github.com/ggml-org/whisper.cpp/blob/v1.9.2/LICENSE), copyright 2023–2026 The ggml authors |
| Whisper weights | ggerganov/whisper.cpp `5359861c739e955e79d9a303bcbc70fb988958b1`: `ggml-base.bin`, `ggml-medium.bin`, `ggml-large-v3.bin`, `ggml-large-v3-turbo-q8_0.bin` | [MIT model card at the pinned revision](https://huggingface.co/ggerganov/whisper.cpp/blob/5359861c739e955e79d9a303bcbc70fb988958b1/README.md); [OpenAI licence](https://github.com/openai/whisper/blob/main/LICENSE), copyright 2022 OpenAI |
| Silero VAD weights | ggml-org/whisper-vad `9ffd54a1e1ee413ddf265af9913beaf518d1639b`: `ggml-silero-v6.2.0.bin` | [MIT model card](https://huggingface.co/ggml-org/whisper-vad/blob/9ffd54a1e1ee413ddf265af9913beaf518d1639b/README.md), [Silero licence](https://github.com/snakers4/silero-vad/blob/v6.2/LICENSE), copyright 2020–present Silero Team |
| llama.cpp | b11577: `llama-b11577-bin-win-cpu-x64.zip`, `llama-b11577-bin-win-cuda-12.4-x64.zip` | [MIT](https://github.com/ggml-org/llama.cpp/blob/b11577/LICENSE), copyright 2023–2026 The ggml authors |
| NVIDIA CUDA runtime components | CUDA 12.4, in the whisper CUDA archive and `cudart-llama-bin-win-cuda-12.4-x64.zip` from [llama.cpp b11577](https://github.com/ggml-org/llama.cpp/releases/tag/b11577) | [NVIDIA CUDA 12.4 EULA and redistribution terms](https://docs.nvidia.com/cuda/archive/12.4.0/eula/index.html); components under other licences retain their notices. Optional; CPU setup needs no CUDA. |
| Qwen3-4B Q4_K_M | Qwen/Qwen3-4B-GGUF `bc640142c66e1fdd12af0bd68f40445458f3869b`: `Qwen3-4B-Q4_K_M.gguf` | [Apache-2.0 model card](https://huggingface.co/Qwen/Qwen3-4B-GGUF/blob/bc640142c66e1fdd12af0bd68f40445458f3869b/README.md), [licence](https://huggingface.co/Qwen/Qwen3-4B-GGUF/blob/bc640142c66e1fdd12af0bd68f40445458f3869b/LICENSE) |
| Qwen3.5-4B Q4_K_M | unsloth/Qwen3.5-4B-GGUF `e87f176479d0855a907a41277aca2f8ee7a09523`: `Qwen3.5-4B-Q4_K_M.gguf` | [Apache-2.0 model card](https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/blob/e87f176479d0855a907a41277aca2f8ee7a09523/README.md), quantised from [Qwen/Qwen3.5-4B](https://huggingface.co/Qwen/Qwen3.5-4B/blob/main/LICENSE) |

The .NET runtime and desktop libraries are copyright .NET Foundation and
Contributors, all rights reserved. Their licence and runtime third-party notices
are included under `licenses/` in the app package.

The MIT notices for .NET, NAudio, whisper.cpp, Whisper, llama.cpp and Silero use these terms:

> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in
> all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
> THE SOFTWARE.

## Evaluation-only downloads

These are never shipped or redistributed by Private Flow. The scripts download
and verify them into `artifacts/test-assets`. Voice model and configuration
files share the pinned revision `6b9cfaacb4fb2e02a9621227e3c1a48bec9710c3`
of rhasspy/piper-voices. Each linked model card was read at that revision;
the licence column records its declared dataset terms.

| Asset | Pinned source | Licence |
| --- | --- | --- |
| Piper Windows x64 runtime | [2023.11.14-2](https://github.com/rhasspy/piper/releases/tag/2023.11.14-2), `piper_windows_amd64.zip`; [licence](https://github.com/rhasspy/piper/blob/2023.11.14-2/LICENSE.md) | MIT, copyright 2022 Michael Hansen; bundled dependencies keep their own notices (including eSpeak NG and ONNX Runtime). |
| `en_GB-vctk-medium` | [model card](https://huggingface.co/rhasspy/piper-voices/blob/6b9cfaacb4fb2e02a9621227e3c1a48bec9710c3/en/en_GB/vctk/medium/MODEL_CARD) | CC BY 4.0 |
| `en_US-l2arctic-medium` | [model card](https://huggingface.co/rhasspy/piper-voices/blob/6b9cfaacb4fb2e02a9621227e3c1a48bec9710c3/en/en_US/l2arctic/medium/MODEL_CARD) | CC BY-NC 4.0; evaluation only, no commercial use |
| `es_AR-daniela-high` | [model card](https://huggingface.co/rhasspy/piper-voices/blob/6b9cfaacb4fb2e02a9621227e3c1a48bec9710c3/es/es_AR/daniela/high/MODEL_CARD) | CC BY-SA 4.0 |
| `es_MX-ald-medium` | [model card](https://huggingface.co/rhasspy/piper-voices/blob/6b9cfaacb4fb2e02a9621227e3c1a48bec9710c3/es/es_MX/ald/medium/MODEL_CARD) | Unlicense |
| `es_MX-claude-high` | [model card](https://huggingface.co/rhasspy/piper-voices/blob/6b9cfaacb4fb2e02a9621227e3c1a48bec9710c3/es/es_MX/claude/high/MODEL_CARD) | Apache-2.0 |
| `de_DE-kerstin-low` | [model card](https://huggingface.co/rhasspy/piper-voices/blob/6b9cfaacb4fb2e02a9621227e3c1a48bec9710c3/de/de_DE/kerstin/low/MODEL_CARD) | CC0 |
| `de_DE-thorsten-high` | [model card](https://huggingface.co/rhasspy/piper-voices/blob/6b9cfaacb4fb2e02a9621227e3c1a48bec9710c3/de/de_DE/thorsten/high/MODEL_CARD) | CC0 |
| `de_DE-karlsson-low` | [model card](https://huggingface.co/rhasspy/piper-voices/blob/6b9cfaacb4fb2e02a9621227e3c1a48bec9710c3/de/de_DE/karlsson/low/MODEL_CARD) | M-AILABS dataset terms: the card says "See URL". Its [dataset terms page](https://www.caito.de/2019/01/03/the-m-ailabs-speech-dataset/) did not resolve when checked, so these terms could not be verified. Do not assume a licence grant from this notice. |
| FLEURS dev archives and transcripts, `en_us`, `es_419`, `de_de` | google/fleurs `70bb2e84b976b7e960aa89f1c648e09c59f894dd`, [dataset card](https://huggingface.co/datasets/google/fleurs/blob/70bb2e84b976b7e960aa89f1c648e09c59f894dd/README.md) | CC BY 4.0 |

Piper's native dependencies include [piper-phonemize (MIT)](https://github.com/rhasspy/piper-phonemize/blob/v1.1.0/LICENSE.md)
and [eSpeak NG (GPL-3.0)](https://github.com/espeak-ng/espeak-ng/blob/1.51/COPYING),
whose upstream licence files were checked. Its ONNX Runtime dependency's
[upstream source](https://github.com/microsoft/onnxruntime) returned 404 during
this check, so that dependency's licence and the exact bundled dependency
revisions could not be verified. The pinned Piper archive contains no licence
files. These evaluation-only binaries are not included in Private Flow's package.
