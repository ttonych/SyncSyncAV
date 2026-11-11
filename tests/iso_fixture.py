"""Build a small UDF 2.50 Blu-ray fixture from generated video and audio."""
import json
from struct import pack
import subprocess

import pythoncom
from win32com.client import Dispatch


def playlist(items):
    def section(data):
        return pack('>H', len(data)) + data

    # Primary AVC (1080p24, PID 0x1011) and English AC-3 (PID 0x1100).
    video = bytes.fromhex('03 01 1011 02 1b 62')
    audio = bytes.fromhex('03 01 1100 05 81 31') + b'eng'
    streams = section(bytes(2) + bytes([1, 1]) + bytes(10) + video + audio)
    entries = []
    for clip, start in items:
        # Each play item uses one second from the clip, in 45 kHz ticks.
        timing = pack('>HBII', 1, 0, start, start + 45000)
        entries.append(section(clip.encode('ascii') + b'M2TS' + timing + bytes(12) + streams))
    body = pack('>HHH', 0, len(entries), 0) + b''.join(entries)
    header = b'MPLS0200' + pack('>III', 64, 68 + len(body), 0) + bytes(20)
    app_info = pack('>I', 14) + b'\0\1' + bytes(12)
    return (header + app_info).ljust(64, b'\0') + pack('>I', len(body)) + body + pack('>IH', 2, 0)


def build_iso(folder):
    disc = folder / 'sample-disc'
    bdmv = disc / 'BDMV'
    for name in ('STREAM', 'PLAYLIST'):
        (bdmv / name).mkdir(parents=True)
    (bdmv / 'index.bdmv').write_bytes(b'INDX0200' + bytes(512))
    items = []
    for clip, color, tone in [('00001', 'red', 440), ('00002', 'blue', 660)]:
        path = bdmv / 'STREAM' / (clip + '.m2ts')
        subprocess.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', f'color={color}:s=1920x1080:r=24',
                        '-f', 'lavfi', '-i', f'sine=frequency={tone}:sample_rate=48000',
                        '-t', '2', '-c:v', 'libx264', '-preset', 'ultrafast', '-g', '12', '-bf', '0',
                        '-c:a', 'ac3', '-mpegts_m2ts_mode', '1', '-muxdelay', '0', path], check=True)
        stream = json.loads(subprocess.check_output(['ffprobe', '-v', 'error', '-select_streams', 'v:0',
                            '-show_entries', 'stream=start_pts,time_base', '-of', 'json', path]))['streams'][0]
        assert stream['time_base'] == '1/90000'
        items.append((clip, round(stream['start_pts'] / 2)))
    for name, order in [('00001', items), ('00002', list(reversed(items))), ('00003', items[:1])]:
        (bdmv / 'PLAYLIST' / (name + '.mpls')).write_bytes(playlist(order))

    image = Dispatch('IMAPI2FS.MsftFileSystemImage')
    image.FileSystemsToCreate = 4  # UDF only
    image.UDFRevision = 0x250
    image.Root.AddTree(str(disc), False)
    result = image.CreateResultImage()
    stream = result.ImageStream.QueryInterface(pythoncom.IID_IStream)
    iso = folder / 'Тест Blu-ray.Режиссёрская версия.2026.2160p.HEVC.Original soundtrack.ISO'
    with iso.open('wb') as output:
        while chunk := stream.Read(1024 * 1024):
            output.write(chunk)
    return iso
