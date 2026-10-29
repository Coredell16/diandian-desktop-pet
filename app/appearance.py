"""Photo-based v1 appearances: four eight-pose sheets, generated only on request."""
import base64
import binascii
import json
import uuid
from pathlib import Path
from urllib.parse import urlsplit

from PyQt5.QtCore import QBuffer, QByteArray, QIODevice, Qt, QTimer, QUrl
from PyQt5.QtGui import QImage, QImageReader, QPainter, QPixmap
from PyQt5.QtNetwork import (QNetworkAccessManager, QNetworkProxyFactory,
    QNetworkProxyQuery, QNetworkReply, QNetworkRequest)
from PyQt5.QtWidgets import (QComboBox, QFileDialog, QFormLayout, QHBoxLayout,
    QLabel, QLineEdit, QListWidget, QPushButton, QVBoxLayout, QWidget)


GROUPS = {'base': '站立与走路', 'curious': '好奇与冠羽',
          'play': '咬手与摇摇车', 'flight': '展翅与飞行'}
POSES = {
    'base': 'resting, eyes closed blinking, head turned inquisitively, leaning forward, '
            'walking step 1, walking step 2, walking step 3, walking step 4',
    'curious': 'crest slightly raised, crest rising, crest fully raised, curious lean, '
               'looking down, head tilted, looking back, relaxed crest',
    'play': 'lean toward an implied hand, beak opening, gentle nibbling, beak closing, '
            'on a small wooden T perch at rest, on the same perch leaning left, '
            'on the same perch leaning right, on the same perch returning to rest',
    'flight': 'wings folded preparing to fly, wings opening, wings fully raised, '
              'wings beginning downstroke, wings fully lowered, wings beginning upstroke, '
              'wings halfway raised, wings almost fully raised',
}
MAX_IMAGE = 30 * 1024**2
MAX_RESPONSE = 48 * 1024**2


def read_image(source):
    """Inspect dimensions before decoding; a QImage alone can allocate too much."""
    buffer = None
    if isinstance(source, bytes):
        if len(source) > MAX_IMAGE:
            raise ValueError('图片请控制在 30 MB 以内。')
        buffer = QBuffer()
        buffer.setData(source)
        buffer.open(QIODevice.ReadOnly)
        reader = QImageReader(buffer)
    else:
        path = Path(source)
        if not path.is_file() or path.stat().st_size > MAX_IMAGE:
            raise ValueError('图片不存在或超过 30 MB。')
        reader = QImageReader(str(path))
    reader.setAutoTransform(True)
    size = reader.size()
    if not size.isValid() or max(size.width(), size.height()) > 8192:
        raise ValueError('请选择可以读取且不超过 8192 × 8192 像素的图片。')
    image = reader.read()
    if image.isNull():
        raise ValueError('图片无法解码，请换一张 PNG、JPG 或 WebP 图片。')
    return image.convertToFormat(QImage.Format_RGBA8888)


def png_bytes(image):
    buffer = QBuffer()
    buffer.open(QIODevice.WriteOnly)
    if not image.save(buffer, 'PNG'):
        raise ValueError('图片转换失败。')
    return bytes(buffer.data())


