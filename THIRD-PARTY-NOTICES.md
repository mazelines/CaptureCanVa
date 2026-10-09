# Third-party notices

## Bundled FFmpeg

CaptureCanva release packages include an unmodified FFmpeg 9.0.2 executable
from the [Gyan release essentials build](https://www.gyan.dev/ffmpeg/builds/).
The build is licensed under the GNU General Public License version 3 (GPLv3).
CaptureCanva invokes this executable as a separate process.

The release package preserves the upstream license, build configuration, and
external library version list in `licenses/ffmpeg/`. The exact binary package
URL, checksum, and FFmpeg source revision are recorded in
`licenses/ffmpeg/SOURCE.txt`.

- [FFmpeg source revision](https://github.com/FFmpeg/FFmpeg/commit/946fcce07b)
- [FFmpeg source archive](https://github.com/FFmpeg/FFmpeg/archive/946fcce07b.tar.gz)
- [FFmpeg license information](https://ffmpeg.org/legal.html)
- [Upstream binary and build information](https://www.gyan.dev/ffmpeg/builds/)

The upstream `README.txt` lists the exact versions of the external libraries
included in this build. Their source repositories and release archives are
linked from the [FFmpeg external library documentation](https://ffmpeg.org/general.html#External-libraries)
and the [Gyan build library list](https://www.gyan.dev/ffmpeg/builds/#libraries).

FFmpeg and its external libraries retain their respective copyrights and
licenses. They are distributed without warranty under their license terms.
