"""Local, visual editing of the pet's sounds, animations and interactions."""
import copy
import shutil
import struct
import time
import uuid
import wave
from contextlib import contextmanager
from pathlib import Path

from PyQt5.QtCore import QRectF, QSize, Qt, QTimer, QUrl
from PyQt5.QtGui import QColor, QIcon, QImage, QPainter, QPen
from PyQt5.QtMultimedia import QSoundEffect
from PyQt5.QtWidgets import (QAbstractItemView, QCheckBox, QComboBox, QDialog,
    QDoubleSpinBox, QFileDialog, QFormLayout, QHBoxLayout, QLabel,
    QLineEdit, QListView, QListWidget, QListWidgetItem, QMessageBox, QPushButton,
    QScrollArea, QSpinBox, QTabWidget, QVBoxLayout, QWidget)

from content import asset_path, validate_project
from updater import VERSION


BUILTIN_ACTIONS = {'pet', 'eat', 'look', 'walk', 'curious', 'bite', 'rock', 'stretch'}


def button(text, callback):
    control = QPushButton(text)
    control.clicked.connect(callback)
    return control


def spin(low, high, step=1, decimals=0):
    control = QDoubleSpinBox() if decimals else QSpinBox()
    control.setRange(low, high)
    control.setSingleStep(step)
    if decimals:
        control.setDecimals(decimals)
    return control


class Waveform(QWidget):
    def __init__(self):
        super().__init__()
        self.peaks = []
        self.setMinimumHeight(88)

    def load(self, path):
        self.peaks = []
        with wave.open(str(path), 'rb') as audio:
            channels, width, rate, count = (audio.getnchannels(), audio.getsampwidth(),
                                           audio.getframerate(), audio.getnframes())
            if width == 2 and count:
                # Read one display column at a time, never a large list of float samples.
                visible = min(count, rate * 60)
                stride = max(1, (visible + 179) // 180)
                for offset in range(0, visible, stride):
                    data = audio.readframes(min(stride, visible - offset))
                    self.peaks.append(max((abs(sample[0]) / 32768
                        for sample in struct.iter_unpack('<h', data)), default=0))
        self.update()
        return count / rate, channels, rate

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.fillRect(self.rect(), QColor('#eff3ed'))
        painter.setPen(QPen(QColor('#62816a'), 2))
        middle = self.height() / 2
        maximum = max(self.peaks, default=1) or 1
        for index, peak in enumerate(self.peaks):
            x = 8 + index * max(1, self.width() - 16) / max(1, len(self.peaks))
            height = max(1, peak / maximum * (middle - 12))
            painter.drawLine(round(x), round(middle - height), round(x), round(middle + height))
        painter.end()


class ActionPreview(QWidget):
    def __init__(self, editor):
        super().__init__()
        self.editor = editor
        self.elapsed = 0.0
        self.last_tick = time.monotonic()
        self.playing = True
        self.normal, self.flight = [], []
        self.setMinimumSize(280, 300)
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.tick)
        self.timer.start(33)

    def reload(self):
        self.normal, self.flight = self.editor.pet.preview_frames(self.editor.draft, size=280)
        self.update()

    def restart(self):
        self.elapsed = 0.0
        self.last_tick = time.monotonic()
        self.playing = True
        self.editor.play_button.setText('暂停')
        self.update()

    def toggle(self):
        self.playing = not self.playing
        self.last_tick = time.monotonic()
        self.editor.play_button.setText('暂停' if self.playing else '播放')

    def tick(self):
        now = time.monotonic()
        if self.playing:
            self.elapsed += min(now - self.last_tick, 0.1)
            action = self.editor.draft['actions'].get(self.editor.action_id, {})
            duration = max(0.1, action.get('duration', 3))
            if self.elapsed > duration + 0.6:
                self.elapsed = 0.0
            self.update()
        self.last_tick = now

    def paintEvent(self, event):
        painter = QPainter(self)
        painter.setRenderHint(QPainter.Antialiasing)
        painter.fillRect(self.rect(), QColor('#edf1e8'))
        painter.setPen(QPen(QColor('#e3e9dd'), 1))
        for x in range(0, self.width(), 20):
            painter.drawLine(x, 0, x, self.height())
        for y in range(0, self.height(), 20):
            painter.drawLine(0, y, self.width(), y)
        if self.normal and self.editor.action_id:
            action = self.editor.action_entry()
            sequence = action.get('frames', []) if action else []
            if action and action.get('kind') != 'flight' and (not sequence or
                    any(not isinstance(frame, int) or not 0 <= frame < len(self.normal) for frame in sequence)):
                painter.setPen(QColor('#748079'))
                painter.drawText(self.rect(), Qt.AlignCenter, '从姿态列表加入至少一帧')
                painter.end()
                return
            painter.save()
            try:
                self.editor.pet.draw_preview(painter, QRectF(self.rect()), self.editor.draft,
                    self.editor.action_id, self.elapsed, self.normal, self.flight)
            except (KeyError, IndexError, TypeError, ValueError) as error:
                painter.restore()
                painter.setPen(QColor('#945b45'))
                painter.drawText(self.rect().adjusted(14, 14, -14, -14),
                                 Qt.AlignCenter | Qt.TextWordWrap, '请补全动作设置\n' + str(error))
            else:
                painter.restore()
        painter.end()


