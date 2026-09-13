# Third-Party Notices

## b-em Disc Drive WAV Samples

The 5.25 inch disc drive WAV samples in `Assets/Sound/*.wav` are from the b-em BBC Micro emulator.

- Project: b-em
- Repository: https://github.com/stardot/b-em
- Website: https://b-em.bbcmicro.com/
- Source paths: `ddnoise/525/*.wav`
- Licence: GNU General Public License version 2, as provided by b-em in `COPYING`

## DotMatrix Font

`Assets/Fonts/DotMatrix-Regular.ttf` is from the DotMatrix font project.

- Project: DotMatrix
- Repository: https://github.com/Gissio/font_DotMatrix
- Licence: OFL-GPL

## MAME TMS5220 Tables

The TMS5220 energy, pitch, reflection-coefficient and chirp table values in
`SRC/TMS5220_Speech.cs` are derived from MAME's TMS51xx/TMS52xx sound-device
tables.

- Project: MAME
- Repository: https://github.com/mamedev/mame
- Source paths: `src/devices/sound/tms5110r.hxx`, `src/devices/sound/tms5220.cpp`
- Copyright: Frank Palazzolo, Couriersud, Jonathan Gevaryahu and the MAME contributors
- Licence: BSD 3-Clause

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice,
   this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors
   may be used to endorse or promote products derived from this software
   without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS “AS IS”
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Teletext Hardware and Packet References

The ANE01 register behaviour and field timings in `SRC/TeletextAdapter.cs` were
implemented using BeebEm's Teletext implementation as a hardware reference.

- Source: https://github.com/stardot/beebem-windows/blob/master/Src/Teletext.cpp
- Copyright: (C) 2006 Jon Welch; (C) 2019 Alistair Cree
- Licence: GNU General Public License version 2 or later

The TTI control-bit mapping, packet format and page CRC in `SRC/TeletextPage.cs`
were checked against VBIT2, by Peter Kwan and its contributors.

- Source: https://github.com/peterkvt80/vbit2
- Licence: upstream permissive licence, reproduced below:

Copyright (C) 2025, Peter Kwan, Alistair Cree, Alistair Buxton

Permission to use, copy, modify, and distribute this software
and its documentation for any purpose and without fee is hereby
granted, provided that the above copyright notice appear in all
copies and that both that the copyright notice and this
permission notice and warranty disclaimer appear in supporting
documentation, and that the names of the authors not be used in
advertising or publicity pertaining to distribution of the
software without specific, written prior permission.

The authors disclaim all warranties with regard to this
software, including all implied warranties of merchantability
and fitness.  In no event shall the authors be liable for any
special, indirect or consequential damages or any damages
whatsoever resulting from loss of use, data or profits, whether
in an action of contract, negligence or other tortious action,
arising out of or in connection with the use or performance of
this software.

NMS Ceefax pages are fetched at runtime from https://feeds.nmsni.co.uk/ and are
not bundled with this project. Service: https://nmsceefax.co.uk/.
The ATS ROM is supplied separately and remains the property of its rights holder.
