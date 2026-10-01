# Third-Party Software Notices and Information

This project incorporates components from the open source software projects listed below.

## Compact ICC Profiles — Adobe-compatible RGB profile

- **Project URL:** https://github.com/saucecontrol/Compact-ICC-Profiles
- **File:** `src/PhotoReview.Imaging.Raw/AdobeCompat-v2.icc` (374 bytes; SHA-256 `60FB2ADECACF82132DB0B1C09B303316F3BBD9E2823E7BA096D01627D12D57C9`)
- **License:** CC0 1.0 Universal; profile is released to the public domain.
- **Use:** source profile for Adobe RGB tagged embedded RAW JPEG previews, transformed by WIC to sRGB.
- **Profile catalog and license:** https://github.com/saucecontrol/Compact-ICC-Profiles#readme

---

## libjpeg-turbo

- **Project URL:** https://libjpeg-turbo.org / https://github.com/libjpeg-turbo/libjpeg-turbo
- **Version:** 3.0.0
- **License:** BSD-3-Clause / IJG License / zlib License

### License Summary:

libjpeg-turbo is covered by three compatible licenses:

1. **The Modified (3-clause) BSD License**, which covers the TurboJPEG API library and associated test programs, as well as the build system.
2. **The Independent JPEG Group (IJG) License**, which covers the libjpeg API library and associated programs.
3. **The zlib License**, which covers certain extensions and components.

### Modified BSD License Text:

```text
Copyright (C) 2009-2023 D. R. Commander. All Rights Reserved.
Copyright (C) 2015 Viktor Szathmáry. All Rights Reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

- Redistributions of source code must retain the above copyright notice,
  this list of conditions and the following disclaimer.
- Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.
- Neither the name of the libjpeg-turbo Project nor the names of its
  contributors may be used to endorse or promote products derived from this
  software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS",
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDERS OR CONTRIBUTORS BE
LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGE.
```

---

## LibRaw

- **Project URL:** https://www.libraw.org
- **Version:** 0.22.2 (official Windows x64 release), shipped unmodified as `libraw.dll` and loaded dynamically at runtime.
- **License:** GNU Lesser General Public License v2.1 **or** Common Development and Distribution License v1.0 (dual license, at the recipient's choice). Full texts ship as `LibRaw-LICENSE.LGPL` and `LibRaw-LICENSE.CDDL` (sources: `native/libraw/LICENSE.LGPL`, `native/libraw/LICENSE.CDDL`).
- **Source availability:** the official LibRaw 0.22.2 package, including the complete source code and build files, ships next to the binary as `LibRaw-SOURCE.zip` (SHA-256 pinned in `native/libraw.package.sha256`). A compatible modified `libraw.dll` placed beside `PhotoReview.App.exe` is loaded in place of the bundled one.
- **Use:** full-resolution RAW demosaic (zoom decode) and embedded-thumbnail extraction.
- **Notice file:** `LibRaw-NOTICE.txt` (source: `native/libraw/NOTICE.txt`).

### Copyright notices (from the `COPYRIGHT` file of the LibRaw 0.22.2 package):

```text
Copyright (C) 2008-2025 LibRaw LLC (http://www.libraw.org, info@libraw.org)

LibRaw uses code from dcraw.c -- Dave Coffin's raw photo decoder,
dcraw.c is copyright 1997-2018 by Dave Coffin, dcoffin a cybercom o net.
LibRaw do not use RESTRICTED code from dcraw.c

LibRaw uses DCB demosaic and FBDD denoise licensed under BSD-like 3-clause license
DCB and FBDD are Copyright (C) 2010,  Jacek Gozdz (cuniek@kft.umcs.lublin.pl)

LibRaw uses X3F library to unpack Foveon Files, licensed BSD-style license
Copyright (c) 2010, Roland Karlsson (roland@proxel.se)
All rights reserved.

LibRaw uses pieces of code from Adobe DNG SDK 1.4,
Copyright (c) 2005 Adobe Systems Incorporated, licensed under MIT license
```