def sheet_cells(image):
    """Reject missing poses, opaque backgrounds and silhouettes crossing cell borders."""
    width, height = image.width(), image.height()
    if not image.hasAlphaChannel() or width % 4 or height % 2 or min(width // 4, height // 2) < 32:
        raise ValueError('动作图需为透明 PNG，按 4 列 × 2 行排列 8 个完整姿态。')
    cells = []
    for index in range(8):
        cell = image.copy(index % 4 * (width // 4), index // 4 * (height // 2), width // 4, height // 2)
        cell = cell.convertToFormat(QImage.Format_RGBA8888)
        alpha = cell.constBits().asstring(cell.byteCount())[3::4]
        w = cell.width()
        border = alpha[:w] + alpha[-w:] + alpha[::w] + alpha[w - 1::w]
        if max(alpha) <= 16 or max(border) > 16:
            raise ValueError(f'第 {index + 1} 个姿态为空、背景不透明或碰到分格边界，请重新生成这一组。')
        cells.append(cell)
    return cells


def endpoint_url(text):
    value = text.strip()
    parsed = urlsplit(value)
    if (parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password
            or parsed.query or parsed.fragment or not parsed.path.endswith('/images/edits')):
        raise ValueError('服务地址需为 HTTPS 的 /images/edits 接口，且不能包含账号、密钥或查询参数。')
    return value


def make_request(endpoint, key, model, quality, photo, guide, group, reference=None):
    endpoint = endpoint_url(endpoint)
    if not key.strip() or any(ord(c) < 33 or ord(c) > 126 for c in key):
        raise ValueError('请填写有效的 API Key（不要包含空格或换行）。')
    if not model.strip() or len(model) > 128:
        raise ValueError('请填写服务支持的图像编辑模型名称。')
    images = [photo, guide] + ([reference] if reference is not None else [])
    prompt = (
        'Create a photorealistic transparent PNG sprite sheet of the ONE pet cockatiel in image 1. '
        'Preserve its individual feather colors, cheek patches, crest, beak, proportions and long tail. '
        'Image 2 is ONLY a pose/layout guide; do NOT copy that bird identity or colors. '
        'Any third image is a previously generated sheet of this same pet; match its identity and style. '
        'Exactly 8 separate full-body poses, 4 equal columns and 2 equal rows, row-major order. '
        'One complete bird per cell, facing left, consistent body size and feet baseline. '
        'Keep transparent padding on ALL FOUR sides of EVERY cell; no crossing cell boundaries, '
        'no cropped tail or crest, no text, no grid lines, no shadows, no background or checkerboard. '
        'No hands or humans. Only include a wooden T perch in the last four play poses. '
        'Poses in order: ' + POSES[group] + '.'
    )
    payload = {'model': model.strip(), 'images': [
        {'image_url': 'data:image/png;base64,' + base64.b64encode(png_bytes(image)).decode('ascii')}
        for image in images], 'prompt': prompt, 'n': 1, 'size': '1536x1024',
        'background': 'transparent', 'output_format': 'png', 'quality': quality}
    request = QNetworkRequest(QUrl(endpoint))
    request.setHeader(QNetworkRequest.ContentTypeHeader, 'application/json')
    request.setRawHeader(b'Authorization', ('Bearer ' + key).encode('ascii'))
    # Refuse redirects rather than forwarding a user credential to another host.
    request.setAttribute(QNetworkRequest.RedirectPolicyAttribute, QNetworkRequest.ManualRedirectPolicy)
    return request, QByteArray(json.dumps(payload).encode('utf-8'))


def response_image(raw):
    try:
        data = json.loads(raw)
        encoded = data['data'][0]['b64_json']
        if not isinstance(encoded, str) or len(encoded) > MAX_IMAGE * 4 // 3 + 4:
            raise ValueError()
        image = read_image(base64.b64decode(encoded, validate=True))
    except (ValueError, TypeError, KeyError, IndexError, binascii.Error):
        raise ValueError('服务未返回有效图片；需要支持 Images Edit JSON 请求和 data[0].b64_json 响应。') from None
    sheet_cells(image)
    return image


class AppearancePage(QWidget):
    def __init__(self, editor):
        super().__init__()
        self.editor = editor
        self.photo = None
        self.generated, self.guides = {}, {}
        self.reply = None
        self.queue = []
        self.raw = bytearray()
        self.timer = QTimer(self)
        self.timer.setSingleShot(True)
        self.timer.timeout.connect(self.timed_out)
        self.network = QNetworkAccessManager(self)
        layout = QVBoxLayout(self)
        intro = QLabel('选一张自己的玄凤照片，生成整套二维动作。先预览，再保存应用；可恢复默认点点。')
        intro.setWordWrap(True)
        layout.addWidget(intro)
        self.photo_label = QLabel('尚未选择照片（建议主体完整、羽色清晰、无遮挡）')
        self.photo_label.setMinimumHeight(90)
        self.photo_label.setAlignment(Qt.AlignCenter)
        layout.addWidget(self.photo_label)
        self.choose = QPushButton('选择玄凤照片…')
        self.choose.clicked.connect(self.choose_photo)
        layout.addWidget(self.choose)
        form = QFormLayout()
        self.endpoint = QLineEdit('https://api.openai.com/v1/images/edits')
        self.endpoint.setToolTip('可填写兼容 Images Edit JSON 格式的 HTTPS 接口；并非所有图像服务都兼容。')
        self.model = QLineEdit('gpt-image-2.5-sunburst')
        self.key = QLineEdit()
        self.key.setEchoMode(QLineEdit.Password)
        self.key.setPlaceholderText('使用你自己的 API Key；关闭编辑器即清空')
        self.quality = QComboBox()
        for label, value in [('标准', 'medium'), ('高', 'high'), ('低', 'low')]:
            self.quality.addItem(label, value)
        form.addRow('图像服务地址', self.endpoint)
        form.addRow('图像模型', self.model)
        form.addRow('API Key', self.key)
        form.addRow('生成质量', self.quality)
        layout.addLayout(form)
        notice = QLabel('点击生成会把照片和姿态参考图发送到上方服务，并使用你自己的 API 额度。'
                        '整套为 4 次图像请求；重生成一组为 1 次。密钥不保存、不随项目分享。')
        notice.setWordWrap(True)
        notice.setObjectName('muted')
        layout.addWidget(notice)
        self.groups = QListWidget()
        for label in GROUPS.values():
            self.groups.addItem(label + ' · 未生成')
        self.groups.setCurrentRow(0)
        self.groups.setMaximumHeight(116)
        self.groups.currentRowChanged.connect(self.show_sheet)
        layout.addWidget(self.groups)
        row = QHBoxLayout()
        self.generate = QPushButton('生成整套动作 / 继续未完成组')
        self.generate.clicked.connect(lambda: self.start(False))
        self.regenerate = QPushButton('重新生成选中组')
        self.regenerate.clicked.connect(lambda: self.start(True))
        self.cancel = QPushButton('停止生成')
        self.cancel.clicked.connect(self.stop)
        self.cancel.setEnabled(False)
        for control in (self.generate, self.regenerate, self.cancel):
            row.addWidget(control)
        layout.addLayout(row)
        current = '当前已使用个人外观。' if editor.draft.get('appearance') else '当前使用默认点点。'
        self.message = QLabel(current + '生成结果需要人工预览，尤其是羽色、翅膀和尾羽。')
        self.message.setWordWrap(True)
        layout.addWidget(self.message)
        row = QHBoxLayout()
        self.preview = QPushButton('预览这套外观')
        self.preview.setEnabled(False)
        self.preview.clicked.connect(self.preview_appearance)
        self.restore = QPushButton('恢复默认点点')
        self.restore.clicked.connect(self.restore_default)
        row.addWidget(self.preview)
        row.addWidget(self.restore)
        layout.addLayout(row)
        layout.addStretch()
        self.sheet_preview = QLabel('生成后点击分组查看动作图')
        self.sheet_preview.setAlignment(Qt.AlignCenter)
        self.sheet_preview.setMinimumHeight(105)
        layout.addWidget(self.sheet_preview)

    def choose_photo(self):
        filename, _ = QFileDialog.getOpenFileName(self, '选择自己的玄凤照片', '', '图片 (*.png *.jpg *.jpeg *.webp)')
        if not filename:
            return
        try:
            photo = read_image(filename)
            if max(photo.width(), photo.height()) > 2048:
                photo = photo.scaled(2048, 2048, Qt.KeepAspectRatio, Qt.SmoothTransformation)
            self.photo = photo
            self.generated.clear()
            self.photo_label.setPixmap(QPixmap.fromImage(photo).scaled(160, 95, Qt.KeepAspectRatio, Qt.SmoothTransformation))
            self.refresh_groups()
            self.message.setText('照片已选择。填写图像服务信息后点击生成，原照片不会被改动。')
        except (OSError, ValueError) as error:
            self.editor.fail('无法选择照片', error)

    def prepare_guides(self):
        draft = dict(self.editor.draft)
        draft.pop('appearance', None)
        normal, flight = self.editor.pet.preview_frames(draft, size=500)
        if len(normal) < 24:
            raise ValueError('默认互动姿态不足，无法制作整套外观参考。')
        for group, sprites in zip(GROUPS, (normal[:8], normal[8:16], normal[16:24], flight)):
            sheet = QImage(1536, 1024, QImage.Format_RGBA8888)
            sheet.fill(Qt.transparent)
            painter = QPainter(sheet)
            for i, sprite in enumerate(sprites):
                target = sprite.scaled(360, 480, Qt.KeepAspectRatio, Qt.SmoothTransformation)
                painter.drawPixmap(i % 4 * 384 + (384 - target.width()) // 2,
                                   i // 4 * 512 + 512 - 16 - target.height(), target)
            painter.end()
            self.guides[group] = sheet

    def start(self, selected):
        if self.reply is not None:
            return
        try:
            if self.photo is None:
                raise ValueError('请先选择一张自己的玄凤照片。')
            groups = list(GROUPS)
            queue = [groups[self.groups.currentRow()]] if selected else [g for g in groups if g not in self.generated]
            if not queue:
                self.message.setText('四组已生成，可预览整套外观；需要调整时选择一组重新生成。')
                return
            self.prepare_guides()
            make_request(self.endpoint.text(), self.key.text().strip(), self.model.text(),
                         self.quality.currentData(), self.photo, self.guides[queue[0]], queue[0])
            self.queue = queue
            self.next_request()
        except (OSError, ValueError) as error:
            self.editor.fail('暂时无法生成', error)

    def busy(self, active):
        for control in (self.choose, self.endpoint, self.model, self.key, self.quality,
                        self.generate, self.regenerate, self.restore):
            control.setEnabled(not active)
        self.preview.setEnabled(not active and len(self.generated) == 4)
        self.cancel.setEnabled(active)

    def next_request(self):
        if not self.queue:
            self.busy(False)
            self.message.setText('四组动作已准备好。请逐组检查后点击“预览这套外观”，再到右侧切换动作查看效果。')
            return
        self.current = self.queue.pop(0)
        request, payload = make_request(self.endpoint.text(), self.key.text().strip(), self.model.text(),
            self.quality.currentData(), self.photo, self.guides[self.current], self.current,
            self.generated.get('base') if self.current != 'base' else None)
        proxies = QNetworkProxyFactory.systemProxyForQuery(QNetworkProxyQuery(request.url()))
        if proxies:
            self.network.setProxy(proxies[0])
        self.raw = bytearray()
        self.failure = ''
        self.busy(True)
        self.message.setText('正在生成：' + GROUPS[self.current] + '。每组可能需要几分钟，请保持窗口打开。')
        reply = self.reply = self.network.post(request, payload)
        reply.readyRead.connect(lambda: self.receive(reply))
        reply.finished.connect(lambda: self.finished(reply))
        self.timer.start(10 * 60 * 1000)

    def receive(self, reply):
        if self.reply is not reply:
            return
        self.raw.extend(bytes(reply.readAll()))
        if len(self.raw) > MAX_RESPONSE and not self.failure:
            self.failure = '服务返回内容超过大小限制，已停止本地接收。'
            self.queue.clear()
            reply.abort()

    def finished(self, reply):
        if self.reply is not reply:
            reply.deleteLater()
            return
        self.timer.stop()
        self.receive(reply)
        self.reply = None
        try:
            status = reply.attribute(QNetworkRequest.HttpStatusCodeAttribute)
            if self.failure:
                raise ValueError(self.failure)
            if status != 200 or reply.error() != QNetworkReply.NoError:
                messages = {401: 'API Key 无效或无权限。', 403: '账号无此图像模型权限。',
                            429: '服务额度不足或请求过于频繁，请检查账号后再试。'}
                raise ValueError(messages.get(status, f'图像服务请求未成功（HTTP {status or "无响应"}）。请检查地址、模型、网络和服务兼容性。'))
            image = response_image(bytes(self.raw))
            self.generated[self.current] = image
            self.refresh_groups()
        except ValueError as error:
            self.queue.clear()
            self.message.setText(str(error) + ' 已完成的分组仍保留，可继续或重生成。')
            self.busy(False)
        else:
            self.next_request()
        finally:
            self.raw.clear()
            reply.deleteLater()

    def timed_out(self):
        self.failure = '等待本组超过 10 分钟，已停止本地等待；服务端可能仍在处理并计费。'
        self.queue.clear()
        if self.reply is not None:
            self.reply.abort()

    def stop(self):
        self.queue.clear()
        if self.reply is not None:
            self.failure = '已停止本地生成等待；正在处理的请求可能仍由服务端计费。'
            self.reply.abort()

    def refresh_groups(self):
        for i, group in enumerate(GROUPS):
            self.groups.item(i).setText(GROUPS[group] + (' · 已生成' if group in self.generated else ' · 未生成'))
        self.preview.setEnabled(self.reply is None and len(self.generated) == 4)
        self.show_sheet()

    def show_sheet(self, *args):
        group = list(GROUPS)[max(0, self.groups.currentRow())]
        image = self.generated.get(group)
        if image is not None:
            self.sheet_preview.setPixmap(QPixmap.fromImage(image).scaled(480, 160, Qt.KeepAspectRatio, Qt.SmoothTransformation))
        else:
            self.sheet_preview.setText('本组尚未生成')

    def preview_appearance(self):
        if len(self.generated) != 4 or self.reply is not None:
            return
        try:
            relative = Path('appearance') / uuid.uuid4().hex
            folder = (self.editor.root / relative).resolve()
            if not folder.is_relative_to(self.editor.root.resolve()):
                raise ValueError('外观保存目录不能超出当前项目文件夹。')
            folder.mkdir(parents=True)
            mapping = {}
            for group, image in self.generated.items():
                sheet_cells(image)
                path = relative / (group + '.png')
                if not image.save(str(self.editor.root / path), 'PNG'):
                    raise OSError('生成的动作图片无法保存。')
                mapping[group] = path.as_posix()
            before = self.editor.draft.get('appearance')
            self.editor.draft['appearance'] = mapping
            try:
                self.editor.preview.reload()
            except (OSError, ValueError):
                if before is None:
                    self.editor.draft.pop('appearance', None)
                else:
                    self.editor.draft['appearance'] = before
                raise
            self.editor.refresh_gallery()
            self.editor.refresh_action_list()
            self.editor.changed()
            self.message.setText('新外观已载入预览。请在“动作”页查看；满意后点击“保存并应用”。')
        except (OSError, ValueError) as error:
            self.editor.fail('无法预览外观', error)

    def restore_default(self):
        if self.reply is not None:
            return
        self.editor.draft.pop('appearance', None)
        self.editor.preview.reload()
        self.editor.refresh_gallery()
        self.editor.refresh_action_list()
        self.editor.changed()
        self.message.setText('预览已恢复默认点点。点击“保存并应用”后生效；生成图片仍保留在本地。')

    def shutdown(self):
        self.stop()
        self.key.clear()
        self.raw.clear()
