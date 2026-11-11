"""Exercise the registered Windows OLE target on an isolated desktop without moving the mouse."""
from pathlib import Path
import argparse
import json
import shutil
import subprocess
import tempfile
import uuid

from iso_fixture import build_iso

import win32api
import win32event
import win32process
import win32service

parser = argparse.ArgumentParser()
parser.add_argument('build', type=Path)
parser.add_argument('--report', type=Path)
parser.add_argument('--disc', type=Path, help='Also select and open a playlist from a mounted or extracted Blu-ray disc')
args = parser.parse_args()
build = args.build.resolve()
with tempfile.TemporaryDirectory(prefix='syncsyncav-drop-') as temp:
    folder = Path(temp)
    app = folder / 'SyncSyncAV.exe'
    shutil.copy2(build / app.name, app)
    shutil.copytree(build / 'Fonts', folder / 'Fonts')
    shutil.copytree(build / 'Engine', folder / 'Engine')
    for suffix, color in [('один.mkv', 'red'), ('два.mp4', 'blue')]:
        subprocess.run(['ffmpeg', '-v', 'error', '-f', 'lavfi', '-i', f'color={color}:size=320x180:rate=24',
                        '-t', '2', '-c:v', 'mpeg4', folder / ('Видео ' + suffix)], check=True)
    (folder / 'ignored.txt').write_text('text')
    find_vs = Path(__file__).resolve().parents[1] / 'tools/find-vs.ps1'
    installation = subprocess.check_output(
        ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', find_vs], text=True).strip()
    compiler = Path(installation) / 'MSBuild/Current/Bin/Roslyn/csc.exe'
    wpf = Path('C:/Windows/Microsoft.NET/Framework64/v4.0.30319/WPF')
    runner = folder / 'FileDropTests.exe'
    refs = [str(wpf / (n+'.dll')) for n in ['WindowsBase','PresentationCore','PresentationFramework']]
    refs += [str(app), 'System.Xaml.dll','System.Web.Extensions.dll']
    subprocess.run([compiler, '/nologo', '/platform:x64', '/warnaserror+', f'/out:{runner}',
                    *['/reference:'+r for r in refs], Path(__file__).with_name('FileDropTests.cs')], check=True)
    name = 'SyncSyncAVTest-' + uuid.uuid4().hex
    desktop = win32service.CreateDesktop(name, 0, 0x1ff, None)
    start = win32process.STARTUPINFO()
    start.lpDesktop = name
    args_base = [runner,folder/'Engine/VirtualDub2.exe',folder/'Видео один.mkv',folder/'Видео два.mp4']
    args_base += ['--iso', build_iso(folder)]
    if args.disc:
        args_base += ['--disc', args.disc.resolve()]
    try:
        for flags in ([], ['--restart']):
            command = subprocess.list2cmdline(args_base + flags)
            process,thread,_,_ = win32process.CreateProcess(None,command,None,None,False,0x08000000,None,str(folder),start)
            try:
                if win32event.WaitForSingleObject(process,90000) != win32event.WAIT_OBJECT_0:
                    raise TimeoutError('UI test exceeded 90 seconds')
                error = folder / 'failure.txt'
                if error.exists():
                    raise RuntimeError(error.read_text(encoding='utf-8'))
                assert win32process.GetExitCodeProcess(process) == 0
            finally:
                if win32event.WaitForSingleObject(process,0) != win32event.WAIT_OBJECT_0:
                    win32api.TerminateProcess(process,1)
                    win32event.WaitForSingleObject(process,5000)
                thread.Close()
                process.Close()
        report = (folder/'report.json').read_text(encoding='utf-8')
        for case in json.loads(report):
            print('PASS',case)
        if args.report:
            args.report.write_text(report,encoding='utf-8')
    finally:
        desktop.CloseDesktop()
