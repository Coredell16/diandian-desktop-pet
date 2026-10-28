"""Offline update regressions. Fixtures never execute a downloaded program."""
import copy
import hashlib
import io
import json
from pathlib import Path
import stat
import tempfile
import threading
import time
from unittest.mock import patch
import zipfile

from PyQt5.QtCore import QSettings
from PyQt5.QtTest import QTest
from PyQt5.QtWidgets import QWidget


def run(app, temporary_root=None):
    import updater as u
    checked = []

    def rejected(call):
        try:
            call()
        except (ValueError, OSError, KeyError, zipfile.BadZipFile):
            return
        raise AssertionError('Invalid update accepted')

    release = {'schema': 1, 'product': u.PRODUCT, 'series': 'v1', 'version': '1.5.2', 'buildNumber': 10502}
    baseline = dict(release, platform='windows-x64', url='https://updates.example/diandian-v1.zip',
                    size=1, sha256='0' * 64, notes='v1 update')
    assert u.validate_manifest(baseline) == baseline and u.newer(baseline)
    invalid = [('product', 'diandian-v2'), ('series', 'v2'), ('version', '2.0.0'),
               ('version', '1.5'), ('schema', True), ('buildNumber', True), ('size', True),
               ('size', u.MAX_DOWNLOAD+1), ('sha256', 'g'*64), ('platform', 'linux-x64'),
               ('url', 'http://updates.example/v1.zip'), ('url', 'https://me:pw@updates.example/v1.zip'),
               ('url', 'https://updates.example/v1.zip#part'), ('notes', 'x'*8001)]
    for key, value in invalid:
        data = dict(baseline, **{key: value})
        rejected(lambda: u.validate_manifest(data))
    assert not u.newer(dict(baseline, version=u.VERSION))
    rejected(lambda: u.read_json('{"schema":1,"schema":2}'))
    rejected(lambda: u.HttpsRedirect().redirect_request(None, None, 302, '', {}, 'http://updates.example/a'))
    checked.append('v1-only manifest: product, series, version, platform, URL, size/hash and duplicate-key rejection')

    class Response(io.BytesIO):
        def __init__(self, payload, declared=None, url='https://updates.example/test'):
            super().__init__(payload)
            self.headers = {} if declared is None else {'Content-Length': str(declared)}
            self.final_url = url

        def geturl(self):
            return self.final_url

    class Opener:
        def __init__(self, payload, declared=None, url='https://updates.example/test'):
            self.payload, self.declared, self.url = payload, declared, url

        def open(self, request, timeout):
            assert request.full_url.startswith('https://') and timeout > 0
            return Response(self.payload, self.declared, self.url)

    def archive(entries=None, metadata=None):
        buffer = io.BytesIO()
        with zipfile.ZipFile(buffer, 'w', zipfile.ZIP_DEFLATED) as bundle:
            bundle.writestr('点点/点点.exe', b'MZ-test-only-never-executed')
            bundle.writestr('点点/release.json', json.dumps(metadata or release))
            bundle.writestr('点点/update-source.json', '{"manifestUrl":""}')
            bundle.writestr('点点/project.json', '{"sounds":[],"frames":[]}')
            for name in ('motions.png', 'curious.png', 'flight.png', 'diandian.ico'):
                bundle.writestr('点点/'+name, b'new-default-'+name.encode())
            for name, data in entries or []:
                bundle.writestr(name, data)
        return buffer.getvalue()

    scratch = Path(temporary_root) if temporary_root else Path.cwd()/'work'
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='diandian-v1-update-', dir=scratch) as temporary:
        root = Path(temporary)
        payload = archive()
        manifest = dict(baseline, size=len(payload), sha256=hashlib.sha256(payload).hexdigest())
        with patch('urllib.request.build_opener', return_value=Opener(json.dumps(manifest).encode())):
            assert u.fetch_manifest('https://updates.example/latest-v1.json') == manifest
        with patch('urllib.request.build_opener', return_value=Opener(payload, len(payload))):
            downloaded = u.download_package(manifest, root/'valid')
        assert u.verified_package(downloaded, manifest)
        with patch('urllib.request.build_opener') as network:
            assert u.download_package(manifest, root/'valid') == downloaded
            network.assert_not_called()
        for broken in (payload[:-1], b'wrong-content'):
            with patch('urllib.request.build_opener', return_value=Opener(broken)):
                rejected(lambda: u.download_package(manifest, root/'bad-download'))
            assert not list((root/'bad-download').rglob('*.part'))
        with patch('urllib.request.build_opener', return_value=Opener(payload, len(payload)+1)):
            rejected(lambda: u.download_package(manifest, root/'oversized-download'))
        with patch('urllib.request.build_opener', return_value=Opener(payload, url='http://updates.example/test')):
            rejected(lambda: u.download_package(manifest, root/'downgrade-download'))
        event = threading.Event()
        big = b'x' * 180000
        cancelled_manifest = dict(manifest, size=len(big), sha256=hashlib.sha256(big).hexdigest())
        with patch('urllib.request.build_opener', return_value=Opener(big)):
            try:
                u.download_package(cancelled_manifest, root/'cancel', event, lambda *_: event.set())
            except u.Cancelled:
                pass
            else:
                raise AssertionError('Cancelled download accepted')
        assert not list((root/'cancel').rglob('*.part')) and not list((root/'cancel').rglob('*.zip'))
        checked.append('streamed downloads: good/cached package, truncation, wrong hash, oversize, HTTPS downgrade and cancellation')

        source = root/'personal'
        source.mkdir()
        project = {'sounds': [{'file': 'imports/my-call.wav'}], 'frames': [{'file': 'imports/my-pose.png'}]}
        (source/'project.json').write_text(json.dumps(project), encoding='utf-8')
        for name in ['motions.png', 'curious.png', 'flight.png', 'diandian.ico', 'imports/my-call.wav', 'imports/my-pose.png']:
            file = source/name
            file.parent.mkdir(exist_ok=True)
            file.write_bytes(b'personal-'+name.encode())
        factory = root/'factory'
        factory.mkdir()
        (factory/'release.json').write_text(json.dumps(dict(release, contentHashes=u.content_hashes(source))), encoding='utf-8')
        assert not u.customized(factory, source)
        (source/'imports/my-call.wav').write_bytes(b'edited-personal-call')
        assert u.customized(factory, source)
        (factory/'release.json').write_text('[]', encoding='utf-8')
        assert u.customized(factory, source)
        installed = u.install_package(downloaded, manifest, root/'installed', source, 'https://updates.example/latest-v1.json')
        assert installed.is_file() and (installed.parent/'user-content/imports/my-call.wav').read_bytes() == b'edited-personal-call'
        assert (installed.parent/'user-content/project.json').read_bytes() == (source/'project.json').read_bytes()
        assert (installed.parent/'motions.png').read_bytes() == b'new-default-motions.png'
        assert u.read_source(installed.parent) == 'https://updates.example/latest-v1.json'
        assert not (root/'installed/current-version.json').exists(), 'Unstarted app must not become active'
        installed.write_bytes(b'MZ-existing-version-marker')
        rejected(lambda: u.install_package(downloaded, manifest, root/'installed'))
        assert installed.read_bytes() == b'MZ-existing-version-marker'
        default_installed = u.install_package(downloaded, manifest, root/'defaults')
        assert not (default_installed.parent/'user-content').exists()
        assert (default_installed.parent/'project.json').read_text() == '{"sounds":[],"frames":[]}'
        checked.append('install: immutable old versions, fresh defaults, custom audio/frames/project retention and source URL inheritance')

        bad_names = ['点点/../escape.txt', '点点/C:/escape', '点点/a:stream', '点点/CON.txt', '点点/CON .txt',
                     '点点/trailing./file', '/点点/absolute', 'other/product.exe', '点点/a//b', '点点/a/../b']
        bad_payloads = [archive([(name, b'x')]) for name in bad_names]
        bad_payloads += [archive([('点点/DUP', b'a'), ('点点/dup', b'b')]),
                         archive([('点点/parent', b'a'), ('点点/parent/child', b'b')]),
                         archive(metadata=dict(release, product='diandian-v2')),
                         archive(metadata=dict(release, version='1.5.9')), archive(metadata=[1])]
        link = zipfile.ZipInfo('点点/link')
        link.create_system = 3
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        bad_payloads.append(archive([(link, b'../../escape')]))
        for index, broken in enumerate(bad_payloads):
            file = root/f'bad-{index}.zip'
            file.write_bytes(broken)
            invalid_manifest = dict(manifest, size=len(broken), sha256=hashlib.sha256(broken).hexdigest())
            rejected(lambda: u.install_package(file, invalid_manifest, root/f'rejected-{index}'))
            assert not list(root.rglob('.stage-*'))
        with patch.object(u, 'MAX_EXPANDED', 8):
            rejected(lambda: u.install_package(downloaded, manifest, root/'zip-bomb'))
        assert not (root/'escape.txt').exists()
        checked.append('ZIP validation: traversal, drive/ADS/device paths, case duplicates, parent conflict, symlink, wrong product/version and expanded-size limit')

        pointer_root = root/'pointers'
        current = pointer_root/'Versions'/str(u.BUILD_NUMBER)
        current.mkdir(parents=True)
        u.activate_install(current, pointer_root)
        assert json.loads((pointer_root/'current-version.json').read_text())['buildNumber'] == u.BUILD_NUMBER
        future = pointer_root/'Versions'/str(release['buildNumber'])
        future.mkdir()
        (future/'release.json').write_text(json.dumps(release), encoding='utf-8')
        (future/'点点.exe').write_bytes(b'MZ-test-never-executed')
        u.atomic_json(pointer_root/'current-version.json', {'product': u.PRODUCT, 'buildNumber': release['buildNumber']})
        with patch('subprocess.Popen') as launch:
            assert u.launch_newer(root/'old-portable', pointer_root, ['--editor'])
            arguments = launch.call_args.args[0]
            assert arguments[0] == str(future/'点点.exe') and '--wait-parent' in arguments and '--editor' in arguments
            launch.reset_mock()
            assert not u.launch_newer(future, pointer_root, [])
            launch.assert_not_called()
        for bad_pointer in ([], {'product':'diandian-v2','buildNumber':10502}, {'product':u.PRODUCT,'buildNumber':'../../bad'}):
            u.atomic_json(pointer_root/'current-version.json', bad_pointer)
            with patch('subprocess.Popen') as launch:
                assert not u.launch_newer(root/'old-portable', pointer_root, [])
                launch.assert_not_called()
        checked.append('startup: healthy-install activation, newer-only redirect, no redirect loop or cross-series/path pointer launch')

        class Pet(QWidget):
            def __init__(self):
                super().__init__()
                self.settings = QSettings(str(root/'update-test.ini'), QSettings.IniFormat)
                self.content_root, self.editor, self.messages = source, None, []

            def say(self, text, seconds=0):
                self.messages.append(text)

        pet = Pet()
        gui_root = root/'gui'
        gui_root.mkdir()
        for invalid_config in ([], {'manifestUrl': 123}, {'manifestUrl': 'http://bad.example'}):
            u.atomic_json(gui_root/'update-source.json', invalid_config)
            rejected(lambda: u.read_source(gui_root))
        (gui_root/'update-source.json').unlink()
        controller = u.Updater(pet, gui_root, root/'gui-data')
        controller.start()
        assert controller.check_timer.isActive() and controller.check_timer.interval() == 8000

        def wait_job():
            deadline = time.monotonic()+5
            while controller.job and time.monotonic()<deadline:
                QTest.qWait(10)
            assert controller.job is None, 'Update job did not finish'

        with patch('urllib.request.build_opener') as network:
            controller.auto_check()
            assert controller.check_timer.isActive() and controller.check_timer.interval() == 15*60*1000
            controller.check()
            assert controller.job is None and '尚未配置' in controller.status
            network.assert_not_called()
        u.atomic_json(gui_root/'update-source.json', {'manifestUrl':'https://updates.example/latest-v1.json'})
        with patch.object(u, 'fetch_manifest', return_value=manifest):
            controller.check()
            wait_job()
        assert controller.manifest == manifest
        controller.skip()
        with patch.object(u, 'fetch_manifest', return_value=manifest):
            controller.check(True)
            wait_job()
        assert '略过' in controller.status and not pet.messages
        with patch('urllib.request.build_opener', return_value=Opener(payload)):
            controller.download()
            wait_job()
        assert controller.downloaded and u.verified_package(controller.downloaded, manifest)
        controller.open()
        assert controller.dialog.buttons[2].isEnabled()
        controller.dialog.auto.setChecked(False)
        assert pet.settings.value('update/v1/auto', True, type=bool) is False
        controller.dialog.close()
        controller.stop()
        assert not controller.check_timer.isActive()
        pet.close()
        pet.deleteLater()
        app.processEvents()
        checked.append('Qt update UI/jobs: empty source stays offline, manual check, skip, async verified download and persisted auto-check setting')
    return checked
