"""Offline appearance regressions: simulated image service, real editor and renderer."""
import base64
import copy
import hashlib
import json
from pathlib import Path
import shutil
import tempfile
from unittest.mock import patch

from PyQt5.QtCore import QByteArray, QObject, QSettings, Qt, QTimer, pyqtSignal
from PyQt5.QtGui import QColor, QImage, QPainter
from PyQt5.QtNetwork import QNetworkReply, QNetworkRequest, QSslSocket
from PyQt5.QtTest import QTest


def run(app, output):
    from appearance import (GROUPS, AppearancePage, endpoint_url, make_request,
                            png_bytes, read_image, response_image, sheet_cells)
    from content import load_project, validate_project
    from diandian import Diandian, ROOT
    from editor import ProjectEditor
    from updater import content_files, content_hashes
    checked = []
    assert QSslSocket.supportsSsl(), 'The image service requires Qt HTTPS support in the packaged runtime.'

    def rejects(call):
        try:
            call()
        except (OSError, ValueError):
            return
        raise AssertionError('Invalid appearance accepted')

    sheets = {}
    for group, color in zip(GROUPS, ('#e34238', '#42ab59', '#3a75df', '#edbb29')):
        image = QImage(256, 128, QImage.Format_RGBA8888)
        image.fill(Qt.transparent)
        painter = QPainter(image)
        for index in range(8):
            painter.fillRect(index % 4 * 64 + 12, index // 4 * 64 + 8, 40, 48, QColor(color))
        painter.end()
        sheets[group] = image
    photo = sheets['base']
    for endpoint in ('http://example.com/images/edits', 'https://key@example.com/images/edits',
                     'https://example.com/images/edits?key=private', 'https://example.com/images/edits#key',
                     'https://example.com/v1/images/generations'):
        rejects(lambda endpoint=endpoint: endpoint_url(endpoint))
    request, payload = make_request('https://api.openai.com/v1/images/edits', 'session-only-test-key',
        'gpt-image-2.5-sunburst', 'medium', photo, photo, 'base')
    body = json.loads(bytes(payload))
    assert request.attribute(QNetworkRequest.RedirectPolicyAttribute) == QNetworkRequest.ManualRedirectPolicy
    assert request.rawHeader(b'Authorization') == b'Bearer session-only-test-key'
    assert body['background'] == 'transparent' and body['n'] == 1 and len(body['images']) == 2
    assert 'session-only-test-key' not in bytes(payload).decode()
    for bad in (b'{}', b'{"data": []}', b'{"data":[{"b64_json":"bad-base64"}]}'):
        rejects(lambda bad=bad: response_image(bad))
    opaque = QImage(256, 128, QImage.Format_RGB32)
    opaque.fill(Qt.white)
    rejects(lambda: sheet_cells(opaque))
    empty = photo.copy()
    empty.fill(Qt.transparent)
    rejects(lambda: sheet_cells(empty))
    border = photo.copy()
    border.setPixelColor(0, 0, Qt.black)
    rejects(lambda: sheet_cells(border))
    assert len(sheet_cells(photo)) == 8
    checked.append('appearance input limits, empty/opaque/cross-cell sheets and malformed API responses rejected; HTTPS only and redirects refused')

    class Reply(QObject):
        readyRead = pyqtSignal()
        finished = pyqtSignal()

        def __init__(self, image, status, parent):
            super().__init__(parent)
            self.status, self.code = status, QNetworkReply.NoError
            self.data = json.dumps({'data': [{'b64_json': base64.b64encode(png_bytes(image)).decode()}]}).encode()
            QTimer.singleShot(1, self.finish)

        def finish(self):
            if self.code == QNetworkReply.NoError:
                self.readyRead.emit()
                self.finished.emit()

        def readAll(self):
            result, self.data = self.data, b''
            return QByteArray(result)

        def attribute(self, name):
            return self.status

        def error(self):
            return self.code

        def abort(self):
            self.code = QNetworkReply.OperationCanceledError
            self.finished.emit()

    class Network(QObject):
        def __init__(self, page, statuses=None):
            super().__init__(page)
            self.calls, self.statuses = [], list(statuses or [])

        def setProxy(self, proxy):
            pass

        def post(self, request, payload):
            self.calls.append(json.loads(bytes(payload)))
            status = self.statuses.pop(0) if self.statuses else 200
            return Reply(sheets[page.current], status, self)

    def wait(page):
        for _ in range(100):
            app.processEvents()
            if page.reply is None and not page.queue:
                return
            QTest.qWait(10)
        raise AssertionError('Simulated generation did not finish')

    with tempfile.TemporaryDirectory(prefix='appearance-', dir=output) as temporary:
        scratch = Path(temporary)
        root = scratch / 'project'
        root.mkdir()
        original_config = (ROOT / 'project.json').read_bytes()
        original_digest = hashlib.sha256(original_config).digest()
        for name in content_files(ROOT):
            destination = root / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(ROOT / name, destination)
        source = scratch / 'personal-photo.png'
        assert photo.save(str(source))
        source_bytes = source.read_bytes()
        bird = Diandian(settings=QSettings(str(scratch / 'test.ini'), QSettings.IniFormat), content_root=root)
        bird.timer.stop()
        bird.tray.hide()
        bird.muted = True
        editor = ProjectEditor(bird)
        editor.preview.timer.stop()
        page = editor.appearance
        assert isinstance(page, AppearancePage)
        original_sprite = bird.scaled[0].toImage()
        original_project = copy.deepcopy(bird.project)
        try:
            page.network = Network(page, [200, 401])
            with patch('appearance.QFileDialog.getOpenFileName', return_value=(str(source), 'PNG')):
                page.choose_photo()
            page.key.setText('session-only-test-key')
            page.start(False)
            assert page.reply is not None and not page.key.isEnabled()
            wait(page)
            assert list(page.generated) == ['base'] and len(page.network.calls) == 2
            assert page.generate.isEnabled() and 'API Key' in page.message.text()
            page.network = Network(page)
            page.start(False)
            wait(page)
            assert len(page.generated) == 4 and len(page.network.calls) == 3
            assert all(len(call['images']) == 3 for call in page.network.calls)
            assert page.preview.isEnabled() and bird.project == original_project
            page.preview_appearance()
            assert 'appearance' in editor.draft and 'appearance' not in bird.project
            assert editor.preview.normal[0].toImage() != original_sprite
            assert editor.save() and 'appearance' in bird.project
            bird.timer.stop()
            saved = load_project(root)
            for group, indices in [('base', range(8)), ('curious', range(8, 16)), ('play', range(16, 24))]:
                expected = sheets[group].pixelColor(20, 20)
                for index in indices:
                    image = bird.scaled[index].toImage().convertToFormat(QImage.Format_RGBA8888)
                    pixels = image.constBits().asstring(image.byteCount())
                    assert any(pixels[i+3] > 200 and QColor(*pixels[i:i+3]) == expected for i in range(0, len(pixels), 4)), (group, index)
            expected_flight = sheets['flight'].pixelColor(20, 20)
            for sprite in bird.flight_scaled:
                image = sprite.toImage().convertToFormat(QImage.Format_RGBA8888)
                pixels = image.constBits().asstring(image.byteCount())
                assert sum(pixels[i+3] > 200 and QColor(*pixels[i:i+3]) == expected_flight
                           for i in range(0, len(pixels), 4)) > len(pixels) // 400
            assert set(saved['appearance'].values()).issubset(content_files(root))
            hashes = content_hashes(root)
            assert all(name in hashes for name in saved['appearance'].values())
            serialized = (root / 'project.json').read_text(encoding='utf-8')
            assert 'session-only-test-key' not in serialized and 'api.openai.com' not in serialized
            assert not (root / source.name).exists() and source.read_bytes() == source_bytes
            for value in ('../escape.png', str(source), 'missing.png'):
                invalid = copy.deepcopy(saved)
                invalid['appearance']['base'] = value
                rejects(lambda: validate_project(invalid, root))
            invalid = copy.deepcopy(saved)
            del invalid['appearance']['flight']
            rejects(lambda: validate_project(invalid, root))
            # Only a successful response replaces an existing generated group.
            before = page.generated['play']
            page.groups.setCurrentRow(2)
            page.network = Network(page, [429])
            page.start(True)
            wait(page)
            assert page.generated['play'] == before and len(page.network.calls) == 1
            # Cancellation prevents queued requests and leaves the applied pet untouched.
            page.network = Network(page)
            page.start(True)
            page.stop()
            assert page.reply is None and not page.queue and bird.project == saved
            page.start(True)
            page.timed_out()
            assert page.reply is None and '10' in page.message.text()
            page.network = Network(page)
            with patch('appearance.MAX_RESPONSE', 16):
                page.start(True)
                wait(page)
            assert page.reply is None and '大小限制' in page.message.text()
            editor.resize(1100, 900)
            editor.show()
            app.processEvents()
            assert editor.grab().save(str(Path(output) / 'appearance-editor.png'))
            page.restore_default()
            assert editor.save() and 'appearance' not in bird.project
            assert bird.scaled[0].toImage() == original_sprite
            assert hashlib.sha256((ROOT / 'project.json').read_bytes()).digest() == original_digest
            checked.append('simulated four-group generation, partial failure/resume, single-group retry, cancellation and timeout; real preview/save/restore and all default pose overrides')
            checked.append('personal appearance assets survive content enumeration/hashing; original photo untouched; no API key or service address persisted; unsafe and incomplete appearance mappings rejected')
        finally:
            editor.draft = copy.deepcopy(editor.saved)
            editor.close()
            assert not page.key.text()
            bird.timer.stop()
            bird.stop_call()
            bird.tray.hide()
            bird.close()
            app.processEvents()
    return checked