class ProjectEditor(QDialog):
    def __init__(self, pet):
        super().__init__(pet, Qt.Window)
        self.pet = pet
        self.root = Path(pet.content_root)
        self.draft = copy.deepcopy(pet.project)
        self.saved = copy.deepcopy(self.draft)
        self.loading = False
        self.action_id = next(iter(self.draft['actions']), None)
        self.pending_audio = False
        self.audio = QSoundEffect(self)
        self.audio.setVolume(0.3)
        self.audio.statusChanged.connect(self.audio_ready)
        self.setAttribute(Qt.WA_DeleteOnClose)
        self.setWindowTitle(f'玄凤点点桌宠 · v{VERSION} 可视化编辑器')
        self.resize(1060, 760)
        self.setMinimumSize(920, 650)
        self.setStyleSheet('''
            QDialog, QWidget { color: #35433b; font-family: "Microsoft YaHei"; font-size: 12px; }
            QDialog { background: #faf9f5; }
            QLabel#heading { font-size: 23px; font-weight: 600; }
            QLabel#muted { color: #748079; }
            QTabWidget::pane { border: 1px solid #dedfd7; background: #fffefb; }
            QTabBar::tab { padding: 10px 24px; background: #eceee8; }
            QTabBar::tab:selected { background: #fffefb; color: #365f43; }
            QLineEdit, QSpinBox, QDoubleSpinBox, QComboBox, QListWidget {
                background: #fffefb; border: 1px solid #d6dbd2; border-radius: 4px; padding: 5px; }
            QListWidget::item:selected { background: #dde9dc; color: #294832; }
            QPushButton { background: #edf1e8; border: 1px solid #d3ddce; border-radius: 5px; padding: 7px 12px; }
            QPushButton:hover { background: #e1eadd; }
            QPushButton:disabled { color: #a3aaa2; background: #f2f3ef; }
            QPushButton#primary { background: #4e7158; color: white; border: 0; padding: 10px 22px; }
            QScrollArea { border: 0; }
        ''')
        layout = QVBoxLayout(self)
        layout.setContentsMargins(22, 18, 22, 18)
        heading = QLabel(f'玄凤点点工作台 · v{VERSION}')
        heading.setObjectName('heading')
        layout.addWidget(heading)
        description = QLabel('生成自己的玄凤外观，试听叫声、编排动作。预览满意后，保存并应用。')
        description.setObjectName('muted')
        layout.addWidget(description)
        body = QHBoxLayout()
        self.tabs = QTabWidget()
        from appearance import AppearancePage
        self.appearance = AppearancePage(self)
        appearance_scroll = QScrollArea()
        appearance_scroll.setWidgetResizable(True)
        appearance_scroll.setWidget(self.appearance)
        self.tabs.addTab(appearance_scroll, '外观生成')
        self.build_sounds()
        self.build_actions()
        self.build_interactions()
        body.addWidget(self.tabs, 1)
        side = QVBoxLayout()
        self.preview_title = QLabel('动作预览')
        side.addWidget(self.preview_title)
        self.preview = ActionPreview(self)
        side.addWidget(self.preview)
        playback = QHBoxLayout()
        self.play_button = button('暂停', self.preview.toggle)
        playback.addWidget(self.play_button)
        playback.addWidget(button('重播', self.preview.restart))
        side.addLayout(playback)
        self.preview_info = QLabel('选择动作查看效果；预览不会发出叫声。')
        self.preview_info.setWordWrap(True)
        self.preview_info.setObjectName('muted')
        side.addWidget(self.preview_info)
        side.addStretch()
        self.location = QLabel('项目位置\n' + str(self.root))
        self.location.setWordWrap(True)
        self.location.setTextInteractionFlags(Qt.TextSelectableByMouse)
        self.location.setObjectName('muted')
        side.addWidget(self.location)
        side_widget = QWidget()
        side_widget.setLayout(side)
        side_widget.setFixedWidth(300)
        body.addWidget(side_widget)
        layout.addLayout(body, 1)
        footer = QHBoxLayout()
        self.status = QLabel('所有修改先在这里预览，点击保存后应用。')
        footer.addWidget(self.status, 1)
        footer.addWidget(button('关闭', self.close))
        apply_button = button('保存并应用', self.save)
        apply_button.setObjectName('primary')
        footer.addWidget(apply_button)
        layout.addLayout(footer)
        self.preview.reload()
        self.refresh_gallery()
        self.refresh_sound_list()
        self.refresh_action_list()
        self.load_interactions()

    @contextmanager
    def filling(self):
        previous, self.loading = self.loading, True
        try:
            yield
        finally:
            self.loading = previous

    def changed(self):
        if not self.loading:
            self.status.setText('有未保存的修改')
            self.preview.update()

    def fail(self, title, error):
        QMessageBox.warning(self, title, str(error))

    def build_sounds(self):
        page = QWidget()
        layout = QHBoxLayout(page)
        self.sounds = QListWidget()
        self.sounds.setMaximumWidth(175)
        self.sounds.currentRowChanged.connect(self.select_sound)
        layout.addWidget(self.sounds)
        right = QVBoxLayout()
        form = QFormLayout()
        self.sound_name = QLineEdit()
        self.sound_name.textChanged.connect(self.change_sound)
        self.sound_enabled = QCheckBox('加入随机叫声和菜单')
        self.sound_enabled.toggled.connect(self.change_sound)
        form.addRow('名字', self.sound_name)
        form.addRow('', self.sound_enabled)
        right.addLayout(form)
        self.sound_info = QLabel()
        self.sound_info.setWordWrap(True)
        self.sound_info.setObjectName('muted')
        right.addWidget(self.sound_info)
        self.waveform = Waveform()
        right.addWidget(self.waveform)
        controls = QHBoxLayout()
        controls.addWidget(button('试听', self.play_sound))
        controls.addWidget(button('停止', self.stop_sound))
        right.addLayout(controls)
        right.addWidget(button('导入新的 WAV 叫声', lambda: self.import_sound(False)))
        right.addWidget(button('替换所选叫声', lambda: self.import_sound(True)))
        right.addWidget(button('移出叫声列表', self.remove_sound))
        hint = QLabel('支持单声道或双声道、16 位 PCM WAV。试听音量 30%。\n移出列表不会删除素材文件，也不会修改原始录音。')
        hint.setObjectName('muted')
        hint.setWordWrap(True)
        right.addWidget(hint)
        right.addStretch()
        layout.addLayout(right, 1)
        self.tabs.addTab(page, '叫声')

    def sound_entry(self):
        index = self.sounds.currentRow()
        return self.draft['sounds'][index] if 0 <= index < len(self.draft['sounds']) else None

    def refresh_sound_list(self, selected=None):
        selected = self.sounds.currentRow() if selected is None else selected
        with self.filling():
            self.sounds.clear()
            for sound in self.draft['sounds']:
                self.sounds.addItem(sound['label'] + ('' if sound.get('enabled', True) else ' · 停用'))
            self.sounds.setCurrentRow(max(0, min(selected, self.sounds.count() - 1)))
        self.select_sound(self.sounds.currentRow())
        self.refresh_sound_choices()

    def select_sound(self, index):
        if self.loading:
            return
        self.stop_sound()
        sound = self.sound_entry()
        with self.filling():
            self.sound_name.setText(sound['label'] if sound else '')
            self.sound_enabled.setChecked(sound.get('enabled', True) if sound else False)
            self.sound_name.setEnabled(sound is not None)
            self.sound_enabled.setEnabled(sound is not None)
        self.waveform.peaks = []
        self.waveform.update()
        self.sound_info.setText('导入一段叫声开始编辑。')
        if sound:
            try:
                seconds, channels, rate = self.waveform.load(asset_path(self.root, sound['file']))
                self.sound_info.setText(f'{seconds:.2f} 秒 · {rate} Hz · {channels} 声道\n{sound["file"]}')
            except (OSError, ValueError, EOFError, wave.Error) as error:
                self.sound_info.setText('无法读取这段叫声：' + str(error))

    def change_sound(self, *args):
        if self.loading or not self.sound_entry():
            return
        sound = self.sound_entry()
        sound.update(label=self.sound_name.text(), enabled=self.sound_enabled.isChecked())
        self.sounds.currentItem().setText(sound['label'] + ('' if sound['enabled'] else ' · 停用'))
        self.refresh_sound_choices()
        self.changed()

    def import_sound(self, replace):
        sound = self.sound_entry()
        if replace and sound is None:
            return
        filename, _ = QFileDialog.getOpenFileName(self, '选择叫声', '', 'WAV 音频 (*.wav)')
        if not filename:
            return
        source = Path(filename)
        try:
            if source.stat().st_size > 30 * 1024 * 1024:
                raise ValueError('单段叫声请控制在 30 MB 以内。')
            with wave.open(str(source), 'rb') as audio:
                if (audio.getcomptype() != 'NONE' or audio.getsampwidth() != 2 or
                        audio.getnchannels() not in (1, 2)):
                    raise ValueError('请选择单声道或双声道、16 位 PCM WAV 音频。')
                if not 8000 <= audio.getframerate() <= 96000:
                    raise ValueError('叫声采样率应在 8000～96000 Hz 之间。')
                seconds = audio.getnframes() / audio.getframerate()
                if not 0.05 <= seconds <= 120:
                    raise ValueError('单段叫声请控制在 0.05～120 秒。')
                expected = audio.getnframes() * audio.getnchannels() * 2
                if len(audio.readframes(audio.getnframes())) != expected:
                    raise ValueError('这段 WAV 数据不完整。')
            relative = 'calls/user-' + uuid.uuid4().hex[:12] + '.wav'
            target = asset_path(self.root, relative)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            if replace:
                sound['file'] = relative
                for key in ('source', 'start', 'end', 'selection_reason'):
                    sound.pop(key, None)
                index = self.sounds.currentRow()
            else:
                self.draft['sounds'].append({'id': 'call_' + uuid.uuid4().hex[:8],
                    'label': source.stem, 'file': relative, 'enabled': True})
                index = len(self.draft['sounds']) - 1
            self.refresh_sound_list(index)
            self.changed()
        except (OSError, ValueError, EOFError, wave.Error) as error:
            self.fail('无法导入叫声', error)

    def remove_sound(self):
        sound = self.sound_entry()
        if not sound:
            return
        self.stop_sound()
        sound_id = sound['id']
        self.draft['sounds'].remove(sound)
        for action in self.draft['actions'].values():
            if action.get('sound') == sound_id:
                action['sound'] = 'none'
        self.refresh_sound_list()
        self.load_action()
        self.changed()

    def play_sound(self):
        sound = self.sound_entry()
        if not sound:
            return
        try:
            path = asset_path(self.root, sound['file'])
            if not path.is_file():
                raise ValueError('叫声文件不存在。')
            self.stop_sound()
            self.audio.setSource(QUrl.fromLocalFile(str(path)))
            self.pending_audio = True
            self.audio_ready()
        except (OSError, ValueError) as error:
            self.fail('无法试听', error)

    def audio_ready(self):
        if self.pending_audio and self.audio.status() == QSoundEffect.Ready:
            self.pending_audio = False
            self.audio.play()
        elif self.pending_audio and self.audio.status() == QSoundEffect.Error:
            self.pending_audio = False
            self.status.setText('这段叫声无法播放，请检查 WAV 格式。')

    def stop_sound(self):
        self.pending_audio = False
        self.audio.stop()

    def build_actions(self):
        page = QWidget()
        layout = QHBoxLayout(page)
        left = QVBoxLayout()
        self.actions = QListWidget()
        self.actions.setMaximumWidth(150)
        self.actions.currentItemChanged.connect(self.select_action)
        left.addWidget(self.actions)
        left.addWidget(button('新增动作', self.new_action))
        self.remove_action_button = button('删除自定义动作', self.remove_action)
        left.addWidget(self.remove_action_button)
        layout.addLayout(left)
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        controls = QWidget()
        right = QVBoxLayout(controls)
        form = QFormLayout()
        self.action_name = QLineEdit()
        self.action_name.textChanged.connect(self.change_action)
        self.action_enabled = QCheckBox('显示在桌宠菜单中')
        self.action_enabled.toggled.connect(self.change_action)
        self.category = QComboBox()
        self.category.addItems(['互动', '动作'])
        self.category.currentTextChanged.connect(self.change_action)
        self.fps = spin(1, 30, 0.5, 1)
        self.duration = spin(0.3, 120, 0.1, 1)
        self.weight = spin(0, 20)
        self.loop = QCheckBox('持续时间内循环播放')
        self.motion = QComboBox()
        for label, key in [('原位播放', 'none'), ('摸摸', 'pet'), ('走动', 'walk'),
                           ('轻咬前探', 'bite'), ('左右摇摆', 'rock')]:
            self.motion.addItem(label, key)
        self.action_sound = QComboBox()
        self.action_message = QLineEdit()
        for widget in (self.fps, self.duration, self.weight):
            widget.valueChanged.connect(self.change_action)
        self.loop.toggled.connect(self.change_action)
        self.motion.currentIndexChanged.connect(self.change_action)
        self.action_sound.currentIndexChanged.connect(self.change_action)
        self.action_message.textChanged.connect(self.change_action)
        form.addRow('名字', self.action_name)
        form.addRow('菜单分组', self.category)
        form.addRow('', self.action_enabled)
        form.addRow('播放速度（帧/秒）', self.fps)
        form.addRow('持续时间（秒）', self.duration)
        form.addRow('', self.loop)
        form.addRow('自主活动权重', self.weight)
        form.addRow('身体动作', self.motion)
        form.addRow('搭配叫声', self.action_sound)
        form.addRow('气泡文字', self.action_message)
        right.addLayout(form)
        self.flight_note = QLabel('飞行采用完整鸟体帧，保留已校正的展翅与收翅顺序。')
        self.flight_note.setWordWrap(True)
        self.flight_note.setObjectName('muted')
        right.addWidget(self.flight_note)
        right.addWidget(QLabel('播放顺序 · 拖动缩略图调整'))
        self.sequence = QListWidget()
        self.sequence.setIconSize(QSize(66, 66))
        self.sequence.setViewMode(QListView.IconMode)
        self.sequence.setFlow(QListView.LeftToRight)
        self.sequence.setWrapping(False)
        self.sequence.setDragDropMode(QAbstractItemView.InternalMove)
        self.sequence.setSelectionMode(QAbstractItemView.ExtendedSelection)
        self.sequence.setMinimumHeight(105)
        self.sequence.setMaximumHeight(130)
        self.sequence.model().rowsMoved.connect(self.sequence_changed)
        right.addWidget(self.sequence)
        self.remove_frames_button = button('移除选中帧', self.remove_frames)
        right.addWidget(self.remove_frames_button)
        right.addWidget(QLabel('可用姿态 · 双击加入动作'))
        self.gallery = QListWidget()
        self.gallery.setIconSize(QSize(54, 54))
        self.gallery.setViewMode(QListView.IconMode)
        self.gallery.setResizeMode(QListView.Adjust)
        self.gallery.setMovement(QListView.Static)
        self.gallery.setSelectionMode(QAbstractItemView.ExtendedSelection)
        self.gallery.setMinimumHeight(160)
        self.gallery.setMaximumHeight(220)
        self.gallery.itemDoubleClicked.connect(lambda item: self.add_frame(item.data(Qt.UserRole)))
        right.addWidget(self.gallery)
        self.add_frames_button = button('加入选中姿态', self.add_selected_frames)
        right.addWidget(self.add_frames_button)
        self.import_frame_button = button('导入透明 PNG 姿态', self.import_frame)
        right.addWidget(self.import_frame_button)
        hint = QLabel('0～15 为内置姿态。导入的姿态会自动对齐底部中心。自主活动权重为 0 时，仅由点击或菜单触发。')
        hint.setWordWrap(True)
        hint.setObjectName('muted')
        right.addWidget(hint)
        right.addStretch()
        scroll.setWidget(controls)
        layout.addWidget(scroll, 1)
        self.tabs.addTab(page, '动作')

    def action_entry(self):
        return self.draft['actions'].get(self.action_id)

    def refresh_action_list(self):
        with self.filling():
            self.actions.clear()
            for key, action in self.draft['actions'].items():
                item = QListWidgetItem(action['label'])
                item.setData(Qt.UserRole, key)
                self.actions.addItem(item)
                if key == self.action_id:
                    self.actions.setCurrentItem(item)
        if self.actions.currentItem() is None and self.actions.count():
            self.actions.setCurrentRow(0)
        self.load_action()
        self.refresh_action_choices()

    def select_action(self, item, previous=None):
        if self.loading or item is None:
            return
        self.action_id = item.data(Qt.UserRole)
        self.load_action()
        self.preview.restart()

    def refresh_sound_choices(self):
        if not hasattr(self, 'action_sound'):
            return
        selected = self.action_entry().get('sound', 'none') if self.action_entry() else 'none'
        with self.filling():
            self.action_sound.clear()
            self.action_sound.addItem('不播放', 'none')
            self.action_sound.addItem('随机叫声', 'random')
            for sound in self.draft['sounds']:
                self.action_sound.addItem(sound['label'], sound['id'])
            self.action_sound.setCurrentIndex(max(0, self.action_sound.findData(selected)))

    def load_action(self):
        action = self.action_entry()
        if not action:
            return
        flight = action.get('kind') == 'flight'
        with self.filling():
            self.action_name.setText(action['label'])
            self.action_enabled.setChecked(action.get('enabled', True))
            self.category.setCurrentText(action.get('category', '动作'))
            self.fps.setValue(action.get('fps', 4))
            self.duration.setValue(action.get('duration', 3))
            self.weight.setValue(action.get('auto_weight', 0))
            self.loop.setChecked(action.get('loop', False))
            self.motion.setCurrentIndex(max(0, self.motion.findData(action.get('motion', 'none'))))
            self.action_message.setText(action.get('message', ''))
            self.refresh_sound_choices()
            self.sequence.clear()
            for frame in action.get('frames', []):
                self.sequence.addItem(self.frame_item(frame))
        for widget in (self.category, self.action_enabled, self.fps, self.duration,
                       self.weight, self.loop, self.motion, self.sequence, self.gallery,
                       self.remove_frames_button, self.add_frames_button, self.import_frame_button):
            widget.setEnabled(not flight)
        self.flight_note.setVisible(flight)
        self.remove_action_button.setEnabled(self.action_id not in BUILTIN_ACTIONS)
        self.preview_title.setText(action['label'] + ' · 预览')
        self.preview_info.setText('飞行动作使用完整鸟体帧。' if flight else
                                 '预览循环播放，便于检查衔接；不会发出叫声。')
        self.preview.update()

    def change_action(self, *args):
        if self.loading or not self.action_entry():
            return
        action = self.action_entry()
        action.update(label=self.action_name.text(), message=self.action_message.text(),
                      sound=self.action_sound.currentData() or 'none')
        if action.get('kind') != 'flight':
            action.update(enabled=self.action_enabled.isChecked(), category=self.category.currentText(),
                fps=self.fps.value(), duration=self.duration.value(), loop=self.loop.isChecked(),
                auto_weight=self.weight.value(), motion=self.motion.currentData())
        if self.actions.currentItem():
            self.actions.currentItem().setText(action['label'])
        self.preview_title.setText(action['label'] + ' · 预览')
        self.refresh_action_choices()
        self.changed()

    def new_action(self):
        key = 'action_' + uuid.uuid4().hex[:8]
        self.draft['actions'][key] = {'label': '新动作', 'kind': 'frames', 'frames': [0],
            'fps': 4, 'duration': 2, 'loop': False, 'enabled': True, 'auto_weight': 0,
            'sound': 'none', 'message': '', 'motion': 'none', 'category': '动作'}
        self.action_id = key
        self.refresh_action_list()
        self.preview.restart()
        self.changed()

    def remove_action(self):
        if self.action_id in BUILTIN_ACTIONS or self.action_id is None:
            return
        removed = self.action_id
        self.draft['actions'].pop(removed)
        for name in ('click', 'double_click'):
            if self.draft['interactions'].get(name) == removed:
                self.draft['interactions'][name] = 'pet' if name == 'click' else 'stretch'
        self.action_id = next(iter(self.draft['actions']), None)
        self.refresh_action_list()
        self.changed()

    def frame_item(self, frame):
        item = QListWidgetItem(str(frame))
        item.setData(Qt.UserRole, frame)
        if hasattr(self, 'preview') and 0 <= frame < len(self.preview.normal):
            item.setIcon(QIcon(self.preview.normal[frame]))
        item.setSizeHint(QSize(78, 91))
        return item

    def refresh_gallery(self):
        self.gallery.clear()
        for frame in range(len(self.preview.normal)):
            self.gallery.addItem(self.frame_item(frame))

    def sequence_changed(self, *args):
        if self.loading or not self.action_entry() or self.action_entry().get('kind') == 'flight':
            return
        self.action_entry()['frames'] = [self.sequence.item(index).data(Qt.UserRole)
                                        for index in range(self.sequence.count())]
        self.changed()

    def add_frame(self, frame):
        if self.action_entry().get('kind') == 'flight':
            return
        self.sequence.addItem(self.frame_item(frame))
        self.sequence_changed()

    def add_selected_frames(self):
        for item in self.gallery.selectedItems():
            self.add_frame(item.data(Qt.UserRole))

    def remove_frames(self):
        for row in sorted((self.sequence.row(item) for item in self.sequence.selectedItems()), reverse=True):
            self.sequence.takeItem(row)
        self.sequence_changed()

    def import_frame(self):
        filenames, _ = QFileDialog.getOpenFileNames(self, '选择透明 PNG 姿态', '', 'PNG 图像 (*.png)')
        if not filenames:
            return
        try:
            validated = []
            for filename in filenames:
                image = QImage(filename)
                if image.isNull() or not image.hasAlphaChannel():
                    raise ValueError(Path(filename).name + ' 不是带透明通道的 PNG。')
                if image.width() > 8192 or image.height() > 8192:
                    raise ValueError('单张姿态请缩小到 8192 × 8192 像素以内。')
                validated.append((filename, image.width(), image.height()))
            added = []
            for filename, width, height in validated:
                relative = 'pose-' + uuid.uuid4().hex[:12] + '.png'
                shutil.copy2(filename, asset_path(self.root, relative))
                self.draft['frames'].append({'file': relative, 'rect': [0, 0, width, height],
                    'anchor': [width / 2, height * 0.95], 'scale': min(10, 400 / width, 400 / height)})
                added.append(15 + len(self.draft['frames']))
            self.preview.reload()
            self.refresh_gallery()
            for frame in added:
                self.add_frame(frame)
            self.changed()
        except (OSError, ValueError) as error:
            self.fail('无法导入姿态', error)

    def build_interactions(self):
        page = QWidget()
        layout = QVBoxLayout(page)
        form = QFormLayout()
        self.click_action = QComboBox()
        self.double_action = QComboBox()
        self.click_action.currentIndexChanged.connect(self.change_interactions)
        self.double_action.currentIndexChanged.connect(self.change_interactions)
        form.addRow('单击点点', self.click_action)
        form.addRow('双击点点', self.double_action)
        self.intervals = {}
        for title, first, second, low, high in [
                ('自主活动间隔（秒）', 'idle_min', 'idle_max', 1, 3600),
                ('自动叫声间隔（秒）', 'call_min', 'call_max', 1, 3600)]:
            row = QHBoxLayout()
            for index, name in enumerate((first, second)):
                control = spin(low, high)
                control.valueChanged.connect(self.change_interactions)
                self.intervals[name] = control
                if index:
                    row.addWidget(QLabel('至'))
                row.addWidget(control)
            form.addRow(title, row)
        layout.addLayout(form)
        hint = QLabel('拖动仍会展翅飞行，松手收翅。\n自主活动和自动叫声的总开关在桌宠右键菜单中。\n动作页的权重越大，自主活动时越常被选中；权重为 0 则不会自动出现。')
        hint.setWordWrap(True)
        hint.setObjectName('muted')
        layout.addWidget(hint)
        layout.addStretch()
        self.tabs.addTab(page, '互动')

    def refresh_action_choices(self):
        if not hasattr(self, 'click_action'):
            return
        with self.filling():
            for control, name in ((self.click_action, 'click'), (self.double_action, 'double_click')):
                control.clear()
                for key, action in self.draft['actions'].items():
                    label = action['label'] + ('' if action.get('enabled', True) else ' · 停用')
                    control.addItem(label, key)
                control.setCurrentIndex(control.findData(self.draft['interactions'].get(name)))

    def load_interactions(self):
        with self.filling():
            self.refresh_action_choices()
            for name, control in self.intervals.items():
                control.setValue(self.draft['interactions'].get(name, 10))

    def change_interactions(self, *args):
        if self.loading:
            return
        self.draft['interactions'].update(click=self.click_action.currentData(),
            double_click=self.double_action.currentData(),
            **{name: control.value() for name, control in self.intervals.items()})
        self.changed()

    def save(self):
        try:
            validated = validate_project(copy.deepcopy(self.draft), self.root)
            self.pet.apply_project(validated)
            self.draft = copy.deepcopy(self.pet.project)
            self.saved = copy.deepcopy(self.draft)
            self.preview.reload()
            self.refresh_gallery()
            self.refresh_sound_list()
            self.refresh_action_list()
            self.load_interactions()
            self.status.setText('已保存，桌宠已应用这些设置。')
            return True
        except (OSError, ValueError, TypeError, KeyError) as error:
            self.fail('暂时无法保存', error)
            return False

    def reject(self):
        self.close()

    def closeEvent(self, event):
        if self.draft != self.saved:
            message = QMessageBox(self)
            message.setWindowTitle('保留修改')
            message.setText('还有未保存的修改，要保存后再关闭吗？')
            save_button = message.addButton('保存并关闭', QMessageBox.AcceptRole)
            discard_button = message.addButton('放弃修改', QMessageBox.DestructiveRole)
            message.addButton('继续编辑', QMessageBox.RejectRole)
            message.exec_()
            chosen = message.clickedButton()
            if chosen is save_button:
                if not self.save():
                    event.ignore()
                    return
            elif chosen is not discard_button:
                event.ignore()
                return
        self.stop_sound()
        self.preview.timer.stop()
        self.appearance.shutdown()
        event.accept()
