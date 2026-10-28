"""Build, test and package the editable pet using the current Python environment."""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
import hashlib

ROOT = Path(__file__).resolve().parent
APP = ROOT / 'app'
update_source = (APP/'updater.py').read_text(encoding='utf-8')
VERSION = re.search(r"VERSION = '([^']+)'", update_source).group(1)
BUILD_NUMBER = int(re.search(r'BUILD_NUMBER = (\d+)', update_source).group(1))
OUTPUT = ROOT / 'outputs'
OUTPUT.mkdir(exist_ok=True)
WORK = ROOT / 'work'
WORK.mkdir(exist_ok=True)

with tempfile.TemporaryDirectory(prefix='build-', dir=WORK) as temporary:
    stage = Path(temporary).resolve()
    subprocess.run([sys.executable, '-m', 'PyInstaller', '--noconfirm', '--onedir',
        '--windowed', '--noupx', '--name', '点点', '--icon', str(APP/'diandian.ico'),
        '--distpath', str(stage/'dist'), '--workpath', str(stage/'cache'),
        '--specpath', str(stage), str(APP/'diandian.py')], check=True)
    package = stage/'dist/点点'
    project = json.loads((APP/'project.json').read_text(encoding='utf-8'))
    assets = {'motions.png','curious.png','flight.png','diandian.ico','project.json'}
    assets.update(item['file'] for item in project['sounds'])
    assets.update(item['file'] for item in project['frames'])
    for name in assets:
        source, destination = (APP/name).resolve(), (package/name).resolve()
        if not source.is_relative_to(APP) or not destination.is_relative_to(package):
            raise ValueError('素材路径超出项目：' + name)
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
    shutil.copy2(APP/'update-source.json', package/'update-source.json')
    release = {'schema': 1, 'product': 'diandian-v1', 'series': 'v1',
               'version': VERSION, 'buildNumber': BUILD_NUMBER,
               'contentHashes': {name: hashlib.sha256((package/name).read_bytes()).hexdigest() for name in sorted(assets)}}
    (package/'release.json').write_text(json.dumps(release, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    shutil.copytree(ROOT/'licenses', package/'licenses')
    (package/'source').mkdir()
    for source in APP.glob('*.py'):
        shutil.copy2(source, package/'source'/source.name)
    shutil.copy2(ROOT/'build.py', package/'source/build.py')
    shutil.copy2(ROOT/'项目说明.md', package/'使用说明.md')
    env = os.environ.copy()
    for key in ('PYTHONPATH','PYTHONHOME','QT_PLUGIN_PATH','QT_QPA_PLATFORM_PLUGIN_PATH'):
        env.pop(key, None)
    env['PATH'] = r'C:\Windows\System32'
    env['QT_QPA_PLATFORM'] = 'offscreen'
    def check(exe, report):
        subprocess.run([str(exe),'--self-test',str(report)], env=env, check=True, timeout=120)
        assert json.loads((report/'self-test.json').read_text(encoding='utf-8'))['result'] == 'PASS'
    check(package/'点点.exe', stage/'check')
    archive = stage/f'点点桌宠-v{VERSION}-Windows.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        for path in sorted(package.rglob('*')):
            if path.is_file():
                bundle.write(path, Path('点点')/path.relative_to(package))
    with zipfile.ZipFile(archive) as bundle:
        assert bundle.testzip() is None
        bundle.extractall(stage/'中文 空格验证')
    check(stage/'中文 空格验证/点点/点点.exe', stage/'relocated-check')
    target = (OUTPUT/f'Windows-v{VERSION}').resolve()
    if target.parent != OUTPUT.resolve() or target.is_symlink():
        raise ValueError('发布目录校验失败')
    if target.exists():
        # Windows refuses write access while this executable is running; fail before deleting anything.
        with (target/'点点/点点.exe').open('r+b'):
            pass
        if any(p.is_symlink() or getattr(p, 'is_junction', lambda: False)() for p in target.rglob('*')):
            raise ValueError('发布目录包含链接，停止覆盖')
        shutil.rmtree(target)
    shutil.copytree(package, target/'点点')
    shutil.copy2(archive, OUTPUT/archive.name)
    manifest = {key: release[key] for key in ('schema', 'product', 'series', 'version', 'buildNumber')}
    manifest.update(platform='windows-x64', url='', size=archive.stat().st_size,
                    sha256=hashlib.sha256(archive.read_bytes()).hexdigest(),
                    notes='v1 写实二维桌宠：新增可选更新、下载校验和安装重启；可保留自行编辑的叫声、动作与互动。')
    (OUTPUT/'latest-v1.template.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print(f'PASS: v{VERSION}; {archive.stat().st_size/1024**2:.1f} MiB; source + packaged + relocated checks')
