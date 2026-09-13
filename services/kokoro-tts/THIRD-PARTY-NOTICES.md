# Third-party notices

This directory contains source for an operator-installed local service. It does
not include Kokoro weights, eSpeak NG binaries, phonemizer binaries, Python
environments, or generated audio.

| Component | Version evaluated | License | Operational note |
| --- | --- | --- | --- |
| Kokoro | 0.9.4 | Apache-2.0 | Local CPU model runtime and separately obtained weights. |
| aiohttp | 3.14.3 | Apache-2.0 | Local loopback HTTP server. |
| PyTorch CPU | 2.14.0+cpu runtime | Composite SPDX in package metadata | CPU-only runtime. |
| NumPy | 2.4.6 | BSD-3-Clause and bundled notices | PCM conversion and gain. |
| Misaki | 0.9.4 | Apache-2.0 | Phonemization support. |
| eSpeak NG | operator-provided | GPL-3.0-or-later | Required operational executable; not bundled. |
| phonemizer-fork | 3.3.2 | GPL-3.0-or-later | Operational dependency; not bundled. |
| espeakng-loader | 0.2.4 | Package metadata | Locates operator-provided eSpeak NG. |

Redistributing this service or its environment requires a dedicated licensing
review, including the model weights and all generated dependency notices.
