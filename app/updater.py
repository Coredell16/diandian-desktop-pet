"""Optional updates for the independent v1 series; portable installs stay intact."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request
import uuid
import zipfile

from PyQt5.QtCore import QObject, QThread, QTimer, pyqtSignal
from PyQt5.QtWidgets import (QApplication, QCheckBox, QDialog, QHBoxLayout,
                            QInputDialog, QLabel, QMessageBox, QProgressBar,
                            QPushButton, QTextBrowser, QVBoxLayout)

VERSION = '1.6.3'
BUILD_NUMBER = 10603
PRODUCT = 'diandian-v1'
SERIES = 'v1'
MAX_DOWNLOAD = 512 * 1024**2
MAX_EXPANDED = 1024**3
MAX_JSON = 64 * 1024


class Cancelled(Exception):
    pass


def check_cancel(cancel):
    if cancel and cancel.is_set():
        raise Cancelled('已取消，点点继续陪伴。')


def read_json(payload):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError('JSON 含重复字段。')
            result[key] = value
        return result
    return json.loads(payload, object_pairs_hook=unique)


def https_url(value):
    if not isinstance(value, str) or not value or len(value) > 4096 or any(ord(c) < 33 for c in value):
        raise ValueError('请输入有效的 HTTPS 地址。')
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password or parsed.fragment:
        raise ValueError('更新地址必须使用 HTTPS，且不能含账号、密码或片段。')
    try:
        parsed.port
    except ValueError as error:
        raise ValueError('更新地址端口无效。') from error
    return value


def validate_manifest(data):
    if not isinstance(data, dict) or type(data.get('schema')) is not int or data['schema'] != 1:
        raise ValueError('不支持的更新清单。')
    if data.get('product') != PRODUCT or data.get('series') != SERIES or data.get('platform') != 'windows-x64':
        raise ValueError('这不是点点 v1 系列的 Windows 更新，已停止。')
    if not isinstance(data.get('version'), str) or not re.fullmatch(r'1\.\d{1,3}\.\d{1,3}', data['version']):
        raise ValueError('v1 更新必须使用 1.x.x 版本号。')
    if type(data.get('buildNumber')) is not int or not 1 <= data['buildNumber'] <= 2147483647:
        raise ValueError('更新构建编号无效。')
    if type(data.get('size')) is not int or not 1 <= data['size'] <= MAX_DOWNLOAD:
        raise ValueError('更新包大小无效或超过 512 MiB。')
    if not isinstance(data.get('sha256'), str) or not re.fullmatch(r'[0-9a-fA-F]{64}', data['sha256']):
        raise ValueError('更新包缺少有效校验值。')
    if not isinstance(data.get('notes', ''), str) or len(data.get('notes', '')) > 8000:
        raise ValueError('更新说明过长。')
    https_url(data.get('url'))
    return dict(data)


def newer(data):
    return data['buildNumber'] > BUILD_NUMBER and tuple(map(int, data['version'].split('.'))) > tuple(map(int, VERSION.split('.')))


class HttpsRedirect(urllib.request.HTTPRedirectHandler):
    def __init__(self, cancel=None):
        super().__init__()
        self.cancel = cancel

    def redirect_request(self, request, fp, code, message, headers, new_url):
        check_cancel(self.cancel)
        https_url(new_url)
        return super().redirect_request(request, fp, code, message, headers, new_url)


def transfer(url, limit, cancel=None, progress=None, destination=None, ssl_context=None):
    https_url(url)
    opener = urllib.request.build_opener(HttpsRedirect(cancel), urllib.request.HTTPSHandler(context=ssl_context))
    request = urllib.request.Request(url, headers={'User-Agent': 'Diandian-v1/' + VERSION})
    digest, received, chunks = hashlib.sha256(), 0, []
    check_cancel(cancel)
    with opener.open(request, timeout=12) as response:
        https_url(response.geturl())
        length = response.headers.get('Content-Length')
        if length is not None and (not length.isdigit() or int(length) > limit):
            raise ValueError('服务器返回的数据超过允许大小。')
        output = Path(destination).open('xb') if destination else None
        try:
            while True:
                check_cancel(cancel)
                chunk = response.read(64 * 1024)
                if not chunk:
                    break
                received += len(chunk)
                if received > limit:
                    raise ValueError('下载内容超过声明大小。')
                digest.update(chunk)
                if output:
                    output.write(chunk)
                else:
                    chunks.append(chunk)
                if progress:
                    progress(received, int(length) if length else limit)
            if output:
                output.flush()
                os.fsync(output.fileno())
        finally:
            if output:
                output.close()
    check_cancel(cancel)
    return (received, digest.hexdigest()) if destination else b''.join(chunks)


def fetch_manifest(url, cancel=None, ssl_context=None):
    return validate_manifest(read_json(transfer(url, MAX_JSON, cancel=cancel, ssl_context=ssl_context)))


def plain_path(path):
    path = Path(path).absolute()
    for item in (path, *path.parents):
        try:
            info = item.lstat()
        except FileNotFoundError:
            continue
        if stat.S_ISLNK(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ValueError('更新路径不能经过符号链接或联接目录。')
    return path


def atomic_json(path, data):
    path = plain_path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        with temporary.open('x', encoding='utf-8') as output:
            json.dump(data, output, ensure_ascii=False, indent=2)
            output.write('\n')
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def verified_package(path, manifest):
    path = plain_path(path)
    with path.open('rb') as source:
        return path.stat().st_size == manifest['size'] and hashlib.file_digest(source, 'sha256').hexdigest().lower() == manifest['sha256'].lower()


def download_package(manifest, root, cancel=None, progress=None, ssl_context=None):
    manifest = validate_manifest(manifest)
    folder = plain_path(Path(root) / 'Downloads')
    folder.mkdir(parents=True, exist_ok=True)
    destination = folder / f"Diandian-v1-{manifest['buildNumber']}-{manifest['sha256'][:12].lower()}.zip"
    if destination.exists() and verified_package(destination, manifest):
        return destination
    part = destination.with_suffix('.' + uuid.uuid4().hex + '.part')
    try:
        received, digest = transfer(manifest['url'], manifest['size'], cancel, progress, part, ssl_context)
        if received != manifest['size'] or digest.lower() != manifest['sha256'].lower():
            raise ValueError('更新包校验不一致，已丢弃下载。')
        check_cancel(cancel)
        os.replace(part, destination)
        return destination
    finally:
        part.unlink(missing_ok=True)


def relative_path(value):
    if not isinstance(value, str) or not value or len(value) > 1024:
        raise ValueError('更新包路径无效。')
    parts = value.replace('\\', '/').split('/')
    for part in parts:
        stem = part.split('.')[0].rstrip(' ').upper()
        if (not part or part in ('.', '..') or part.endswith(('.', ' '))
                or any(ord(c) < 32 or c in '<>:"|?*' for c in part)
                or stem in {'CON', 'PRN', 'AUX', 'NUL', 'CLOCK$', 'CONIN$', 'CONOUT$'}
                or re.fullmatch(r'(COM|LPT)[1-9¹²³]', stem)):
            raise ValueError('更新包含不安全的 Windows 路径。')
    return Path(*parts)


def content_files(root):
    root = plain_path(root)
    if (root / 'project.json').stat().st_size > 1024**2:
        raise ValueError('编辑项目文件过大。')
    data = read_json((root / 'project.json').read_text(encoding='utf-8-sig'))
    if (not isinstance(data, dict) or any(not isinstance(data.get(key), list)
            or any(not isinstance(item, dict) or not isinstance(item.get('file'), str) for item in data[key])
            for key in ('sounds', 'frames'))):
        raise ValueError('个人编辑项目的素材列表无效。')
    names = {'project.json', 'motions.png', 'curious.png', 'flight.png', 'diandian.ico'}
    names.update(item['file'] for key in ('sounds', 'frames') for item in data[key])
    if 'appearance' in data:
        from appearance import GROUPS
        appearance = data['appearance']
        if not isinstance(appearance, dict) or set(appearance) != set(GROUPS) or any(not isinstance(name, str) for name in appearance.values()):
            raise ValueError('个人外观的四组动作图列表无效。')
        names.update(appearance.values())
    for name in names:
        relative = relative_path(name)
        if name != 'project.json' and relative.suffix.lower() not in ('.png', '.wav', '.ico'):
            raise ValueError('个人素材只能是 PNG、WAV 或 ICO。')
        file = plain_path(root / relative)
        if not file.is_relative_to(root) or not file.is_file():
            raise ValueError('个人素材缺失或超出项目目录。')
    return sorted(names)


def content_hashes(root):
    result = {}
    for name in content_files(root):
        with (Path(root) / name).open('rb') as source:
            result[name] = hashlib.file_digest(source, 'sha256').hexdigest()
    return result


def customized(program_root, content_root):
    try:
        release = read_json((Path(program_root) / 'release.json').read_text(encoding='utf-8'))
        return not isinstance(release, dict) or release.get('contentHashes') != content_hashes(content_root)
    except (OSError, ValueError, KeyError, TypeError):
        return True


def install_package(archive_path, manifest, root, content_root=None, source_config=None, cancel=None):
    manifest = validate_manifest(manifest)
    if not newer(manifest):
        raise ValueError('只能安装 v1 系列的新版本。')
    if not verified_package(archive_path, manifest):
        raise ValueError('更新包已变化，请重新下载。')
    versions = plain_path(Path(root) / 'Versions')
    versions.mkdir(parents=True, exist_ok=True)
    destination = plain_path(versions / str(manifest['buildNumber']))
    if destination.exists():
        raise ValueError('此版本目录已存在，原目录已保留。')
    stage = versions / ('.stage-' + uuid.uuid4().hex)
    stage.mkdir()
    try:
        with zipfile.ZipFile(archive_path) as bundle:
            entries = bundle.infolist()
            if not 1 <= len(entries) <= 10000:
                raise ValueError('更新包文件数不正确。')
            targets, expanded = {}, 0
            for entry in entries:
                unix_type = entry.external_attr >> 16 & 0o170000
                if unix_type not in (0, 0o100000, 0o040000) or entry.external_attr & 0x400:
                    raise ValueError('更新包不能含链接或特殊文件。')
                name = entry.filename.replace('\\', '/')
                if not name.startswith('点点/'):
                    raise ValueError('v1 更新包必须包含点点文件夹。')
                name = name[len('点点/'):].rstrip('/')
                if not name:
                    if not entry.is_dir():
                        raise ValueError('更新包根条目无效。')
                    continue
                relative = relative_path(name)
                key = relative.as_posix().casefold()
                if key in targets:
                    raise ValueError('更新包含重复路径。')
                targets[key] = (entry, relative)
                expanded += entry.file_size
                if expanded > MAX_EXPANDED or entry.flag_bits & 1:
                    raise ValueError('更新包过大或受到密码保护。')
            for entry, relative in targets.values():
                for parent in relative.parents:
                    if parent == Path('.'):
                        continue
                    previous = targets.get(parent.as_posix().casefold())
                    if previous and not previous[0].is_dir():
                        raise ValueError('更新包文件与目录冲突。')
            written = 0
            for entry, relative in targets.values():
                check_cancel(cancel)
                target = plain_path(stage / relative)
                if entry.is_dir():
                    if entry.file_size:
                        raise ValueError('目录条目不能含数据。')
                    target.mkdir(parents=True, exist_ok=True)
                    continue
                target.parent.mkdir(parents=True, exist_ok=True)
                count = 0
                with bundle.open(entry) as source, target.open('xb') as output:
                    while True:
                        check_cancel(cancel)
                        chunk = source.read(64 * 1024)
                        if not chunk:
                            break
                        count += len(chunk)
                        written += len(chunk)
                        if count > entry.file_size or written > MAX_EXPANDED:
                            raise ValueError('更新包解压大小超过声明。')
                        output.write(chunk)
                if count != entry.file_size:
                    raise ValueError('更新包文件不完整。')
        release_file = stage / 'release.json'
        if release_file.stat().st_size > 256 * 1024:
            raise ValueError('版本信息过大。')
        release = read_json(release_file.read_text(encoding='utf-8-sig'))
        if (not isinstance(release, dict) or type(release.get('schema')) is not int or release['schema'] != 1
                or any(release.get(k) != manifest[k] for k in ('product', 'series', 'version', 'buildNumber'))):
            raise ValueError('更新包内的版本或系列与清单不一致。')
        executable = stage / '点点.exe'
        with executable.open('rb') as source:
            if source.read(2) != b'MZ':
                raise ValueError('更新包缺少 Windows 程序。')
        if content_root:
            user_content = stage / 'user-content'
            user_content.mkdir()
            total = 0
            for name in content_files(content_root):
                check_cancel(cancel)
                source = Path(content_root) / name
                total += source.stat().st_size
                if total > MAX_EXPANDED:
                    raise ValueError('个人素材合计超过 1 GiB。')
                target = user_content / name
                target.parent.mkdir(parents=True, exist_ok=True)
                with source.open('rb') as incoming, target.open('xb') as output:
                    while True:
                        check_cancel(cancel)
                        chunk = incoming.read(64 * 1024)
                        if not chunk:
                            break
                        output.write(chunk)
        config = stage / 'update-source.json'
        if source_config and not read_source(stage):
            atomic_json(config, {'manifestUrl': https_url(source_config)})
        check_cancel(cancel)
        stage.rename(destination)
        # The new app activates its pointer only after loading its project.
        return destination / '点点.exe'
    finally:
        if stage.exists():
            plain_path(stage)
            shutil.rmtree(stage)


def persistent_root():
    return Path(os.environ.get('LOCALAPPDATA', str(Path.home() / 'AppData/Local'))) / 'Diandian' / 'DesktopPet-v1'


def read_source(program_root):
    path = Path(program_root) / 'update-source.json'
    if not path.exists():
        return ''
    if path.stat().st_size > MAX_JSON:
        raise ValueError('更新地址配置过大。')
    data = read_json(path.read_text(encoding='utf-8-sig'))
    if not isinstance(data, dict) or not isinstance(data.get('manifestUrl', ''), str):
        raise ValueError('更新地址配置必须包含文本格式的 manifestUrl。')
    value = data.get('manifestUrl', '').strip()
    return https_url(value) if value else ''


def activate_install(program_root, root):
    expected = Path(root).absolute() / 'Versions' / str(BUILD_NUMBER)
    if Path(program_root).absolute() == expected:
        plain_path(expected)
        atomic_json(Path(root) / 'current-version.json', {'product': PRODUCT, 'buildNumber': BUILD_NUMBER})


def launch_newer(program_root, root, args):
    try:
        pointer = plain_path(Path(root) / 'current-version.json')
        if pointer.stat().st_size > MAX_JSON:
            return False
        data = read_json(pointer.read_text(encoding='utf-8'))
        if not isinstance(data, dict):
            return False
        build = data.get('buildNumber')
        if data.get('product') != PRODUCT or type(build) is not int or not BUILD_NUMBER < build <= 2147483647:
            return False
        folder = plain_path(Path(root) / 'Versions' / str(build))
        if folder == Path(program_root).absolute():
            return False
        release_path = folder / 'release.json'
        if release_path.stat().st_size > 256 * 1024:
            return False
        release = read_json(release_path.read_text(encoding='utf-8'))
        if not isinstance(release, dict) or release.get('product') != PRODUCT or release.get('series') != SERIES or release.get('buildNumber') != build:
            return False
        if not re.fullmatch(r'1\.\d{1,3}\.\d{1,3}', release.get('version', '')):
            return False
        subprocess.Popen([str(folder / '点点.exe'), '--wait-parent', str(os.getpid()), *args], cwd=folder,
                         env=dict(os.environ, PYINSTALLER_RESET_ENVIRONMENT='1'), close_fds=True)
        return True
    except (OSError, ValueError, KeyError, TypeError):
        return False


def wait_parent(pid):
    if type(pid) is not int or pid <= 0 or pid == os.getpid():
        raise ValueError('更新交接进程编号无效。')
    if os.name == 'nt':
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        kernel.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel.CloseHandle.argtypes = [wintypes.HANDLE]
        handle = kernel.OpenProcess(0x100000, False, pid)
        if handle:
            try:
                if kernel.WaitForSingleObject(handle, 15000) != 0:
                    raise ValueError('旧版点点未能退出，请稍后重试。')
            finally:
                kernel.CloseHandle(handle)


class Job(QThread):
    result = pyqtSignal(object, str)
    progress = pyqtSignal(int, int)

    def __init__(self, operation, parent):
        super().__init__(parent)
        self.operation = operation
        self.cancel = threading.Event()

    def run(self):
        try:
            value = self.operation(self)
            self.result.emit(value, '')
        except Exception as error:
            self.result.emit(None, str(error) or type(error).__name__)


class Updater(QObject):
    changed = pyqtSignal()

    def __init__(self, pet, program_root, data_root=None):
        super().__init__(pet)
        self.pet, self.program_root = pet, Path(program_root)
        self.data_root = Path(data_root) if data_root else persistent_root()
        self.manifest = self.downloaded = self.job = self.dialog = None
        self.status, self.progress = '点点 v1 · 当前版本 ' + VERSION, 0
        self.automatic = False
        self.check_timer = QTimer(self)
        self.check_timer.timeout.connect(self.auto_check)

    def start(self):
        self.check_timer.start(8000)

    def auto_check(self):
        self.check_timer.setInterval(15 * 60 * 1000)
        try:
            enabled = self.pet.settings.value('update/v1/auto', True, type=bool)
            last = self.pet.settings.value('update/v1/last', 0, type=float)
            if enabled and time.time() - last >= 86400 and read_source(self.program_root):
                self.check(True)
        except (OSError, ValueError, TypeError):
            pass

    def open(self):
        if self.dialog is None:
            self.dialog = UpdateDialog(self)
        self.dialog.refresh()
        self.dialog.show()
        self.dialog.raise_()
        self.dialog.activateWindow()

    def run(self, operation, callback):
        if self.job:
            return
        job = Job(operation, self)
        self.job = job
        job.progress.connect(self.set_progress)
        job.result.connect(callback)
        job.finished.connect(lambda: self.finished(job))
        job.start()
        self.changed.emit()

    def finished(self, job):
        if self.job is job:
            self.job = None
        job.deleteLater()
        self.changed.emit()

    def set_progress(self, count, total):
        self.progress = min(100, int(count * 100 / max(1, total)))
        self.changed.emit()

    def check(self, automatic=False):
        if self.job:
            return
        try:
            source = read_source(self.program_root)
            if not source:
                self.status = '尚未配置 v1 发布地址，可稍后在“设置更新地址”中填写。'
                self.changed.emit()
                return
        except (OSError, ValueError, TypeError) as error:
            self.status = str(error)
            self.changed.emit()
            return
        self.status, self.automatic = '正在检查 v1 系列更新…', automatic
        self.run(lambda job: fetch_manifest(source, job.cancel), self.checked)

    def checked(self, value, error):
        self.pet.settings.setValue('update/v1/last', time.time())
        self.pet.settings.sync()
        if error:
            self.status = '检查未完成：' + error
        elif not newer(value):
            self.manifest = self.downloaded = None
            self.status = '当前已是最新的 v1 版本（' + VERSION + '）。'
        else:
            if self.manifest != value:
                self.downloaded = None
            self.manifest = value
            skipped = self.pet.settings.value('update/v1/skip', 0, type=int)
            self.status = '发现 v' + value['version'] + '，由你选择是否下载。'
            if self.automatic and skipped == value['buildNumber']:
                self.status = '已略过 v' + value['version'] + '，手动检查仍可查看。'
            elif self.automatic:
                self.pet.say('点点 v1 有新版啦，可在更新菜单查看～', 5)
        self.changed.emit()

    def download(self):
        if not self.manifest or self.job:
            return
        self.status, self.progress = '正在下载 v' + self.manifest['version'] + '…', 0
        self.run(lambda job: download_package(self.manifest, self.data_root, job.cancel, job.progress.emit), self.downloaded_result)

    def downloaded_result(self, value, error):
        self.downloaded = value if not error else None
        self.status = ('下载未完成：' + error) if error else '下载和校验完成，可以安装并重启，也可以稍后再处理。'
        self.changed.emit()

    def install(self):
        if not self.downloaded or self.job:
            return
        if not getattr(sys, 'frozen', False):
            QMessageBox.information(self.dialog, '开发模式', '请使用打包后的点点应用进行安装和重启。')
            return
        if self.pet.editor and self.pet.editor.isVisible():
            QMessageBox.information(self.dialog, '先保存编辑', '请先保存并关闭点点编辑器，再安装更新。')
            return
        box = QMessageBox(QMessageBox.Question, '安装 v' + self.manifest['version'],
                          '安装后会重启点点。旧版目录会保留。', parent=self.dialog)
        keep = QCheckBox('保留我修改过的叫声、动作和互动')
        keep.setChecked(customized(self.program_root, self.pet.content_root))
        box.setCheckBox(keep)
        proceed = box.addButton('安装并重启', QMessageBox.AcceptRole)
        box.addButton('取消', QMessageBox.RejectRole)
        box.exec_()
        if box.clickedButton() is not proceed:
            return
        self.status = '正在准备新版点点…'
        source = self.pet.content_root if keep.isChecked() else None
        try:
            address = read_source(self.program_root)
        except (OSError, ValueError):
            address = ''
        self.run(lambda job: install_package(self.downloaded, self.manifest, self.data_root, source, address, job.cancel), self.installed)

    def installed(self, value, error):
        if error:
            self.status = '安装未完成，当前版本继续使用：' + error
            self.changed.emit()
            return
        try:
            subprocess.Popen([str(value), '--wait-parent', str(os.getpid())], cwd=value.parent,
                             env=dict(os.environ, PYINSTALLER_RESET_ENVIRONMENT='1'), close_fds=True)
            self.status = '正在重启点点…'
            QTimer.singleShot(100, QApplication.instance().quit)
        except OSError as error:
            self.status = '新版未能启动，当前版本继续使用：' + str(error)
        self.changed.emit()

    def skip(self):
        if self.manifest and not self.job:
            self.pet.settings.setValue('update/v1/skip', self.manifest['buildNumber'])
            self.pet.settings.sync()
            self.status = '已略过此版本；手动检查仍可查看。'
            self.changed.emit()

    def cancel(self):
        if self.job:
            self.job.cancel.set()
            self.status = '正在取消…'
            self.changed.emit()

    def stop(self):
        self.check_timer.stop()
        if self.job:
            self.job.cancel.set()
            self.job.wait(14000)

    def set_source(self):
        if self.job:
            return
        try:
            current = read_source(self.program_root)
        except (OSError, ValueError, TypeError):
            current = ''
        value, accepted = QInputDialog.getText(self.dialog, '设置 v1 更新地址', '填写 v1 版本清单的 HTTPS 地址（留空可停用）：', text=current)
        if not accepted:
            return
        try:
            value = value.strip()
            if value:
                https_url(value)
            atomic_json(self.program_root / 'update-source.json', {'manifestUrl': value})
            self.manifest = self.downloaded = None
            self.status = 'v1 更新地址已保存。' if value else '更新地址已清空，不会自动联网。'
        except (OSError, ValueError, TypeError) as error:
            self.status = '地址未保存：' + str(error)
        self.changed.emit()


class UpdateDialog(QDialog):
    def __init__(self, updater):
        super().__init__(updater.pet)
        self.updater = updater
        self.setWindowTitle('点点 v1 · 更新')
        self.resize(510, 330)
        layout = QVBoxLayout(self)
        layout.addWidget(QLabel('写实二维桌宠 · v' + VERSION + ' · 仅接收 v1 系列更新'))
        self.status = QLabel()
        self.status.setWordWrap(True)
        layout.addWidget(self.status)
        self.notes = QTextBrowser()
        layout.addWidget(self.notes)
        self.bar = QProgressBar()
        layout.addWidget(self.bar)
        buttons = QHBoxLayout()
        self.buttons = []
        for label, action in [('检查更新', updater.check), ('下载新版', updater.download), ('安装并重启', updater.install), ('略过此版', updater.skip), ('取消任务', updater.cancel)]:
            button = QPushButton(label)
            button.clicked.connect(lambda checked=False, fn=action: fn())
            buttons.addWidget(button)
            self.buttons.append(button)
        layout.addLayout(buttons)
        self.auto = QCheckBox('每天检查一次，有更新时提醒我')
        self.auto.setChecked(updater.pet.settings.value('update/v1/auto', True, type=bool))
        self.auto.toggled.connect(self.auto_changed)
        layout.addWidget(self.auto)
        self.source_button = QPushButton('设置更新地址…')
        self.source_button.clicked.connect(updater.set_source)
        layout.addWidget(self.source_button)
        updater.changed.connect(self.refresh)
        self.refresh()

    def auto_changed(self, value):
        self.updater.pet.settings.setValue('update/v1/auto', value)
        self.updater.pet.settings.sync()

    def refresh(self):
        updater = self.updater
        self.status.setText(updater.status)
        self.notes.setPlainText(updater.manifest.get('notes', '') if updater.manifest else '更新由你决定。可保留自己编辑过的素材；旧版本目录会保留。')
        self.bar.setValue(updater.progress)
        busy = updater.job is not None
        for button, enabled in zip(self.buttons, [not busy, bool(updater.manifest) and not busy, bool(updater.downloaded) and not busy, bool(updater.manifest) and not busy, busy]):
            button.setEnabled(enabled)
        self.source_button.setEnabled(not busy)
