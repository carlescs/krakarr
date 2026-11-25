# Media fixtures

Tiny clips (2 s, about 30 KB) for `MediaInfoReaderTests`, made with ffmpeg:

```bash
printf '1\n00:00:00,000 --> 00:00:01,000\nHi\n' > s.srt

# HEVC 10-bit HDR10 (PQ), 1920x800; AC3 5.1 Spanish + AAC stereo English; forced French SRT
ffmpeg -f lavfi -i "color=c=black:s=1920x800:d=2:r=5" -f lavfi -i "sine=d=2:sample_rate=8000" -f lavfi -i "sine=d=2:sample_rate=8000" -i s.srt \
  -map 0 -map 1 -map 2 -map 3 -c:v libx265 -x265-params log-level=none:keyint=10 -pix_fmt yuv420p10le \
  -color_trc smpte2084 -color_primaries bt2020 -colorspace bt2020nc \
  -c:a:0 ac3 -ac:a:0 6 -b:a:0 64k -c:a:1 aac -ac:a:1 2 -b:a:1 16k -c:s srt \
  -metadata:s:a:0 language=spa -metadata:s:a:1 language=eng -metadata:s:s:0 language=fre -disposition:s:0 forced hdr10-hevc.mkv

# AVC 8-bit SDR 1280x720; E-AC-3 5.1 German; Italian mov_text
ffmpeg -f lavfi -i "color=c=black:s=1280x720:d=2:r=5" -f lavfi -i "sine=d=2:sample_rate=8000" -i s.srt \
  -map 0 -map 1 -map 2 -c:v libx264 -preset ultrafast -c:a eac3 -ac 6 -b:a 96k -c:s mov_text \
  -metadata:s:a:0 language=ger -metadata:s:s:0 language=ita sdr-avc.mp4
```
